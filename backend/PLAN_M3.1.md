# M3.1 队列与 Worker 详细设计方案

> **所属里程碑**：M3 异步评测与提交历史（`DESIGN_M3.md`）
> **任务边界**：M3.1 队列与 Worker —— Redis 服务 + `SubmissionQueue` 入队 + `Acm.JudgeWorker` 独立进程骨架 + 评测逻辑抽共享类库 `Acm.Judge.Core`（总设计"四、4.1-4.4 + 4.7-4.9"）
> **前置**：M2 已收官（同步评测全链路 + 测试点管理 + 前端提交区）✅
> **本阶段不涉及**：前端轮询（M3.2）、提交历史页（M3.3）、测试自动化收尾（M3.4）

---

## 一、目标

提交接口**不再阻塞评测**：POST 后秒回 `{id, status:"PENDING"}`，评测任务入 Redis 队列；新增独立进程 `Acm.JudgeWorker` 后台消费并写终态。为达成"Web/Worker 共用一份评测代码"，把 M2 的评测逻辑（JudgeService 主体 + SandboxRunner + OutputComparer + 实体/上下文）抽成共享类库 `Acm.Judge.Core`。完成后 Web 已可"提交秒回 + Worker 后台出结果"（前端轮询 M3.2 补）。

## 二、范围

| 做 | 不做 |
|----|------|
| `Acm.Judge.Core` 共享类库（Data/Models/Judge 三块迁入） | 前端轮询/状态流转（M3.2） |
| `Acm.JudgeWorker` 独立进程（BRPOP 消费 + 评测 + 写库） | 提交历史页 API/页面（M3.3） |
| Redis 服务（compose 新增）+ `SubmissionQueue` 入队 | TestContainer / 自动起 Worker（M3.4 选型） |
| `JudgeService` 改入口：写 PENDING + 入队即回 | 多 Worker 水平扩展（Redis 天然支持，暂不需要） |
| Web/Worker 双 appsettings + compose/Dockerfile 双入口 | SignalR 推送、任务重试机制 |
| 测试最小适配（提交改轮询等待） | 历史页、排名、统计（M4） |

---

## 三、设计决策及理由

| 决策 | 理由 |
|------|------|
| 评测逻辑抽 `Acm.Judge.Core` 类库，**AppDbContext + 实体 + Migrations 一并迁入** | Web/Worker 两进程必须共用同一套 EF 模型与评测代码，避免双份实现漂移（DESIGN_M3 §6）；DbContext 与迁移同处，迁移不裂 |
| Core 命名空间 `Acm.Judge.Core.{Data,Models,Judge}` | 与"文件在哪个项目"一致，避免 `Acm.Api.Data` 命名空间存在于 Core 程序集的错乱 |
| `JudgeEngine.ExecuteAsync(long sid)` **返回 void**（DESIGN_M3 4.2 草图返回 SubmissionRead） | 异步架构下轮询结果由 Web 从 DB 读，Engine 返回值无人消费；顺带消除 Core→Api.Dtos 的依赖 |
| 状态流：Web 写 PENDING + LPUSH → Worker BRPOP 后先置 JUDGING → 评测写终态 | 保留 M2 的 PENDING→JUDGING 两段语义；JUDGING 由实际执行者（Worker）置，避免 Web 写后入队失败状态错乱 |
| 队列键 `queue:judge`：Web `ListLeftPushAsync`（LPUSH）+ Worker BRPOP | LPUSH 左进 + 右出 = 天然 FIFO；推模式 0 延迟 |
| BRPOP 用 `db.ExecuteAsync("BRPOP", key, 5)` + 每轮检查 stoppingToken | SE.Redis 的 `ListRightPopAsync` 是 **RPOP 非阻塞**，无 CT 版阻塞 API；5s 阻塞超时后重查停机信号，换来优雅停机（最多延迟 5s） |
| Web 侧 Redis：`AbortOnConnectFail=false` 后台重连 | Redis 短暂不可用时 Web 仍能启动/存活（DESIGN_M3 §6"Web 不因 Redis 挂而挂"） |
| 入队失败：**回滚刚插入的 PENDING 行** + 抛 503 `{detail:"评测队列不可用"}` | 不留"永远 PENDING"的幽灵行；前端可重试。注：LPUSH 成功但响应丢失时可能回滚了已入队的任务——由 Worker 侧"提交不存在则跳过"兜底 |
| Worker 消费前 `SingleOrDefault` 判空，缺失则跳过+日志 | 兜住上一条的竞态，也兜住测试 TRUNCATE 后残留的队列任务 |
| 评测异常兜底：仅当状态仍为 PENDING/JUDGING 才置 RE | 不覆盖已写好的终态（如置 RE 过程本身二次异常） |
| 计数更新（SubmitCount/AcCount）留在 JudgeEngine.FinishAsync，由 Worker 写 | 评测结果归属 Worker 落库；单 Worker 串行，无并发竞争 |
| `Acm.Judge.Core` 只引 EF Core(+Relational+Design) + Docker.DotNet；Npgsql 只在 Api/Worker，Redis 包只在 Api/Worker | 依赖最小化：Core 无 Web/Redis 依赖；迁移工具走 `--project Core --startup-project Api`（Api 有 Design 包 + Npgsql 提供程序） |
| Dockerfile 改单阶段 `dotnet publish Acm.sln`，worker 用 `command` 覆盖入口 | 比多 target 简单，同一镜像两个入口；两个 DLL 都在 /app |
| compose 补 `redis` + `judge-worker`；backend/worker 均设 `Judge__TestcaseRoot=/data/testcases` + 挂 `./data:/data` | **顺带修复 M2 遗留**：现在 backend 容器内 TestcaseRoot 是 Windows 宿主路径，容器内根本不可用；M3 起 judge-worker 需要真实测试点目录 |
| appsettings 连接串沿用既有约定：默认 `redis:6379`（docker 网络内），本地用环境变量 `ConnectionStrings__Redis=localhost:6379` 覆盖 | 与 DB 连接串同一套模式，AGENTS.md 已有此坑记录 |
| 测试最小适配：SubmitJudgeTests 提交后**轮询等待终态** | 提交不再同步返回，旧断言必挂；轮询模式即 DESIGN_M3 5.1 的最终形态，M3.4 再加"自动起 Worker" |
| 测试前置条件：本地起 redis + Worker 进程 | TestAppFactory 无法托管独立进程（DESIGN_M3 5.1 已注明），M3.4 细化 TestContainer/前置脚本 |

