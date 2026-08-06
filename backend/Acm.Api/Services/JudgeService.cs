using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
            UserId = userId,
            ProblemId = problemId,
            Language = req.Language,
            Code = req.Code,
            CodeLength = Encoding.UTF8.GetByteCount(req.Code),
            Status = "PENDING",
        };
        db.Submissions.Add(sub);
        await db.SaveChangesAsync();
        sub.Status = "JUDGING";
        await db.SaveChangesAsync();

        // 3. 准备宿主工作目录：data/tmp/{submissionId}（源码/编译产物放这里，沙箱挂载）
        var tmpRoot = Path.GetFullPath(Path.Combine(_cfg.TestcaseRoot, "..", "tmp"));
        Directory.CreateDirectory(tmpRoot);
        var workDir = Path.Combine(tmpRoot, sub.Id.ToString());
        Directory.CreateDirectory(workDir);
        string sourceFile = req.Language == "cpp17" ? "main.cpp" : "main.py";
        await File.WriteAllTextAsync(Path.Combine(workDir, sourceFile), req.Code);

        try
        {
            // 4. 编译（仅 cpp17；Python 解释型直接跑）
            if (req.Language == "cpp17")
            {
                var compileRes = await sandbox.RunAsync(new SandboxRequest(
                    Image: _cfg.DockerImageCpp,
                    Cmd: ["/bin/sh", "-c", "cd /work && g++ -O2 -std=c++17 main.cpp -o main"],
                    WorkDir: workDir,
                    InputFile: null,
                    TimeLimitMs: 30_000,            // 编译宽限
                    MemoryLimitMb: problem.MemoryLimit,
                    ReadOnlyWorkDir: false));       // 编译容器可写，产出 main
                if (compileRes.TimedOut || compileRes.ExitCode != 0)
                    return await FinishAsync(sub, "CE", 0,
                        compileError: (compileRes.Stderr + compileRes.Stdout).Trim());
            }

            // 5. 找测试点（*.in 文件名升序）
            var tcDir = Path.Combine(_cfg.TestcaseRoot, problemId.ToString());
            var inFiles = Directory.Exists(tcDir)
                ? Directory.GetFiles(tcDir, "*.in")
                    .OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f))).ToList()
                : new List<string>();
            if (inFiles.Count == 0)
                return await FinishAsync(sub, "RE", 0, compileError: "无测试点数据");

            // 6. 逐测试点运行沙箱
            var results = new List<TestcaseResult>();
            int acCount = 0;
            long maxTime = 0;
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
                if (res.TimedOut)               st = "TLE";
                else if (res.OomKilled)         st = "MLE";
                else if (res.ExitCode != 0)     st = "RE";
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

            // 7. 汇总：score = AC 点数占比，状态取最严重
            int score = results.Count == 0 ? 0 : acCount * 100 / results.Count;
            return await FinishAsync(sub, worst, score, results, (int)maxTime);
        }
        finally
        {
            // 清理工作目录（无论成败）
            try { Directory.Delete(workDir, true); }
            catch (Exception ex) { logger.LogWarning("清理工作目录失败 {dir}: {msg}", workDir, ex.Message); }
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

    // 优先级：AC=0 < WA=1 < RE=2 < MLE=3 < TLE=4（st 只来自已知五态，_ 仅编译器兜底）
    private static int Priority(string s) => s switch
    {
        "AC" => 0, "WA" => 1, "RE" => 2, "MLE" => 3, "TLE" => 4, _ => 0
    };

    // 实体 → 响应 DTO（Detail jsonb 按 Status 分支解析）
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
