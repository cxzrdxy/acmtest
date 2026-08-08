namespace Acm.Judge.Core.Judge;

// 单个测试点结果（Detail jsonb 数组元素；Api 的 SubmissionDtos.TestcaseResult 形状相同，二者独立）
public record TestcaseResult(
    int Id,
    string Status,
    int? TimeMs,
    int? MemoryKb = null);
