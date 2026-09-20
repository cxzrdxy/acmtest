# acmtest · ACM 在线评测系统

个人自用的在线评测系统（Online Judge）：题目管理、代码提交、**Docker 容器沙箱判题**、异步评测与提交历史。
前后端分离，评测进程与 API 进程分离，整套环境用 docker-compose 编排。

`C#` · `ASP.NET Core 8` · `EF Core 8 (Npgsql)` · `PostgreSQL 16` · `Redis 7` · `Docker`
· `Vue 3 + Vite + Pinia` · `xUnit` · `JWT + bcrypt`

**里程碑状态**：M1 用户鉴权 + 题目 CRUD ✅ ｜ M2 评测核心（Docker 沙箱 + 同步判题 + 测试点管理）✅ ｜
M3 异步评测（Redis 队列 + 独立 Worker 进程 + 提交历史页）✅ ｜ M3.4 测试自动化 ✅

---

## 一、系统架构

```text
                      ┌──────────────────────────────────────────────┐
    浏览器 (5173) ─────│  Vue 3 + Vite + Pinia + axios                │
                      │  登录/注册 · 题目列表·详情 · 提交区 · 提交历史   │
                      └───────────────────┬──────────────────────────┘
                                          │ REST (8000) + JWT
                      ┌───────────────────▼──────────────────────────┐
                      │  Acm.Api   (ASP.NET Core 8 Web API)          │
                      │  鉴权 / 题目 CRUD / 测试点管理 / 提交查询        │
                      │  提交 → 写 PENDING 行 + LPUSH ──┐             │
                      └────────────┬────────────────────┼─────────────┘
                                   │ EF Core            │
                      ┌────────────▼──────────┐   ┌─────▼──────────────────┐
                      │  PostgreSQL 16 (5433) │   │  Redis 7 (6380)        │
                      │  users / problems /   │   │  List  queue:judge     │
                      │  submissions          │   └─────┬──────────────────┘
                      └────────────▲──────────┘         │ BRPOP
                                   │                    │
                      ┌────────────┴────────────────────▼─────────────┐
                      │  Acm.JudgeWorker  (独立进程, 无 Web 依赖)       │
                      │  QueueConsumer → JudgeEngine (共享类库)        │
                      │  编译容器 → 逐测试点沙箱 → 汇总写终态 + 计数      │
                      └────────────────────┬──────────────────────────┘
                                           │ Docker npipe / socket
                      ┌────────────────────▼──────────────────────────┐
                      │  每次评测创建一次性容器（judge-cpp / judge-     │
                      │  python 镜像）：断网 · 只读根 · 内存与 PID 限制 · │
                      │  仅 /tmp 可写 · 工作目录与输入文件只读挂载        │
                      └───────────────────────────────────────────────┘
```

设计要点：

- **API 与评测进程分离**：提交只做"写 PENDING + 入队"，秒回提交编号，前端轮询取终态；评测崩溃或超时
  不影响 Web 可用性。
- **评测引擎在共享类库 `Acm.Judge.Core`**：Web 与 Worker 引用同一份 `AppDbContext` / 实体 / EF 迁移 /
  `JudgeEngine` / `SandboxRunner`，避免"两份实现各自漂移"。
- **不可信代码一律进容器**：资源限制、断网、只读根文件系统、超时 kill 全在容器层兜住，不在宿主直接执行。

---

## 二、工程结构

| 项目 | 类型 | 职责 |
|---|---|---|
| **`Acm.Api`** | ASP.NET Core 8 Web API | 鉴权（JWT + bcrypt）、题目 CRUD、测试点上传与管理、提交入队与查询 |
| **`Acm.Judge.Core`** | 类库（Web / Worker 共用） | `AppDbContext` + 实体 + EF 迁移 + `JudgeEngine` + `SandboxRunner` + `OutputComparer` + `JudgeOptions` |
| **`Acm.JudgeWorker`** | 控制台（HostedService） | `QueueConsumer` BRPOP 取任务 → `JudgeEngine.ExecuteAsync` → 写终态与计数 |
| **`Acm.Api.Tests`** | xUnit | 集成测试：真实 Postgres + 真实 Redis + Worker 子进程 + Docker 沙箱 |
| **`frontend`** | Vue 3 + Vite + Pinia | 登录/注册、题目列表与详情、代码编辑器与提交区、提交历史与详情 |

---

## 三、功能

**M1 · 用户与题目**
- 注册 / 登录 / 修改密码 / `me`，JWT 鉴权，密码 bcrypt 存储
- 题目 CRUD：关键字 / 标签 / 难度筛选 + 分页；统计提交数与通过人数（首次 AC 去重）
- 前端：路由守卫 + 401 拦截 + 编辑模态框

**M2 · 评测核心**
- 支持 C++17 与 Python3 提交
- Docker 沙箱执行：内存上限、禁 swap、PID 限制、断网、只读根文件系统、仅 `/tmp` 可写、输入文件只读挂载
- 判定 AC / WA / TLE / MLE / RE / CE，逐测试点结果 + 耗时 + 得分
- 测试点管理：上传 / 列表 / 删除（文件系统 `data/testcases/{problemId}/{n}.in|.out`）

