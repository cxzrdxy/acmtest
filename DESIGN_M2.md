# M2 评测核心 详细设计方案

> **目标产出**：可提交代码、可判题、可见结果 —— 完成"评测判定"这一系统心脏
> **技术栈**：沿用 M1（ASP.NET Core 8 + EF Core 8 + PostgreSQL 16 + Vue 3）+ Docker 沙箱
> **前置**：M1 已收官（鉴权 + 题目 CRUD + 前端 4 页面 + 迁移建表）

---

## 一、M2 范围与验收标准

### 1.1 范围

| 类别 | 包含 | 不包含（留至 M3+） |
|------|------|------|
| 提交 | 提交代码 + 选择语言 + 结果查询 | 实时推送（SignalR/WebSocket） |
| 评测 | 编译 + 逐测试点运行 + 判定 | 子任务/部分分/SPJ 特判 |
| 语言 | **C++17 + Python3**（两种最常用） | Java/Go、shell 脚本类 |
| 沙箱 | Docker 容器隔离（资源限制 + 断网） | seccomp profile 深度加固、多机集群 |
| 测试点 | 文件系统存储 + 上传/查看接口 | 数据打包下载、自动生成数据 |
| 队列 | **同步评测**（提交 → 阻塞 → 返回结果） | Redis 队列 + Worker 异步解耦 |
| 前端 | 详情页提交区 + 代码编辑器 + 结果展示 | 提交历史列表、排名、统计图表 |
| 计数 | 题目 AC/提交数统计 | 用户通过率、标签统计 |

### 1.2 验收标准

- 提交 C++/Python 代码 → 返回状态（AC/WA/TLE/MLE/RE/CE）+ 耗时/内存 + 逐测试点结果
- 判题结果与手测一致（错题 WA、超时 TLE、编译错 CE、正确 AC）
- 测试点可从接口上传管理，评测按题读取对应文件
- `dotnet test` 全绿（新增提交评测用例）
- Playwright UI 验收：提交 → 见结果

### 1.3 与原方案（DESIGN.md 2.2）的核心差异

| 项 | 原方案 | 本方案（个人自用简化） |
|----|--------|----------------------|
| 评测模式 | Redis 队列 + 独立 Worker 进程（M3 目标） | **同步评测**：API 内完成，一步到位 |
| 实时推送 | SignalR 逐点推送 | 无：提交接口阻塞至出结果，一次返回 |
| 沙箱 | Docker + seccomp 深度配置 | Docker 容器资源限制（Memory/Pids/Network），不加 seccomp |
| 语言 | 5 种（C++/C/Python/Java/Go） | **C++17 + Python3**，其余 M3 按需加 |
| SPJ/子任务 | 完整支持 | 不支持（M3+） |
| Detail 结构 | 子任务嵌套 | 扁平测试点数组（无子任务） |

> 同步评测的代价：提交接口可能阻塞数秒（多个测试点串行执行），个人自用单用户场景完全可接受；M3 再升级为队列 + 轮询/推送。

---

## 二、数据库模型（M2 部分）

### 2.1 Submission 实体（新增）

```csharp
// Acm.Api/Models/Submission.cs
public class Submission
{
    public long Id { get; set; }              // 提交 ID（long：评测系统提交量大）
    public int UserId { get; set; }           // FK -> users.id
    public int ProblemId { get; set; }        // FK -> problems.id
    public string Language { get; set; } = null!;   // cpp17 | python3
    public string Code { get; set; } = null!;       // 源码（Text）
    public int CodeLength { get; set; }             // 字节数（前端算或后端算）
    public string Status { get; set; } = "PENDING"; // PENDING/JUDGING/AC/WA/TLE/MLE/RE/CE
    public int Score { get; set; }                  // 0-100（M2 简单计：AC 点占比）
    public int? TimeMs { get; set; }                // 所有测试点最大耗时
    public int? MemoryKb { get; set; }              // 所有测试点最大内存
    public string? Detail { get; set; }             // 逐点结果 JSON（jsonb）
    public DateTime CreatedAt { get; set; }         // DB 默认 now()
}
```

### 2.2 Problem 实体（加两个计数，M1 注释预留）

