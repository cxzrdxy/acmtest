# M1 基础阶段 详细设计方案

> **目标产出**：可登录、可看题、可建改删题 —— 完成数据库模型、用户鉴权、题目 CRUD 三大基石
> **技术栈**：ASP.NET Core 8 Web API + EF Core 8 (Npgsql) + PostgreSQL 16 + JWT Bearer + BCrypt.Net + xUnit
> **工期估算**：3–4 个工作日（个人自用，权限模型简化）

---

## 一、M1 范围与验收标准

### 1.1 范围

| 类别 | 包含 | 不包含（留至 M2+） |
|------|------|------|
| 用户 | 注册 / 登录 / 获取当前用户 / 改密 / 注销 | 邮箱激活、第三方登录、找回密码 |
| 权限 | **单角色模型**：登录即可读写题目，无 admin/user 区分 | 多用户隔离、RBAC 细粒度 |
| 题目 | CRUD + 列表分页/搜索/标签筛选 + 详情 | 测试点上传、SPJ、子任务 |
| 前端 | 登录页 / 注册页 / 题目列表 / 题目详情 / 提交占位页 | 代码编辑器、评测结果、统计 |
| 基建 | Docker Compose 起后端 + DB | Nginx、评测机、Redis |

### 1.2 验收标准

- 注册登录后拿 JWT，**所有登录用户均可创建 / 编辑 / 删除题目**。
- 题目列表支持 `keyword`（标题模糊）、`tag`、`difficulty` 过滤，分页返回。
- 全部接口有 DTO 模型校验（DataAnnotations）与统一错误响应。
- 核心流程有 xUnit 用例，`dotnet test` 可一键运行。
- `docker compose up` 一键启动 backend + db，Kestrel 在容器内监听 8000。

### 1.3 与原方案的核心差异（基于个人自用）

| 项 | 原方案 | 本方案 |
|----|--------|--------|
| 角色模型 | `user` / `admin` 双角色 + admin 授权策略 | 单角色，登录即可全部 CRUD，无 admin 判定 |
| `is_public` 过滤 | 普通用户只读公开题 | **移除 `IsPublic` 字段与过滤逻辑**，登录即可见全部 |
| 鉴权强度 | 密码强度校验、防枚举报错、邮箱必填 | 保留 bcrypt + JWT，**移除邮箱强校验、密码复杂度校验**（自用可简化） |
| 注册策略 | 任意人注册 | 可选配置 `AllowRegister=true/false`，默认 `true`，自用可关掉自助注册 |

---

## 二、数据库模型设计（M1 部分）

### 2.1 ER 关系

```
┌──────────┐1         *┌───────────┐
│  users   │───────────│ problems  │
│ id (PK)  │  author   │ id (PK)   │
│ username │           │ author_id │
│ password │           └───────────┘
└──────────┘
```

> 不再设 `role` 字段；单用户即"超级管理员"概念，所有表均可自由读写。

### 2.2 实体类

#### User 实体

```csharp
// Acm.Api/Models/User.cs
namespace Acm.Api.Models;

public class User
{
    public int Id { get; set; }                          // 用户 ID，自增主键
    public string Username { get; set; } = null!;        // 登录名，唯一索引，最长 32
    public string HashedPassword { get; set; } = null!;  // bcrypt 哈希后的密码
    public bool IsActive { get; set; } = true;           // 软停用标记（自用通常恒为 true）
    public DateTime CreatedAt { get; set; }              // 注册时间，DB 默认 now()
}
```

#### Problem 实体（M1 精简版，去掉测试点相关字段，逐字段注释）

