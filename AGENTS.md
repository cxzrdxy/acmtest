# AGENTS.md

个人自用 ACM 在线评测系统（M1 已收官：用户鉴权 + 题目 CRUD 全栈；M2 已收官：评测核心——提交 C++/Python 代码同步评测，AC/WA/TLE/MLE/RE/CE 判定 + 前端提交区）。ASP.NET Core 8 Web API + EF Core 8 (Npgsql) + PostgreSQL 16 + Vue 3。

## 常用命令

```bash
# 数据库（必需先启动，测试也依赖真实 Postgres）
docker compose up -d db

# 后端（workdir: backend/Acm.Api）
dotnet run

# 集成测试（依赖 ①Postgres 运行中 ②连接串指向 localhost，WebApplicationFactory 会读 appsettings 的 Host=db，本地必须先设置下面的环境变量再跑；注意本机 5432 被其他项目占用，acmtest db 映射在 5433）
$env:ConnectionStrings__Default="Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"
dotnet test

# EF 迁移（已存在 InitUsersAndProblems + AddSubmissionsAndCounters，改实体后必须新建）
dotnet ef migrations add Xxx -o Data/Migrations
dotnet ef database update --connection "Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"

# 沙箱镜像（改 backend/Judge/Dockerfile.runtimes 后重建）
docker build -f backend/Judge/Dockerfile.runtimes --target cpp -t judge-cpp:latest backend/Judge
docker build -f backend/Judge/Dockerfile.runtimes --target python-runtime -t judge-python:latest backend/Judge
```

## 关键注意事项

- **代码修改须先确认**：任何对代码/文件的修改，执行前必须先向用户确认。
- **前端已完整实现**（M1.3-M1.5：登录/注册/列表/详情 + ProblemForm 模态框 + 守卫 + 拦截器），依赖已装（Vue3+Vite+Pinia+Router+axios）。
- **连接串陷阱**：`appsettings.json` 的 `Host=db` 只在 docker 网络内可用；本地跑必须用环境变量覆盖为 `Host=localhost`。**端口**：本机 5432 被 shellquest-pg 占用，acmtest db 在 compose 里映射为宿主 5433（容器内仍 5432，docker 网络内 Host=db;Port=5432 不变）。
- **测试连真实数据库**：`TestAppFactory.cs` 用 `WebApplicationFactory<Program>` 起内存站点但连同一个 Postgres，每个测试前 TRUNCATE users/problems/submissions。改表结构可能影响测试。**M2 起测试还依赖 Docker 沙箱**（评测用例真实起容器），跑测试前需 Docker Desktop 运行 + judge 镜像存在。
- **测试并行化已禁用**：`AssemblyInfo.cs` 禁用 xunit 并行（多个测试类共用一个真实 Postgres，并行会互相污染，如 M1 的 keyword 搜索被 M2 的题目命中）。
- **个人自用简化设计**（勿"修正"）：无 admin/角色区分，所有登录用户可 CRUD 题目；无邮箱强校验；无密码复杂度校验。见 DESIGN_M1.md 1.3。
- **错误契约**：Service 抛 `ApiException(status, detail)`（定义于 AuthService.cs），Program.cs 全局处理器转 `{detail: msg}`，非 ApiException 一律 500。
- **DTO 约定**：请求/响应一律 record + DataAnnotations 校验；`ProblemUpdate` 全部 nullable，null=不修改（部分更新）。
- **代码风格**：注释用中文（inline 行尾注释 + 简短中文 XML doc）；主构造函数注入（`class Foo(AppDbContext db)`）；实体用 class（可变），DTO 用 record（不可变）。
- **M2 评测核心已实现**：`DESIGN_M2.md` 为设计文档（同步评测 + Docker 沙箱 + C++/Python）。要点：JudgeService 编排（编译容器复用 judge-cpp 镜像 rw 挂载 → 逐测试点运行容器 ro 挂载）；测试点存 `data/testcases/{pid}/{n}.in/.out`（文件系统，接口辅助）；评测工作目录 `data/tmp/{submissionId}`（gitignore 排除，评测完清理）；状态优先级 TLE>MLE>RE>WA>AC；CE 时 Detail 存 `{"compileError":...}`。
- **Docker 沙箱**：judge-cpp/judge-python 镜像构建自 `backend/Judge/Dockerfile.runtimes`（多阶段 --target cpp / python-runtime）；SandboxRunner 单例持 DockerClient，npipe 连 Docker Desktop；`Judge` 配置节在 appsettings.json（TestcaseRoot/UseDockerSandbox/镜像名）。

## 架构速览

```
Controller (路由/收请求) → Service (业务+抛 ApiException) → AppDbContext (EF) → PostgreSQL
    ↕ JWT 鉴权：TokenService 签发，UseAuthentication 验证，CurrentUserId() 取 userId
```

- DI 注册全部在 `Program.cs`：AuthService/ProblemService/JudgeService/TestcaseService 是 Scoped，TokenService/SandboxRunner 是 Singleton。
- 表结构与约束在 `Data/AppDbContext.cs` 用 Fluent API 配置（唯一索引、默认 now()）。
- 项目级文档：DESIGN_M1.md（详细设计+示例代码）、PROJECT_OVERVIEW.md（目录总览）。代码与文档不一致时以代码为准。
- docker-compose.yml 含 `db` 和 `backend`（端口 8000）两个服务；`backend` 构建自 `backend/Dockerfile`，未绑定卷，改代码需 `docker compose up -d --build backend`。

## 设计开发工作流（本会话已验证的可复用模式）

每个里程碑（M1/M2/...）按此流程推进，每步之间等待用户确认：

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
