namespace Acm.Api.Models;

/// <summary>
/// 提交记录表。一次提交一行；评测结果 Detail 以 jsonb 整存逐测试点数组。
/// </summary>
public class Submission
{
    // 提交 ID（long：评测系统提交量大，不用 int）
    public long Id { get; set; }

    // 提交者，FK -> users.id
    public int UserId { get; set; }

    // 题目，FK -> problems.id
    public int ProblemId { get; set; }

    // 语言：cpp17 | python3
    public string Language { get; set; } = null!;

    // 源码（text，长度不限）
    public string Code { get; set; } = null!;

    // 代码字节数（提交时后端按 UTF-8 计算）
    public int CodeLength { get; set; }

    // 评测状态：PENDING/JUDGING/AC/WA/TLE/MLE/RE/CE
    public string Status { get; set; } = "PENDING";

    // 分数 0-100（AC 测试点占比）
    public int Score { get; set; }

    // 所有测试点最大耗时（ms）
    public int? TimeMs { get; set; }

    // 所有测试点最大内存（KB）
    public int? MemoryKb { get; set; }

    // 逐测试点结果 JSON 数组（jsonb），[{"id":1,"status":"AC","timeMs":12,"memoryKb":1024}, ...]
    public string? Detail { get; set; }

    // 提交时间，DB 默认 now()
    public DateTime CreatedAt { get; set; }
}
