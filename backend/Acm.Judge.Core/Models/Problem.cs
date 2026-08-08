namespace Acm.Judge.Core.Models;

/// <summary>
/// 竞赛题目表。
/// 存储题面元信息与样例；正式测试点数据（input/answer）较大，
/// 留至 M2 以文件形式存储于 data/testcases/，表内只记录其元数据。
/// </summary>
public class Problem
{
    // 主键，自增
    public int Id { get; set; }

    // URL 友好标识，如 P1000；全局唯一，用于前端路由与对外展示
    public string Slug { get; set; } = null!;

    // 题目标题，最长 128 字符
    public string Title { get; set; } = null!;

    // 题面正文，Markdown 格式，长度不限
    public string? Description { get; set; }

    // 输入格式说明（Markdown）
    public string? InputDesc { get; set; }

    // 输出格式说明（Markdown）
    public string? OutputDesc { get; set; }

    // 时间限制，单位毫秒（ms），默认 1000ms
    public int TimeLimit { get; set; } = 1000;

    // 内存限制，单位兆字节（MB），默认 256MB
    public int MemoryLimit { get; set; } = 256;

    // 知识点标签数组；Npgsql 原生映射 text[]，用于列表筛选
    public List<string> Tags { get; set; } = new();

    // 难度等级 1~5，可空表示尚未评级
    public short? Difficulty { get; set; }

    // 样例输入，多个样例以 "---" 分隔的纯字符串（M1 简化存储）
    public string SampleInputs { get; set; } = "";

    // 样例输出，与 SampleInputs 一一对应
    public string SampleOutputs { get; set; } = "";

    // 题目创建者，外键关联 users.id；自用单用户场景下值恒为同一用户
    public int? AuthorId { get; set; }

    // 通过人数（评测写回时：该用户首次 AC 才 +1，去重 UserId）
    public int AcCount { get; set; }

    // 提交总数（每次评测 +1）
    public int SubmitCount { get; set; }

    // 创建时间，DB 默认 now()
    public DateTime CreatedAt { get; set; }

    // 更新时间
    public DateTime UpdatedAt { get; set; }
}