> **Windows 部署注意**：Docker Desktop（Windows）下 Linux 容器无法挂载宿主 npipe 的 docker 引擎 socket，因此 **compose 里的 judge-worker 在 Windows 上连不上沙箱**（M2 的 backend 容器同样如此，从未真正判题）。compose 的 worker 是 Linux 部署形态；**Windows 本地开发一律 `dotnet run` 起 Worker**（沙箱走宿主 Docker Desktop）。

---

## 四、逐文件内容

### 4.1 项目结构总览

```
backend/
├── Acm.sln                          # 追加 Acm.Judge.Core + Acm.JudgeWorker
├── Acm.Api/                         # Web API（提交入口 + 查询 + 测试点管理 + 鉴权/CRUD）
├── Acm.Judge.Core/                  # 【新增】共享评测核心（实体无关 Web/Worker，两边都引用）
│   ├── Acm.Judge.Core.csproj
│   ├── Data/                        # ← 从 Acm.Api/Data 迁入
│   │   ├── AppDbContext.cs          #   namespace: Acm.Api.Data → Acm.Judge.Core.Data
│   │   └── Migrations/              #   4 个文件（2 迁移 + 2 Designer + 快照）整体迁入
│   ├── Models/                      # ← 从 Acm.Api/Models 迁入（User/Problem/Submission）
│   └── Judge/                       # ← 评测域
│       ├── JudgeOptions.cs          #   ← 从 Acm.Api/Config.cs 拆出（Jwt/App 选项留在 Api）
│       ├── SandboxRunner.cs         #   ← 从 Acm.Api/Services 迁入
│       ├── OutputComparer.cs        #   ← 同上
│       └── JudgeEngine.cs           #   【新增】M2 JudgeService 评测主体抽出
├── Acm.JudgeWorker/                 # 【新增】独立 Worker 进程
│   ├── Acm.JudgeWorker.csproj
│   ├── Program.cs                   # 宿主构建 + 服务注册
│   ├── QueueConsumer.cs             # BRPOP 主循环（BackgroundService）
│   └── appsettings.json             # DB/Redis 连接 + Judge 配置（与 Api 同内容）
└── Acm.Api.Tests/                   # SubmitJudgeTests 改轮询等待
```

**迁移操作顺序**（git mv 保历史，改名 `JudgeOptions`/`Config.cs` 拆份）：

```powershell
# 1. Core 项目就位
dotnet new classlib -n Acm.Judge.Core -o backend/Acm.Judge.Core -f net8.0
# 2. 迁 Data + Models + Judge 相关文件（git mv 保留历史）
git mv backend/Acm.Api/Data backend/Acm.Judge.Core/Data
git mv backend/Acm.Api/Models backend/Acm.Judge.Core/Models
git mv backend/Acm.Api/Services/SandboxRunner.cs backend/Acm.Judge.Core/Judge/
git mv backend/Acm.Api/Services/OutputComparer.cs backend/Acm.Judge.Core/Judge/
# 3. Worker 项目就位
dotnet new console -n Acm.JudgeWorker -o backend/Acm.JudgeWorker -f net8.0
# 4. 追加进解决方案
dotnet sln backend/Acm.sln add backend/Acm.Judge.Core backend/Acm.JudgeWorker
```