```csharp
// Acm.Api/Models/Problem.cs
namespace Acm.Api.Models;

/// <summary>
/// 竞赛题目表。
/// 存储题面元信息与样例；正式测试点数据（input/answer）较大，
/// 留至 M2 以文件形式存储于 data/testcases/，表内只记录其元数据。
/// </summary>
public class Problem
{
    // 主键，自增
    public int Id { get; set; }

    // URL 友好标识，如 P1000；全局唯一，用于前端路由与对外展示
    public string Slug { get; set; } = null!;

    // 题目标题，最长 128 字符
    public string Title { get; set; } = null!;

    // 题面正文，Markdown 格式，长度不限
    public string? Description { get; set; }

    // 输入格式说明（Markdown）
    public string? InputDesc { get; set; }

    // 输出格式说明（Markdown）
    public string? OutputDesc { get; set; }

    // 时间限制，单位毫秒（ms），默认 1000ms
    public int TimeLimit { get; set; } = 1000;

    // 内存限制，单位兆字节（MB），默认 256MB
    public int MemoryLimit { get; set; } = 256;

    // 知识点标签数组，如 ['DP', '图论', '树状数组']；Npgsql 原生映射 text[]，用于列表筛选
    public List<string> Tags { get; set; } = new();

    // 难度等级 1~5，可空表示尚未评级
    public short? Difficulty { get; set; }

    // 样例输入，多个样例以 "---" 分隔的纯字符串（M1 简化存储）
    public string SampleInputs { get; set; } = "";

    // 样例输出，与 SampleInputs 一一对应
    public string SampleOutputs { get; set; } = "";

    // 题目创建者，外键关联 users.id；自用单用户场景下值恒为同一用户
    public int? AuthorId { get; set; }

    // 创建时间，DB 默认 now()
    public DateTime CreatedAt { get; set; }

    // 更新时间，SaveChanges 拦截刷新
    public DateTime UpdatedAt { get; set; }

    // AC / 提交计数字段留至 M2 评测阶段补充
}
```

字段约束（唯一索引 / 长度 / 默认值）统一在 `AppDbContext.OnModelCreating` 用 Fluent API 配置，见第五章。

### 2.3 迁移 (EF Core Migrations) 初始化

```bash
# 安装一次迁移 CLI 工具
dotnet tool install -g dotnet-ef

# 在 backend/Acm.Api 目录生成首版迁移（输出到 Data/Migrations/）
dotnet ef migrations add InitUsersAndProblems -o Data/Migrations

# 应用迁移到数据库
dotnet ef database update
```

> EF Core 迁移由 `AppDbContext` 的模型快照自动比对生成，无需像 Alembic 那样手工维护 `env.py`；
> 连接串从 `appsettings.json` / 环境变量 `ConnectionStrings__Default` 读取。

---

## 三、项目骨架（M1 后端）

```
backend/
├── Dockerfile
├── Acm.sln
├── Acm.Api/
│   ├── Acm.Api.csproj           # NuGet 依赖清单
│   ├── Program.cs               # 入口：DI 注册 / 中间件管道 / 路由
│   ├── appsettings.json         # 配置（连接串 / JWT / CORS / AllowRegister）
│   ├── Models/
│   │   ├── User.cs
│   │   └── Problem.cs
│   ├── Data/
│   │   ├── AppDbContext.cs      # DbContext + Fluent API 配置
│   │   └── Migrations/          # EF Core 迁移（自动生成）
│   ├── Dtos/
│   │   ├── UserDtos.cs          # RegisterRequest / LoginRequest / UserRead / TokenResponse
│   │   └── ProblemDtos.cs       # ProblemCreate / Update / Read / ListResponse
│   ├── Services/
│   │   ├── TokenService.cs      # 签发 JWT
│   │   ├── AuthService.cs       # 注册、登录、改密
│   │   └── ProblemService.cs    # CRUD + 查询
│   └── Controllers/
│       ├── AuthController.cs    # /register /login /me /change-password
│       └── ProblemsController.cs# CRUD + list
└── Acm.Api.Tests/
    ├── Acm.Api.Tests.csproj
    └── M1/
        └── AuthProblemTests.cs  # 注册→登录→CRUD 全流程集成测试
```

> 不再有 `RequireAdmin` 授权策略与自定义异常体系；自用场景业务错误直接返回
> `Results.Problem` / 自定义 `ApiException` 即可。密码哈希与 JWT 逻辑简单，
> 直接放 `Services/`，不单独分层。

---

## 四、配置层

