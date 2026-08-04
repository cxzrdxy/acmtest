# AGENTS.md

个人自用 ACM 在线评测系统（M1 阶段：用户鉴权 + 题目 CRUD）。ASP.NET Core 8 Web API + EF Core 8 (Npgsql) + PostgreSQL 16 + Vue 3。

## 常用命令

```bash
# 数据库（必需先启动，测试也依赖真实 Postgres）
docker compose up -d db

# 后端（workdir: backend/Acm.Api）
dotnet run

# 集成测试（依赖 ①Postgres 运行中 ②连接串指向 localhost，WebApplicationFactory 会读 appsettings 的 Host=db，本地必须先设置下面的环境变量再跑；注意本机 5432 被其他项目占用，acmtest db 映射在 5433）
$env:ConnectionStrings__Default="Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"
dotnet test

# EF 迁移（需先安装 dotnet-ef：dotnet tool install -g dotnet-ef；当前还没有 Data/Migrations，改实体后必须新建）
dotnet ef migrations add Xxx -o Data/Migrations
dotnet ef database update --connection "Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"
```

## 关键注意事项

- **代码修改须先确认**：任何对代码/文件的修改，执行前必须先向用户确认。
- **前端全部为空占位**：`frontend/` 下所有文件（含 package.json）是 0 字节空文件，无依赖可装。写前端代码前需先脚手架（npm init / 建 Vite 项目）。PROJECT_OVERVIEW.md 三章描述了规划职责。
- **连接串陷阱**：`appsettings.json` 的 `Host=db` 只在 docker 网络内可用；本地跑必须用环境变量覆盖为 `Host=localhost`。**端口**：本机 5432 被 shellquest-pg 占用，acmtest db 在 compose 里映射为宿主 5433（容器内仍 5432，docker 网络内 Host=db;Port=5432 不变）。
- **测试连真实数据库**：`TestAppFactory.cs` 用 `WebApplicationFactory<Program>` 起内存站点但连同一个 Postgres，每个测试前 TRUNCATE users/problems。改表结构可能影响测试。
- **个人自用简化设计**（勿"修正"）：无 admin/角色区分，所有登录用户可 CRUD 题目；无邮箱强校验；无密码复杂度校验。见 DESIGN_M1.md 1.3。
- **错误契约**：Service 抛 `ApiException(status, detail)`（定义于 AuthService.cs），Program.cs 全局处理器转 `{detail: msg}`，非 ApiException 一律 500。
- **DTO 约定**：请求/响应一律 record + DataAnnotations 校验；`ProblemUpdate` 全部 nullable，null=不修改（部分更新）。
- **代码风格**：注释用中文（inline 行尾注释 + 简短中文 XML doc）；主构造函数注入（`class Foo(AppDbContext db)`）；实体用 class（可变），DTO 用 record（不可变）。
- **M2+ 未实现**：data/testcases、data/spj、nginx/、评测机均为占位。

## 架构速览

```
Controller (路由/收请求) → Service (业务+抛 ApiException) → AppDbContext (EF) → PostgreSQL
    ↕ JWT 鉴权：TokenService 签发，UseAuthentication 验证，CurrentUserId() 取 userId
```

- DI 注册全部在 `Program.cs`：AuthService/ProblemService 是 Scoped，TokenService 是 Singleton。
- 表结构与约束在 `Data/AppDbContext.cs` 用 Fluent API 配置（唯一索引、默认 now()）。
- 项目级文档：DESIGN_M1.md（详细设计+示例代码）、PROJECT_OVERVIEW.md（目录总览）。代码与文档不一致时以代码为准。
- docker-compose.yml 含 `db` 和 `backend`（端口 8000）两个服务；`backend` 构建自 `backend/Dockerfile`，未绑定卷，改代码需 `docker compose up -d --build backend`。