### 4.2 `Acm.Judge.Core/Acm.Judge.Core.csproj`（新增）

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Docker.DotNet" Version="3.125.15" />
    <PackageReference Include="Microsoft.EntityFrameworkCore" Version="8.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="8.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="8.*" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

> Npgsql 不加：`UseNpgsql` 只在 Api/Worker 的 Program.cs 调用；迁移生成时提供程序由 startup 项目（Api）注入。若 `dotnet ef` 报找不到提供程序，再把 Npgsql 补进 Core（风险表已列）。

### 4.3 搬移文件改动清单

| 文件 | 改动 |
|------|------|
| `Data/AppDbContext.cs` | namespace → `Acm.Judge.Core.Data`；using `Acm.Api.Models` → `Acm.Judge.Core.Models`；代码零改动 |
| `Data/Migrations/*.cs` | namespace → `Acm.Judge.Core.Data.Migrations`（迁移类 + Designer + 快照）；Designer 里 `[DbContext(typeof(Acm.Api.Data.AppDbContext))]` → `Acm.Judge.Core.Data.AppDbContext` |
| `Models/User.cs` `Problem.cs` `Submission.cs` | namespace → `Acm.Judge.Core.Models`；代码零改动 |
| `Judge/JudgeOptions.cs` | 从 `Config.cs` 拆出，namespace → `Acm.Judge.Core.Judge`，类体原样 |
| `Judge/SandboxRunner.cs` `OutputComparer.cs` | namespace → `Acm.Judge.Core.Judge`；代码零改动 |
| `Acm.Api/Config.cs` | 删掉 JudgeOptions 类，保留 JwtOptions/AppOptions |

### 4.4 `Acm.Judge.Core/Judge/JudgeEngine.cs`（新增：M2 JudgeService 评测主体）

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Acm.Judge.Core.Data;
using Acm.Judge.Core.Models;

namespace Acm.Judge.Core.Judge;

