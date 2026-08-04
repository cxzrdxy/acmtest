# ACM 编程算法竞赛系统设计方案

> **定位**：个人 / 小型团队自用的轻量级 Online Judge 系统
> **技术栈**：后端 C# ASP.NET Core 8 (.NET 8) + 前端 Vue3 + PostgreSQL + Redis + Docker
> **核心模块**：在线评测判定 · 题目管理 · 数据统计与可视化

---

## 一、系统总体架构

### 1.1 架构总览

```
                        ┌─────────────────────────────┐
                        │         用户浏览器           │
                        │     (Vue3 + Vite SPA)       │
                        └──────────────┬──────────────┘
                               HTTPS 反向代理
                                       │
                        ┌──────────────▼──────────────┐
                        │         Nginx 网关           │
                        │   (静态托管 + API 转发)      │
                        └──────────────┬──────────────┘
                                       │
                  ┌────────────────────┼────────────────────┐
                  │                    │                    │
          ┌───────▼──────┐    ┌────────▼────────┐   ┌───────▼──────┐
          │ ASP.NET Core │    │   评测机集群     │   │   Redis      │
          │  Web 服务    │◄──►│  (Judge Worker)  │   │  (队列/缓存)  │
          │  (REST API)  │    │  每台多沙箱实例   │   └──────────────┘
          └───────┬──────┘    └────────┬─────────┘
                  │                    │
          ┌───────▼─────────────────────▼──────┐
          │           PostgreSQL 数据库         │
          │   (题目/用户/提交/统计 持久化层)     │
          └────────────────────────────────────┘
```

### 1.2 设计原则

| 原则 | 说明 |
|------|------|
| **单体优先** | ASP.NET Core 单服务承载 API + 后台任务 (Hosted Service)，避免过早微服务化 |
| **评测解耦** | 评测机作为独立进程，通过 Redis 队列消费提交任务，水平扩展 |
| **安全隔离** | 用户代码运行于 Linux container / seccomp 沙箱，资源受限 |
| **可容器化** | 全栈 Docker Compose 一键拉起，便于个人部署 |

---

## 二、模块详细设计

### 2.1 题目管理模块

#### 2.1.1 题目数据模型

```csharp
// Acm.Api/Models/Problem.cs (EF Core 实体)
public class Problem
{
    public int Id { get; set; }                          // 题目号
    public string Slug { get; set; } = null!;            // URL 友好标识 P1000 (唯一索引)
    public string Title { get; set; } = null!;           // 最长 128
    public string? Description { get; set; }             // 题面正文 (Markdown)
    public string? InputDesc { get; set; }                // 输入说明
    public string? OutputDesc { get; set; }               // 输出说明
    public int TimeLimit { get; set; } = 1000;            // ms
    public int MemoryLimit { get; set; } = 256;           // MB
    public List<string> Tags { get; set; } = new();       // 标签: DP/图论/搜索… (Npgsql 映射 text[])
    public short? Difficulty { get; set; }                // 1-5 难度
    public string? SampleInputs { get; set; }              // 样例输入 (JSON)
    public string? SampleOutputs { get; set; }             // 样例输出 (JSON)
    public bool IsPublic { get; set; } = true;
    public int? AuthorId { get; set; }                     // FK -> users.id
    public DateTime CreatedAt { get; set; }
    public int AcCount { get; set; }                       // 通过人数 (冗余计数)
    public int SubmitCount { get; set; }
}
```

#### 2.1.2 测试数据存储策略

- **测试点数据**（input/answer/子任务配置）较大且属二进制特征，存于 **文件系统**，数据库仅存元信息：

```
data/testcases/
  └── P1000/
      ├── meta.yaml          # 子任务划分、评分规则
      ├── 1.in / 1.out
      ├── 2.in / 2.out
      └── ...
```

`meta.yaml` 示例：
```yaml
subtasks:
  - id: 1
    score: 30
    testcases: [1, 2]           # 该子任务包含的测试点
    scoring: sum                 # sum|min
  - id: 2
    score: 70
    testcases: [3, 4, 5]
    scoring: min
special_judge: false            # 是否使用 SPJ
```

#### 2.1.3 接口设计