```csharp
// Models/Problem.cs 追加：
public int AcCount { get; set; }       // 通过人数（去重 UserId）
public int SubmitCount { get; set; }   // 提交总数
```

> 计数在评测写回时更新：AC 且该用户首次 AC → AcCount+1；SubmitCount 每次评测 +1。

### 2.3 表配置（AppDbContext.cs 追加）

```csharp
b.Entity<Submission>(e =>
{
    e.ToTable("submissions");
    e.Property(s => s.Code).HasColumnType("text");
    e.Property(s => s.Detail).HasColumnType("jsonb");
    e.Property(s => s.Status).HasMaxLength(16);
    e.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
    e.HasOne<User>().WithMany().HasForeignKey(s => s.UserId);
    e.HasOne<Problem>().WithMany().HasForeignKey(s => s.ProblemId);
    // 索引：按用户查提交历史 / 按题查提交
    e.HasIndex(s => new { s.UserId, s.Id });
    e.HasIndex(s => new { s.ProblemId, s.Id });
});
```

> 迁移：`dotnet ef migrations add AddSubmissionsAndCounters -o Data/Migrations`

---

## 三、测试点管理（文件系统 + 接口）

### 3.1 目录结构

```
data/testcases/{problemId}/          # 按题目 ID 分目录（gitignore 已排除）
├── 1.in
├── 1.out
├── 2.in
├── 2.out
└── ...
```

**命名约定**：`{n}.in` / `{n}.out`，n 从 1 递增。评测机按序读全部 `.in` 文件。

### 3.2 配置（appsettings.json 新增）

```json
"Judge": {
  "TestcaseRoot": "/data/testcases",     // 容器内挂载路径；本地 dev 用 C:/.../acmtest/data/testcases
  "UseDockerSandbox": true,              // false = 宿主进程直跑（仅开发调试）
  "DockerImageCpp": "judge-cpp:latest",
  "DockerImagePython": "judge-python:latest"
}
```

### 3.3 测试点接口（ProblemsController 追加，全部需登录）

| 方法 | 路由 | 功能 |
|------|------|------|
| GET | `/api/v1/problems/{pid}/testcases` | 列出测试点（编号 + in/out 大小） |
| POST | `/api/v1/problems/{pid}/testcases` | 上传单个测试点（multipart：in 文件 + out 文件 + 编号） |
| DELETE | `/api/v1/problems/{pid}/testcases/{n}` | 删除指定测试点 |

> M2 不做 zip 批量上传/下载（个人自用逐个传即可，或直接往 data 目录丢文件——评测只认文件系统，接口只是辅助）。

---

## 四、评测核心设计

### 4.1 评测服务（JudgeService）

工作目录约定：`data/tmp/{submissionId}/` 存放源码、编译产物（main）与输入副本；评测结束 finally 清理。

```
SubmitAsync 流程（同步）：
1. 校验：题存在 / 语言合法 / 代码非空
2. 写库 Submission(status=PENDING) → SaveChanges
3. status=JUDGING → SaveChanges
4. 准备宿主工作目录 data/tmp/{submissionId}/（写入源码 main.cpp / main.py）
5. 找测试点文件列表（1..N，按文件名升序枚举实际存在的 .in）
6. 编译（仅 cpp17）：起编译容器，judge-cpp 镜像，挂 rw 工作目录，
   容器内 g++ -O2 -std=c++17 main.cpp -o main
   ├─ 编译退出码非 0 → status=CE，detail 存编译错误，跳至 9
7. 逐测试点运行沙箱（见 4.2）：C++ 容器内 ./main，Python 容器内 python3 main.py
8. 汇总：全 AC → AC；有 TLE → TLE（M2 简单判定，优先级 TLE>MLE>RE>WA）
   时间取最大，内存取最大，score = AC 点数/总点数*100
9. 写回 Submission + 更新 Problem 计数 → SaveChanges
10. 清理工作目录 → 返回完整结果
```

### 4.2 沙箱执行（SandboxRunner）

