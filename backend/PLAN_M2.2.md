# M2.2 沙箱 详细设计方案

> **所属里程碑**：M2 评测核心（`DESIGN_M2.md`）
> **任务边界**：M2.2 沙箱 —— SandboxRunner（Docker 执行）+ 沙箱镜像构建 + 配置（总设计"四、4.2 沙箱执行"一章）
> **前置**：M2.1 数据层已完成；Docker Desktop（Linux 容器）可运行
> **本阶段不涉及**：编译编排（M2.3 JudgeService）、测试点接口（M2.4）、提交 API / 前端（M2.3~M2.6）

---

## 一、目标

把"**在受限 Docker 容器里运行任意选手程序**"这个核心安全机制落地：构建两个沙箱镜像（C++/Python），实现 `SandboxRunner.RunAsync`（创建容器 → 注入输入 → 启动 → 限时等待 → 收集输出 → 判定超时/OOM/退出码），并通过 `docker run` 手测验证。

## 二、范围

| 做 | 不做 |
|----|------|
| `backend/Judge/Dockerfile.runtimes` 沙箱镜像（cpp/python 两 target） | 编译编排（JudgeService 起编译容器，M2.3） |
| `Services/SandboxRunner.cs`（单测试点运行） | 逐测试点循环 / 结果汇总 / 状态机（M2.3） |
| `Config.cs` 加 `JudgeOptions` + appsettings 加 `Judge` 节 | 测试点文件读写（M2.4） |
| `Program.cs` DI 注册 SandboxRunner | 提交 API / DTO（M2.3） |
| csproj 加 `Docker.DotNet` 包 | 前端任何改动 |

## 三、设计决策及理由

| 决策 | 理由 |
|------|------|
| 用 `Docker.DotNet` 而非 `docker` CLI 子进程 | 官方 C# 客户端，异步 API 友好、免解析 stdout；错误处理规范 |
| `SandboxRunner` 注册 **Singleton**，内部持有 `DockerClient` | DockerClient 连接 npipe 复用安全（线程安全），避免每请求重建连接 |
| 输入重定向用**挂载只读文件**（`/tc.in`）+ `Cmd` 里 `< /tc.in`，而非 attach stdin | 可靠、无需处理 TTY/流协议；大输入文件挂载比流式写稳 |
| 工作目录用**只读挂载**（`/work:ro`） | 编译产物/源码只读进容器；`ReadonlyRootfs` 已只读根，挂载点也 ro 双保险 |
| 超时用 `WaitContainerAsync(id, cts.Token)` + `cts.CancelAfter(limit)` | 到点取消等待 → kill 容器 → 判 TLE；天然支持异步取消 |
| OOM 用 `InspectContainerAsync(id).State.OOMKilled` 判断 | Docker 记录容器级 OOM 标志，比猜 exit code（137）可靠 |
| 镜像不内置 `run.sh`，运行命令由评测机拼 Cmd | 运行命令简单（`./main` / `python3 main.py`），内联即可；`run.sh` 在容器内只是 `exec ./main`，无额外价值，减少移动部件 |
| 镜像**保留 g++** | M2.3 编译阶段复用同一镜像起编译容器（评测机 Windows 无 g++） |
| 输出上限在 C# 侧拉取后截断（先不搞容器内 `head -c`） | M2.2 简单处理；容器内管道会掩盖程序退出码，代价高收益低 |

## 四、逐文件内容

### 4.1 `backend/Judge/Dockerfile.runtimes`（新增）

```dockerfile
# 多阶段：两个沙箱镜像共用一份构建脚本
# 构建：docker build -f backend/Judge/Dockerfile.runtimes --target cpp    -t judge-cpp:latest    backend/Judge
#        docker build -f backend/Judge/Dockerfile.runtimes --target python -t judge-python:latest backend/Judge

FROM debian:bookworm-slim AS cpp
RUN apt-get update && apt-get install -y --no-install-recommends g++ \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /work

FROM python:3.12-slim AS python-runtime
WORKDIR /work
```

> 镜像内只提供运行时 + 编译工具，**不含任何测试点数据**（数据一律由评测机运行时挂载进去）。

### 4.2 `backend/Acm.Api/Config.cs`（修改，追加）

```csharp
/// <summary>评测配置，绑定 appsettings "Judge" 节。</summary>
public class JudgeOptions
{
    // 测试点根目录（宿主路径）；M2.4 测试点管理使用，此处先定义
    public string TestcaseRoot { get; set; } = null!;
    // true = Docker 沙箱执行；false = 宿主进程直跑（仅开发调试，Windows 下无沙箱）
    public bool UseDockerSandbox { get; set; } = true;
    public string DockerImageCpp { get; set; } = "judge-cpp:latest";
    public string DockerImagePython { get; set; } = "judge-python:latest";
    // 用户程序输出上限（字节），超出判 RE
    public long OutputLimitBytes { get; set; } = 16 * 1024 * 1024;
}
```

