# M3.4 测试自动化与总验收 详细设计方案

> **所属里程碑**：M3 异步评测与提交历史（`DESIGN_M3.md`）
> **任务边界**：M3.4 收尾 —— 测试环境自动化（自动起 db/redis/Worker）+ 总验收（`dotnet test` 全绿 + Playwright 全流程 UI）
> **前置**：M3.1-M3.3 已收官（队列/Worker/轮询/历史页）✅
> **本阶段不涉及**：SignalR 推送（M4）、TestContainers 全容器化（决策：不用，见下）、多 Worker 扩展

---

## 一、目标

把"测试前手动起 Worker + 手动起 db/redis + 手动设环境变量"三件事全部消灭：跑 `dotnet test` 一条命令，fixture 自动完成 ①幂等拉起 compose db/redis ②注入默认连接串（用户已设则尊重）③启动 Worker 子进程并等它就绪，测完自动杀 Worker。最后做总验收：冷环境一条命令全绿 + Playwright 用户全流程。

## 二、范围

| 做 | 不做 |
|----|------|
| 测试 csproj：引用 Acm.JudgeWorker + 复制其 appsettings.json | TestContainers 起 Postgres/Redis 容器（收益有限，见决策表） |
| 新增 `EnvBootstrap`（幂等确保 compose db/redis 运行） | 容器级就绪外推（compose healthcheck 已承担） |
| 新增 `WorkerFixture`（CollectionFixture：起 Worker 子进程 + 就绪探测 + 清理） | 多 Worker 并发消费（单实例够用） |
| 两个测试类接入 Collection（SubmitJudgeTests / SubmissionHistoryTests） | 改 WebProgram 的自动迁移（测试沿用已迁移的 compose db） |
| AGENTS.md 更新：测试前置简化 + 常用命令 | 预编译测试产物加速（fixture 内首次 `dotnet build` 兜底即可） |
| 总验收：冷环境 `dotnet test` 全绿 + Playwright 全流程 | M4 功能 |

## 三、设计决策及理由

| 决策 | 理由 |
|------|------|
| **不用 TestContainers**（Postgres/Redis 仍用 compose） | 本机 compose db/redis 已托管且数据持久、已迁移；TestContainer 每次全新容器需处理迁移 + 连接串动态化，收益仅"免敲一条 compose 命令"；**Windows 沙箱约束（npipe 挂不进 Linux 容器）决定 Worker 必须宿主进程跑**，TestContainer 解决不了核心痛点 |
| **fixture 自动 `docker compose up -d db redis`**（幂等：TCP 探测 5433/6380，已监听则跳过） | 测试不依赖任何手动前置；compose 文件在仓库根，Docker Desktop 运行中即可；"已起则跳过"保证本地日常开发起的环境不被干扰 |
| **Worker 子进程由 CollectionFixture 管理**（Process.Start Worker dll + 继承 env + 日志关键字就绪探测 + Dispose Kill） | xunit CollectionFixture 单例（collection 内共享），两个测试类共用同一 Worker；worker dll 冷启动 ~1-2s，就绪探测 15s 超时兜底 |
| 测试 csproj **ProjectReference Acm.JudgeWorker** + 复制 appsettings.json | dotnet test 构建 solution 时 Worker 一并构建，其 dll 复制到测试输出目录；`Path.Combine(AppContext.BaseDirectory, "Acm.JudgeWorker.dll")` 直接起，无 `dotnet run` 编译开销 |
| Worker 就绪探测用 **stdout 日志关键字**（重定向标准输出/错误，轮询出现"Worker 启动，开始消费 queue:judge"） | Worker 无监听端口可探测；日志是唯一可靠信号；超时抛异常并附捕获日志定位启动失败原因 |
| **env 注入默认值仅当未设置**（`ConnectionStrings__Default`→5433 / `ConnectionStrings__Redis`→6380） | 一条命令跑测试的体验；同时尊重用户显式覆盖（AGENTS.md 连接串约定）；`Environment.SetEnvironmentVariable` 进程级，TestAppFactory 与 Worker 子进程同时生效 |
| `Judge__TestcaseRoot` 不额外注入 | Worker appsettings.json 与 Web 的默认一致（两侧都是仓库内相对路径 `data/testcases`，启动时由 TestcaseRootResolver 解析为绝对路径）；用户用 env 覆盖时子进程自动继承 |
| 现有 `TestAppFactory` 保持 IClassFixture 每类一个实例 | 只加 Collection 标注（`[Collection("env")]` + `ICollectionFixture<WorkerFixture>`），改动最小；并行已禁用，无共享污染 |
| 总验收顺序：**先停光环境**（进程 + compose）→ 冷启动 `dotnet test` 验证自动化 → 再起 Web/前端做 Playwright 全流程 | 冷环境验证最能暴露前置依赖遗漏；UI 全流程验证用户真实路径 |

