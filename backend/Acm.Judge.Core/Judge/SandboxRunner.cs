using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Options;

namespace Acm.Judge.Core.Judge;

// 单测试点运行请求（M2.3 JudgeService 构造）
public record SandboxRequest(
    string Image,          // 沙箱镜像名
    string[] Cmd,          // 容器内运行命令，如 ["/bin/sh","-c","cd /work && ./main < /tc.in"]
    string WorkDir,        // 宿主目录，含 main / main.py，只读挂载到容器 /work
    string? InputFile,     // 测试点 .in 宿主绝对路径，只读挂载到容器 /tc.in；null=无输入
    int TimeLimitMs,       // 运行时限
    int MemoryLimitMb,     // 内存上限
    bool ReadOnlyWorkDir = true);    // true=运行容器只读；false=编译容器可写

// 单测试点运行结果
public record SandboxResult(
    bool TimedOut,         // 超时（TLE）
    bool OomKilled,        // 内存超限（MLE）
    int? ExitCode,         // 正常退出码；被 kill 时为 null/非0
    string Stdout,         // 用户程序输出
    string Stderr,         // 错误输出（RE 排查用）
    long WallTimeMs);      // 容器运行耗时（FinishedAt - StartedAt）

/// <summary>Docker 沙箱执行器：创建受限容器运行选手程序，收集输出，判定超时/OOM。</summary>
public class SandboxRunner(IOptions<JudgeOptions> opt)
{
    private readonly JudgeOptions _cfg = opt.Value;
    private readonly DockerClient _docker = new DockerClientConfiguration().CreateClient(); // Windows 默认 npipe 连接 Docker Desktop

    public async Task<SandboxResult> RunAsync(SandboxRequest req, CancellationToken ct = default)
    {
        // 1. 创建容器（安全限制全在 HostConfig）
        var create = await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = req.Image,
            Cmd = req.Cmd,
            HostConfig = new HostConfig
            {
                Memory = req.MemoryLimitMb * 1024L * 1024,          // 题目内存上限
                MemorySwap = req.MemoryLimitMb * 1024L * 1024,      // 禁止 swap
                PidsLimit = 50,                                     // 防 fork bomb
                NetworkMode = "none",                               // 断网
                ReadonlyRootfs = true,                              // 根文件系统只读
                Tmpfs = new Dictionary<string, string> { ["/tmp"] = "size=64m" }, // 仅 /tmp 可写，限 64m
                Binds = BuildBinds(req),                            // 挂载工作目录 + 输入文件（只读）
            },
        }, ct);
        var id = create.ID;

        try
        {
            // 2. 启动容器
            await _docker.Containers.StartContainerAsync(id, new ContainerStartParameters(), ct);

            // 3. 限时等待退出：超时取消 -> kill -> 判 TLE
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(req.TimeLimitMs);
            bool timedOut = false;
            try
            {
                await _docker.Containers.WaitContainerAsync(id, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                timedOut = true;
                try { await _docker.Containers.KillContainerAsync(id, new ContainerKillParameters(), ct); } catch { /* 容器可能已退出 */ }
            }

            // 4. 查退出信息（exit code / OOM）+ 计算耗时（StartedAt/FinishedAt 是 ISO 字符串需解析）
            var inspect = await _docker.Containers.InspectContainerAsync(id, ct);
            var wallMs = (long)(DateTime.Parse(inspect.State.FinishedAt) - DateTime.Parse(inspect.State.StartedAt)).TotalMilliseconds;

            // 5. 收集 stdout / stderr（容器退出后读全量日志；tty=false 用普通模式）
            string stdout = "", stderr = "";
            using (var ms = await _docker.Containers.GetContainerLogsAsync(id, false,
                       new ContainerLogsParameters { ShowStdout = true, ShowStderr = true }, ct))
            {
                (stdout, stderr) = await ms.ReadOutputToEndAsync(ct);
            }
            // 6. 输出上限保护：超出截断（M2.2 简单处理，M2.3 判 RE）
            if (stdout.Length > _cfg.OutputLimitBytes) stdout = stdout[..(int)_cfg.OutputLimitBytes];

            return new SandboxResult(timedOut,
                inspect.State.OOMKilled, timedOut ? null : (int?)inspect.State.ExitCode,
                stdout, stderr, wallMs);
        }
        finally
        {
            // 7. 清理：无论成败都删容器（force 忽略 404）
            try { await _docker.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }, ct); }
            catch (DockerApiException) { /* 已被删除 */ }
        }
    }

    // 挂载列表：工作目录 -> /work:ro；输入文件 -> /tc.in:ro
    private static string[] BuildBinds(SandboxRequest req)
    {
        var mode = req.ReadOnlyWorkDir ? "ro" : "rw";
        var binds = new List<string> { $"{req.WorkDir}:/work:{mode}" };
        if (req.InputFile is not null) binds.Add($"{req.InputFile}:/tc.in:ro");
        return binds.ToArray();
    }
}