```json
// Acm.Api/appsettings.json
{
  "ConnectionStrings": {
    "Default": "Host=db;Port=5432;Database=acm;Username=acm;Password=acm"
  },
  "Jwt": {
    "SecretKey": "change-me-please-at-least-32-chars!!",
    "Issuer": "acm-oj",
    "AccessTtlMinutes": 10080
  },
  "App": {
    "AllowRegister": true,
    "CorsOrigins": [ "http://localhost:5173" ]
  }
}
```

```csharp
// Acm.Api/Config.cs — 强类型 Options
public class JwtOptions
{
    public string SecretKey { get; set; } = null!;
    public string Issuer { get; set; } = "acm-oj";
    public int AccessTtlMinutes { get; set; } = 60 * 24 * 7;   // 7 天，自用可设长
}

public class AppOptions
{
    // 是否开放自助注册；个人自用可在创建账号后关闭防止他人注册
    public bool AllowRegister { get; set; } = true;
    public string[] CorsOrigins { get; set; } = ["http://localhost:5173"];
}
```

环境变量覆盖（不入库，容器里注入）：

```
ConnectionStrings__Default=Host=db;Port=5432;Database=acm;Username=acm;Password=acm
Jwt__SecretKey=<generate-with: openssl rand -hex 32>
App__AllowRegister=false       # 创建好自己的账号后改为 false
```

> ASP.NET Core 配置系统原生支持 `appsettings.json` + 环境变量（`__` 分层）叠加，无需额外库。

---

## 五、数据库上下文层

```csharp
// Acm.Api/Data/AppDbContext.cs
using Microsoft.EntityFrameworkCore;
using Acm.Api.Models;

namespace Acm.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Problem> Problems => Set<Problem>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasIndex(u => u.Username).IsUnique();
            e.Property(u => u.Username).HasMaxLength(32);
            e.Property(u => u.HashedPassword).HasMaxLength(256);
            e.Property(u => u.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<Problem>(e =>
        {
            e.ToTable("problems");
            e.HasIndex(p => p.Slug).IsUnique();
            e.Property(p => p.Slug).HasMaxLength(64);
            e.Property(p => p.Title).HasMaxLength(128);
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
            e.Property(p => p.UpdatedAt).HasDefaultValueSql("now()");
            e.HasOne<User>().WithMany().HasForeignKey(p => p.AuthorId);
        });
    }
}
```

```csharp
// Program.cs 中注册（连接池版 DbContext，等价于 async engine + session 池）
builder.Services.AddDbContextPool<AppDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
```

> EF Core 的 `DbContext` 按请求作用域注入，用完自动释放；异常时事务自动回滚，
> 无需手写 `get_db` 式的 yield/rollback 样板。

---

## 六、安全层（精简版）

> 个人自用：保留 bcrypt 哈希 + JWT 签发，**移除密码复杂度校验、防枚举差异化报错、邮箱强校验**。强度足够防普通误用即可。

```csharp
// Acm.Api/Services/TokenService.cs
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Acm.Api.Services;

public class TokenService(IOptions<JwtOptions> opt)
{
    private readonly JwtOptions _jwt = opt.Value;

    public string CreateAccessToken(int userId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SecretKey));
        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            claims: [new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())],
            expires: DateTime.UtcNow.AddMinutes(_jwt.AccessTtlMinutes),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
```

密码哈希直接用 `BCrypt.Net-Next`，两行搞定，不再单独封装类：

```csharp
string hash = BCrypt.Net.BCrypt.HashPassword(password);
bool ok    = BCrypt.Net.BCrypt.Verify(password, hash);
```

### 6.1 鉴权接入（框架中间件，无手写依赖注入）

token 校验交给框架的 JWT Bearer 中间件，控制器打 `[Authorize]` 即可，等价于 FastAPI 的 `get_current_user` 依赖：

```csharp
// Program.cs
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()!;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SecretKey)),
        };
    });
builder.Services.AddAuthorization();
```

控制器内取当前用户 ID（来自 token 的 `sub` claim）：

```csharp
// Acm.Api/Controllers/ControllerExtensions.cs
public static class ControllerExtensions
{
    public static int CurrentUserId(this ControllerBase c) =>
        int.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
```

