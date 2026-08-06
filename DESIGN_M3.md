# M3 异步评测与提交历史 详细设计方案

> **目标产出**：提交不阻塞 API（秒回 PENDING），独立评测进程消费 Redis 队列后台评测，前端轮询结果；新增提交历史页
> **技术栈**：ASP.NET Core 8 + Vue 3 + PostgreSQL + Docker 沙箱 + **Redis 7**（队列）
> **前置**：M1/M2 已收官（鉴权 + CRUD + 评测核心 + 测试点管理 + 前端提交区）

---

## 一、M3 范围与验收标准

### 1.1 范围

| 类别 | 包含 | 不包含（留至 M4+） |
|------|------|------|
| 评测模式 | **异步队列**：提交秒回 PENDING，独立 Worker 进程后台评测 | 多 Worker 水平扩展（Redis 天然支持，M3 单实例够用） |
| 队列 | **Redis**（BRPOP，推模式 0 延迟） | 任务持久化/重试机制（M3 简单：失败标记 RE） |
| 结果通知 | **前端轮询**（1.5s 间隔 GET 结果） | SignalR/WebSocket 实时推送 |
| 提交历史 | 提交历史页（按用户/题目筛选 + 分页） | 排名、统计图表 |
| 语言 | 维持 C++17 + Python3（M3.5 可选扩 Java/Go） | SPJ 特判、子任务/部分分 |

### 1.2 验收标准

- POST 提交 → **立即返回**（<1s）PENDING + submissionId
- 独立 Worker 进程从 Redis 取任务评测 → 写库终态；前端轮询见 AC/WA/TLE/CE + 完整结果
- **Web 崩溃/重启不影响 Worker 评测**；Worker 崩溃重启后继续消费（任务在 Redis/DB）
- 提交历史页：最新在前，含状态/语言/耗时/分数，可进详情
- `dotnet test` 全绿（测试适配异步轮询等待）
- Playwright：提交 → 排队中 → 结果；历史页 → 详情

### 1.3 与原方案（DESIGN.md 2.2）的差异

| 项 | 原方案 | 本方案 |
|----|--------|--------|
| 队列 | Redis BRPOP | **Redis BRPOP**（一致） |
| Worker | 独立评测机进程 | **独立进程 Acm.JudgeWorker**（一致） |
| 推送 | SignalR 逐点推送 | **前端轮询**（M3 简化，SignalR 留 M4） |
| Worker 数量 | 评测机集群 | 单实例（Redis 支持扩展） |
| 语言 | 5 种 | C++17 + Python3（M3.5 按需加） |

> **为何独立进程 + Redis**：评测是重负载且可能崩溃（沙箱跑用户代码），必须与 Web 隔离——Web 挂不牵连评测，Worker 挂不阻塞提交。独立进程间无法共享内存，Redis 是进程间队列的事实标准（推模式 0 延迟、天然支持多 Worker）。这是 OJ 系统的标准架构。

---

## 二、异步评测架构

```
前端提交 ──► POST /submissions ──► Web API：写库(PENDING) + LPUSH queue:judge {subId} ──► 秒回 {id, status:PENDING}
                                                                  │
                                                        Redis List queue:judge
                                                                  │ BRPOP（即时弹出）
                                                                  ▼
                          Acm.JudgeWorker（独立进程）
                          │ 取任务 → 连 Postgres 读 submission → 复用评测逻辑
                          ▼
                    编译容器 → 逐测试点沙箱 → 汇总 → 写回 DB 终态 + 计数
                                                                  ▼
前端轮询：GET /submissions/{sid}（1.5s 间隔）──► 终态 → 展示结果
```

**进程拓扑**：
```
┌─────────────────┐     ┌──────────┐     ┌──────────────────────┐
│  Web API (8000)  │◄───►│  Redis 7 │◄───►│ JudgeWorker (独立)   │
└────────┬────────┘     └──────────┘     └──────────┬───────────┘
         │ 共享 Postgres                             │ 只写库
         └──────────────► acmtest-db ◄──────────────┘
```

**关键点**：
- Web 只做"校验 + 写库 PENDING + LPUSH"，不碰沙箱 → 秒回
- Worker 独立进程：连 Redis 取任务 + 连 Postgres 评测写回
- 评测逻辑从 JudgeService 抽出为**可共享的类库**，Web/Worker 都能引用

---

## 三、设计决策及理由

