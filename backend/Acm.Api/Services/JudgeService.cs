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

    // 历史列表：强制当前用户 + 可选 problemId/status 筛选，最新在前（Id 降序），JOIN 题目名
    public async Task<SubmissionListResponse> ListAsync(
        int userId, int? problemId, string? status, int page, int size)
    {
        var q = db.Submissions.AsNoTracking().Where(s => s.UserId == userId);
        if (problemId.HasValue) q = q.Where(s => s.ProblemId == problemId);
        if (!string.IsNullOrEmpty(status)) q = q.Where(s => s.Status == status);

        var total = await q.CountAsync();
        var items = await q.OrderByDescending(s => s.Id)
            .Skip((page - 1) * size).Take(size)
            .Join(db.Problems, s => s.ProblemId, p => p.Id,
                (s, p) => new SubmissionListItem(
                    s.Id, s.ProblemId, p.Title, s.Language,
                    s.Status, s.Score, s.TimeMs, s.CreatedAt))
            .ToListAsync();
        return new SubmissionListResponse(items, total, page, size);
    }

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
            s.Status, s.Score, s.TimeMs, s.MemoryKb, detail, compileError, s.CreatedAt,
            s.Code);
    }
}
