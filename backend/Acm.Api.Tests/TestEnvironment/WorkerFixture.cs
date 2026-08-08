using System.Diagnostics;
using System.Text;
using Xunit;

namespace Acm.Api.Tests.TestEnvironment;

/// <summary>共享 Worker 子进程的 collection 定义：xunit 要求 ICollectionFixture 标注在 collection 定义类上。</summary>
[CollectionDefinition(WorkerFixture.Collection)]
public class EnvCollection : ICollectionFixture<WorkerFixture>
{
}

/// <summary>共享 Worker 子进程（CollectionFixture 单例）：起进程 → 日志就绪探测 → Dispose 杀进程。</summary>
public sealed class WorkerFixture : IDisposable
{
    public const string Collection = "env";   // 与 [Collection] 标注对应

    private readonly StringBuilder _log = new();
    private Process? _proc;

    public WorkerFixture()
    {
        // xunit CollectionFixture 构造同步：这里只注入默认连接串（进程级，TestAppFactory 与子进程共用）
        EnvBootstrap.EnsureConnectionStrings();
    }

    /// <summary>启动环境（幂等）：compose 服务 + Worker 子进程。由各测试类 InitializeAsync 调用。</summary>
    public async Task StartAsync()
    {
        await EnvBootstrap.EnsureServicesAsync();   // 幂等拉起 db/redis
        if (_proc is not null) return;              // 已启动

        // Worker 项目自身输出目录（依赖 dll + appsettings.json 齐全；Exe 项目 ProjectReference 不把依赖复制到测试输出）
        // 测试输出 backend/Acm.Api.Tests/bin/Debug/net8.0 → 向上 4 级到 backend/ → Acm.JudgeWorker/bin/Debug/net8.0
        var workerDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Acm.JudgeWorker", "bin", "Debug", "net8.0"));
        var dll = Path.Combine(workerDir, "Acm.JudgeWorker.dll");
        if (!File.Exists(dll))
            throw new InvalidOperationException($"Worker dll 不存在：{dll}");

        var psi = new ProcessStartInfo("dotnet", dll)
        {
            WorkingDirectory = workerDir,   // appsettings.json 同目录（Worker 按 BaseDirectory 找）
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        _proc = Process.Start(psi);
        if (_proc is null) throw new InvalidOperationException("Worker 进程启动失败");
        _proc.OutputDataReceived += (_, e) => AppendLog(e.Data);
        _proc.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();

        // 就绪探测：stdout 出现 "开始消费"（QueueConsumer 启动日志），500ms × 30 = 15s 上限
        for (int i = 0; i < 30; i++)
        {
            if (_proc.HasExited) break;
            if (LogContains("开始消费")) return;
            await Task.Delay(500);
        }
        throw new InvalidOperationException($"Worker 15s 未就绪，日志：\n{_log}");
    }

    private void AppendLog(string? line)
    {
        if (line is not null) lock (_log) _log.AppendLine(line);
    }

    private bool LogContains(string text)
    {
        lock (_log) return _log.ToString().Contains(text);
    }

    public void Dispose()
    {
        if (_proc is not null && !_proc.HasExited)
        {
            try { _proc.Kill(entireProcessTree: true); _proc.WaitForExit(5000); } catch { }
        }
        _proc?.Dispose();
    }
}
