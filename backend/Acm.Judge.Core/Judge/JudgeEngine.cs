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
                        compileError: (compileRes.Stderr + compileRes.Stdout).Trim(), ct: ct);
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
                await FinishAsync(sub, "RE", 0, compileError: "无测试点数据", ct: ct);
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
            await FinishAsync(sub, worst, score, results, (int)maxTime, ct: ct);
        }
        finally
        {
            // 清理工作目录（无论成败）
            try { Directory.Delete(workDir, true); }
            catch (Exception ex) { logger.LogWarning("清理工作目录失败 {dir}: {msg}", workDir, ex.Message); }
        }
    }

    // 写回最终状态 + 更新题目计数
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
