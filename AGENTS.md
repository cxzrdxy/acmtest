# AGENTS.md

个人自用 ACM 在线评测系统（M1 已收官：用户鉴权 + 题目 CRUD 全栈；M2 已收官：评测核心——提交 C++/Python 代码同步评测，AC/WA/TLE/MLE/RE/CE 判定 + 前端提交区；M3 进行中：异步评测，M3.1 已落地——Redis 队列 + 独立 Worker 进程 + 评测逻辑抽共享类库 Acm.Judge.Core，提交秒回 PENDING、Worker 后台出结果）。ASP.NET Core 8 Web API + EF Core 8 (Npgsql) + PostgreSQL 16 + Vue 3 + Redis 7。

## 常用命令

```bash
# 数据库 + Redis（必需先启动，测试也依赖真实 Postgres；本机 WSL 常规发行版已移除、6379 空闲，但约定一律用 compose 的 6380）
docker compose up -d db redis

# 后端 Web（workdir: backend/Acm.Api；本地跑须环境变量覆盖连接串 + 指定端口）
$env:ConnectionStrings__Default="Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"
$env:ConnectionStrings__Redis="localhost:6380"
$env:ASPNETCORE_URLS="http://localhost:8000"
dotnet run --project backend/Acm.Api

# 评测 Worker（独立进程，另开终端；本地跑须同样设置上面两个环境变量）
$env:ConnectionStrings__Default="Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"
$env:ConnectionStrings__Redis="localhost:6380"
dotnet run --project backend/Acm.JudgeWorker

# 集成测试（依赖 ①Postgres 运行中 ②Redis 运行中 ③Worker 进程在跑 ④连接串环境变量；Worker 不在跑则提交永远卡 PENDING）
$env:ConnectionStrings__Default="Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"
$env:ConnectionStrings__Redis="localhost:6380"
dotnet test

# EF 迁移（AppDbContext 已迁入 Acm.Judge.Core，故 --project 指向 Core；startup 用 Api 拿提供程序）
dotnet ef migrations add Xxx --project backend/Acm.Judge.Core --startup-project backend/Acm.Api -o Data/Migrations
dotnet ef database update --connection "Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"

# 沙箱镜像（改 backend/Judge/Dockerfile.runtimes 后重建）
docker build -f backend/Judge/Dockerfile.runtimes --target cpp -t judge-cpp:latest backend/Judge
docker build -f backend/Judge/Dockerfile.runtimes --target python-runtime -t judge-python:latest backend/Judge
```

## 关键注意事项

