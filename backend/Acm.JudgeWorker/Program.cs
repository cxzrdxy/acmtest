using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Acm.Judge.Core.Data;
using Acm.Judge.Core.Judge;
using Acm.JudgeWorker;

// 独立评测进程：只连 Redis 取任务 + Postgres 评测写回，无 Web 依赖
var builder = Host.CreateApplicationBuilder(args);

// console 项目 dotnet run 不改工作目录，默认 appsettings.json 源在构造时已按 cwd 解析（此时 cwd 可能是仓库根而非输出目录）：
// 追加一个绝对路径的 appsettings.json（输出目录），确保 Judge 配置节可被读到；再追加 env 提供器，保证 ConnectionStrings__X 覆盖 json。
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddDbContextPool<AppDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.Configure<JudgeOptions>(builder.Configuration.GetSection("Judge"));
builder.Services.AddSingleton<SandboxRunner>();
builder.Services.AddScoped<JudgeEngine>();

var redisOpts = ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("Redis")!);
redisOpts.AbortOnConnectFail = false;
redisOpts.AsyncTimeout = 15_000;   // 须大于 BRPOP 阻塞时长（5s），否则客户端超时先触发
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOpts));

builder.Services.AddHostedService<QueueConsumer>();   // 主循环

var host = builder.Build();
await host.RunAsync();