| 方法 | 路径 | 说明 | 权限 |
|------|------|------|------|
| GET | `/api/problems` | 题目列表分页/搜索/标签筛选 | 登录 |
| GET | `/api/problems/{slug}` | 题目详情 | 登录 |
| POST | `/api/problems` | 创建题目 | 管理员 |
| PUT | `/api/problems/{id}` | 编辑题目 | 管理员 |
| POST | `/api/problems/{id}/testcases` | 上传测试点(支持 zip 打包) | 管理员 |
| GET | `/api/problems/{id}/statistics` | 该题通过率/标签统计 | 登录 |

---

### 2.2 在线评测判定模块

这是系统的心脏，分为 **提交流程** 与 **评测机** 两部分。

#### 2.2.1 提交流程时序

```
 用户提交代码
      │
      ▼
ASP.NET Core API ── 写入 submissions 表 (status=PENDING) ── 生成 submission_id
      │
      ▼
 推送任务到 Redis 队列 queue:judge (含 sub_id / problem_id / language)
      │
      ▼
返回 submission_id 给前端, 前端经 SignalR 加入分组 sub:{id}
      │
      ▼
评测机 BRPOP 队列 ── 取出任务 ── 拉取代码/题面/测试点
      │
      ▼
拉起沙箱容器执行 ── 每个测试点: 编译一次, 多点运行
      │
      ▼
逐点判分 ── 通过 Redis PUBLISH 实时推送每点结果到 SignalR Hub
      │
      ▼
写回 submissions 表最终状态 ── 更新题目 AC/提交计数
```

#### 2.2.2 提交数据模型

```csharp
// Acm.Api/Models/Submission.cs
public class Submission
{
    public long Id { get; set; }
    public int UserId { get; set; }                    // FK -> users.id
    public int ProblemId { get; set; }                 // FK -> problems.id
    public string Language { get; set; } = null!;      // cpp17/python3/...
    public string Code { get; set; } = null!;          // 源代码
    public int CodeLength { get; set; }
    public string Status { get; set; } = "PENDING";   // PENDING/JUDGING/AC/WA/TLE/MLE/RE/CE
    public int Score { get; set; }                      // 0-100
    public int? TimeMs { get; set; }                    // 最大运行时间
    public int? MemoryKb { get; set; }                  // 最大内存
    public string? Detail { get; set; }                 // 每个测试点结果数组 (jsonb)
    public DateTime CreatedAt { get; set; }
}
```

`detail` 结构示例：
```json
{
  "compile_ok": true,
  "subtasks": [
    {
      "id": 1, "passed": 2, "total": 2, "score": 30,
      "testcases": [
        {"id": 1, "status": "AC", "time": 12, "memory": 1024, "exit": 0},
        {"id": 2, "status": "AC", "time": 15, "memory": 1080, "exit": 0}
      ]
    }
  ]
}
```

#### 2.2.3 沙箱设计（核心安全机制）

评测机使用 **Docker 容器 + seccomp** 双重隔离：

```csharp
// judge/Acm.Judge/Sandbox.cs 核心逻辑
public async Task<Verdict> RunInSandboxAsync(string codePath, string testcaseIn, Limits limits)
{
    // 使用 Docker.DotNet 启动受限容器执行用户代码
    // limits: { TimeLimit, MemoryLimit, CpuQuota, Pids }
    var container = await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
    {
        Image = $"judge/{langRuntime}",
        Cmd   = new[] { "/bin/sh", "-c", $"/run.sh {codePath} < {testcaseIn}" },
        HostConfig = new HostConfig
        {
            Memory         = limits.MemoryLimit * 1024L * 1024,
            MemorySwap     = limits.MemoryLimit * 1024L * 1024,  // 禁止 swap
            CPUQuota       = limits.CpuQuota * 1000,
            PidsLimit      = 50,                                  // 限制进程数防 fork bomb
            NetworkMode    = "none",                              // 断网
            ReadonlyRootfs = true,
            Tmpfs          = new() { ["/tmp"] = $"size={limits.MemoryLimit}m" },
            SecurityOpt    = new[] { "no-new-privileges" },
        },
    });
    await _docker.Containers.StartContainerAsync(container.ID, new());
    // 超时 kill，统计 exit code / time / memory
}
```

评测机镜像内置各语言运行时（gcc/g++/python3/openjdk 等），按 `language` 选择对应镜像。

#### 2.2.4 评测结果状态机