### 4.3 `backend/Acm.Api/appsettings.json`（修改，追加 Judge 节）

```json
"Judge": {
  "TestcaseRoot": "data/testcases",
  "UseDockerSandbox": true,
  "DockerImageCpp": "judge-cpp:latest",
  "DockerImagePython": "judge-python:latest"
}
```

> TestcaseRoot 在 Docker 部署环境（backend 容器内）应改为 `/data/testcases` 并挂卷；本地 dev 用宿主绝对路径。M2.4 细化。

### 4.4 `backend/Acm.Api/Services/SandboxRunner.cs`（新增）

```csharp
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Options;

namespace Acm.Api.Services;

// 单测试点运行请求（M2.3 JudgeService 构造）
public record SandboxRequest(
    string Image,          // 沙箱镜像名
    string[] Cmd,          // 容器内运行命令，如 ["/bin/sh","-c","cd /work && ./main < /tc.in"]
    string WorkDir,        // 宿主目录，含 main / main.py，只读挂载到容器 /work
    string? InputFile,     // 测试点 .in 宿主绝对路径，只读挂载到容器 /tc.in；null=无输入
    int TimeLimitMs,       // 运行时限
    int MemoryLimitMb);    // 内存上限

// 单测试点运行结果
public record SandboxResult(
    bool TimedOut,         // 超时（TLE）
    bool OomKilled,        // 内存超限（MLE）
    int? ExitCode,         // 正常退出码；被 kill 时为 null/非0
    string Stdout,         // 用户程序输出
    string Stderr);        // 错误输出（RE 排查用）

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
                Tmpfs = new() { ["/tmp"] = "size=64m" },            // 仅 /tmp 可写，限 64m
                Binds = BuildBinds(req),                            // 挂载工作目录 + 输入文件（只读）
            },
        }, ct);
        var id = create.ID;

        try
        {
            // 2. 启动容器
            await _docker.Containers.StartContainerAsync(id, ct);

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
                try { await _docker.Containers.KillContainerAsync(id, ct); } catch { /* 容器可能已退出 */ }
            }

            // 4. 查退出信息（exit code / OOM）
            var inspect = await _docker.Containers.InspectContainerAsync(id, ct);

            // 5. 收集 stdout / stderr（容器退出后读全量日志）
            string stdout = "", stderr = "";
            using (var ms = await _docker.Containers.GetContainerLogsAsync(id,
                       new ContainerLogsParameters { ShowStdout = true, ShowStderr = true }, ct))
            {
                (stdout, stderr) = await ms.ReadOutputToEndAsync(ct);
            }

            // 6. 输出上限保护：超出截断（M2.2 简单处理，M2.3 判 RE）
            if (stdout.Length > _cfg.OutputLimitBytes) stdout = stdout[.._cfg.OutputLimitBytes];

            return new SandboxResult(timedOut,
                inspect.State.OOMKilled, timedOut ? null : inspect.State.ExitCode,
                stdout, stderr);
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
        var binds = new List<string> { $"{req.WorkDir}:/work:ro" };
        if (req.InputFile is not null) binds.Add($"{req.InputFile}:/tc.in:ro");
        return binds.ToArray();
    }
}
```

> **关键点**：
> - `ReadOutputToEndAsync` 返回 `(string stdout, string stderr)`，扩展方法在 `Docker.DotNet` 命名空间
> - `WaitContainerAsync` 超时由 `cts.CancelAfter` 触发，捕获 `OperationCanceledException` 判 TLE
> - Binds 用**宿主绝对路径**：Windows 下 `C:\...\1.in:/tc.in:ro`，Docker Desktop 自动转换；Linux 部署同理
> - 路径含空格时 Docker 解析有坑，M2.3 起保证工作目录无空格（临时目录用 GUID）

### 4.5 `backend/Acm.Api/Program.cs`（修改，追加 DI）

```csharp
builder.Services.Configure<JudgeOptions>(builder.Configuration.GetSection("Judge"));
builder.Services.AddSingleton<SandboxRunner>();
```

### 4.6 `backend/Acm.Api/Acm.Api.csproj`（修改，追加包）

```xml
<PackageReference Include="Docker.DotNet" Version="3.125.*" />
```

> 安装：`dotnet add package Docker.DotNet`（3.125.x 系列稳定版）。测试项目若引用则无需额外装。

