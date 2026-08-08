using Microsoft.Extensions.Options;
using Acm.Judge.Core.Judge;
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