```
PENDING ──yatQueue──> JUDGING ──编译失败──> CE
                            │
              ┌─────────────┼──────────────┬────────────┐
              ▼             ▼              ▼            ▼
           (通过)         (WA)           (TLE)        (RE/MLE)
              │             ▼              ▼            ▼
            所有点AC      部分通过→记录分数→最终状态
```

#### 2.2.5 特判（SPJ）支持

当 `meta.yaml` 中 `special_judge: true` 时，评测机使用标准输出与用户输出一起送入坐落于 `data/spj/{problem_id}/spj.cpp` 编译出的 `spj` 程序：

```
./spj user.out answer.in answer.out  →  exit 0 (AC) / exit 1 (WA)
```

#### 2.2.6 支持语言

| Language | 编译/运行 | 时限建议 |
|----------|-----------|----------|
| C++17 (`g++ -O2 -std=c++17`) | 编译型 | 1s |
| C11 | 编译型 | 1s |
| Python3 | 解释型 | 2s |
| Java17 (`javac`/`java`) | 编译+VM | 2s |
| Go | 编译型 | 1s |

---

### 2.3 数据统计与可视化模块

#### 2.3.1 统计维度

| 统计对象 | 指标 | 可视化形式 |
|----------|------|------------|
| 用户个人 | 各标签 AC 数 | 雷达图 (ECharts) |
| 用户个人 | 每日做题数 | 日历热力图 |
| 用户个人 | 题目难度分布 | 环形图 |
| 用户个人 | 通过率趋势 | 折线图 |
| 题目 | 通过率 / 提交分布 | 进度条/柱状图 |
| 平台 | 各语言提交占比 | 饼图 |
| 平台 | 近 30 日活跃提交量 | 折线图 |

#### 2.3.2 数据来源表

新增一张 **做题记录宽表**，供统计查询加速（异步聚合）：

```csharp
// Acm.Api/Models/UserSolvedStat.cs  (复合主键: UserId + ProblemId)
public class UserSolvedStat
{
    public int UserId { get; set; }
    public int ProblemId { get; set; }
    public bool IsAc { get; set; }              // 是否首次通过
    public DateTime? AcAt { get; set; }
    public int Attempts { get; set; }           // 该题尝试次数
    public string? Tag { get; set; }            // 冗余题目主标签加速聚合
    public short? Difficulty { get; set; }
}
```

通过后台定时任务（.NET `BackgroundService` + `PeriodicTimer`）每分钟增量更新：扫描新结束的 submission，回写 `user_solved_stats` 并维护 `problems.AC_count`。

#### 2.3.3 可视化接口

| 方法 | 路径 | 返回 | 示例 |
|------|------|------|------|
| GET | `/api/stats/me/radar` | 各标签 AC 计数 | `{"DP":5,"图论":3,...}` |
| GET | `/api/stats/me/heatmap` | 近一年每日做题数 | `[{date,count}...]` |
| GET | `/api/stats/me/progress` | 难度分布+通过率 | `{1:{AC,WA},2:{...}}` |
| GET | `/api/stats/problem/{id}` | 该题提交统计 | `{ac_rate,status_dist}` |
| GET | `/api/stats/overview` | 平台全局活跃/Language分布 | 后台总览用 |

前端使用 **ECharts** 渲染，雷达图示例组件：

```vue
<!-- frontend/src/components/RadarChart.vue -->
<template>
  <div ref="chart" style="width:480px;height:360px"></div>
</template>
<script setup>
import * as echarts from 'echarts'
import { ref, onMounted } from 'vue'
import { getMyRadar } from '@/api/stats'
const chart = ref()
onMounted(async () => {
  const { data } = await getMyRadar()
  echarts.init(chart.value).setOption({
    radar: {
      indicator: Object.keys(data).map(k => ({ name: k, max: 20 }))
    },
    series: [{ type: 'radar', data: [{ value: Object.values(data) }] }]
  })
})
</script>
```

---

## 三、数据库整体 ER 图

```
┌──────────┐      ┌───────────┐      ┌──────────────────┐
│  users   │1────*│ submissions │*────1│    problems      │
│ -id      │      │ -id         │      │ -id              │
│ -name    │      │ -lang       │      │ -slug            │
│ -role    │      │ -status     │      │ -title           │
│ -rating? │      │ -score      │      │ -time_limit      │
└──────────┘      │ -detail     │      └──────────────────┘
   │              └─────────────┘              │
   │1                                      data/
   │                                          │
   │*                                          │
┌────────────────────┐                ┌──────────────────────┐
│ user_solved_stats  │                │  testcases(files)    │
└────────────────────┘                └──────────────────────┘
```