> 全系统不再有 admin 授权策略；凡是携带有效 JWT（`[Authorize]` 通过）的用户即拥有全部 CRUD 权限。
> token 无效/过期由中间件统一返回 401。`IsActive=false` 的停用校验在登录时拦截。

---

## 七、DTO（请求/响应模型）

```csharp
// Acm.Api/Dtos/UserDtos.cs
using System.ComponentModel.DataAnnotations;

namespace Acm.Api.Dtos;

public record RegisterRequest(
    [Required, MaxLength(32)] string Username,
    [Required] string Password,
    // 个人自用：邮箱不做强校验，可空
    string? Email = null);

public record LoginRequest(
    [Required] string Username,
    [Required] string Password);

public record UserRead(int Id, string Username, string? Email = null);

public record TokenResponse(string AccessToken, UserRead User, string TokenType = "bearer");

public record ChangePasswordRequest(
    [Required] string OldPassword,
    [Required] string NewPassword);
```

```csharp
// Acm.Api/Dtos/ProblemDtos.cs
using System.ComponentModel.DataAnnotations;

namespace Acm.Api.Dtos;

public record ProblemCreate(
    [Required, RegularExpression(@"^P\d{3,5}$")] string Slug,   // P1000-P99999
    [Required, MaxLength(128)] string Title,
    [Required] string Description,
    string InputDesc = "",
    string OutputDesc = "",
    [Range(100, 30000)] int TimeLimit = 1000,
    [Range(16, 1024)] int MemoryLimit = 256,
    List<string>? Tags = null,
    [Range(1, 5)] short? Difficulty = null,
    string SampleInputs = "",
    string SampleOutputs = "");

// 更新为部分更新：null 表示不修改该字段
public record ProblemUpdate(
    [MaxLength(128)] string? Title = null,
    string? Description = null,
    string? InputDesc = null,
    string? OutputDesc = null,
    [Range(100, 30000)] int? TimeLimit = null,
    [Range(16, 1024)] int? MemoryLimit = null,
    List<string>? Tags = null,
    [Range(1, 5)] short? Difficulty = null,
    string? SampleInputs = null,
    string? SampleOutputs = null);

public record ProblemRead(
    int Id, string Slug, string Title, string? Description,
    string? InputDesc, string? OutputDesc,
    int TimeLimit, int MemoryLimit,
    List<string> Tags, short? Difficulty,
    string SampleInputs, string SampleOutputs, int? AuthorId);

public record ProblemListResponse(List<ProblemRead> Items, int Total, int Page, int Size);
```

> DTO 中已移除 `IsPublic` 字段；题目默认全可见。DataAnnotations 校验失败由框架
> 自动返回 400 `ValidationProblemDetails`，等价于 FastAPI 的 422。

---

## 八、Service 层

```csharp
// Acm.Api/Services/AuthService.cs
using Microsoft.EntityFrameworkCore;
using Acm.Api.Data;
using Acm.Api.Dtos;
using Acm.Api.Models;

namespace Acm.Api.Services;

// 业务错误统一抛 ApiException，由全局异常处理器转 {detail: msg}
public class ApiException(int status, string detail) : Exception(detail)
{
    public int Status { get; } = status;
}

public class AuthService(AppDbContext db)
{
    public async Task<User> RegisterAsync(RegisterRequest req)
    {
        if (await db.Users.AnyAsync(u => u.Username == req.Username))
            throw new ApiException(409, "用户名已存在");

        var user = new User
        {
            Username = req.Username,
            HashedPassword = BCrypt.Net.BCrypt.HashPassword(req.Password),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task<User> LoginAsync(LoginRequest req)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Username == req.Username);
        // 个人自用：使用统一报错信息（不再做防枚举差异化处理）
        if (user is null || !BCrypt.Net.BCrypt.Verify(req.Password, user.HashedPassword))
            throw new ApiException(401, "用户名或密码错误");
        if (!user.IsActive)
            throw new ApiException(403, "账号已停用");
        return user;
    }

    public async Task ChangePasswordAsync(int userId, ChangePasswordRequest req)
    {
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        if (!BCrypt.Net.BCrypt.Verify(req.OldPassword, user.HashedPassword))
            throw new ApiException(400, "旧密码错误");
        user.HashedPassword = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
        await db.SaveChangesAsync();
    }
}
```

