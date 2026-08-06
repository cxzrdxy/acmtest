# M2.1 数据层 详细设计方案

> **所属里程碑**：M2 评测核心（`DESIGN_M2.md`）
> **任务边界**：M2.1 数据层 —— Submission 实体 + 迁移 + Problem 计数（总设计中"二、数据库模型"一章）
> **前置**：M1 已收官；Postgres（5433）运行中；现有迁移 `InitUsersAndProblems`
> **本阶段不涉及**：评测逻辑 / 沙箱 / 测试点接口 / 提交 API / 前端 —— 均留 M2.2~M2.6

---

## 一、目标

把 M2 需要的**持久化底座**落地：新增 `submissions` 表（提交记录），`problems` 表加两个统计列（AC 数 / 提交数），并通过 EF 迁移同步到真实 Postgres。为 M2.3 评测写回、M2.5 前端展示提供数据承载。

## 二、范围

| 做 | 不做 |
|----|------|
| `Models/Submission.cs` 实体（新增） | 评测/判定逻辑（M2.3 JudgeService） |
| `Problem` 加 `AcCount`/`SubmitCount` | 计数更新业务（评测写回时才动，M2.3） |
| `AppDbContext` 加 submissions 表配置 | 提交 API / DTO（M2.3 起） |
| EF 迁移 `AddSubmissionsAndCounters` | 前端任何改动 |
| 测试基建同步（CleanDbAsync 含新表） | 提交/评测集成测试（M2.6 统一补） |

## 三、设计决策及理由

| 决策 | 理由 |
|------|------|
| `Submission.Id` 用 `long`（bigint） | 评测系统提交量大、长期积累；M1 的 users/problems 是 `int`，不迁移它们（改类型要动已有表数据，收益低） |
| `Code` 用 `text`（`HasColumnType("text")`） | 源码长度不可预期，默认 varchar(4000) 会截断长代码 |
| `Detail` 用 `string?` + `jsonb` | 逐测试点结果是 JSON 数组，jsonb 可结构化查询/扩展，避免解析 text 字符串 |
| `Status` 用 `string` + `HasMaxLength(16)` | 状态有限集合（PENDING/JUDGING/AC/WA/TLE/MLE/RE/CE），字符串简单直观；不建枚举+转换器（自用简化） |
| `Score`/`TimeMs`/`MemoryKb` 默认值由 C# 侧兜底 | 插入时是 PENDING 初始态，评测写回才填真实值 |
| 复合索引 `(UserId, Id)` 与 `(ProblemId, Id)` | 两个高频查询：按用户查历史、按题查提交，`WHERE + ORDER BY Id` 一步走索引（见 AGENTS.md 关于 Id 作排序键的讨论） |
| 外键 `UserId`/`ProblemId` 非空 | 提交必属于某用户某题，不允许孤儿数据 |
| `TestcaseResult` 结构不落表 | M2 用 jsonb 整存 Detail，不拆表（个人自用扁平结构，无子任务） |

## 四、逐文件内容

### 4.1 `backend/Acm.Api/Models/Submission.cs`（新增）

```csharp
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
```

> 对应 `DESIGN_M2.md` 2.1，字段一一对应，无删改。

### 4.2 `backend/Acm.Api/Models/Problem.cs`（修改）

在 `SampleOutputs` 之后、`AuthorId` 之前（或末尾 M2 注释处）追加两个属性，替换行 55 的占位注释：

```csharp
    // 通过人数（评测写回时：该用户首次 AC 才 +1，去重 UserId）
    public int AcCount { get; set; }

    // 提交总数（每次评测 +1）
    public int SubmitCount { get; set; }
```

> 普通 int，由 EF 约定自动映射 integer 列，**无需**在 AppDbContext 追加 Fluent 配置（无长度/唯一/默认值特殊需求，默认 0）。

### 4.3 `backend/Acm.Api/Data/AppDbContext.cs`（修改）

新增 `DbSet`（文件顶部第 8-9 行旁）：

```csharp
public DbSet<Submission> Submissions => Set<Submission>();//submissions 表入口
```

`OnModelCreating` 中 `b.Entity<Problem>` 块之后追加：

