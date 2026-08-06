# M2.3 判题编排 详细设计方案

> **所属里程碑**：M2 评测核心（`DESIGN_M2.md`）
> **任务边界**：M2.3 判题 —— JudgeService 编排 + 提交/查询 API + 状态判定 + 输出比对（总设计"四、4.1/4.3/4.4 + 五、API"）
> **前置**：M2.1 数据层（Submission 表）✅；M2.2 沙箱（SandboxRunner + 镜像）✅
> **本阶段不涉及**：测试点上传接口（M2.4）、前端提交区（M2.5）、集成测试（M2.6）

---

## 一、目标

打通**提交 → 评测 → 返回结果**的完整链路：新增 `JudgeService`（编排编译/逐点运行/汇总判定/计数更新），暴露提交与查询 API。完成后可通过 HTTP 提交 C++/Python 代码，获得 AC/WA/TLE/RE/CE 判定。

## 二、范围

| 做 | 不做 |
|----|------|
| `JudgeService.SubmitAsync`（全流程编排） | 测试点上传/删除/列表接口（M2.4） |
| 输出比对 `OutputComparer` | 前端提交区/结果展示（M2.5） |
| 状态机判定（TLE>MLE>RE>WA>AC 优先级） | 提交历史列表页（M2.5 起） |
| POST `/problems/{pid}/submissions` + GET `/submissions/{sid}` | SPJ/子任务/部分分（M3+） |
| Submission DTO + SubmissionsController | 队列/异步（M3） |
| 题目计数更新（SubmitCount/AcCount） | |

## 三、设计决策及理由

| 决策 | 理由 |
|------|------|
| 编译走 SandboxRunner（`ReadOnlyWorkDir:false`） | 复用镜像 + 容器执行逻辑，不另写编译专用路径；挂载 rw 即可产出 main |
| SandboxRequest 加 `ReadOnlyWorkDir` 参数 | 编译容器需可写挂载，运行容器只读——同一执行器两种模式 |
| SandboxRunner 返回 `WallTimeMs`（inspect 时间差） | DESIGN 要求"时间取最大"，inspect 有 StartedAt/FinishedAt，零成本；MLE 用 OomKilled 判定，内存数值 M2 用 stats 单次采样 |
| 编译超时 = 编译容器 TimeLimitMs 30s（宽限），超时/非0 → CE | 编译不该判 TLE，统一归 CE（编译期错误） |
| 汇总按**优先级** TLE > MLE > RE > WA > AC | 一个测试点命中即终态，与 DESIGN 4.4 一致；全 AC 才 AC |
| Detail 用 `System.Text.Json` 序列化 `List<TestcaseResult>` 存 jsonb | jsonb 列已建（M2.1）；CE 时存 `{"compileError":"..."}` 对象 |
| CE 的 Detail 结构 = `{"compileError": str}`，非数组 | 编译无测试点，数组语义不适用；读取时按 Status 分支解析 |
| 计数更新放评测写回阶段 | 同一事务/同一 SaveChanges；首次 AC 判定 = 查该用户该题是否已有 AC 记录 |
| `SubmitAsync` 抛 `ApiException(400)` 于校验失败 | 沿用 M1 错误契约 |
| 测试点按 `Directory.GetFiles("*.in")` 文件名升序 | 不依赖连续编号；与实际存在的文件一一对应（异议 B 结论） |
| 工作目录 `data/tmp/{submissionId}/` 评测后 finally 删除 | 与 DESIGN 4.1 第 10 步一致；避免残留垃圾 |

## 四、逐文件内容

### 4.1 `Services/SandboxRunner.cs`（修改，追加能力）

SandboxRequest 加 `ReadOnlyWorkDir`：

```csharp
public record SandboxRequest(
    string Image,
    string[] Cmd,
    string WorkDir,
    string? InputFile,
    int TimeLimitMs,
    int MemoryLimitMb,
    bool ReadOnlyWorkDir = true);   // true=运行容器只读；false=编译容器可写
```

SandboxResult 加 `WallTimeMs`：

```csharp
public record SandboxResult(
    bool TimedOut,
    bool OomKilled,
    int? ExitCode,
    string Stdout,
    string Stderr,
    long WallTimeMs);   // 容器运行耗时（FinishedAt - StartedAt）
```

`BuildBinds` 按标志决定权限：

```csharp
private static string[] BuildBinds(SandboxRequest req)
{
    var mode = req.ReadOnlyWorkDir ? ":ro" : ":rw";
    var binds = new List<string> { $"{req.WorkDir}:/work{mode}" };
    if (req.InputFile is not null) binds.Add($"{req.InputFile}:/tc.in:ro");
    return binds.ToArray();
}
```