```csharp
// Acm.Api/Services/ProblemService.cs
using Microsoft.EntityFrameworkCore;
using Acm.Api.Data;
using Acm.Api.Dtos;
using Acm.Api.Models;

namespace Acm.Api.Services;

public class ProblemService(AppDbContext db)
{
    public async Task<Problem> CreateAsync(ProblemCreate req, int authorId)
    {
        if (await db.Problems.AnyAsync(p => p.Slug == req.Slug))
            throw new ApiException(409, $"slug {req.Slug} 已存在");

        var problem = new Problem
        {
            Slug = req.Slug, Title = req.Title, Description = req.Description,
            InputDesc = req.InputDesc, OutputDesc = req.OutputDesc,
            TimeLimit = req.TimeLimit, MemoryLimit = req.MemoryLimit,
            Tags = req.Tags ?? [], Difficulty = req.Difficulty,
            SampleInputs = req.SampleInputs, SampleOutputs = req.SampleOutputs,
            AuthorId = authorId,
        };
        db.Problems.Add(problem);
        await db.SaveChangesAsync();
        return problem;
    }

    // 个人自用：无 includePrivate 参数，所有登录用户可见全部题目
    public async Task<Problem> GetByIdAsync(int pid) =>
        await db.Problems.SingleOrDefaultAsync(p => p.Id == pid)
            ?? throw new ApiException(404, "题目不存在");

    public async Task<Problem> GetBySlugAsync(string slug) =>
        await db.Problems.SingleOrDefaultAsync(p => p.Slug == slug)
            ?? throw new ApiException(404, "题目不存在");

    public async Task<(List<Problem> Items, int Total)> ListAsync(
        string? keyword, string? tag, short? difficulty, int page, int size)
    {
        var q = db.Problems.AsNoTracking();
        if (!string.IsNullOrEmpty(keyword))
            q = q.Where(p => EF.Functions.ILike(p.Title, $"%{keyword}%"));
        if (!string.IsNullOrEmpty(tag))
            q = q.Where(p => p.Tags.Contains(tag));   // Npgsql 翻译为 text[] @> 包含查询
        if (difficulty.HasValue)
            q = q.Where(p => p.Difficulty == difficulty);

        var total = await q.CountAsync();
        var items = await q.OrderBy(p => p.Id)
                           .Skip((page - 1) * size).Take(size)
                           .ToListAsync();
        return (items, total);
    }

    public async Task<Problem> UpdateAsync(int pid, ProblemUpdate req)
    {
        var p = await GetByIdAsync(pid);
        // 部分更新：仅覆盖非 null 字段
        if (req.Title is not null)         p.Title = req.Title;
        if (req.Description is not null)   p.Description = req.Description;
        if (req.InputDesc is not null)     p.InputDesc = req.InputDesc;
        if (req.OutputDesc is not null)    p.OutputDesc = req.OutputDesc;
        if (req.TimeLimit.HasValue)        p.TimeLimit = req.TimeLimit.Value;
        if (req.MemoryLimit.HasValue)      p.MemoryLimit = req.MemoryLimit.Value;
        if (req.Tags is not null)          p.Tags = req.Tags;
        if (req.Difficulty.HasValue)       p.Difficulty = req.Difficulty;
        if (req.SampleInputs is not null)  p.SampleInputs = req.SampleInputs;
        if (req.SampleOutputs is not null) p.SampleOutputs = req.SampleOutputs;
        p.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return p;
    }

    public async Task DeleteAsync(int pid)
    {
        var p = await GetByIdAsync(pid);
        db.Problems.Remove(p);
        await db.SaveChangesAsync();
    }
}
```

---

## 九、API 控制器

### 9.1 AuthController