- **代码修改须先确认**：任何对代码/文件的修改，执行前必须先向用户确认。**确认后不要每步停等**：无风险的常规操作（起服务、查端口/日志、造测试数据、跑构建/测试/验证）应一口气连续做完，只在真正需要用户拍板的节点停（修改文件前、git 提交前、方案分歧时）。
- **前端已完整实现**（M1.3-M1.5：登录/注册/列表/详情 + ProblemForm 模态框 + 守卫 + 拦截器），依赖已装（Vue3+Vite+Pinia+Router+axios）。**M3.2 已落地**：提交区改异步轮询（递归 setTimeout 1.5s×60 次上限，onUnmounted 清理，秒回"排队中"自动流转终态），Playwright 7 场景验收通过。**M3.3 已落地**：提交历史闭环——后端 `GET /api/v1/submissions` 列表 API（强制当前用户 + problemId/status 筛选 + 分页 + JOIN 题目名，`SubmissionRead` 末尾追加 `Code`）+ 前端 `SubmissionHistory`/`SubmissionDetail` 页面 + 导航"提交记录"；结果区抽共享组件 `SubmissionResult.vue`（ProblemDetail 与详情页共用），`CodeEditor` 支持 readonly。**M3.4 待做**：测试自动化（自动起 Worker/TestContainer 选型）+ 总验收。
- **连接串陷阱**：`appsettings.json` 的 `Host=db`/`Redis=redis:6379` 只在 docker 网络内可用；本地跑必须用环境变量覆盖为 `Host=localhost` / `ConnectionStrings__Redis=localhost:6380`。**端口**：本机 5432 被 shellquest-pg 占用（db→5433）；acmtest compose redis 映射 6380（WSL 常规发行版已移除、宿主 6379 实际空闲，但约定一律用 6380，勿动 compose 映射）。
- **测试连真实数据库 + 真实 Redis + Worker 进程**：`TestAppFactory.cs` 用 `WebApplicationFactory<Program>` 起内存站点但连同一个 Postgres，每个测试前 TRUNCATE。M3 起测试还依赖 Redis 运行 + **Worker 进程在跑**（提交不再同步返回终态，测试改轮询等待；Worker 不在跑则提交永远 PENDING，测试超时失败）。**Worker 进程 TestAppFactory 无法托管**——跑测试前手动起 Worker（见常用命令）。M3.4 再做 TestContainer/前置脚本自动化。
- **测试并行化已禁用**：`AssemblyInfo.cs` 禁用 xunit 并行（多个测试类共用一个真实 Postgres，并行会互相污染，如 M1 的 keyword 搜索被 M2 的题目命中）。
- **个人自用简化设计**（勿"修正"）：无 admin/角色区分，所有登录用户可 CRUD 题目；无邮箱强校验；无密码复杂度校验。见 DESIGN_M1.md 1.3。
- **错误契约**：Service 抛 `ApiException(status, detail)`（定义于 AuthService.cs），Program.cs 全局处理器转 `{detail: msg}`，非 ApiException 一律 500。入队失败专用：抛 `ApiException(503, "评测队列不可用")` + 回滚刚插入的 PENDING 行。
- **DTO 约定**：请求/响应一律 record + DataAnnotations 校验；`ProblemUpdate` 全部 nullable，null=不修改（部分更新）。
- **代码风格**：注释用中文（inline 行尾注释 + 简短中文 XML doc）；主构造函数注入（`class Foo(AppDbContext db)`）；实体用 class（可变），DTO 用 record（不可变）。
- **M2 评测核心**：`DESIGN_M2.md` 为设计文档。要点：测试点存 `data/testcases/{pid}/{n}.in/.out`（文件系统，接口辅助）；评测工作目录 `data/tmp/{submissionId}`（gitignore 排除，评测完清理）；状态优先级 TLE>MLE>RE>WA>AC；CE 时 Detail 存 `{"compileError":...}`。
- **Docker 沙箱**：judge-cpp/judge-python 镜像构建自 `backend/Judge/Dockerfile.runtimes`（多阶段 --target cpp / python-runtime）；SandboxRunner 单例持 DockerClient，npipe 连 Docker Desktop；`Judge` 配置节在 appsettings.json（TestcaseRoot/UseDockerSandbox/镜像名）。
- **M3.1 异步评测已落地**：`DESIGN_M3.md` 为总体设计，`backend/PLAN_M3.1.md` 为子任务设计。要点：评测逻辑抽共享类库 `Acm.Judge.Core`（AppDbContext/实体/迁移/SandboxRunner/OutputComparer/JudgeOptions/JudgeEngine 全迁入，Web+Worker 都引用，避免双份漂移）；Web 的 `JudgeService` 瘦身为"写 PENDING + LPUSH 入队 + 查询"；新增 `SubmissionQueue`（Redis List `queue:judge`，LPUSH 左进）；新增 `Acm.JudgeWorker` 独立进程（`QueueConsumer` BRPOP 右出 → `JudgeEngine.ExecuteAsync` → 写终态/计数 → 失败兜底置 RE）。
- **Worker 配置坑（必读）**：①**console 项目默认不拷 appsettings.json**——`Acm.JudgeWorker.csproj` 须显式 `<None Update="appsettings.json" CopyToOutputDirectory>`。②**`Host.CreateApplicationBuilder`（console 宿主）构造时已按 cwd 解析默认 appsettings.json + 不带无前缀环境变量**（Web 宿主才有），故 Worker Program.cs 需：追加绝对路径的 `appsettings.json`（`Path.Combine(AppContext.BaseDirectory, ...)`）保证 Judge 配置节读到 + 再追加 `AddEnvironmentVariables()` 保证 `ConnectionStrings__X` 覆盖 json。③**`dotnet run` 对 console 项目不改 cwd**（cwd=调用目录），web 项目则会改到项目目录——这是上一条配置坑的根因。④compose 的 judge-worker 在 Windows 上连不上宿主沙箱（npipe 无法挂入 Linux 容器），**Windows 本地一律 `dotnet run` 起 Worker**；compose worker 仅供 Linux 部署。
- **SE.Redis 阻塞命令坑**：`ListRightPopAsync` 是 RPOP（非阻塞）并非 BRPOP；阻塞用 `db.ExecuteAsync("BRPOP", key, 5)`（命令级，版本无关）。BRPOP 阻塞时长须 **小于** `ConfigurationOptions.AsyncTimeout`（默认 5000ms，否则客户端超时先于 BRPOP 触发），Worker 已设 15s。

## 架构速览

```
Web（Acm.Api，8000）   ──校验/CRUD/查询──►  AppDbContext (EF)  ──►  PostgreSQL (5433)
   └─ 提交：写 PENDING 行 + LPUSH submissionId ──►  Redis List `queue:judge`  ◄── BRPOP ──  Acm.JudgeWorker（独立进程）
Worker：取任务 → JudgeEngine（编译/逐点沙箱/汇总）→ 写终态+计数 → Postgres
前端轮询 GET /submissions/{sid} → Web 读 Postgres 返回 Worker 写好的终态
```