`RunAsync` 里计算 WallTimeMs：

```csharp
var inspect = await _docker.Containers.InspectContainerAsync(id, ct);
var wallMs = (long)(inspect.State.FinishedAt - inspect.State.StartedAt).TotalMilliseconds;
// 返回时带上 wallMs
```

> 内存数值：M2 用 `OomKilled` 判 MLE，`MemoryKb` 留空（Submission 表字段可空）；不轮询 stats（简化，个人自用可接受）。

### 4.2 `Dtos/SubmissionDtos.cs`（新增）

```csharp
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Acm.Api.Dtos;

// 提交请求：语言 + 源码
public record SubmissionCreate(
    [Required, RegularExpression(@"^(cpp17|python3)$")] string Language,
    [Required, MaxLength(65536)] string Code);

// 单个测试点结果（Detail 数组元素）
public record TestcaseResult(
    int Id,                    // 测试点编号（1..N）
    string Status,             // AC/WA/TLE/MLE/RE
    int? TimeMs,               // 该点耗时
    int? MemoryKb = null);     // M2 暂不填

// 提交读取（完整响应）
public record SubmissionRead(
    long Id, int UserId, int ProblemId, string Language,
    string Status, int Score, int? TimeMs, int? MemoryKb,
    List<TestcaseResult> Detail,   // 正常评测：逐点数组；CE：空列表
    string? CompileError,          // CE 时编译错误文本；其他 null
    DateTime CreatedAt);
```

### 4.3 `Services/OutputComparer.cs`（新增）

```csharp
namespace Acm.Api.Services;

/// <summary>输出比对：忽略行尾空白和文件尾换行后逐字符比对。</summary>
public static class OutputComparer
{
    public static bool EqualsIgnoreTrailingWhitespace(string expected, string actual)
    {
        static string Normalize(string s) =>
            // 每行去掉尾部空白 + 去掉文件末尾空行
            string.Join("\n",
                s.Split('\n')
                 .Select(l => l.TrimEnd(' ', '\t', '\r'))   // 去行尾空白
                 .SkipLastWhile(string.IsNullOrEmpty));      // 去尾部空行
        return Normalize(expected) == Normalize(actual);
    }
}
```

### 4.4 `Services/JudgeService.cs`（新增，核心）

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Acm.Api.Data;
using Acm.Api.Dtos;
using Acm.Api.Models;

namespace Acm.Api.Services;