```csharp
// Acm.Api/Controllers/AuthController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Acm.Api.Dtos;
using Acm.Api.Services;

namespace Acm.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController(AuthService auth, TokenService tokens, IOptions<AppOptions> app)
    : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<TokenResponse>> Register(RegisterRequest req)
    {
        // 个人自用：可通过 App__AllowRegister=false 关闭自助注册
        if (!app.Value.AllowRegister)
            throw new ApiException(403, "管理员已关闭注册");
        var user = await auth.RegisterAsync(req);
        var resp = new TokenResponse(tokens.CreateAccessToken(user.Id),
                                     new UserRead(user.Id, user.Username));
        return StatusCode(201, resp);
    }

    [HttpPost("login")]
    public async Task<ActionResult<TokenResponse>> Login(LoginRequest req)
    {
        var user = await auth.LoginAsync(req);
        return new TokenResponse(tokens.CreateAccessToken(user.Id),
                                 new UserRead(user.Id, user.Username));
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<UserRead>> Me([FromServices] AppDbContext db)
    {
        var user = await db.Users.FindAsync(this.CurrentUserId());
        return new UserRead(user!.Id, user.Username);
    }

    [Authorize]
    [HttpPut("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest req)
    {
        await auth.ChangePasswordAsync(this.CurrentUserId(), req);
        return Ok(new { msg = "ok" });
    }
}
```

> 登录用 JSON body 而非 form；Swagger 可点右上角 Authorize 填 `Bearer <token>` 测试。

### 9.2 ProblemsController（登录即可全部 CRUD，无角色区分）

```csharp
// Acm.Api/Controllers/ProblemsController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acm.Api.Dtos;
using Acm.Api.Services;

namespace Acm.Api.Controllers;

[ApiController]
[Authorize]                       // 控制器级：所有接口需登录
[Route("api/v1/problems")]
public class ProblemsController(ProblemService svc) : ControllerBase
{
    private static ProblemRead ToRead(Models.Problem p) => new(
        p.Id, p.Slug, p.Title, p.Description, p.InputDesc, p.OutputDesc,
        p.TimeLimit, p.MemoryLimit, p.Tags, p.Difficulty,
        p.SampleInputs, p.SampleOutputs, p.AuthorId);

    [HttpGet]
    public async Task<ActionResult<ProblemListResponse>> List(
        [FromQuery] string? keyword,
        [FromQuery] string? tag,
        [FromQuery, Range(1, 5)] short? difficulty,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, 100)] int size = 20)
    {
        var (items, total) = await svc.ListAsync(keyword, tag, difficulty, page, size);
        return new ProblemListResponse(items.Select(ToRead).ToList(), total, page, size);
    }

    [HttpGet("{pid:int}")]
    public async Task<ActionResult<ProblemRead>> Get(int pid) =>
        ToRead(await svc.GetByIdAsync(pid));

    [HttpGet("by-slug/{slug}")]
    public async Task<ActionResult<ProblemRead>> GetBySlug(string slug) =>
        ToRead(await svc.GetBySlugAsync(slug));

    // 个人自用：所有登录用户均可 CRUD，无需 admin 策略
    [HttpPost]
    public async Task<ActionResult<ProblemRead>> Create(ProblemCreate req)
    {
        var p = await svc.CreateAsync(req, this.CurrentUserId());
        return StatusCode(201, ToRead(p));
    }

    [HttpPut("{pid:int}")]
    public async Task<ActionResult<ProblemRead>> Update(int pid, ProblemUpdate req) =>
        ToRead(await svc.UpdateAsync(pid, req));

    [HttpDelete("{pid:int}")]
    public async Task<IActionResult> Delete(int pid)
    {
        await svc.DeleteAsync(pid);
        return NoContent();
    }
}
```

> 路由前缀直接写在 `[Route]` 上，无需 FastAPI 式的 Router 聚合层。

---

## 十、应用入口

