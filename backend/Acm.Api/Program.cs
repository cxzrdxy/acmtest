using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Acm.Api;
using Acm.Api.Data;
using Acm.Api.Services;

var builder = WebApplication.CreateBuilder(args); //创建应用构建器

// 配置绑定
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt")); // 注册JWT Configure - 绑定配置到强类型类
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));

// DB / 业务服务 DI
builder.Services.AddDbContextPool<AppDbContext>(opt =>// 注册 DbContext + 连接池 + 配置到 DI 容器
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Default")));//把 UseNpgsql(连接串) 的配置打包成 DbContextOptions<AppDbContext>，注册进 DI
builder.Services.AddScoped<AuthService>(); //直接注册 AuthService到 DI容器；注册普通服务（每请求一个）
builder.Services.AddScoped<ProblemService>();//直接注册 ProblemService到 DI容器；注册普通服务（每请求一个）
builder.Services.AddSingleton<TokenService>();//注册单例服务（全局一个）

// JWT 鉴权
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()!;// 读取注册的 JWT 配置
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)//注册认证服务到 DI 容器，声明"本应用使用 JWT Bearer 方案"
    .AddJwtBearer(o =>//扩展方法，添加 Bearer 方案的具体实现
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,// 验证 token 的签发者
            ValidIssuer = jwt.Issuer,// 期望的签发者是谁
            ValidateAudience = false,// 不验证受众
            ValidateLifetime = true,// 验证过期时间
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SecretKey)),// 用密钥验签名
        };
    });
builder.Services.AddAuthorization();//注册授权服务到 DI 容器

// CORS + 控制器 + Swagger
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("App:CorsOrigins").Get<string[]>()
                 ?? ["http://localhost:5173"]) // 允许哪些前端来源
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));// 允许任意请求头，允许任意方法（GET/POST/PUT/DELETE...），允许凭证（Cookie/token 头）
builder.Services.AddControllers();// 注册控制器（接口的载体）到 DI 容器
builder.Services.AddEndpointsApiExplorer();// 添加端点 API 探索器，扫描这些接口
builder.Services.AddSwaggerGen();// 添加 Swagger 生成器，基于扫描结果生成文档

var app = builder.Build();// 创建应用

// 业务异常 -> {detail: msg}，对齐前端错误契约
app.UseExceptionHandler(e => e.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    ctx.Response.StatusCode = ex is ApiException api ? api.Status : 500;
    await ctx.Response.WriteAsJsonAsync(new { detail = ex?.Message ?? "服务器内部错误" });
}));

app.UseSwagger(); // 中间件：拦截请求，若路径匹配 /swagger 相关端点则生成 OpenAPI JSON 文档
app.UseSwaggerUI(); // 中间件：提供 /swagger 可视化页面，在浏览器中调试接口
app.UseCors(); // 中间件：应用 AddCors 定义的策略，处理跨域请求（含 OPTIONS 预检）
app.UseAuthentication(); // 中间件：解析请求中的 Bearer token，验证签名/过期时间，身份存入 ctx.User
app.UseAuthorization(); // 中间件：检查 [Authorize] 特性，无权限返回 401/403
app.MapControllers(); // 路由映射：把控制器中 [Route] 定义的所有 API 端点挂到请求管道
app.MapGet("/health", () => new { status = "ok" }); // 最小 API：直接内联一个 GET /health 健康检查端点

app.Run();

// 暴露给 WebApplicationFactory<Program> 集成测试
public partial class Program { }