/// <summary>评测主体：按 submissionId 执行 编译→逐点沙箱→汇总写库+计数（由 Worker 调用）。</summary>
public class JudgeEngine(
    AppDbContext db,
    SandboxRunner sandbox,
    IOptions<JudgeOptions> opt,
    ILogger<JudgeEngine> logger)
{
    private readonly JudgeOptions _cfg = opt.Value;

    // 评测单次提交：置 JUDGING → 编译 → 逐点运行 → 写终态 + 计数。异常向上抛，由 Worker 兜底置 RE
    public async Task ExecuteAsync(long sid, CancellationToken ct)
    {
        // 1. 取提交，置 JUDGING（Web 侧已写 PENDING + 入队）
        var sub = await db.Submissions.SingleAsync(s => s.Id == sid, ct);
        sub.Status = "JUDGING";
        await db.SaveChangesAsync(ct);

        // 题目（时限/内存上限从题目取）
        var problem = await db.Problems.SingleOrDefaultAsync(p => p.Id == sub.ProblemId, ct)
            ?? throw new InvalidOperationException($"题目 {sub.ProblemId} 不存在");

        // 2. 宿主工作目录 data/tmp/{sid}（源码/编译产物，沙箱挂载）
        var tmpRoot = Path.GetFullPath(Path.Combine(_cfg.TestcaseRoot, "..", "tmp"));
        Directory.CreateDirectory(tmpRoot);
        var workDir = Path.Combine(tmpRoot, sub.Id.ToString());
        Directory.CreateDirectory(workDir);
        string sourceFile = sub.Language == "cpp17" ? "main.cpp" : "main.py";
        await File.WriteAllTextAsync(Path.Combine(workDir, sourceFile), sub.Code, ct);

        try
        {
            // 3. 编译（仅 cpp17；Python 解释型直接跑）
            if (sub.Language == "cpp17")
            {
                var compileRes = await sandbox.RunAsync(new SandboxRequest(
                    Image: _cfg.DockerImageCpp,
                    Cmd: ["/bin/sh", "-c", "cd /work && g++ -O2 -std=c++17 main.cpp -o main"],
                    WorkDir: workDir,
                    InputFile: null,
                    TimeLimitMs: 30_000,            // 编译宽限
                    MemoryLimitMb: problem.MemoryLimit,
                    ReadOnlyWorkDir: false), ct);   // 编译容器可写，产出 main
                if (compileRes.TimedOut || compileRes.ExitCode != 0)
                {
                    await FinishAsync(sub, "CE", 0,
                        compileError: (compileRes.Stderr + compileRes.Stdout).Trim(), ct);
                    return;
                }
            }

            // 4. 找测试点（*.in 文件名升序）
            var tcDir = Path.Combine(_cfg.TestcaseRoot, sub.ProblemId.ToString());
            var inFiles = Directory.Exists(tcDir)
                ? Directory.GetFiles(tcDir, "*.in")
                    .OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f))).ToList()
                : new List<string>();
            if (inFiles.Count == 0)
            {
                await FinishAsync(sub, "RE", 0, compileError: "无测试点数据", ct);
                return;
            }

            // 5. 逐测试点运行沙箱
            var results = new List<TestcaseResult>();
            int acCount = 0;
            long maxTime = 0;
            string worst = "AC";
            foreach (var inf in inFiles)
            {
                var n = int.Parse(Path.GetFileNameWithoutExtension(inf));
                var res = await sandbox.RunAsync(new SandboxRequest(
                    Image: sub.Language == "cpp17" ? _cfg.DockerImageCpp : _cfg.DockerImagePython,
                    Cmd: sub.Language == "cpp17"
                        ? ["/bin/sh", "-c", "cd /work && ./main < /tc.in"]
                        : ["/bin/sh", "-c", "cd /work && python3 main.py < /tc.in"],
                    WorkDir: workDir,
                    InputFile: inf,
                    TimeLimitMs: problem.TimeLimit,
                    MemoryLimitMb: problem.MemoryLimit), ct);

                // 判定单个测试点
                string st;
                if (res.TimedOut)               st = "TLE";
                else if (res.OomKilled)         st = "MLE";
                else if (res.ExitCode != 0)     st = "RE";
                else
                {
                    var expected = File.Exists(inf[..^3] + ".out")
                        ? await File.ReadAllTextAsync(inf[..^3] + ".out", ct)
                        : "";
                    st = OutputComparer.EqualsIgnoreTrailingWhitespace(expected, res.Stdout)
                        ? "AC" : "WA";
                }
                if (st == "AC") acCount++;
                maxTime = Math.Max(maxTime, res.WallTimeMs);
                if (Priority(st) > Priority(worst)) worst = st;
                results.Add(new TestcaseResult(n, st, (int)res.WallTimeMs));
            }

            // 6. 汇总：score = AC 点数占比，状态取最严重
            int score = results.Count == 0 ? 0 : acCount * 100 / results.Count;
            await FinishAsync(sub, worst, score, results, (int)maxTime, ct);
        }
        finally
        {
            // 清理工作目录（无论成败）
            try { Directory.Delete(workDir, true); }
            catch (Exception ex) { logger.LogWarning("清理工作目录失败 {dir}: {msg}", workDir, ex.Message); }
        }
    }

    // 写回最终状态 + 更新题目计数（M2 逻辑原样搬入）
    private async Task FinishAsync(Submission sub, string status, int score,
        List<TestcaseResult>? detail = null, int? timeMs = null, string? compileError = null,
        CancellationToken ct = default)
    {
        sub.Status = status;
        sub.Score = score;
        sub.TimeMs = timeMs;
        if (compileError is not null)
            sub.Detail = JsonSerializer.Serialize(new { compileError });
        else if (detail is not null)
            sub.Detail = JsonSerializer.Serialize(detail);

        // 计数：SubmitCount 每次 +1；首次 AC 才 AcCount +1
        var problem = await db.Problems.SingleAsync(p => p.Id == sub.ProblemId, ct);
        problem.SubmitCount++;
        if (status == "AC")
        {
            bool firstAc = !await db.Submissions.AnyAsync(s =>
                s.UserId == sub.UserId && s.ProblemId == sub.ProblemId && s.Status == "AC", ct);
            if (firstAc) problem.AcCount++;
        }
        await db.SaveChangesAsync(ct);
    }

    // 优先级：AC=0 < WA=1 < RE=2 < MLE=3 < TLE=4
    private static int Priority(string s) => s switch
    {
        "AC" => 0, "WA" => 1, "RE" => 2, "MLE" => 3, "TLE" => 4, _ => 0
    };
}
```

> 说明：`TestcaseResult` 原为 Api 的 DTO，这里搬一份到 Core（record，字段相同）。Web 的 `SubmissionRead`/`ToRead` 解析逻辑保留在 Api（DTO 是 Api 层关心的事），两边互不依赖 Dtos。

### 4.5 `Acm.Judge.Core/Judge/TestcaseResult.cs`（新增，Core 内部结果类型）

```csharp
namespace Acm.Judge.Core.Judge;

// 单个测试点结果（Detail jsonb 数组元素；Api 的 SubmissionDtos.TestcaseResult 形状相同，二者独立）
public record TestcaseResult(
    int Id,
    string Status,
    int? TimeMs,
    int? MemoryKb = null);
