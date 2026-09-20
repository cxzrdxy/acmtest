namespace Acm.Judge.Core.Judge;

/// <summary>
/// 测试点根目录解析。
///
/// 为什么需要它：<see cref="SandboxRunner"/> 把测试点目录 / 输入文件直接当作 Docker 的 Bind 源，
/// 而 Docker **要求 Bind 源是宿主绝对路径** —— 所以配置里不能写相对路径，只能写绝对路径；
/// 但把某台开发机的绝对路径（<c>C:/Users/xxx/...</c>）提交进仓库，会让仓库不可移植、也会泄露开发机信息。
///
/// 折中做法：配置里写仓库内相对路径，**启动时**解析成绝对路径。解析顺序：
///   1. 配置是绝对路径 → 原样使用（容器里由 <c>Judge__TestcaseRoot=/data/testcases</c> 注入的就是这种）；
///   2. 配置是相对路径 → 相对**仓库根**解析；
///   3. 配置为空       → 默认 <c>&lt;仓库根&gt;/data/testcases</c>。
///
/// 仓库根 = 从 <see cref="AppContext.BaseDirectory"/> 向上找到第一个含 <c>docker-compose.yml</c> 的目录
/// （与测试自举 <c>EnvBootstrap.FindRepoRoot</c> 用同一套判定，保证 API / Worker / 测试三方看到同一个目录）。
/// 找不到仓库根时退化为 BaseDirectory，至少不会抛出。
/// </summary>
public static class TestcaseRootResolver
{
    /// <summary>把配置值解析成宿主绝对路径。</summary>
    public static string Resolve(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured))
            return Path.GetFullPath(configured);

        string root = FindRepoRoot() ?? AppContext.BaseDirectory;

        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(root, "data", "testcases")
            : Path.Combine(root, configured));
    }

    /// <summary>从运行目录向上找含 docker-compose.yml 的目录；找不到返回 null。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docker-compose.yml"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
