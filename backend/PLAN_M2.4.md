# M2.4 测试点管理 详细设计方案

> **所属里程碑**：M2 评测核心（`DESIGN_M2.md`）
> **任务边界**：M2.4 测试点 —— 上传/删除/列表接口 + 文件系统读写（总设计"三、测试点管理"一章）
> **前置**：M2.3 判题编排完成（JudgeService 已按 `data/testcases/{pid}/` 读取测试点）
> **本阶段不涉及**：前端（M2.5）、集成测试（M2.6）、zip 批量上传/下载（M3+，DESIGN 明确不做）

---

## 一、目标

为测试点提供 **HTTP 管理接口**：上传单个测试点（in + out）、列出题目测试点、删除测试点。评测只认文件系统（M2.3 已实现读取），本阶段补上"文件系统 ↔ 接口"的管理通道，避免手改文件。

## 二、范围

| 做 | 不做 |
|----|------|
| `TestcaseService`（列/存/删文件） | zip 批量上传/下载 |
| GET `/problems/{pid}/testcases`（列表+大小） | 自动生成测试数据 |
| POST `/problems/{pid}/testcases`（multipart 上传） | 前端页面（M2.5） |
| DELETE `/problems/{pid}/testcases/{n}` | |
| 安全校验（编号/路径穿越/大小） | |

## 三、设计决策及理由

| 决策 | 理由 |
|------|------|
| 新建 `TestcaseService`（独立文件） | 文件系统操作与 ProblemService（DB）职责分离，清晰 |
| 路由挂在 `ProblemsController` | DESIGN 3.3 指定：测试点是题目的子资源，`/problems/{pid}/testcases` |
| multipart/form-data 上传（n + inFile + outFile 三字段） | 一个请求传"编号+输入+输出"；接口简单，前端 `FormData` 直传 |
| in/out 都必传（不允许只传一个） | 测试点 = 成对数据，缺一半无法评测；避免半成品状态 |
| 覆盖语义：上传同名编号 = 更新 | 评测按编号找文件，同名覆盖最直观（改数据=重传） |
| 文件大小上限：in ≤ 10MB、out ≤ 10MB | 个人自用足够；防超大文件撑爆磁盘（数据目录在项目下） |
| 编号校验：`[Range(1, 100000)]` + 路由 `:int` | 防路径穿越（`../` 无法通过 int 绑定）；编号即文件名 |
| 路径用 `Path.Combine` + 编号白名单 | 防目录穿越：`n` 是 int 约束，不可能含 `..` |
| 文件扩展名强制 `.in`/`.out` | 与评测约定一致（`*.in` 扫描） |
| 列表返回编号 + 文件大小（不返回内容） | 数据可能大，列表只给元信息；内容不通过接口读（评测直接读文件） |
| 无测试点目录 → 空列表（不 404） | 题还没传过数据是正常状态；DELETE 不存在的编号 → 204（幂等） |

## 四、逐文件内容

### 4.1 `Services/TestcaseService.cs`（新增）

```csharp
using Microsoft.Extensions.Options;
using Acm.Api.Dtos;

namespace Acm.Api.Services;

/// <summary>测试点文件系统管理：列/存/删 data/testcases/{problemId}/ 下的 .in/.out 文件。</summary>
public class TestcaseService(IOptions<JudgeOptions> opt)
{
    private readonly JudgeOptions _cfg = opt.Value;
    private const long MaxFileSize = 10 * 1024 * 1024;   // 单文件 10MB 上限

    // 题目测试点目录：data/testcases/{pid}
    private string Dir(int pid) => Path.Combine(_cfg.TestcaseRoot, pid.ToString());

    // 列出全部测试点：编号 + in/out 大小（按编号升序）
    public List<TestcaseInfo> List(int pid)
    {
        var dir = Dir(pid);
        if (!Directory.Exists(dir)) return new();
        return Directory.GetFiles(dir, "*.in")
            .OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f)))
            .Select(f =>
            {
                var n = int.Parse(Path.GetFileNameWithoutExtension(f));
                var outFile = Path.Combine(dir, $"{n}.out");
                return new TestcaseInfo(n,
                    new FileInfo(f).Length,
                    File.Exists(outFile) ? new FileInfo(outFile).Length : 0);
            })
            .ToList();
    }

    // 保存（上传/覆盖）单个测试点
    public async Task SaveAsync(int pid, TestcaseUpload req, CancellationToken ct)
    {
        // 大小校验（IFormFile.Length 已有，双保险）
        if (req.InFile.Length > MaxFileSize || req.OutFile.Length > MaxFileSize)
            throw new ApiException(400, "测试点文件超过 10MB 上限");

        var dir = Dir(pid);
        Directory.CreateDirectory(dir);
        var inPath = Path.Combine(dir, $"{req.N}.in");
        var outPath = Path.Combine(dir, $"{req.N}.out");
        using (var fs = File.Create(inPath))
            await req.InFile.CopyToAsync(fs, ct);
        using (var fs = File.Create(outPath))
            await req.OutFile.CopyToAsync(fs, ct);
    }

    // 删除指定编号（in + out 一起删；不存在也 204 幂等）
    public void Delete(int pid, int n)
    {
        var dir = Dir(pid);
        if (!Directory.Exists(dir)) return;
        foreach (var f in new[] { Path.Combine(dir, $"{n}.in"), Path.Combine(dir, $"{n}.out") })
        {
            if (File.Exists(f)) File.Delete(f);
        }
    }
}
```