| 决策 | 理由 |
|------|------|
| Redis List + BRPOP（推模式） | 任务入队**即时**弹出，0 轮询延迟；List 天然 FIFO；BRPOP 阻塞式等任务（省 CPU） |
| 独立进程 Acm.JudgeWorker | 评测与 Web 隔离：Web 重启不影响评测、评测崩溃不拖垮 Web；行业标准 |
| Worker 单实例（并发 1） | 个人自用串行评测足够；Redis 支持将来加 Worker 实例 |
| 评测逻辑抽成共享类库 `Acm.Judge.Core` | Web/Worker 复用同一套评测代码，避免两份实现漂移 |
| Worker 直接连 Postgres 写库 | 评测结果由 Worker 落库，Web 只读展示；天然解耦 |
| 前端轮询 1.5s（终态或 60 次停） | 个人自用够用；SignalR 复杂度高留 M4 |
| 提交接口返回 `{id, status:"PENDING"}` | 语义变化：不再阻塞至出结果，前端必须轮询 |
| 失败兜底：Worker 评测异常 → 标记 RE + 日志 | 不阻塞队列继续消费；Redis 任务已弹出不可回滚（简化：失败即终态） |
| 历史页 API：GET /submissions?userId&problemId&page&size | 沿用分页约定；索引 (UserId,Id)/(ProblemId,Id) 已建 |
| docker-compose 加 redis + judge-worker 服务 | 一键拉起全栈；worker 崩溃 compose 自动重启（restart: unless-stopped） |

---

## 四、逐文件内容

### 4.1 项目结构调整

```
backend/
├── Acm.Api/                    # Web API（提交入口 + 查询 + 历史）
├── Acm.Judge.Core/             # 新增：共享评测核心（实体无关，纯评测逻辑）
│   ├── JudgeEngine.cs          # 评测主体（编译/逐点/汇总/计数）
│   └── JudgeEngineOptions.cs   # 评测配置（镜像名/测试点根/输出上限）
├── Acm.JudgeWorker/            # 新增：独立 Worker 进程
│   ├── Program.cs              # 主循环：BRPOP → 调 JudgeEngine → 写库
│   └── appsettings.json        # Redis 连接 + Judge 配置 + DB 连接串
└── Acm.Api.Tests/              # 集成测试
```

> **共享方式**：Acm.Judge.Core 是类库，Acm.Api 与 Acm.JudgeWorker 都引用。评测逻辑（M2 的 JudgeService 主体 + SandboxRunner + OutputComparer）移入 Core；Web 的 JudgeService 变成"入口 + 入队"，Worker 用 Core 执行。

### 4.2 `Acm.Judge.Core/`（从 M2 JudgeService 迁移）

```csharp
// JudgeEngine.cs —— 评测主体（M2 JudgeService.SubmitAsync 的编译/逐点/汇总/计数部分）
public class JudgeEngine(
    AppDbContext db,               // 共享 EF（Worker 也连同一 Postgres）
    SandboxRunner sandbox,
    IOptions<JudgeOptions> opt,
    ILogger<JudgeEngine> logger)
{
    // 按 submissionId 评测（Worker 调用）
    public async Task<SubmissionRead> ExecuteAsync(long sid, CancellationToken ct)
    {
        var sub = await db.Submissions.SingleAsync(s => s.Id == sid);
        sub.Status = "JUDGING";
        await db.SaveChangesAsync();
        // ... M2 的编译容器/逐测试点/汇总/计数逻辑原样搬入 ...
        return ToRead(sub);
    }
}
```

> 依赖（EF 的 AppDbContext、SandboxRunner、JudgeOptions）一并移入 Core，Worker 与 Web 共用。

### 4.3 `Acm.JudgeWorker/Program.cs`（新增：独立进程主循环）

```csharp
using StackExchange.Redis;
using Acm.Judge.Core;

// 构建宿主（读 appsettings：Redis 连接 + Postgres 连接 + Judge 配置）
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDbContextPool<AppDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.Configure<JudgeOptions>(builder.Configuration.GetSection("Judge"));
builder.Services.AddSingleton<SandboxRunner>();
builder.Services.AddScoped<JudgeEngine>();
builder.Services.AddHostedService<QueueConsumer>();   // 主循环
var host = builder.Build();
host.Run();

// QueueConsumer.cs —— BRPOP 消费
public class QueueConsumer(
    IConnectionMultiplexer redis,
    IServiceScopeFactory scopeFactory,
    ILogger<QueueConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = redis.GetDatabase();
        while (!stoppingToken.IsCancellationRequested)
        {
            // 阻塞式取任务：有任务立即返回，无任务挂起（省 CPU）
            var val = await db.ListRightPopAsync("queue:judge", stoppingToken);
            if (val.IsNull) continue;   // 空队列：BRPOP 会阻塞，理论走不到
            var sid = (long)val;

            using var scope = scopeFactory.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<JudgeEngine>();
            try
            {
                await engine.ExecuteAsync(sid, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "评测 {sid} 失败", sid);
                // 兜底标记 RE（数据库里该提交）
                var dbc = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var sub = await dbc.Submissions.SingleOrDefaultAsync(s => s.Id == sid);
                if (sub is not null && (sub.Status is "PENDING" or "JUDGING"))
                {
                    sub.Status = "RE";
                    await dbc.SaveChangesAsync();
                }
            }
        }
    }
}
```