---

## 四、技术栈与依赖清单

### 4.1 后端（ASP.NET Core / .NET 8）

```
# 关键 NuGet 依赖 (Acm.Api.csproj / Acm.Judge.csproj)
Microsoft.EntityFrameworkCore                  8.*   # ORM
Npgsql.EntityFrameworkCore.PostgreSQL          8.*   # PostgreSQL EF Core 驱动
Microsoft.EntityFrameworkCore.Design           8.*   # dotnet-ef 迁移工具支持
Microsoft.AspNetCore.Authentication.JwtBearer  8.*   # JWT 鉴权
BCrypt.Net-Next                                4.*   # 密码哈希 (bcrypt)
StackExchange.Redis                            2.*   # 评测队列 + 缓存
Microsoft.AspNetCore.SignalR.StackExchangeRedis 8.*  # SignalR Redis backplane
Docker.DotNet                                  3.*   # 评测机调用 Docker API
Swashbuckle.AspNetCore                         6.*   # Swagger / OpenAPI
```

> 框架内置能力，无需额外依赖：SignalR（评测实时推送）、RateLimiter 限流中间件、
> `BackgroundService`（统计聚合定时任务）、`IFormFile`（文件上传）、配置系统（环境变量/appsettings）。

### 4.2 前端（Vue3）

```
# frontend/package.json 关键依赖
vue@^3.4
vite@^5
pinia                  # 状态管理
vue-router@^4
axios
echarts@^5             # 可视化
monaco-editor          # 代码编辑器组件
element-plus           # UI 组件库
```

### 4.3 基础设施

| 组件 | 用途 | 镜像 |
|------|------|------|
| PostgreSQL 16 | 主数据库 | postgres:16 |
| Redis 7 | 评测队列 + 实时推送 + 缓存 | redis:7 |
| Nginx | 反向代理 + 前端托管 | nginx:alpine |
| Docker | 沙箱执行引擎 | host docker |

---

## 五、目录结构

```
acmtest/
├── docker-compose.yml
├── README.md
├── DESIGN.md                 # 本文档
│
├── backend/
│   ├── Dockerfile
│   ├── Acm.sln
│   ├── Acm.Api/                    # ASP.NET Core Web API
│   │   ├── Acm.Api.csproj
│   │   ├── Program.cs              # 入口：DI 注册 / 中间件管道 / 路由
│   │   ├── appsettings.json        # 配置 (连接串/JWT/CORS)
│   │   ├── Controllers/
│   │   │   ├── ProblemsController.cs
│   │   │   ├── SubmissionsController.cs
│   │   │   └── StatsController.cs
│   │   ├── Hubs/
│   │   │   └── JudgeHub.cs         # SignalR 评测实时推送
│   │   ├── Models/                 # EF Core 实体
│   │   ├── Dtos/                   # 请求/响应 DTO (record)
│   │   ├── Data/
│   │   │   ├── AppDbContext.cs
│   │   │   └── Migrations/         # EF Core 数据库迁移
│   │   ├── Services/               # 业务逻辑
│   │   └── Workers/
│   │       └── StatsAggregator.cs  # BackgroundService 统计聚合
│   └── Acm.Api.Tests/              # xUnit 集成测试
│
├── judge/
│   ├── Dockerfile                  # 评测机镜像
│   ├── Dockerfile.runtimes         # 用户代码运行沙箱镜像 (含各语言运行时)
│   └── Acm.Judge/                  # .NET Worker Service 评测机
│       ├── Acm.Judge.csproj
│       ├── Program.cs              # 消费队列主循环
│       ├── Sandbox.cs
│       ├── Compiler.cs
│       └── Comparer.cs             # 输出比对 / SPJ
│
├── frontend/
│   ├── Dockerfile
│   ├── package.json
│   └── src/
│       ├── views/
│       │   ├── ProblemList.vue
│       │   ├── ProblemDetail.vue
│       │   ├── Submit.vue    # 代码提交 + 实时结果
│       │   └── Stats.vue      # 个人统计仪表盘
│       ├── components/
│       │   ├── CodeEditor.vue
│       │   ├── RadarChart.vue
│       │   └── Heatmap.vue
│       ├── api/              # axios 封装
│       ├── stores/           # pinia
│       └── router/
│
├── data/
│   ├── testcases/            # 测试点 (git-ignored)
│   └── spj/                  # 特判程序
│
└── nginx/
    └── default.conf
```