```csharp
// 用 Docker.DotNet 启动一次性容器执行用户程序
// 输入重定向：把测试点 .in 内容写入容器 stdin（docker exec -i 方式）或挂载只读卷
// 输出收集：容器 stdout 写回文件
var container = await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
{
    Image = lang == "cpp17" ? cfg.DockerImageCpp : cfg.DockerImagePython,
    // C++: 容器内 ./main（编译产物已由编译容器产出）；Python: python3 main.py
    Cmd = new[] { "/bin/sh", "-c", runCmd },
    HostConfig = new HostConfig
    {
        Memory = memoryLimitMB * 1024L * 1024,   // 题目内存限制
        MemorySwap = memoryLimitMB * 1024L * 1024, // 禁止 swap
        PidsLimit = 50,                          // 防 fork bomb
        NetworkMode = "none",                    // 断网
        ReadonlyRootfs = true,                   // 只读根文件系统
        Tmpfs = new() { ["/tmp"] = "size=64m" }
    }
});
```

**沙箱镜像**（judge/Dockerfile.runtimes）：编译容器与运行容器**复用同一 judge-cpp 镜像**（内含 g++，供编译阶段用）；评测机只负责选镜像、拼命令、排顺序：

```dockerfile
# judge-cpp（编译容器 + 运行容器复用；g++ 用于编译阶段）
FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y g++ coreutils
```

```dockerfile
# judge-python
FROM python:3.12-slim
```

> **开发环境注意（Windows）**：本机是 Docker Desktop（Linux 容器），沙箱可用。`data/testcases` 需要能被评测代码访问——本地开发用绝对路径；部署时 compose 挂载卷。

### 4.3 输出比对（OutputComparer）

- 用户输出与标准输出**忽略行尾空白和文件尾换行**后逐字节比对
- 无标准输出文件时的规则：M2 简单处理，空输出只匹配空输出

### 4.4 状态机（沿用 DESIGN.md 2.2.4 简化）

```
PENDING → JUDGING → AC / WA / TLE / MLE / RE / CE
```

判定优先级（一个点命中即终态）：CE（编译期）> TLE > MLE > RE > WA > AC

---

## 五、API 设计（提交 + 查询）

| 方法 | 路由 | 鉴权 | 功能 |
|------|------|------|------|
| POST | `/api/v1/problems/{pid}/submissions` | 需要 | 提交代码 `{language, code}` → 阻塞评测 → 返回 SubmissionRead |
| GET | `/api/v1/submissions/{sid}` | 需要 | 查询提交结果（同步失败/超时时补救用） |
| GET | `/api/v1/problems/{pid}/submissions` | 需要 | 该题我的提交列表（可选，M2 简单实现） |

DTO（ProblemDtos.cs 追加或新文件 SubmissionDtos.cs）：

```csharp
public record SubmissionCreate(
    [Required, RegularExpression(@"^(cpp17|python3)$")] string Language,
    [Required, MaxLength(65536)] string Code);

public record TestcaseResult(int Id, string Status, int? TimeMs, int? MemoryKb);

public record SubmissionRead(
    long Id, int UserId, int ProblemId, string Language,
    string Status, int Score, int? TimeMs, int? MemoryKb,
    List<TestcaseResult> Detail, DateTime CreatedAt);
```

> 提交接口超时保护：HttpClient/评测总时长上限 = 测试点数 × 时限 × 2 + 编译宽限，超时强制 TLE 收尾。

---

## 六、前端设计

### 6.1 ProblemDetail.vue 提交区（替换占位）

```
提交评测（M2）
┌──────────────────────────────┐
│ [语言▼: C++17 / Python3]     │
│ ┌──────────────────────────┐ │
│ │  CodeEditor.vue          │ │  ← 简易 textarea（M2 不做语法高亮）
│ └──────────────────────────┘ │
│ [提交]（loading 时禁用）      │
└──────────────────────────────┘
提交后结果区：
  状态徽章（AC 绿 / WA 红 / TLE 橙 / CE 黄 / ...）
  耗时/内存：最大 123ms / 4.2MB
  逐测试点：点1 AC 12ms · 点2 WA ...
  分数：80/100
```

### 6.2 CodeEditor.vue（新组件）

M2 实现为**带行号的 textarea**（约 60 行），不做 Monaco/高亮（M3 可选）。

### 6.3 api/index.js 追加