> 注意：`ListRightPopAsync`（BRPOP 语义）阻塞等待；Worker 崩溃时已弹出的任务丢失（简化接受），但**未弹出的任务留在 Redis**，Worker 重启即续跑。

### 4.4 `Acm.Api/Services/SubmissionQueue.cs`（新增：Redis 入队封装）

```csharp
using StackExchange.Redis;

namespace Acm.Api.Services;

/// <summary>评测队列（Redis List，推模式即时可见）。</summary>
public class SubmissionQueue(IConnectionMultiplexer redis)
{
    private readonly IDatabase _db = redis.GetDatabase();
    private const string Key = "queue:judge";

    public Task EnqueueAsync(long submissionId) => _db.ListLeftPushAsync(Key, submissionId);
}
```

### 4.5 `Acm.Api/Services/JudgeService.cs`（修改：提交入口不再评测）

```csharp
public async Task<SubmissionRead> SubmitAsync(int problemId, int userId, SubmissionCreate req)
{
    var problem = await db.Problems.SingleOrDefaultAsync(p => p.Id == problemId)
        ?? throw new ApiException(404, "题目不存在");

    var sub = new Submission
    {
        UserId = userId, ProblemId = problemId, Language = req.Language,
        Code = req.Code, CodeLength = Encoding.UTF8.GetByteCount(req.Code),
        Status = "PENDING",
    };
    db.Submissions.Add(sub);
    await db.SaveChangesAsync();
    await _queue.EnqueueAsync(sub.Id);   // 入 Redis 队列，交给 Worker
    return ToRead(sub);                   // 秒回 PENDING
}
```

> JudgeService 的评测主体移除（已移入 Core），只留入口 + 查询 + ToRead。计数更新逻辑在 Core 的 JudgeEngine 里（Worker 侧写库）。

### 4.6 历史页 API（`SubmissionsController.cs` 追加）

| 方法 | 路由 | 功能 |
|------|------|------|
| GET | `/api/v1/submissions?userId=&problemId=&page=&size=` | 提交历史（分页，最新在前） |

```csharp
[HttpGet]
public async Task<ActionResult<SubmissionListResponse>> List(
    [FromQuery] int? userId, [FromQuery] int? problemId,
    [FromQuery, Range(1, int.MaxValue)] int page = 1,
    [FromQuery, Range(1, 100)] int size = 20)
{
    var q = db.Submissions.AsNoTracking();
    if (userId.HasValue) q = q.Where(s => s.UserId == userId);
    if (problemId.HasValue) q = q.Where(s => s.ProblemId == problemId);
    var total = await q.CountAsync();
    var items = await q.OrderByDescending(s => s.Id)
        .Skip((page - 1) * size).Take(size)
        .Select(s => new SubmissionListItem(s.Id, s.ProblemId, s.Language, s.Status,
            s.Score, s.TimeMs, s.CreatedAt)).ToListAsync();
    return new SubmissionListResponse(items, total, page, size);
}
```

### 4.7 `Program.cs`（Web：注册 Redis + 队列）

```csharp
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis")));
builder.Services.AddSingleton<SubmissionQueue>();
```

`appsettings.json`（Web + Worker 各配）：

```json
"ConnectionStrings": {
  "Default": "Host=db;Port=5432;Database=acm;Username=acm;Password=acm",
  "Redis": "redis:6379"
}
```

### 4.8 `docker-compose.yml`（加 redis + judge-worker）

```yaml
services:
  db: { ...现有... }
  redis:
    image: redis:7-alpine
    ports: ["6379:6379"]
    restart: unless-stopped
  backend:
    build: ./backend
    depends_on: [db, redis]
    environment:
      ConnectionStrings__Default: Host=db;Port=5432;...
      ConnectionStrings__Redis: redis:6379
    ports: ["8000:8000"]
  judge-worker:
    build: ./backend            # 同 Dockerfile 但不同入口（见 4.9）
    command: ["dotnet", "Acm.JudgeWorker.dll"]
    depends_on: [db, redis]
    restart: unless-stopped     # 崩溃自动重启
    volumes: ["./data:/data"]   # 测试点挂载
```

### 4.9 `backend/Dockerfile`（修改：多阶段，双入口）