## 四、逐文件内容

### 4.1 `backend/Acm.Api.Tests/Acm.Api.Tests.csproj`（修改）

```xml
<ItemGroup>
  <!-- 引 Worker：其 dll 复制到测试输出目录，fixture 直接 Process.Start 启动 -->
  <ProjectReference Include="..\Acm.JudgeWorker\Acm.JudgeWorker.csproj" />
</ItemGroup>
<ItemGroup>
  <!-- Worker 在输出目录读 appsettings.json（Judge 配置节），随测试输出一并复制 -->
  <None Include="..\Acm.JudgeWorker\appsettings.json" CopyToOutputDirectory="PreserveNewest" Link="Worker.appsettings.json" />
</ItemGroup>
```

> **坑**：复制后文件名是 `Worker.appsettings.json`，Worker 启动路径不同会读不到——故复制时 `Link` 改名会导致 Worker 找不着；**正确做法是 Link 保持 `appsettings.json` 原名**，但会与测试输出目录里（若有）的同名文件冲突（测试输出无 appsettings.json，Web 站点的配置由 TestAppFactory 从项目源读，不会复制过来）。**决策：Link 保持原名 `appsettings.json`**；若冲突，fixture 改为在启动 Worker 前显式设置 `Judge__*` env（AppContext.BaseDirectory 不依赖 json）。

### 4.2 `backend/Acm.Api.Tests/TestEnvironment/EnvBootstrap.cs`（新增：compose 服务幂等自举）

```csharp
namespace Acm.Api.Tests.TestEnvironment;

/// <summary>测试环境自举：注入默认连接串 + 幂等拉起 compose db/redis（已监听则跳过）。</summary>
public static class EnvBootstrap
{
    public static void EnsureConnectionStrings()
    {
        // 用户已显式设置则尊重；缺失时注入 AGENTS.md 约定的本地默认值（5433/6380）
        if (Environment.GetEnvironmentVariable("ConnectionStrings__Default") is null)
            Environment.SetEnvironmentVariable("ConnectionStrings__Default",
                "Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm");
        if (Environment.GetEnvironmentVariable("ConnectionStrings__Redis") is null)
            Environment.SetEnvironmentVariable("ConnectionStrings__Redis", "localhost:6380");
    }

    /// <summary>确保 Postgres(5433)/Redis(6380) 可连；缺失则 docker compose up -d db redis 并等 healthy。</summary>
    public static async Task EnsureServicesAsync()
    {
        if (PortOpen(5433) && PortOpen(6380)) return;   // 已运行，直接复用

        // 从仓库根跑 compose（TestAppFactory 输出目录往上找 backend/.. 或直接用当前目录？测试运行目录不定，
        // 用环境变量/探测：优先当前目录，其次上溯找 docker-compose.yml）
        var root = FindRepoRoot();
        var psi = new ProcessStartInfo("docker", "compose up -d db redis")
        { WorkingDirectory = root, UseShellExecute = false };
        using var p = Process.Start(psi);
        await p!.WaitForExitAsync();

        // 等 db healthy（compose healthcheck 已定义 pg_isready）
        for (int i = 0; i < 60; i++)
        {
            if (PortOpen(5433) && PortOpen(6380)) return;
            await Task.Delay(1000);
        }
        throw new InvalidOperationException("compose db/redis 60s 内未就绪（Docker Desktop 是否在运行？）");
    }

    private static bool PortOpen(int port)
    {
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
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
```