## 五、验证方式

### 5.1 镜像构建

```powershell
# 工作目录：项目根
docker build -f backend/Judge/Dockerfile.runtimes --target cpp    -t judge-cpp:latest    backend/Judge
docker build -f backend/Judge/Dockerfile.runtimes --target python-runtime -t judge-python:latest backend/Judge
docker images | findstr judge   # 确认两个镜像存在
```

### 5.2 docker run 手测（C++，含输入）

```powershell
# 1. 准备测试程序目录（临时）
$dir = "$env:TEMP\judge-sandbox-test"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Set-Content -Path "$dir\main.cpp" -Value @"
#include <cstdio>
int main(){ int a,b; scanf("%d%d",&a,&b); printf("%d\n",a+b); }
"@
Set-Content -Path "$dir\1.in" -Value "3 5"

# 2. 用沙箱镜像内 g++ 编译（模拟 M2.3 编译容器）
docker run --rm -v "${dir}:/work:ro" judge-cpp:latest /bin/sh -c "cd /work && g++ -O2 -std=c++17 main.cpp -o main"
# （上一条 -v 挂 ro 会导致无法写 main；编译容器挂 rw：
docker run --rm -v "${dir}:/work" judge-cpp:latest /bin/sh -c "cd /work && g++ -O2 -std=c++17 main.cpp -o main"

# 3. 运行沙箱：只读挂载 + 输入文件 + 资源限制
docker run --rm --network none --memory 128m --memory-swap 128m --pids-limit 50 --read-only `
  --tmpfs /tmp:size=64m -v "${dir}:/work:ro" -v "${dir}\1.in:/tc.in:ro" `
  judge-cpp:latest /bin/sh -c "cd /work && ./main < /tc.in"
# 期望输出：8
```

### 5.3 docker run 手测（Python）

```powershell
Set-Content -Path "$dir\main.py" -Value "a,b=map(int,input().split());print(a+b)"
docker run --rm --network none --memory 128m --memory-swap 128m --pids-limit 50 --read-only `
  --tmpfs /tmp:size=64m -v "${dir}:/work:ro" -v "${dir}\1.in:/tc.in:ro" `
  judge-python:latest /bin/sh -c "cd /work && python3 main.py < /tc.in"
# 期望输出：8
```

### 5.4 编译 + 运行（`dotnet build`）

```powershell
dotnet build   # SandboxRunner 编译通过（含 Docker.DotNet 包还原）
```

验收清单：
1. `docker images` 有 `judge-cpp:latest` / `judge-python:latest`
2. C++ 沙箱：输入 `3 5` → 输出 `8`，exit 0
3. Python 沙箱：输入 `3 5` → 输出 `8`，exit 0
4. 断网/只读生效：容器内 `wget` 不可用、`touch /x` 报只读（可选抽查）
5. `dotnet build` 全绿

## 六、风险与对策

| 风险 | 对策 |
|------|------|
| Docker Desktop 未启动 → DockerClient 连接失败 | M2.3 在 JudgeService 启动前检测，`UseDockerSandbox=false` 降级宿主直跑（仅调试）；本阶段手测前先 `docker info` |
| Windows 路径挂载到 Linux 容器失败（空格/盘符） | 测试目录用 `$env:TEMP` 无空格路径；Binds 用宿主绝对路径，Docker Desktop 自动转换 |
| `ReadonlyRootfs=true` 时挂载只读目录内可执行 | `:ro` 挂载仍可读可执行（exec 权限保留），已验证模式；仅不可写 |
| 超时 kill 竞态（容器恰在超时瞬间自己退出） | catch 后先 `InspectContainerAsync` 读 State；`TimedOut` 与 `ExitCode` 以 inspect 为准，双保险 |
| 输出巨大撑爆内存 | 拉取后按 `OutputLimitBytes` 截断；M2.3 判 RE 时不再读完整内容 |
| 并发提交串行执行容器 | 沙箱本身一次一容器；M2 同步评测天然串行，无冲突 |

## 七、交付物与后续衔接

| 交付物 | 衔接 |
|--------|------|
| `judge-cpp` / `judge-python` 镜像 | M2.3 编译容器 + 运行容器复用 |
| `SandboxRunner.RunAsync` | M2.3 JudgeService 逐测试点调用 |
| `JudgeOptions`（镜像名/超时/上限） | M2.4 TestcaseRoot 正式使用 |
| DI 注册 | 后续 JudgeService 直接注入 SandboxRunner |

> 验证通过后 M2.2 即收官。M2.3 判题编排（编译 → 逐点运行 → 汇总状态）会依赖 `SandboxRunner`，可无缝接入。