### 4.2 `Dtos/TestcaseDtos.cs`（新增）

```csharp
namespace Acm.Api.Dtos;

// 测试点列表元素：编号 + in/out 字节大小
public record TestcaseInfo(int N, long InSize, long OutSize);

public record TestcaseListResponse(List<TestcaseInfo> Items);
```

### 4.3 `Dtos/TestcaseUpload.cs`（上传请求，multipart 绑定）

```csharp
using System.ComponentModel.DataAnnotations;

namespace Acm.Api.Dtos;

// 上传测试点：编号 + in 文件 + out 文件（multipart/form-data）
public class TestcaseUpload
{
    [Range(1, 100000)] public int N { get; set; }     // 测试点编号
    [Required] public IFormFile InFile { get; set; } = null!;   // 输入文件
    [Required] public IFormFile OutFile { get; set; } = null!;  // 期望输出
}
```

> 注意：上传用 `class` + 属性（不是 record）——IFormFile 绑定需要可写属性；multipart 模型绑定支持属性形式。

### 4.4 `Controllers/ProblemsController.cs`（修改，追加 3 个接口）

```csharp
// 构造注入 TestcaseService（类里加参数）
public class ProblemsController(ProblemService svc, TestcaseService tc) : ControllerBase
{
    // 已有方法不变...

    // 列出测试点
    [HttpGet("{pid:int}/testcases")]
    public ActionResult<TestcaseListResponse> ListTestcases(int pid) =>
        new TestcaseListResponse(tc.List(pid));

    // 上传单个测试点（multipart）
    [HttpPost("{pid:int}/testcases")]
    public async Task<IActionResult> UploadTestcase(int pid, [FromForm] TestcaseUpload req, CancellationToken ct)
    {
        // 题必须存在（复用 ProblemService 校验）
        await svc.GetByIdAsync(pid);
        await tc.SaveAsync(pid, req, ct);
        return NoContent();
    }

    // 删除指定测试点
    [HttpDelete("{pid:int}/testcases/{n:int}")]
    public IActionResult DeleteTestcase(int pid, int n)
    {
        tc.Delete(pid, n);
        return NoContent();
    }
}
```

> `[FromForm]` 指示从 multipart 表单绑定；`CancellationToken ct` 由框架自动传入（请求取消时中止文件写入）。

### 4.5 `Program.cs`（修改，注册）

```csharp
builder.Services.AddScoped<TestcaseService>();
```

## 五、验证方式

### 5.1 编译

```powershell
dotnet build
```

### 5.2 API 黑盒（PowerShell + curl 或 Invoke-WebRequest -Form）

先登录拿 token，造题 P1001。

| 步骤 | 操作 | 断言 |
|------|------|------|
| ① 上传 | `curl -F "n=1" -F "inFile=@1.in" -F "outFile=@1.out"` POST `/problems/{pid}/testcases` | 204 |
| ② 上传 2 号 | 同上 n=2 | 204 |
| ③ 列表 | GET `/problems/{pid}/testcases` | 2 项：`[{"n":1,"inSize":..,"outSize":..},{"n":2,...}]` |
| ④ 覆盖 | 重传 n=1（内容改） | 204，列表 inSize 变 |
| ⑤ 删除 | DELETE `/problems/{pid}/testcases/1` | 204 |
| ⑥ 列表 | GET | 只剩 1 项 n=2 |
| ⑦ 文件系统一致 | `ls data/testcases/{pid}/` | 只有 `2.in`/`2.out` |
| ⑧ 非法编号 | 传 n=0 或 n=abc | 400 |
| ⑨ 只传 in | 缺 outFile | 400 |
| ⑩ 不存在题 | 上传到 pid=9999 | 404 |

### 5.3 与评测联动（可选，验证全链路）

- 上传测试点 → 提交 AC 代码 → 应 AC（证明接口写入的文件被评测正确读取）

### 5.4 回归

```powershell
$env:ConnectionStrings__Default="Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"
dotnet test
```

验收清单：
1. 上传/覆盖/删除/列表全部工作，与文件系统一致
2. 非法输入（编号/缺文件/超限/不存在题）正确报 4xx
3. 接口写入的测试点能被 M2.3 评测读取（联动验证）
4. `dotnet test` 全绿

## 六、风险与对策

| 风险 | 对策 |
|------|------|
| 路径穿越（n=../ 或 pid 注入） | `{pid:int}`/`{n:int}` 路由约束强制整数，无法注入路径 |
| 超大文件撑爆磁盘 | 10MB 上限（IFormFile.Length 校验 + 双保险） |
| 上传一半中断留残文件 | `CancellationToken` 传递，请求取消中止写入；残留文件下次覆盖即可（个人自用可接受） |
| 覆盖评测中的测试点 | 同步评测单用户，评测中不可能同时改文件（可接受） |
| 测试点编号与评测扫描不一致 | 保存即写 `{n}.in/.out`，评测按文件名扫描——天然一致 |
| Windows 文件名非法字符 | 编号 int 约束，无非法字符可能 |

## 七、交付物与后续衔接

| 交付物 | 衔接 |
|--------|------|
| TestcaseService + 3 接口 | M2.5 前端题目详情页"测试点管理"入口 |
| 文件系统一致性 | M2.3 评测直接可用（已验证） |

> M2.4 完成后系统"可传数据、可评测"。M2.5 前端接入提交区 + 结果展示，M2.6 集成测试收尾。