```

### 4.6 `Acm.Api` 修改清单

**`Acm.Api.csproj`**：加 `<ProjectReference Include="..\Acm.Judge.Core\Acm.Judge.Core.csproj" />`；删 Docker.DotNet 引用（随 SandboxRunner 迁走）；其余包保留（EF Design 仍是迁移 startup 需要）。

**`Services/JudgeService.cs`**（重写为入口 + 查询，评测主体删除）：

```csharp
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Acm.Judge.Core.Data;
using Acm.Judge.Core.Models;
using Acm.Api.Dtos;

namespace Acm.Api.Services;

/// <summary>提交入口：写库 PENDING + 入队即回（评测由 Worker 异步执行）；附带单提交查询。</summary>
public class JudgeService(AppDbContext db, SubmissionQueue queue, ILogger<JudgeService> logger)
{
    // 提交：校验题目 → 写 PENDING → LPUSH 入队 → 秒回
    public async Task<SubmissionRead> SubmitAsync(int problemId, int userId, SubmissionCreate req)
    {
        var problem = await db.Problems.SingleOrDefaultAsync(p => p.Id == problemId)
            ?? throw new ApiException(404, "题目不存在");

        var sub = new Submission
        {
            UserId = userId,
            ProblemId = problemId,
            Language = req.Language,
            Code = req.Code,
            CodeLength = Encoding.UTF8.GetByteCount(req.Code),
            Status = "PENDING",
        };
        db.Submissions.Add(sub);
        await db.SaveChangesAsync();

        try
        {
            await queue.EnqueueAsync(sub.Id);
        }
        catch (Exception ex)
        {
            // Redis 不可用：回滚 PENDING 行，避免幽灵提交；前端可重试
            logger.LogError(ex, "入队失败 submission {sid}，回滚", sub.Id);
            db.Submissions.Remove(sub);
            await db.SaveChangesAsync();
            throw new ApiException(503, "评测队列不可用，请稍后重试");
        }
        return ToRead(sub);
    }

    // 查询单次提交（前端轮询入口）
    public async Task<SubmissionRead> GetAsync(long sid)
    {
        var s = await db.Submissions.SingleOrDefaultAsync(x => x.Id == sid)
            ?? throw new ApiException(404, "提交不存在");
        return ToRead(s);
    }

    // 实体 → 响应 DTO（Detail jsonb 按 Status 分支解析，同 M2）
    private static SubmissionRead ToRead(Submission s)
    {
        List<TestcaseResult> detail = new();
        string? compileError = null;
        if (s.Status == "CE" && !string.IsNullOrEmpty(s.Detail))
        {
            var obj = JsonSerializer.Deserialize<Dictionary<string, string>>(s.Detail);
            compileError = obj?.GetValueOrDefault("compileError");
        }
        else if (!string.IsNullOrEmpty(s.Detail))
            detail = JsonSerializer.Deserialize<List<TestcaseResult>>(s.Detail) ?? new();

        return new SubmissionRead(s.Id, s.UserId, s.ProblemId, s.Language,
            s.Status, s.Score, s.TimeMs, s.MemoryKb, detail, compileError, s.CreatedAt);
    }
}
```

**`Services/SubmissionQueue.cs`**（新增）：

```csharp
using StackExchange.Redis;

namespace Acm.Api.Services;

/// <summary>评测队列（Redis List：LPUSH 左进，Worker BRPOP 右出，FIFO）。</summary>
public class SubmissionQueue(IConnectionMultiplexer redis)
{
    private readonly IDatabase _db = redis.GetDatabase();
    private const string Key = "queue:judge";

    public Task EnqueueAsync(long submissionId) => _db.ListLeftPushAsync(Key, submissionId);
}
```

**`Program.cs`**（差异）：

```csharp
using StackExchange.Redis;
using Acm.Judge.Core.Data;
using Acm.Judge.Core.Judge;

// ...原有 JudgeOptions/AppOptions/JwtOptions 注册保留（JudgeOptions 现来自 Core，namespace 换了）...
builder.Services.Configure<JudgeOptions>(builder.Configuration.GetSection("Judge"));

// Redis：AbortOnConnectFail=false —— 连接失败不抛异常，后台自动重连（Redis 短暂不可用 Web 仍能启动/存活）
var redisOpts = ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("Redis")!);
redisOpts.AbortOnConnectFail = false;
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOpts));
builder.Services.AddSingleton<SubmissionQueue>();