```js
submissionApi = {
  submit: (pid, data) => http.post(`/problems/${pid}/submissions`, data),
  get: (sid) => http.get(`/submissions/${sid}`),
  listByProblem: (pid) => http.get(`/problems/${pid}/submissions`)
}
```

---

## 七、目录变更总览

```
backend/Acm.Api/
├── Models/Submission.cs              # 新增
├── Data/AppDbContext.cs              # 追加 submissions 表配置
├── Data/Migrations/AddSubmissions*   # 新增迁移
├── Services/JudgeService.cs          # 新增：评测编排（编译/逐点/汇总）
├── Services/SandboxRunner.cs         # 新增：Docker 沙箱执行
├── Services/OutputComparer.cs        # 新增：输出比对（可并入 JudgeService）
├── Dtos/SubmissionDtos.cs            # 新增
├── Controllers/SubmissionsController.cs   # 新增：/api/v1/submissions
├── Controllers/ProblemsController.cs      # 追加：测试点管理 + 提交入口
├── Config.cs                         # 追加 JudgeOptions
└── appsettings.json                  # 追加 Judge 节

backend/Acm.Api.Tests/M2/SubmitJudgeTests.cs   # 新增集成测试
backend/Judge/Dockerfile.runtimes              # 沙箱镜像构建
judge 或 data/testcases/                       # 测试点文件（已有 .gitkeep）
frontend/src/components/CodeEditor.vue         # 实现（原 0 字节占位）
frontend/src/views/ProblemDetail.vue           # 追加提交区 + 结果展示
frontend/src/api/index.js                      # 追加 submissionApi
```

## 八、验证方式

```bash
# 沙箱镜像构建（首次）
docker build -f judge/Dockerfile.runtimes --target cpp -t judge-cpp .
docker build -f judge/Dockerfile.runtimes --target python -t judge-python .

# 造数据 + 评测测试
POST /problems/P1000/testcases  (1.in=1 2, 1.out=3)
提交 AC 代码 → 应得 AC 100
提交 WA 代码 → WA
提交死循环 → TLE
提交语法错误 → CE
```

验收清单：
1. C++ AC 题返回 AC + 耗时/内存 + 逐点 detail
2. Python3 AC 题同样通过
3. WA/TLE/CE 各返回正确状态
4. 题目计数更新（SubmitCount+1，首次 AC 后 AcCount+1）
5. 测试点上传/删除接口可用
6. `dotnet test` 全绿（新增用例覆盖 AC/WA/CE）
7. Playwright：详情页提交 → 结果徽章显示

---

## 九、风险与对策

| 风险 | 对策 |
|------|------|
| 评测阻塞 API 线程影响并发 | 个人自用单用户，可接受；M3 换队列 |
| Docker Desktop 未启动 → 沙箱失败 | 启动前检测，`UseDockerSandbox=false` 降级宿主直跑（仅调试） |
| 用户代码恶意（死循环/吃内存/写文件） | 容器：Memory 限制 + PidsLimit + 断网 + 只读根 + tmpfs；超时强制 kill |
| 测试点文件缺失 | 无测试点 → 直接返回"无测试点"错误状态（或 400） |
| 输出文件巨大 | 限制输出大小（如 16MB），超出判 RE |
| Windows 路径与容器路径差异 | TestcaseRoot 配置分离：宿主路径（读文件）vs 容器内（若挂载） |

---

## 十、里程碑

| 阶段 | 内容 | 验证 |
|------|------|------|
| M2.1 数据层 | Submission 实体 + 迁移 + 计数 | dotnet ef 迁移成功 |
| M2.2 沙箱 | SandboxRunner + 镜像构建 | docker run 手测成功 |
| M2.3 判题 | JudgeService + 状态判定 + 比对 | API 提交 AC/WA/TLE/CE 四态 |
| M2.4 测试点 | 上传/删除接口 + 目录读写 | 接口与文件系统一致 |
| M2.5 前端 | 提交区 + CodeEditor + 结果展示 | Playwright 全流程 |
| M2.6 收尾 | 集成测试 + 总验收 | dotnet test + UI 全绿 |

完成后进入 **M3 实时化**（Redis 队列 + Worker + 轮询/推送）或先做提交历史页（个人常用）。
