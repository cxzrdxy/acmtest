using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Acm.Api.Dtos;

public record SubmissionCreate(
    [Required, RegularExpression(@"^(cpp17|python3)$")] string Language,
    [Required, MaxLength(65536)] string Code);

public record TestcaseResult(
    int Id,
    string Status,
    int? TimeMs,
    int? MemoryKb = null);

public record SubmissionRead(
    long Id,
    int UserId,
    int ProblemId,
    string Language,
    string Status,
    int Score,
    int? TimeMs,
    int? MemoryKb,
    List<TestcaseResult> Detail,   // 正常评测：逐点数组；CE：空列表
    string? CompileError,          // CE 时编译错误文本；其他 null
    DateTime CreatedAt,
    string? Code = null);          // 提交代码正文（详情页展示；列表接口不返回）

// 历史列表项：含题目名（JOIN Problems）；不含代码正文（列表页不需要，详情单查）
public record SubmissionListItem(
    long Id,
    int ProblemId,
    string ProblemTitle,
    string Language,
    string Status,
    int Score,
    int? TimeMs,
    DateTime CreatedAt);

public record SubmissionListResponse(List<SubmissionListItem> Items, int Total, int Page, int Size);