**M3 · 异步评测**
- 提交入 Redis List `queue:judge`，由独立 Worker 进程消费（BRPOP），提交秒回 PENDING
- 前端提交区改为异步轮询（1.5s 间隔、60 次上限），状态自动流转到终态
- 提交历史闭环：列表 API（当前用户 + 题目/状态筛选 + 分页 + JOIN 题目名）+ 历史页 + 详情页
- 结果展示抽为共享组件，`CodeEditor` 支持只读模式

---

## 四、评测流程

```text
提交 ──► 写 PENDING 行 ──► LPUSH queue:judge ──► 立即返回 submissionId
                                   │
                                   ▼ BRPOP（Worker 独立进程）
                             置 JUDGING
                                   │
                 ┌─────────────────┴─────────────────┐
                 ▼                                   ▼
        C++17：编译容器（可写 /work）           Python3：解释执行
        编译失败 → CE + {"compileError": …}
                                   │
                                   ▼
                    逐测试点起一次性容器运行（只读挂载）
                    判定：超时 TLE / OOM MLE / 非零退出 RE / 输出比对 WA·AC
                                   │
                                   ▼
            汇总：状态取最严重（TLE > MLE > RE > WA > AC），score = AC 点数占比
            写终态 + 更新题目计数（首次 AC 才计通过人数）→ 清理 data/tmp/{id}
```

状态机：`PENDING → JUDGING → {AC, WA, TLE, MLE, RE, CE}`

---

## 五、技术要点

### 1. Docker 沙箱的安全边界

每次评测创建**一次性容器**，恶意或死循环代码跑不出容器：

| 限制 | 手段 |
|---|---|
| 内存 | `Memory` 与 `MemorySwap` 同值（禁止借 swap 超用）；`OOMKilled` 直接判 MLE |
| 进程数 | `PidsLimit = 50`（防 fork bomb） |
| 网络 | `NetworkMode = "none"`（完全断网） |
| 文件系统 | `ReadonlyRootfs = true`，仅 `Tmpfs /tmp` 可写且限 64MB |
| 宿主目录 | 工作目录与输入文件以 `Binds` **只读**挂载（编译容器例外，需产出可执行文件） |
| 运行时长 | `CancelAfter(TimeLimitMs)` → 取消 → `KillContainerAsync` → 判 TLE |
| 清理 | `finally` 中强制删容器（忽略 404），无论成败不留残留 |

### 2. 异步解耦：队列 + 独立进程

Web 侧 `JudgeService` 只负责"写 PENDING + `LPUSH` + 查询"，评测由 `Acm.JudgeWorker` 后台消费。
收益：提交接口不被容器启动与编译耗时阻塞（用户秒回"排队中"）、评测进程可独立重启或水平扩展、
单次评测异常由 Worker 兜底置 RE 而不会把 Web 拖垮。

工程细节：Redis List 用 `LPUSH`（左进）+ `BRPOP`（右出）；`StackExchange.Redis` 没有阻塞版
`ListRightPop`，阻塞必须走命令级 `ExecuteAsync("BRPOP", …)`，且 BRPOP 阻塞时长须**小于**
`ConfigurationOptions.AsyncTimeout`（否则客户端先超时）。

### 3. 测试点目录：配置里不出现开发机绝对路径

`SandboxRunner` 把测试点目录与输入文件直接当作 Docker `Binds` 的源，而 **Docker 要求 Bind 源是宿主
绝对路径** —— 但把某台开发机的绝对路径写进配置文件，会让仓库不可移植（也会泄露开发机信息）。

折中做法：配置里写仓库内相对路径 `data/testcases`，**启动时**由 `TestcaseRootResolver` 解析成绝对路径
（从运行目录向上找含 `docker-compose.yml` 的仓库根）；容器里则由 `Judge__TestcaseRoot=/data/testcases`
覆盖，本来就是绝对路径。Web、Worker、集成测试三方看到的是同一个目录。

### 4. 集成测试自动化

`dotnet test` 一条命令全自动跑完，不依赖任何手动前置：

- `WorkerFixture`（xUnit `ICollectionFixture` 单例）先幂等拉起 `docker compose up -d db redis`
  （TCP 探测已在监听则跳过），再拉起 Worker 子进程，用 stdout 关键字探测就绪（15s 上限），`Dispose` 杀进程；
- `TestAppFactory` 用 `WebApplicationFactory<Program>` 起内存站点，但连**同一个真实 Postgres**，
  每个测试前 TRUNCATE；
- 测试**禁用并行**（多个测试类共用一个真实库，并行会互相污染）。

覆盖范围：M1 注册 → 登录 → 题目 CRUD 全流程；M2 评测 AC / WA / TLE / CE 全判定路径；M3 提交历史与筛选。

---

## 六、快速开始

### 环境要求

- Docker Desktop（Linux 容器）
- .NET 8 SDK
- Node.js 18+

### 1. 启动数据库与 Redis

```bash
docker compose up -d db redis      # db → 5433，redis → 6380
```