```csharp
// Acm.Api/Program.cs
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Acm.Api.Data;
using Acm.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// 配置绑定
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));

// DB / 业务服务 DI
builder.Services.AddDbContextPool<AppDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ProblemService>();
builder.Services.AddSingleton<TokenService>();

// JWT 鉴权（配置见第六章）+ CORS + 控制器 + Swagger
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(/* 第六章 TokenValidationParameters */);
builder.Services.AddAuthorization();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("App:CorsOrigins").Get<string[]>()!)
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// 业务异常 -> {detail: msg}，对齐前端错误契约
app.UseExceptionHandler(e => e.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    ctx.Response.StatusCode = ex is ApiException api ? api.Status : 500;
    await ctx.Response.WriteAsJsonAsync(new { detail = ex?.Message ?? "服务器内部错误" });
}));

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => new { status = "ok" });

app.Run();
```

---

## 十一、Docker 化（M1）

`backend/Dockerfile`（多阶段构建）：

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Acm.Api/Acm.Api.csproj Acm.Api/
RUN dotnet restore Acm.Api/Acm.Api.csproj
COPY . .
RUN dotnet publish Acm.Api/Acm.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8000
EXPOSE 8000
ENTRYPOINT ["dotnet", "Acm.Api.dll"]
```

`Acm.Api/Acm.Api.csproj`（M1 精简依赖）：

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="8.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="8.*" PrivateAssets="all" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="8.*" />
    <PackageReference Include="BCrypt.Net-Next" Version="4.*" />
    <PackageReference Include="Swashbuckle.AspNetCore" Version="6.*" />
  </ItemGroup>
</Project>
```

> 不再需要 email 校验库（自用不强校验邮箱）；测试依赖（xUnit 等）在 `Acm.Api.Tests.csproj` 中。

`docker-compose.yml`（M1）：

```yaml
services:
  db:
    image: postgres:16
    environment:
      POSTGRES_USER: acm
      POSTGRES_PASSWORD: acm
      POSTGRES_DB: acm
    ports: ["5432:5432"]
    volumes: ["pgdata:/var/lib/postgresql/data"]
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U acm"]
      interval: 5s

  backend:
    build: ./backend
    depends_on:
      db: { condition: service_healthy }
    environment:
      ConnectionStrings__Default: Host=db;Port=5432;Database=acm;Username=acm;Password=acm
      Jwt__SecretKey: dev-secret-change-me-at-least-32-chars
      App__AllowRegister: "true"
    ports: ["8000:8000"]

volumes: { pgdata: {} }
```

---

## 十二、初始化与账号创建

### 12.1 首次部署流程

```bash
docker compose up -d db
docker compose up -d --build backend
# 应用 EF Core 迁移（本机装 .NET SDK 时）
cd backend/Acm.Api
dotnet ef database update --connection "Host=localhost;Port=5432;Database=acm;Username=acm;Password=acm"
```

> 也可在 `Program.cs` 启动时 `db.Database.Migrate()` 自动迁移，个人自用更省事；
> 二选一即可，M1 先用 CLI 手动迁移。

### 12.2 创建账号（自用即"超级管理员"概念）

注册接口即可创建账号；创建后建议把环境变量 `App__AllowRegister` 改为 `false` 后重启，防止他人注册：

```bash
# 通过 API 注册（首次注册即系统唯一用户）
curl -X POST http://localhost:8000/api/v1/auth/register \
  -H "Content-Type: application/json" \
  -d '{"username":"me","password":"yourpassword"}'

# 关闭自助注册：docker-compose.yml 中 App__AllowRegister: "false"
docker compose up -d backend
```

> 不再提供 create_admin 脚本；通过注册接口创建即可。

---

## 十三、单元测试

### 13.1 核心

- 使用 xUnit + `Microsoft.AspNetCore.Mvc.Testing`（`WebApplicationFactory` 内存起站点，等价 httpx ASGITransport）
- 测试隔离：连同一个 Postgres，每个测试前 `TRUNCATE` 表

### 13.2 测试基座

```csharp
// Acm.Api.Tests/TestAppFactory.cs
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Acm.Api.Data;

public class TestAppFactory : WebApplicationFactory<Program>
{
    public async Task CleanDbAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var tbl in new[] { "users", "problems" })
            await db.Database.ExecuteSqlRawAsync(
                $"TRUNCATE TABLE \"{tbl}\" RESTART IDENTITY CASCADE");
    }
}
```