> 探测注意：docker compose 起服务后 Postgres 立即监听 5433 但未 ready（健康检查中）——端口探测可能误判，故再加**轮询 docker inspect healthy**（或直接依赖测试首条提交的 60×500ms 轮询兜底；稳妥起见轮询 60s）。

### 4.3 `backend/Acm.Api.Tests/TestEnvironment/WorkerFixture.cs`（新增：Worker 子进程生命周期）

```csharp
namespace Acm.Api.Tests.TestEnvironment;

/// <summary>共享 Worker 子进程（CollectionFixture 单例）：起进程 → 日志就绪探测 → Dispose 杀进程。</summary>
public sealed class WorkerFixture : IDisposable
{
    public const string Collection = "env";   // 与 [Collection] 标注对应
    private readonly StringBuilder _log = new();
    private Process? _proc;

    public WorkerFixture()
    {
        EnvBootstrap.EnsureConnectionStrings();   // 1. 注入默认连接串（TestAppFactory 与子进程共用）
        // 注意：EnsureServicesAsync 需 async，构造函数不行 —— 改为 lazy/手动 InitAsync（见 4.5 接入方式）
    }

    // 由接入层（CollectionFixture 无法 async 构造）在测试前调用
    public async Task StartAsync()
    {
        await EnvBootstrap.EnsureServicesAsync();  // 2. 幂等拉起 db/redis
        if (_proc is not null) return;             // 已起

        var dll = Path.Combine(AppContext.BaseDirectory, "Acm.JudgeWorker.dll");
        if (!File.Exists(dll)) throw new InvalidOperationException(
            $"Worker dll 不存在：{dll}（检查 csproj ProjectReference）");

        var psi = new ProcessStartInfo("dotnet", dll)
        {
            WorkingDirectory = AppContext.BaseDirectory,   // 输出目录（appsettings.json 同目录）
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        _proc = Process.Start(psi);
        _proc!.OutputDataReceived += (_, e) => AppendLog(e.Data);
        _proc.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();

        // 就绪探测：stdout 出现 "Worker 启动，开始消费 queue:judge"
        for (int i = 0; i < 30; i++)   // 15s（500ms 间隔）
        {
            if (_proc.HasExited) break;
            if (_log.ToString().Contains("开始消费")) return;
            await Task.Delay(500);
        }
        throw new InvalidOperationException($"Worker 15s 未就绪，日志：\n{_log}");
    }

    private void AppendLog(string? line)
    {
        if (line is not null) { lock (_log) _log.AppendLine(line); }
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
```

> **xunit 异步构造坑**：`ICollectionFixture` 构造是同步的，无法 `await`。接入方案：测试类继承一个 `IAsyncLifetime` 的基类或让每个测试类在 `InitializeAsync` 里调 `_worker.StartAsync()`（幂等，重复调用安全）。两个测试类各调一次，第二次直接 return。

### 4.4 测试类接入（SubmitJudgeTests / SubmissionHistoryTests 修改）

```csharp
// 两个测试类同样标注 + 注入
[Collection(WorkerFixture.Collection)]
public class SubmissionHistoryTests(
    TestAppFactory factory, WorkerFixture worker)
    : IClassFixture<TestAppFactory>, ICollectionFixture<WorkerFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await worker.StartAsync();          // 幂等：起 compose 服务 + Worker 子进程
        await _factory.CleanDbAsync();
        // ... 原有造数逻辑不变
    }
}
```

> 注意：`ICollectionFixture<WorkerFixture>` 在**类上声明**（与 Collection 标注配套），xunit 保证 collection 内所有测试类共享同一 fixture 实例；`WorkerFixture` 构造同步（仅注入 env），`StartAsync` 由每个类的 `InitializeAsync` 幂等调用。

### 4.5 AGENTS.md 更新

- 常用命令"集成测试"段：删掉"Worker 进程在跑"前置与手动起 Worker 步骤；改为"一条命令，fixture 自动起 compose db/redis + Worker 子进程，测完自动杀"
- 注意事项"测试连真实数据库"段：更新为自动化描述（保留"依赖 Docker Desktop 运行中"）
- M3.4 状态标记完成