// 删除：builder.Services.AddSingleton<SandboxRunner>();  ← 评测已不在 Web
```

**`appsettings.json`**（追加 Redis 连接串，沿用"默认 docker 内地址、本地环境变量覆盖"约定）：

```json
"ConnectionStrings": {
  "Default": "Host=db;Port=5432;Database=acm;Username=acm;Password=acm",
  "Redis": "redis:6379"
},
```

**其余文件 using 更新**（机械替换，编译错误全量提示）：

| 文件 | 改动 |
|------|------|
| `Services/TestcaseService.cs` | `using Acm.Api.Dtos` 保留；加 `using Acm.Judge.Core.Judge;`（JudgeOptions 迁走） |
| `Services/AuthService.cs`、`Services/ProblemService.cs` | `using Acm.Api.Models` → `using Acm.Judge.Core.Models;` |
| `Controllers/SubmissionsController.cs` | 路由/代码零改动；仅更新顶部注释"阻塞评测"→"异步评测（秒回 PENDING）" |
| `Tests/TestAppFactory.cs` | `using Acm.Api.Data` → `using Acm.Judge.Core.Data;` |

### 4.7 `Acm.JudgeWorker/`（新增）

**`Acm.JudgeWorker.csproj`**：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore" Version="8.*" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="8.*" />
    <PackageReference Include="StackExchange.Redis" Version="2.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Acm.Judge.Core\Acm.Judge.Core.csproj" />
  </ItemGroup>
</Project>
```

**`appsettings.json`**（与 Api 的 DB/Redis/Judge 三节同内容）：

```json
{
  "ConnectionStrings": {
    "Default": "Host=db;Port=5432;Database=acm;Username=acm;Password=acm",
    "Redis": "redis:6379"
  },
  "Judge": {
    "TestcaseRoot": "C:/Users/10408/Desktop/acmtest/data/testcases",
    "UseDockerSandbox": true,
    "DockerImageCpp": "judge-cpp:latest",
    "DockerImagePython": "judge-python:latest"
  },
  "Logging": {
    "LogLevel": { "Default": "Information", "Microsoft": "Warning" }
  }
}
```

**`Program.cs`**：

```csharp
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
```

> **执行时发现的配置坑（已写入 AGENTS.md，必读）**：
> 1. **console 项目默认不拷 appsettings.json**——csproj 需显式 `<None Update="appsettings.json" CopyToOutputDirectory>`，否则输出目录无该文件、Judge 配置节读不到（数据库/Redis 连接串因有环境变量覆盖而表面"能用"，掩盖了 json 缺失）。
> 2. **`Host.CreateApplicationBuilder`（console 宿主）构造时已按 cwd 解析默认 appsettings.json + 不带无前缀环境变量**——故需追加绝对路径 json + 再追加 `AddEnvironmentVariables()`。注意：在构造后改默认 json 源的 `Path` 属性**无效**（ConfigurationManager 构造期已 Build 冻结 provider），只能新增源。
> 3. **`dotnet run` 对 console 项目不改 cwd**（cwd=调用目录），web 项目则会改到项目目录——这是上一条的根因。
> 4. **BRPOP 阻塞时长须小于 `ConfigurationOptions.AsyncTimeout`**（SE.Redis 默认 5000ms），否则客户端超时先于 BRPOP 触发抛 `RedisTimeoutException`——故 `AsyncTimeout=15_000` + BRPOP 阻塞 5s。

**`QueueConsumer.cs`**：

```csharp
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Acm.Judge.Core.Data;
using Acm.Judge.Core.Judge;

namespace Acm.JudgeWorker;

/// <summary>Redis 队列消费者：BRPOP 阻塞取任务 → JudgeEngine 评测 → 失败兜底置 RE。</summary>
public class QueueConsumer(
    IConnectionMultiplexer redis,
    IServiceScopeFactory scopeFactory,
    ILogger<QueueConsumer> logger) : BackgroundService
{
    private const string Key = "queue:judge";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = redis.GetDatabase();
        logger.LogInformation("Worker 启动，开始消费 {Key}", Key);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // BRPOP 阻塞 5s：有任务立即返回；超时空转（顺带检查停机信号）
                // 注：SE.Redis 的 ListRightPopAsync 是 RPOP（非阻塞），BRPOP 需走 Execute
                var val = await db.ExecuteAsync("BRPOP", Key, 5);
                if (val.IsNull) continue;   // 5s 无任务

                // BRPOP 返回 [队列名, 值]
                var arr = (RedisResult[])val;
                var sid = (long)(RedisValue)arr[1];

                logger.LogInformation("取到任务 submission {sid}", sid);
                await JudgeOneAsync(sid, stoppingToken);
            }
            catch (OperationCanceledException) { break; }   // 停机信号
            catch (RedisException ex)
            {
                logger.LogError(ex, "Redis 连接异常，1s 后重试");
                await Task.Delay(1000, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "消费循环异常，1s 后重试");
                await Task.Delay(1000, stoppingToken);
            }
        }
        logger.LogInformation("Worker 停止");
    }

    // 评测单条提交：异常兜底置 RE（仅当仍是 PENDING/JUDGING，不覆盖终态）
    private async Task JudgeOneAsync(long sid, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<JudgeEngine>();
        try
        {
            await engine.ExecuteAsync(sid, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "评测 {sid} 失败", sid);
            try
            {
                var dbc = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var sub = await dbc.Submissions.SingleOrDefaultAsync(s => s.Id == sid, ct);
                if (sub is not null && sub.Status is "PENDING" or "JUDGING")
                {
                    sub.Status = "RE";
                    await dbc.SaveChangesAsync(ct);
                }
                // sub 为 null：任务对应提交已被删（入队回滚竞态/测试 TRUNCATE），跳过即可
            }
            catch (Exception ex2) { logger.LogError(ex2, "兜底置 RE 失败 {sid}", sid); }
        }
    }
}
```