```dockerfile
# Web 入口（现状不变）
FROM ... AS web
ENTRYPOINT ["dotnet", "Acm.Api.dll"]

# Worker 入口（同基础镜像，不同 DLL）
FROM ... AS worker
ENTRYPOINT ["dotnet", "Acm.JudgeWorker.dll"]
```

> 本地开发：Worker 用 `dotnet run --project backend/Acm.JudgeWorker` 单独起。

### 4.10 前端

- `ProblemDetail.vue`：提交后轮询（同前方案 4.8，PENDING → 1.5s 轮询 → 终态展示）
- `SubmissionHistory.vue`（新页面）+ 路由 + 导航链接
- `api/index.js`：submissionApi.list 追加
- 状态徽章/结果区样式复用现有 st-* 类

---

## 五、验证方式

### 5.1 单元/集成（dotnet test 适配）

M2 的 SubmitJudgeTests 改为**轮询等待**（提交不再同步返回终态）：

```csharp
var sub = await _client.PostAsJsonAsync(...).ReadFromJsonAsync<SubmissionRead>();
Assert.Equal("PENDING", sub.Status);
SubmissionRead? result = null;
for (int i = 0; i < 60; i++)
{
    await Task.Delay(500);
    result = await _client.GetFromJsonAsync<SubmissionRead>($"/api/v1/submissions/{sub.Id}");
    if (result.Status is "AC" or "WA" or "TLE" or "CE" or "RE") break;
}
Assert.Equal("AC", result!.Status);
```

> **注意**：测试环境需要 Redis 运行 + Worker 进程在跑（否则提交永远 PENDING）。TestAppFactory 无法托管独立进程——测试前先起 Worker，或用 TestContainer 方案（M3.4 细化）。

### 5.2 API 黑盒

| 用例 | 断言 |
|------|------|
| 提交 AC 代码 | ①POST 秒回 PENDING（<1s）②轮询 2-4s 内变 AC + detail |
| 提交死循环 | 轮询 → TLE |
| 历史列表 | GET /submissions?page=1 返回全部（最新在前） |
| Worker 崩溃恢复 | 杀 Worker → 提交 → 重启 Worker → 续跑出结果 |

### 5.3 Playwright

| 场景 | 断言 |
|------|------|
| 提交 → 排队中 → 自动刷新结果 | PENDING → AC 徽章 |
| 历史页 | 列表 + 徽章 → 点击进详情 |

### 5.4 回归

```powershell
$env:ConnectionStrings__Default="Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"
dotnet test
```

验收清单：
1. 提交秒回 PENDING，Worker 独立评测，前端轮询得结果
2. Web 重启不影响 Worker（任务在 Redis/DB）
3. Worker 崩溃 → compose 自动重启续跑
4. 历史页列表 + 详情 + 筛选
5. `dotnet test` 全绿 + Playwright 全流程

---

## 六、风险与对策

| 风险 | 对策 |
|------|------|
| 测试环境要 Redis + Worker 进程 | 集成测试前置：compose 起 redis + 手动起 Worker；或测试内嵌 Worker 宿主（M3.4 细化选型） |
| Worker 崩溃丢**已弹出**任务 | 简化接受（失败标记 RE）；未弹出的留 Redis 续跑。M4 可加"执行中标记 + 启动重扫" |
| Web 与 Worker 两套 EF 模型漂移 | 共用 Acm.Api.Data（Core 引用），单一模型源 |
| 评测逻辑两份实现漂移 | 抽 Acm.Judge.Core 单一实现，Web/Worker 都引用 |
| Redis 连接失败 | Web 提交时 Enqueue 失败 → 500（任务未入队，前端可重试）；compose 依赖保证启动顺序 |
| 前端轮询无限 | 60 次上限（90s）+ 终态即停 |
| Worker 与 Web 抢测试点文件 | 只 Worker 读测试点（Web 不再评测），无竞争 |

---

## 七、里程碑

| 阶段 | 内容 | 验证 |
|------|------|------|
| M3.1 队列与 Worker | Redis 服务 + SubmissionQueue + Acm.JudgeWorker 骨架 + 评测逻辑抽 Core | 提交秒回 PENDING，Worker 后台出结果 |
| M3.2 前端轮询 | ProblemDetail 提交后轮询 + 状态流转 | Playwright 提交→排队中→结果 |
| M3.3 历史页 | 列表 API + SubmissionHistory 页 + 导航 | 历史列表/详情/筛选 |
| M3.4 收尾 | 测试适配（Redis+Worker 前置）+ 总验收 | dotnet test + UI 全绿 |
| M3.5（可选） | 语言扩展 Java/Go（镜像 target + 编译命令） | 按需再做 |

> 完成后进入 M4 候选：SignalR 实时推送 / SPJ 特判 / 子任务 / 排名统计 / Monaco 高亮。