### 13.3 测试样例（注册 → 登录 → CRUD 单用户全权限流程）

```csharp
// Acm.Api.Tests/M1/AuthProblemTests.cs
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Xunit;

public class AuthProblemTests(TestAppFactory factory) : IClassFixture<TestAppFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.CleanDbAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Register_Login_And_Full_Crud()
    {
        // 1. 注册
        var r = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new { username = "me", password = "pw1234" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var token = (await r.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
        _client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        // 2. 无 token 访问应 401
        using var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync("/api/v1/problems")).StatusCode);

        // 3. 登录用户可创建题目（个人自用，无 admin 分级）
        r = await _client.PostAsJsonAsync("/api/v1/problems", new
        {
            slug = "P1001", title = "A+B Problem", description = "读两数输出和",
            inputDesc = "两个整数", outputDesc = "和", tags = new[] { "入门" },
            difficulty = 1, sampleInputs = "1 2", sampleOutputs = "3",
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var pid = (await r.Content.ReadFromJsonAsync<ProblemRead>())!.Id;

        // 4. 列表关键字搜索
        var list = await _client.GetFromJsonAsync<ProblemListResponse>(
            "/api/v1/problems?keyword=A%2BB");
        Assert.Equal(1, list!.Total);

        // 5. tag 筛选
        list = await _client.GetFromJsonAsync<ProblemListResponse>("/api/v1/problems?tag=入门");
        Assert.Equal(1, list!.Total);

        // 6. update
        r = await _client.PutAsJsonAsync($"/api/v1/problems/{pid}", new { title = "A+B Test" });
        Assert.Equal("A+B Test", (await r.Content.ReadFromJsonAsync<ProblemRead>())!.Title);

        // 7. delete
        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.DeleteAsync($"/api/v1/problems/{pid}")).StatusCode);

        // 8. 删除后列表应为空
        list = await _client.GetFromJsonAsync<ProblemListResponse>("/api/v1/problems");
        Assert.Equal(0, list!.Total);
    }
}
```

运行：
```bash
dotnet test
```

---

## 十四、前端协作

### 14.1 CORS

后端 `App:CorsOrigins` 已开放给 `http://localhost:5173`（Vite dev server）。前端 M1 暂不实现，但约定下面 4 个契约点：

- 所有需身份接口加 `Authorization: Bearer <jwt>`
- token 暂存 localStorage；后续可改 httpOnly cookie
- 401 拦截 → 跳登录页
- 400 校验错误体为 `ValidationProblemDetails`（`{errors: {字段: [...]}}`），按字段解析

### 14.2 错误响应统一约定

所有业务错误（全局异常处理器）返回：
```json
{ "detail": "<人类可读中文 msg>" }
```
请求体校验错误（ASP.NET Core 自动，HTTP 400）：
```json
{ "title": "One or more validation errors occurred.",
  "errors": { "Username": ["The Username field is required."] } }
```

---

## 十五、关键库作用速查

| 库 / 组件 | 作用 |
|----|------|
| `Npgsql.EntityFrameworkCore.PostgreSQL` | PostgreSQL EF Core 驱动（原生支持 text[] 数组映射） |
| `BCrypt.Net-Next` | 密码哈希（bcrypt） |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | JWT 校验中间件 |
| `System.IdentityModel.Tokens.Jwt` | JWT 签发（框架自带） |
| `dotnet-ef` (CLI) + `Microsoft.EntityFrameworkCore.Design` | 数据库迁移版本管理 |
| `Microsoft.AspNetCore.Mvc.Testing` | 内存起站点做集成测试 |

---

## 十六、M1 排期建议

| 日 | 任务 |
|----|------|
| D1 | 解决方案骨架 + Docker Compose + 配置/DbContext + 实体 + EF 初始迁移 |
| D2 | TokenService + JWT Bearer 接入 + AuthService + AuthController |
| D3 | ProblemService + ProblemsController + DTO 全量 |
| D4 | xUnit 用例 + 手工冒烟测试 + 最小前端占位页 |

完成后即可推进 **M2 评测核心**。