> 端口约定：PostgreSQL 映射到 **5433**（回避宿主 5432 占用），Redis 映射到 **6380**。

### 2. 构建沙箱镜像（首次）

```bash
docker build -f backend/Judge/Dockerfile.runtimes --target cpp            -t judge-cpp:latest    backend/Judge
docker build -f backend/Judge/Dockerfile.runtimes --target python-runtime -t judge-python:latest backend/Judge
```

### 3. 启动后端与评测 Worker（两个终端）

```bash
# 终端 1：Web API
$env:ConnectionStrings__Default="Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"
$env:ConnectionStrings__Redis="localhost:6380"
$env:ASPNETCORE_URLS="http://localhost:8000"
dotnet run --project backend/Acm.Api

# 终端 2：评测 Worker
$env:ConnectionStrings__Default="Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"
$env:ConnectionStrings__Redis="localhost:6380"
dotnet run --project backend/Acm.JudgeWorker
```

> `appsettings.json` 里的 `Host=db` / `redis:6379` 只在 docker 网络内可用，本地运行必须用环境变量覆盖。
> **Windows 本地必须用 `dotnet run` 起 Worker**：compose 里的 `judge-worker` 在 Linux 容器中无法访问
> 宿主 Docker 引擎（npipe 不能挂进容器），它只用于 Linux 部署。

### 4. 启动前端

```bash
cd frontend
npm install
npm run dev        # http://localhost:5173
```

注册账号后即可创建题目、上传测试点、提交代码并查看评测结果。

### 5. 测试

```bash
docker compose up -d db redis      # 也可交给测试自动拉起
dotnet test                        # 自动起 compose 依赖 + Worker 子进程
```

---

## 七、目录结构

```text
backend/
├── Acm.Api/                 # Web API
│   ├── Controllers/         # Auth / Problems / Submissions
│   ├── Services/            # AuthService / ProblemService / JudgeService(入队) / TestcaseService / TokenService / SubmissionQueue
│   ├── Dtos/  Models/       # 请求响应 DTO（record）与实体（class）
│   └── appsettings.json
├── Acm.Judge.Core/          # Web + Worker 共享类库
│   ├── Data/                # AppDbContext + EF 迁移
│   ├── Models/              # User / Problem / Submission
│   └── Judge/               # JudgeEngine / SandboxRunner / OutputComparer / JudgeOptions / TestcaseRootResolver
├── Acm.JudgeWorker/         # 独立评测进程（QueueConsumer 主循环）
├── Acm.Api.Tests/           # 集成测试（M1 / M2 / M3）
└── Judge/                   # 沙箱镜像构建（Dockerfile.runtimes 多阶段）
frontend/
├── src/views/               # Login / Register / ProblemList / ProblemDetail / SubmissionHistory / SubmissionDetail
├── src/components/          # ProblemForm / CodeEditor / SubmissionResult
├── src/api/  src/stores/    # axios 封装与拦截器 / Pinia auth
└── vite.config.js
data/
├── testcases/               # 测试点 {problemId}/{n}.in|.out（gitignore）
└── tmp/                     # 评测工作目录（gitignore，评测后清理）
docker-compose.yml           # db / redis / backend / judge-worker
```

---

## 八、设计文档

| 文档 | 内容 |
|---|---|
| `DESIGN_M1.md` | M1 详细设计（用户鉴权 + 题目 CRUD） |
| `DESIGN_M2.md` | M2 详细设计（评测核心：Docker 沙箱 + 同步判题 + 测试点管理） |
| `DESIGN_M3.md` | M3 总体设计（异步评测：Redis 队列 + 独立 Worker 进程） |
| `backend/PLAN_M2.1 ~ M2.4.md` | M2 子任务设计（数据层 / 沙箱 / 判题编排 / 测试点管理） |
| `backend/PLAN_M3.1.md`、`PLAN_M3.4.md` | M3 子任务设计（评测引擎抽库与队列 / 测试自动化） |
| `frontend/PLAN_M2.5.md`、`PLAN_M3.2.md`、`PLAN_M3.3.md` | 前端设计（提交区与结果展示 / 异步轮询 / 提交历史闭环） |
| `AGENTS.md` | 项目约定：架构速览、常用命令、连接串与端口陷阱、评测与沙箱要点 |

> 文档与代码不一致时**以代码为准**（设计文档记录决策与理由，代码是最终事实）。

---

## 九、后续计划

M4 候选：SignalR 实时推送评测结果（替代轮询）｜SPJ 特判（special judge）｜子任务与部分分｜
排名与统计｜Monaco 编辑器与语法高亮｜更多语言运行时。

---

## 十、说明

- **个人自用简化设计**（有意为之，非缺陷）：无 admin 角色与权限分级（所有登录用户可 CRUD 题目）、
  无邮箱强校验、无密码复杂度校验。
- **沙箱加固边界**：已做容器级资源 / 网络 / 文件系统限制，未加 seccomp 等深度加固（个人自用可接受）。
- 配置文件中的连接串与 JWT 密钥均为**开发占位值**，部署时请通过环境变量注入真实值
  （`ConnectionStrings__Default`、`Jwt__SecretKey`）。