> 停机语义：BRPOP 每 5s 超时重查 stoppingToken，Ctrl+C 后最多 5s 内退出；`host.RunAsync` 触发后台服务优雅停止。

### 4.8 `docker-compose.yml`（追加 redis + judge-worker）

```yaml
services:
  db: { ...现状不变... }

  redis:
    image: redis:7-alpine
    ports: ["6379:6379"]
    restart: unless-stopped

  backend:
    build: ./backend
    depends_on:
      db: { condition: service_healthy }
      redis: { condition: service_started }
    environment:
      ConnectionStrings__Default: Host=db;Port=5432;Database=acm;Username=acm;Password=acm
      ConnectionStrings__Redis: redis:6379
      Judge__TestcaseRoot: /data/testcases     # 容器内测试点目录（M2 遗留修复）
      Jwt__SecretKey: dev-secret-change-me-at-least-32-chars
      App__AllowRegister: "true"
    volumes: ["./data:/data"]
    ports: ["8000:8000"]

  judge-worker:
    build: ./backend
    command: ["dotnet", "Acm.JudgeWorker.dll"]   # 同镜像，覆盖入口
    depends_on:
      db: { condition: service_healthy }
      redis: { condition: service_started }
    restart: unless-stopped                       # 崩溃自动重启
    environment:
      ConnectionStrings__Default: Host=db;Port=5432;Database=acm;Username=acm;Password=acm
      ConnectionStrings__Redis: redis:6379
      Judge__TestcaseRoot: /data/testcases
    volumes: ["./data:/data"]
```

> 本地起 Redis 用 `docker compose up -d redis`。若本机 6379 被占，把映射改成 `6380:6379` 并同步本地环境变量。

