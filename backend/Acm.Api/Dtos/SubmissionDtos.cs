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
    DateTime CreatedAt);