---

## 六、关键流程：评测机工作主循环

```csharp
// judge/Acm.Judge/Program.cs (Worker 主循环，精简示意)
var redis = await ConnectionMultiplexer.ConnectAsync("redis");
var db = redis.GetDatabase();

while (true)
{
    var task = await db.ListRightPopAsync("queue:judge");   // 消费队列
    if (task.IsNull) { await Task.Delay(200); continue; }

    var job    = JsonSerializer.Deserialize<JudgeJob>(task!)!;
    var tcMeta = LoadTestcases(job.ProblemId);
    var limits = job.Limits;

    // 编译
    var (ok, compileMsg) = await Compiler.CompileAsync(job.Language, job.Code);
    if (!ok) { await FinishAsync(job.Id, "CE", new { compileMsg }); continue; }

    var results = new List<Verdict>();
    foreach (var tc in tcMeta.Testcases)
    {
        var verdict = await sandbox.RunInSandboxAsync(executable, tc.In, limits);
        verdict.Status = verdict switch
        {
            { Timeouted: true }  => "TLE",
            { MemoryOver: true } => "MLE",
            { Exit: not 0 }      => "RE",
            _ => Comparer.Compare(verdict.Stdout, tc.Out, tcMeta) ? "AC" : "WA",
        };

        results.Add(verdict);
        // 实时推送每个测试点结果 (SignalR 经 Redis backplane 转发到前端)
        await db.PublishAsync($"channel:sub:{job.Id}", JsonSerializer.Serialize(verdict));
    }

    await FinalizeAsync(job.Id, tcMeta, results);   // 计算子任务分数、写库、更新计数
}
```

---

## 七、安全策略

1. **用户代码沙箱化**：Docker 容器 + `network_mode=none` + `read_only` + seccomp profile（禁用 `fork/exec` 之外的系统调用）、限制 PID 数防 fork bomb。
2. **输入过滤**：题目上传测试点需管理员权限，且只接受 zip/text，文件名白名单。
3. **API 鉴权**：JWT，管理员判断基于 `users.role`，普通用户仅能查看 `is_public=true` 题目。
4. **限流**：ASP.NET Core 内置 `RateLimiter` 中间件，提交接口对单用户 30s 内限 5 次，防刷评测。
5. **代码存储**：源码入库（Text），不做外链；评测机拉取通过内网，不暴露公网。

---

## 八、部署方案（Docker Compose）

```yaml
# docker-compose.yml (精简示意)
services:
  db:    { image: postgres:16, volumes: ["pgdata:/var/lib/postgresql/data"], env: POSTGRES_* }
  redis: { image: redis:7 }
  api:   { build: ./backend, depends_on: [db, redis], ports: ["8000:8000"] }
  judge: { build: ./judge,   depends_on: [redis], volumes: ["/var/run/docker.sock:/var/run/docker.sock", "./data:/data"] }
  web:   { build: ./frontend, ports: ["80:80"], depends_on: [api] }

volumes: { pgdata: {} }
```

> 评测机需挂载宿主 docker.sock 以拉起沙箱容器；个人部署可接受，公网部署需改为 gVisor / Firecracker microVM 隔离。

---

## 九、开发阶段分期建议

| 阶段 | 内容 | 产出 |
|------|------|------|
| **M1 基础** | 数据库模型 + 用户鉴权 + 题目 CRUD | 可登录、可看题 |
| **M2 评测核心** | 评测机 + 沙箱 + 5 种语言 + 同步判分 | 可提交可判 |
| **M3 实时化** | Redis 队列 + WebSocket 逐点推送 | 大题实时反馈 |
| **M4 统计** | 统计宽表 + 5 类图表 | 个人仪表盘 |
| **M5 完善** | SPJ / 子任务 / 限流 / UPS blobs | 生产可用 |

---

## 十、后续可扩展方向（非本期）

- 比赛系统：ACM/IOI 赛制、排行榜、罚时、气球榜。
- 讨论 / 题解模块：评论区、站内信。
- Email Rating 系统（Elo）与赛季展示。
- 分布式评测机调度（多机 + 心跳注册）。