public class JudgeService(
    AppDbContext db,
    SandboxRunner sandbox,
    IOptions<JudgeOptions> opt,
    ILogger<JudgeService> logger)
{
    private readonly JudgeOptions _cfg = opt.Value;

    // 提交并同步评测，返回完整结果
    public async Task<SubmissionRead> SubmitAsync(int problemId, int userId, SubmissionCreate req)
    {
        // 1. 校验题存在
        var problem = await db.Problems.SingleOrDefaultAsync(p => p.Id == problemId)
            ?? throw new ApiException(404, "题目不存在");

        // 2. 写库 PENDING（先落库，拿到 submissionId 作工作目录名）
        var sub = new Submission
        {
            UserId = userId, ProblemId = problemId,
            Language = req.Language,
            Code = req.Code,
            CodeLength = System.Text.Encoding.UTF8.GetByteCount(req.Code),
            Status = "PENDING",
        };
        db.Submissions.Add(sub);
        await db.SaveChangesAsync();
        sub.Status = "JUDGING";
        await db.SaveChangesAsync();

        var workDir = Path.Combine(_cfg.TestcaseRoot, "..", "tmp", sub.Id.ToString());
        // 实际用：data/tmp/{submissionId}
        Directory.CreateDirectory(workDir);
        string sourceFile = req.Language == "cpp17" ? "main.cpp" : "main.py";
        await File.WriteAllTextAsync(Path.Combine(workDir, sourceFile), req.Code);

        try
        {
            // 3. 编译（仅 cpp17）
            if (req.Language == "cpp17")
            {
                var compileRes = await sandbox.RunAsync(new SandboxRequest(
                    Image: _cfg.DockerImageCpp,
                    Cmd: ["/bin/sh", "-c", "cd /work && g++ -O2 -std=c++17 main.cpp -o main"],
                    WorkDir: workDir,
                    InputFile: null,
                    TimeLimitMs: 30_000,          // 编译宽限
                    MemoryLimitMb: problem.MemoryLimit,
                    ReadOnlyWorkDir: false));     // 编译容器可写
                if (compileRes.TimedOut || compileRes.ExitCode != 0)
                    return await FinishAsync(sub, "CE", 0,
                        CompileError: (compileRes.Stderr + compileRes.Stdout).Trim());
            }

            // 4. 找测试点（*.in 文件名升序）
            var tcDir = Path.Combine(_cfg.TestcaseRoot, problemId.ToString());
            var inFiles = Directory.Exists(tcDir)
                ? Directory.GetFiles(tcDir, "*.in").OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f))).ToList()
                : new List<string>();
            if (inFiles.Count == 0)
                return await FinishAsync(sub, "RE", 0, CompileError: "无测试点数据"); // 简化：无测试点 → RE

            // 5. 逐测试点运行
            var results = new List<TestcaseResult>();
            int acCount = 0; long maxTime = 0;
            string worst = "AC";
            foreach (var inf in inFiles)
            {
                var n = int.Parse(Path.GetFileNameWithoutExtension(inf));
                var res = await sandbox.RunAsync(new SandboxRequest(
                    Image: req.Language == "cpp17" ? _cfg.DockerImageCpp : _cfg.DockerImagePython,
                    Cmd: req.Language == "cpp17"
                        ? ["/bin/sh", "-c", "cd /work && ./main < /tc.in"]
                        : ["/bin/sh", "-c", "cd /work && python3 main.py < /tc.in"],
                    WorkDir: workDir,
                    InputFile: inf,
                    TimeLimitMs: problem.TimeLimit,
                    MemoryLimitMb: problem.MemoryLimit));

                // 判定单个测试点
                string st;
                if (res.TimedOut)      st = "TLE";
                else if (res.OomKilled) st = "MLE";
                else if (res.ExitCode != 0) st = "RE";
                else
                {
                    var expected = File.Exists(inf[..^3] + ".out")
                        ? await File.ReadAllTextAsync(inf[..^3] + ".out")
                        : "";
                    st = OutputComparer.EqualsIgnoreTrailingWhitespace(expected, res.Stdout)
                        ? "AC" : "WA";
                }
                if (st == "AC") acCount++;
                maxTime = Math.Max(maxTime, res.WallTimeMs);
                if (Priority(st) > Priority(worst)) worst = st;
                results.Add(new TestcaseResult(n, st, (int)res.WallTimeMs));
            }

            // 6. 汇总
            int score = results.Count == 0 ? 0 : acCount * 100 / results.Count;
            return await FinishAsync(sub, worst, score, results, (int)maxTime);
        }
        finally
        {
            Directory.Delete(workDir, true);   // 清理工作目录
        }
    }

    // 写回最终状态 + 更新题目计数
    private async Task<SubmissionRead> FinishAsync(Submission sub, string status, int score,
        List<TestcaseResult>? detail = null, int? timeMs = null, string? compileError = null)
    {
        sub.Status = status;
        sub.Score = score;
        sub.TimeMs = timeMs;
        if (compileError is not null)
            sub.Detail = JsonSerializer.Serialize(new { compileError });
        else if (detail is not null)
            sub.Detail = JsonSerializer.Serialize(detail);

        // 计数：SubmitCount 每次 +1；首次 AC 才 AcCount +1
        var problem = await db.Problems.SingleAsync(p => p.Id == sub.ProblemId);
        problem.SubmitCount++;
        if (status == "AC")
        {
            bool firstAc = !await db.Submissions.AnyAsync(s =>
                s.UserId == sub.UserId && s.ProblemId == sub.ProblemId && s.Status == "AC");
            if (firstAc) problem.AcCount++;
        }
        await db.SaveChangesAsync();
        return ToRead(sub);
    }

    // 优先级：AC=0 < WA=1 < RE=2 < MLE=3 < TLE=4（compile 单独走 CE）
    private static int Priority(string s) => s switch
    {
        "AC" => 0, "WA" => 1, "RE" => 2, "MLE" => 3, "TLE" => 4, _ => 0
    };

    // 实体 → 响应 DTO（Detail 解析）
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

    // 查询单次提交
    public async Task<SubmissionRead> GetAsync(long sid)
    {
        var s = await db.Submissions.SingleOrDefaultAsync(x => x.Id == sid)
            ?? throw new ApiException(404, "提交不存在");
        return ToRead(s);
    }
}
```

> 注：`Priority` 里 WA 判重逻辑——TLE 优先级最高，只要一个点 TLE 整体就 TLE（DESIGN 4.4）。

### 4.5 `Controllers/SubmissionsController.cs`（新增）

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acm.Api.Dtos;
using Acm.Api.Services;

namespace Acm.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/problems/{pid:int}/submissions")]
public class SubmissionsController(JudgeService judge) : ControllerBase
{
    // 提交代码 → 阻塞评测 → 返回完整结果
    [HttpPost]
    public async Task<ActionResult<SubmissionRead>> Submit(int pid, SubmissionCreate req) =>
        StatusCode(201, await judge.SubmitAsync(pid, this.CurrentUserId(), req));
}
```