### 4.9 `backend/Dockerfile`（改单阶段发布整个 solution）

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Acm.sln ./
COPY Acm.Api/Acm.Api.csproj Acm.Api/
COPY Acm.Judge.Core/Acm.Judge.Core.csproj Acm.Judge.Core/
COPY Acm.JudgeWorker/Acm.JudgeWorker.csproj Acm.JudgeWorker/
RUN dotnet restore Acm.sln
COPY . .
RUN dotnet publish Acm.sln -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8000
EXPOSE 8000
ENTRYPOINT ["dotnet", "Acm.Api.dll"]   # worker 由 compose command 覆盖
```

### 4.10 测试适配（`Acm.Api.Tests/M2/SubmitJudgeTests.cs`）

提交后**轮询等待终态**（与 DESIGN_M3 5.1 一致）：

```csharp
// 轮询等待终态：提交秒回 PENDING，后台 Worker 写终态后返回；60 次 × 500ms 上限
private static async Task<SubmissionRead> WaitFinalAsync(HttpClient client, SubmissionRead sub)
{
    for (int i = 0; i < 60; i++)
    {
        await Task.Delay(500);
        var cur = await client.GetFromJsonAsync<SubmissionRead>($"/api/v1/submissions/{sub.Id}");
        if (cur!.Status is "AC" or "WA" or "TLE" or "CE" or "RE") return cur;
    }
    throw new TimeoutException($"提交 {sub.Id} 轮询超时");
}
```

各用例改造：`POST → 断言 201 + Status == "PENDING" → var final = await WaitFinalAsync(client, sub); 断言 final.*`。`Submit_Invalid_Language_Returns_400` 不变（不入队）。计数用例同样改轮询后断言。

> **测试前置条件**（M3.4 前的手动步骤，随后写入 AGENTS.md）：
> 1. `docker compose up -d db redis`
> 2. 另开终端起 Worker：`dotnet run --project backend/Acm.JudgeWorker`（连本地 Postgres 5433 + Redis 6379）
> 3. 跑测试前设 `$env:ConnectionStrings__Redis="localhost:6379"`（连同已有的 Default 覆盖）

---

## 五、验证方式

### 5.1 构建

```powershell
dotnet build backend/Acm.sln
```

### 5.2 本地环境（4 进程）

```powershell
docker compose up -d db redis                       # Postgres(5433) + Redis(6379)
# 终端 A（Web）
$env:ConnectionStrings__Default="Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"
$env:ConnectionStrings__Redis="localhost:6379"
dotnet run --project backend/Acm.Api
# 终端 B（Worker，同样设上面两个环境变量后）
dotnet run --project backend/Acm.JudgeWorker
```

### 5.3 API 黑盒（验收清单）

| 用例 | 断言 |
|------|------|
| 提交 AC 代码 | ①POST 201 且 **<1s 返回** `{status:"PENDING"}` ②轮询 GET 2-5s 内变 AC + score 100 + detail 数组 |
| 提交死循环 | 轮询 → TLE |
| 提交语法错误 | 轮询 → CE + compileError 非空 |
| 并发连交 3 个 | 完成顺序 = 提交顺序（FIFO）；最终状态各自正确 |
| **Web 重启续跑** | 提交后立刻重启 Web（Worker/Redis 不动）→ 重启后轮询仍能拿到终态（任务在 Redis/DB） |
| **Worker 重启续跑** | 提交后立刻 Ctrl+C 杀 Worker → 重启 Worker → 任务被消费出结果（未弹出任务留 Redis） |
| **Redis 挂了** | 停 redis → 提交返回 503 `{detail:"评测队列不可用"}` → 查库无残留 PENDING 行 → 起 redis 再提交正常 |
| 回归 | 题目 CRUD/鉴权/测试点接口行为不变；`GET /submissions/{sid}` 终态可读 |

### 5.4 集成测试

```powershell
# 前置：db + redis 运行中 + Worker 进程在跑（4.10）
$env:ConnectionStrings__Default="Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"
$env:ConnectionStrings__Redis="localhost:6379"
dotnet test
```

验收清单：
1. `dotnet build backend/Acm.sln` 全绿
2. 5.3 黑盒全过
3. `dotnet test` 全绿（含 M1 回归）
4. Worker 日志可见"取到任务 → 评测完成"流转，Web 日志无评测相关输出（职责已分离）

---

## 六、风险与对策

| 风险 | 对策 |
|------|------|
| SE.Redis 无阻塞 API / 版本行为差异 | BRPOP 走 `ExecuteAsync`（命令级，版本无关）；`ListRightPopAsync` 是 RPOP 勿用 |
| `dotnet ef` 迁移工具找不到提供程序（DbContext 在 Core） | 命令改为 `dotnet ef migrations add Xxx --project backend/Acm.Judge.Core --startup-project backend/Acm.Api -o Data/Migrations`；仍报错则把 Npgsql 补进 Core |
| 命名空间大改漏改 using | 编译错误全量兜底；重点 grep `Acm.Api.Data`/`Acm.Api.Models` 残留 |
| LPUSH 成功但响应丢失（入队回滚歧义） | Worker 消费时提交不存在则跳过+日志；Web 侧删行 + 503，两侧配合无幽灵任务 |
| 测试期间任务引用了 TRUNCATE 掉的 submission | 同上：Worker 跳过并记日志，测试不受干扰 |
| 本地 6379 端口冲突 | compose 映射改 `6380:6379`，环境变量同步改 `localhost:6380` |
| Worker 崩溃丢**已弹出**任务 | 简化接受（DESIGN_M3 已定，失败即 RE）；未弹出的留 Redis，重启续跑 |
| compose 的 worker 在 Windows 连不上沙箱（npipe 无法挂进 Linux 容器） | Windows 本地一律 `dotnet run` 起 Worker；compose worker 是 Linux 部署形态 |
| Redis 不可用导致 Web 启动失败 | `AbortOnConnectFail=false` 后台重连；入队失败 503 + 回滚，不 500 |
| 前端 M3.1 会"停在 PENDING" | 已知中间态：M3.2 紧跟补轮询，M3.1 只验证 API 层 |

---

## 七、交付物与后续衔接

| 交付物 | 衔接 |
|--------|------|
| `Acm.Judge.Core`（共享 Data/Models/Judge） | M3.2-M3.4 及未来一切评测能力都加在 Core |
| `Acm.JudgeWorker` 独立进程 | M3.4 测试自动化（前置起 Worker/TestContainer 选型） |
| Redis 队列 + `SubmissionQueue` | M3.2 前端轮询消费 PENDING→终态 |
| `JudgeService` 入口化（秒回 PENDING） | M3.2 ProblemDetail 提交区改轮询；M3.3 历史页复用查询 API |
| compose redis/judge-worker + Dockerfile 双入口 | M3.4 总验收一键拉起全栈 |
| 测试轮询适配 | M3.4 完善（自动起 Worker，去掉手动前置） |

> M3.1 完成后：提交秒回 PENDING、Worker 后台出结果、Web/Worker 职责分离。M3.2 接前端轮询，M3.3 历史页，M3.4 收尾（测试自动化 + 总验收 + AGENTS.md 更新：常用命令增 redis/worker/ef 新形态、注意事项增"测试前置 redis+Worker+环境变量"、架构速览更新进程拓扑）。
