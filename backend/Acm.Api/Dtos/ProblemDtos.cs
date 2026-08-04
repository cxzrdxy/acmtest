using System.ComponentModel.DataAnnotations;

namespace Acm.Api.Dtos;

public record ProblemCreate(
    [Required, RegularExpression(@"^P\d{3,5}$")] string Slug,   // P1000-P99999
    [Required, MaxLength(128)] string Title,
    [Required] string Description,
    string InputDesc = "",
    string OutputDesc = "",
    [Range(100, 30000)] int TimeLimit = 1000,
    [Range(16, 1024)] int MemoryLimit = 256,
    List<string>? Tags = null,
    [Range(1, 5)] short? Difficulty = null,
    string SampleInputs = "",
    string SampleOutputs = "");

// 部分更新：null 表示不修改该字段
public record ProblemUpdate(
    [MaxLength(128)] string? Title = null,
    string? Description = null,
    string? InputDesc = null,
    string? OutputDesc = null,
    [Range(100, 30000)] int? TimeLimit = null,
    [Range(16, 1024)] int? MemoryLimit = null,
    List<string>? Tags = null,
    [Range(1, 5)] short? Difficulty = null,
    string? SampleInputs = null,
    string? SampleOutputs = null);

public record ProblemRead(
    int Id, string Slug, string Title, string? Description,
    string? InputDesc, string? OutputDesc,
    int TimeLimit, int MemoryLimit,
    List<string> Tags, short? Difficulty,
    string SampleInputs, string SampleOutputs, int? AuthorId);

public record ProblemListResponse(List<ProblemRead> Items, int Total, int Page, int Size);
