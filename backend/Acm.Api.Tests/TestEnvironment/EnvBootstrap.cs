using System.Diagnostics;
using System.Net.Sockets;

namespace Acm.Api.Tests.TestEnvironment;

/// <summary>测试环境自举：注入默认连接串 + 幂等拉起 compose db/redis（已监听则跳过）。</summary>
public static class EnvBootstrap
{
    // 注入默认连接串：用户已显式设置则尊重；缺失时用 AGENTS.md 约定的本地默认值（5433/6380）
    public static void EnsureConnectionStrings()
    {
        if (Environment.GetEnvironmentVariable("ConnectionStrings__Default") is null)
            Environment.SetEnvironmentVariable("ConnectionStrings__Default",
                "Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm");
        if (Environment.GetEnvironmentVariable("ConnectionStrings__Redis") is null)
            Environment.SetEnvironmentVariable("ConnectionStrings__Redis", "localhost:6380");
    }

    /// <summary>确保 Postgres(5433)/Redis(6380) 可连；缺失则 docker compose up -d db redis 并等就绪。</summary>
    public static async Task EnsureServicesAsync()
    {
        if (PortOpen(5433) && PortOpen(6380) && DbHealthy()) return;   // 已在跑（开发环境），直接复用

        var root = FindRepoRoot();
        var psi = new ProcessStartInfo("docker", "compose up -d db redis")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi);
        if (p is null) throw new InvalidOperationException("无法启动 docker（Docker Desktop 是否在运行？）");
        await p.WaitForExitAsync();

        // 等就绪（60s）：端口可连 + db 容器 healthy（compose healthcheck 是 pg_isready，端口映射先于 PG ready）
        for (int i = 0; i < 60; i++)
        {
            if (PortOpen(5433) && PortOpen(6380) && DbHealthy()) return;
            await Task.Delay(1000);
        }
        throw new InvalidOperationException("compose db/redis 60s 内未就绪（Docker Desktop 是否在运行？）");
    }

    // db 容器健康检查：docker inspect 容器 State.Health.Status == healthy
    private static bool DbHealthy()
    {
        try
        {
            var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("compose");
            psi.ArgumentList.Add("ps");
            psi.ArgumentList.Add("-q");
            psi.ArgumentList.Add("db");
            using var p = Process.Start(psi);
            var id = p!.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            if (id.Length == 0) return false;

            var psi2 = new ProcessStartInfo("docker") { RedirectStandardOutput = true, UseShellExecute = false };
            psi2.ArgumentList.Add("inspect");
            psi2.ArgumentList.Add("--format");
            psi2.ArgumentList.Add("{{.State.Health.Status}}");
            psi2.ArgumentList.Add(id);
            using var p2 = Process.Start(psi2);
            var st = p2!.StandardOutput.ReadToEnd().Trim();
            p2.WaitForExit();
            return st == "healthy";
        }
        catch { return false; }
    }

    // 端口探测（1.5s 超时，快速判定服务是否已在跑）
    private static bool PortOpen(int port)
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", port).Wait(1500);
        }
        catch { return false; }
    }

    // 从当前目录向上找含 docker-compose.yml 的目录（测试输出目录在仓库内）
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docker-compose.yml"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未找到仓库根（docker-compose.yml）");
    }
}