- 进程拓扑：Web + Worker 两个独立进程，共享 Postgres + Redis + 共享类库 `Acm.Judge.Core`（AppDbContext/实体/迁移/评测引擎都在 Core，避免双份漂移）。
- DI 注册：Web 的 `Program.cs` 注册 AuthService/ProblemService/JudgeService/TestcaseService（Scoped）+ TokenService/SubmissionQueue（Singleton）+ Redis `IConnectionMultiplexer`；Worker 的 `Program.cs` 注册 AppDbContext/JudgeOptions/SandboxRunner（Singleton）+ JudgeEngine（Scoped）+ QueueConsumer（HostedService）+ Redis。Web 不再注册 SandboxRunner（已不评测）。
- 表结构与约束在 `Acm.Judge.Core/Data/AppDbContext.cs` 用 Fluent API 配置（唯一索引、默认 now()）。
- Key 命名 `queue:judge`、状态取 `PENDING→JUDGING→{AC,WA,TLE,MLE,RE,CE}`。
- 项目级文档：DESIGN_M1.md（详细设计+示例代码）、DESIGN_M2.md/M3.md、backend/PLAN_MxY.md（子任务设计）、PROJECT_OVERVIEW.md（目录总览）。代码与文档不一致时以代码为准。
- docker-compose.yml 含 `db`/`redis`/`backend`(8000)/`judge-worker` 四个服务；`backend`/`judge-worker` 构建自同一 `backend/Dockerfile`（单阶段发布整个 solution，worker 用 `command` 覆盖入口），挂 `./data:/data` + 设 `Judge__TestcaseRoot=/data/testcases`（修复 M2 遗留：容器内 TestcaseRoot 才有效）；改代码需 `docker compose up -d --build backend judge-worker`。

## 设计开发工作流（本会话已验证的可复用模式）

每个里程碑（M1/M2/...）按此流程推进，确认节点如下：

1. **拆阶段**：大目标拆成子任务（M1.2/M1.3...），每个子任务独立走完"设计→执行→验证"闭环
2. **设计先行**：动手写代码前，先写设计文档（根目录 `DESIGN_Mx.md` 定总体；子任务用 `frontend/PLAN_MxY.md` 或对应目录）——包含：目标、范围、设计决策及理由、逐文件内容或伪代码、验证方式、风险对策。**用户审阅通过后才执行**
3. **执行**：严格按设计文档落地；文档中未覆盖的坑实时补充进文档/AGENTS.md
4. **验证（三层，逐层递进）**：
   - 编译层：`dotnet build` / `npm run build`
   - API 黑盒层：PowerShell 发真实 HTTP 请求，按验收清单逐项断言（注意下方踩坑）
   - UI 层：Playwright MCP 驱动真实浏览器（alert/confirm/守卫跳转/模态框交互），前后端需在跑（5173 + 8000 + db 5433）
5. **测试数据**：造数据覆盖边界（空库/多样例/多难度/中文），测完清理（TRUNCATE 或 API 删除），避免污染演示数据
6. **收尾**：跑 `dotnet test` 回归 → 更新 AGENTS.md 状态 → git 提交（仅用户明确要求时）

### PowerShell 验收脚本踩坑（写验收脚本前必读）

- `$pid` 是 PS 保留变量（当前进程 PID），**不可用**作变量名，用 `$problemId`
- 中文请求体必须 UTF-8：`[System.Text.Encoding]::UTF8.GetBytes($json)` 作为 -Body；否则中文变 `?`
- 响应中文会被 PS 5.1 按 Latin-1 解码成乱码——**断言用 ASCII 唯一串**（如 `UniqueXYZ`），不直接断言中文
- URL 中文参数用 `[System.Uri]::EscapeDataString('中文')`
- 断言顺序跟随数据生命周期（先查询后删除；删后再查应为空）
- `Invoke-WebRequest` 对 4xx/5xx 抛异常，取状态码需 try/catch 读 `.Exception.Response.StatusCode`

### Playwright UI 验收要点

- MCP 已全局配置（`~/.config/opencode/opencode.json`），浏览器复用 `%USERPROFILE%\AppData\Local\ms-playwright`
- 工作流：`browser_navigate` → `browser_snapshot`（拿 ref）→ `fill/click` → `handle_dialog`（alert/confirm）→ `find` 断言文本
- 常见坑：操作前先 snapshot 拿最新 ref（Vue 重渲染后 ref 会失效）；alert/confirm 必须 handle 否则后续操作卡住