## 五、验证方式

### 5.1 冷环境总验收（自动化核心验证）

```powershell
# 1. 停光一切（进程 + compose）
Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force   # 注意会杀本会话起的 Web/Worker
docker compose down
# 2. 新终端，不带任何环境变量，一条命令
dotnet test
```

断言：compose 自动拉起 db/redis → Worker 自动启动 → 15 个测试全绿 → Worker 进程被清理（`Get-CimInstance ... JudgeWorker` 查无）。

### 5.2 日常回归

```powershell
# 环境已在跑时（普通开发日），dotnet test 直接复用现有 db/redis + 起 Worker
dotnet test
```

### 5.3 Playwright 全流程 UI 验收（总验收第二段）

前置：冷环境验收后，起 Web(8000) + 前端(5173)（db/redis/Worker 已由测试或手动起好）。

| 场景 | 操作 | 断言 |
|------|------|------|
| ① 注册登录 | 注册新用户 → 登录 | 进题目列表 |
| ② 建题 + 造测试点 | UI 建题（P9001，slug 校验）+ API/文件系统写 2 个测试点 | 列表出现新题 |
| ③ 提交 AC | 详情页填 AC 代码提交 | 排队中 → 自动"通过" 100/100 + chips |
| ④ 提交 WA/CE | 分别提交错误代码 | "答案错误" / "编译错误" + compileError |
| ⑤ 历史页 | 顶部"提交记录" | 4 条最新在前、徽章正确 |
| ⑥ 详情页 | 点 AC 行 | 代码只读 + 结果完整 + 题目链接可跳 |
| ⑦ 筛选 | 状态筛"编译错误" | 只剩 CE 一条 |

### 5.4 验收清单

1. 冷环境 `dotnet test` 一条命令全绿（自动化生效）
2. 测完 Worker 子进程被清理，无残留
3. Playwright 7 场景全过
4. `npm run build` / `dotnet build` 全绿（回归）

---

## 六、风险与对策

| 风险 | 对策 |
|------|------|
| Exe 项目 ProjectReference 不复制 dll 到测试输出 | 构建后验证；不复制则改方案：fixture 定位 Worker 源目录 `dotnet build` 后起 dll（首测多 ~10s，可接受） |
| appsettings.json 复制改名/Link 冲突 | Link 保持原名 `appsettings.json`；冲突时 fixture 启动前显式注入 `Judge__*` env（Worker 已 AddEnvironmentVariables） |
| `docker compose up -d` 时 Docker Desktop 未运行 | 启动命令立即失败 → 捕获输出抛清晰错误；端口探测先行可提前暴露 |
| Postgres 刚起端口可连但未 ready | 轮询 healthy（docker inspect）+ 测试 60×500ms 轮询双兜底 |
| Worker 冷启动慢/启动失败 | 日志关键字就绪探测 15s 超时 + 附完整日志抛异常 |
| 测试类构造顺序（fixture 构造同步，StartAsync 需 async） | InitializeAsync 幂等调用；xunit 保证 collection fixture 先于测试类构造 |
| 杀 Worker 留残留 | `Kill(entireProcessTree: true)` + WaitForExit；fixture Dispose 必执行（xunit 保证） |
| 总验收停进程误伤用户环境 | 验收前明确告知；日常回归（5.2）不杀进程 |

---

## 七、交付物与后续衔接

| 交付物 | 衔接 |
|--------|------|
| `EnvBootstrap` + `WorkerFixture` | M4 起任何测试类 `[Collection(WorkerFixture.Collection)]` 即自动具备全环境 |
| 测试 csproj Worker 引用 | Worker 改动后 dotnet test 自动重建（子进程起新产物） |
| AGENTS.md 简化 | 新会话新人（AI）跑测试零前置 |
| 总验收记录 | M3 收官，进入 M4 候选（SignalR/SPJ/排名/统计） |

> M3.4 完成后：`dotnet test` 一条命令全自动，M3 里程碑收官。