```csharp
b.Entity<Submission>(e =>
{
    e.ToTable("submissions");
    e.Property(s => s.Code).HasColumnType("text");          // 源码长，text 不限长
    e.Property(s => s.Detail).HasColumnType("jsonb");       // 逐测试点结果，JSON 结构化存
    e.Property(s => s.Status).HasMaxLength(16);         // 状态枚举字符串
    e.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
    e.HasOne<User>().WithMany().HasForeignKey(s => s.UserId);
    e.HasOne<Problem>().WithMany().HasForeignKey(s => s.ProblemId);
    // 索引：按用户查提交历史 / 按题查提交，WHERE + ORDER BY Id 一步走索引
    e.HasIndex(s => new { s.UserId, s.Id });
    e.HasIndex(s => new { s.ProblemId, s.Id });
});
```

### 4.4 迁移（生成，不手写）

```bash
dotnet ef migrations add AddSubmissionsAndCounters -o Data/Migrations
```

预期产物：
- `AddSubmissionsAndCounters.cs`：CreateTable submissions（含 PK_bigint、FK、复合索引）+ `problems` 加 `AcCount`/`SubmitCount`（integer, not null, 默认 0）
- `AddSubmissionsAndCounters.Designer.cs` + `AppDbContextModelSnapshot.cs` 自动更新

### 4.5 `backend/Acm.Api.Tests/TestAppFactory.cs`（修改）

`CleanDbAsync` 的 TRUNCATE 列表补 `submissions`：

```csharp
foreach (var tbl in new[] { "users", "problems", "submissions" })
```

> 现状 `TRUNCATE users CASCADE` 已能级联清空 submissions，显式列出是**防御性**声明：将来直接往 submissions 插测试数据时也保证隔离，不依赖级联隐式行为。M2.1 无接口，此改动只是为后续测试铺路。

## 五、验证方式

编译 + 数据库双层：

```bash
# 1. 生成迁移（工作目录 backend/Acm.Api）
dotnet ef migrations add AddSubmissionsAndCounters -o Data/Migrations

# 2. 编译
dotnet build

# 3. 应用到真实 Postgres（5433）
dotnet ef database update --connection "Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"

# 4. 查表结构确认
psql -h localhost -p 5433 -U acm -d acm -c "\d submissions"
```

验收清单：
1. 迁移生成成功，Up/Down 可逆（Down 删表 + 回滚两列）
2. `\d submissions`：bigint PK、text code、jsonb detail、varchar(16) status、两个复合索引、两个 FK
3. `\d problems`：多出 `ac_count` / `submit_count`（integer not null default 0）
4. 旧数据无损：problems 已有行 AcCount=0、SubmitCount=0（迁移填默认值）
5. `dotnet test` 全绿（M1 回归，CleanDbAsync 改动不破坏现有用例）

## 六、风险与对策

| 风险 | 对策 |
|------|------|
| `long` 主键在 Npgsql 映射意外变 int | 迁移生成后核对 `\d submissions`，PK 应为 bigint（`NpgsqlValueGenerationStrategy.IdentityByDefaultColumn` 照旧） |
| jsonb 映射 string 报类型错误 | Npgsql 8 原生支持 `string ↔ jsonb`；若迁移生成 varchar，检查 AppDbContext 配置是否生效 |
| 迁移命名冲突 / snapshot 不一致 | 在 M1 迁移基础上追加新迁移（不修改旧迁移文件）；生成前 `dotnet build` 保证模型无编译错 |
| 迁移应用到真实库报外键失败 | submissions 全空表，FK 建立无风险；若失败先 `dotnet ef migrations remove` 排查 |
| 测试 TRUNCATE 顺序/外键限制 | 保持现有 CASCADE 语义，submissions 加在列表即可，无顺序依赖 |

## 七、交付物与后续衔接

| 交付物 | 衔接 |
|--------|------|
| `Submission.cs` 实体 | M2.3 JudgeService 写库/读库 |
| `problems.ac_count/submit_count` | M2.3 评测写回时更新计数 |
| `submissions` 表 + 索引 | M2.3 提交 API、M2.5 提交历史页 |
| CleanDbAsync 更新 | M2.6 集成测试（AC/WA/CE 用例） |

> M2.1 完成后进入 M2.2 沙箱（SandboxRunner + 镜像构建），与本阶段无代码依赖，可并行准备。