`Controllers/SubmissionsController.cs`（追加另一个路由，同文件）：

```csharp
[ApiController]
[Authorize]
[Route("api/v1/submissions")]
public class SubmissionQueryController(JudgeService judge) : ControllerBase
{
    // 查询单次提交结果（同步失败/刷新补救）
    [HttpGet("{sid:long}")]
    public async Task<ActionResult<SubmissionRead>> Get(long sid) =>
        await judge.GetAsync(sid);
}
```

> 两个控制器可拆两个文件，也可同文件（类名不同即可）。

### 4.6 `Program.cs`（修改，注册）

```csharp
builder.Services.AddScoped<JudgeService>();   // 每请求一个（内部用 db + sandbox）
```

### 4.7 `appsettings.json`（修改，追加 tmp 路径？）

> 无需改——tmp 目录从 TestcaseRoot 派生（`../tmp`）。**注意**：`Path.Combine(_cfg.TestcaseRoot, "..", "tmp", ...)` 会产生 `data/testcases/../tmp/123`，等价 `data/tmp/123`，但目录创建需确保 `data/tmp` 存在。为清晰，JudgeService 内直接：

```csharp
var tmpRoot = Path.GetFullPath(Path.Combine(_cfg.TestcaseRoot, "..", "tmp"));
Directory.CreateDirectory(tmpRoot);
```

## 五、验证方式

### 5.1 编译 + 测试

```powershell
dotnet build
```

### 5.2 API 黑盒（四态）

准备：造题 P1000 + 测试点 1.in/1.out（3 5 → 8），登录拿 token。

| 提交 | 期望 |
|------|------|
| C++ A+B 正确代码 | AC, score 100, detail `[{"id":1,"status":"AC",...}]` |
| C++ 输出错误（printf 9） | WA |
| C++ 死循环 while(1) | TLE（沙箱超时 kill） |
| C++ 语法错误 | CE, compileError 非空 |
| Python 正确代码 | AC |

### 5.3 计数验证

- SubmitCount 每次 +1
- 同一用户首次 AC 后 AcCount=1；再 AC 不 +1

### 5.4 回归

```powershell
$env:ConnectionStrings__Default="Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"
dotnet test
```

验收清单：
1. POST 提交 AC 代码 → 201 + AC + score 100 + detail 数组
2. WA/TLE/CE 各返回正确状态
3. CE 的 compileError 有内容
4. GET /submissions/{sid} 返回同样结果
5. 题目 SubmitCount/AcCount 更新正确
6. 无测试点 → RE（或明确错误）
7. 非法语言/空代码 → 400

## 六、风险与对策

| 风险 | 对策 |
|------|------|
| 编译容器超时/异常 | TimeLimitMs=30s 宽限，超时或非0退出 → CE |
| 同步评测阻塞数秒 | 个人自用可接受（DESIGN 1.3 已论证）；M3 换队列 |
| 测试点目录不存在 | 判 RE（无测试点），不 500 |
| CE Detail 解析失败 | jsonb 由 DB 保证合法 JSON；反序列化失败兜底空列表 |
| 首次 AC 判定并发 | 单用户场景无并发；即使并发，AcCount 略偏不影响功能 |
| 工作目录残留 | finally 删除；删除失败仅日志，不阻塞返回 |
| Windows 路径斜杠 | 全用 Path.Combine + GetFullPath，Docker 挂载用绝对路径 |

## 七、交付物与后续衔接

| 交付物 | 衔接 |
|--------|------|
| JudgeService（编译/逐点/汇总/计数） | M2.4 测试点接口提供数据源；M2.5 前端调用 |
| 提交/查询 API | M2.5 前端提交区 + 结果展示 |
| OutputComparer | 无后续依赖，独立完成 |
| SandboxRunner 增强（ReadOnlyWorkDir/WallTimeMs） | M2.2 的沙箱能力扩展 |

> M2.3 完成后系统已"可提交可判题"。M2.4 补测试点管理接口，M2.5 前端接入，M2.6 集成测试收尾。
