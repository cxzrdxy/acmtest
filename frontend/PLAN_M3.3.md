# M3.3 提交历史页 详细设计方案

> **所属里程碑**：M3 异步评测与提交历史（`DESIGN_M3.md`）
> **任务边界**：M3.3 历史页 —— 后端列表 API + 前端提交历史页/详情页 + 导航（总设计"四、4.6 历史页 API + 4.10 前端"）
> **前置**：M3.1 已落地（异步评测 + 轮询接口）+ M3.2 已落地（前端提交区异步轮询）✅
> **本阶段不涉及**：排名/统计（M4）、SignalR 推送（M4）、多用户权限体系（个人自用无角色）

---

## 一、目标

新增提交历史：`GET /api/v1/submissions` 列表接口（只看自己的提交，按题目/状态筛选 + 分页，最新在前）+ 前端 `SubmissionHistory` 列表页与 `SubmissionDetail` 详情页（展示代码与完整评测结果）+ 导航入口。评测结果区渲染逻辑抽成共享组件 `SubmissionResult`，ProblemDetail 与详情页复用，消除双份漂移。

## 二、范围

| 做 | 不做 |
|----|------|
| 后端：列表 API（强制当前用户 + problemId/status 筛选 + 分页 + 题目名 JOIN） | 管理员视角查所有用户提交（无角色体系，简化） |
| 后端：`SubmissionRead` 追加 `Code` 字段（详情页展示代码） | 列表接口返回代码正文（仅单条详情返回） |
| 前端：`SubmissionHistory.vue`（筛选 + 表格 + 分页） | 排名、提交统计图表（M4） |
| 前端：`SubmissionDetail.vue`（代码 + 结果 + 题目链接） | 历史页内嵌详情面板（选独立路由，见决策表） |
| 前端：`SubmissionResult.vue` 组件抽取（ProblemDetail 改用） | 状态徽章样式调整（沿用 st-* 类） |
| 路由 + 导航链接 + `submissionApi.list` | 分页条数选择器（固定 size=20，沿用 ProblemList 模式） |
| 后端集成测试（历史列表/筛选/分页/详情 Code） | Playwright 断言每行精确耗时（易 flaky） |

---

## 三、设计决策及理由

| 决策 | 理由 |
|------|------|
| **列表接口强制当前用户**（不提供 userId 参数，从 JWT 取） | DESIGN_M3 4.6 初稿有 `userId` 查询参数——取消。个人自用无 admin 角色，查别人提交既无意义又引入越权面；前端也只展示自己的。`CurrentUserId()` 扩展已存在 |
| 新增 **status 筛选**（可选参数） | 历史页标配（看 AC/WA 分布）；一行 `Where` 成本；总设计未列，属页面需求驱动的合理补充 |
| 列表项 **JOIN 题目名**（`SubmissionListItem` 含 `ProblemTitle`） | 历史页必须显示题目名，否则只给 `#problemId` 用户要自己回忆；个人自用数据量 JOIN 无性能顾虑 |
| **`SubmissionRead` 末尾追加 `string? Code = null`** | 详情页要展示提交代码；放 record 末尾带默认值，JSON 反序列化按属性名不受影响，现有 M2/M3.2 测试与前端零改动 |
| 详情页用**独立路由 `/submissions/:sid`** | DESIGN_M3 5.3 验收明确"历史页 → 详情"；独立路由支持将来从其他入口直达（如题目详情跳最近提交） |
| 结果区抽 **`SubmissionResult.vue`**（props: result） | ProblemDetail 结果区与详情页结果区是同一逻辑（徽章/分数/耗时/CE/detail chips/中间态提示），抽组件消除双份漂移；statusMeta 映射随组件迁移 |
| `CodeEditor` 加 **readonly prop**（默认 false） | 详情页只读展示代码复用现有样式（行号/等宽字体），textarea readonly + 禁输入，一行改动，向后兼容 |
| 题目筛选用**下拉**（`problemApi.list({size:100})` 拉全量） | 个人自用题目量小，下拉比输入框友好；沿用 ProblemList 的 toolbar 样式 |
| 筛选/分页交互沿用 **ProblemList 模式**（条件变化回第 1 页、共 X 条 · 第 X / X 页、size=20） | 前后端分页约定一致（{items,total,page,size}），页面交互心智统一 |
| 语言/时间显示前端映射（cpp17→C++17，`toLocaleString()`） | 展示关心的事归前端，后端返回原始值 |

---

## 四、逐文件内容

### 4.1 后端

#### 4.1.1 `backend/Acm.Api/Dtos/SubmissionDtos.cs`（追加 + 修改）

```csharp
// 历史列表项：含题目名（JOIN Problems）；不含代码正文（列表页不需要，详情单查）
public record SubmissionListItem(
    long Id,
    int ProblemId,
    string ProblemTitle,
    string Language,
    string Status,
    int Score,
    int? TimeMs,
    DateTime CreatedAt);

public record SubmissionListResponse(List<SubmissionListItem> Items, int Total, int Page, int Size);

// 修改：末尾追加 Code（详情页展示；现有调用方按名反序列化，零影响）
public record SubmissionRead(
    long Id, int UserId, int ProblemId, string Language,
    string Status, int Score, int? TimeMs, int? MemoryKb,
    List<TestcaseResult> Detail,
    string? CompileError,
    DateTime CreatedAt,
    string? Code = null);
```

#### 4.1.2 `backend/Acm.Api/Services/JudgeService.cs`（追加 ListAsync + ToRead 补 Code）

```csharp
// 历史列表：强制当前用户 + 可选 problemId/status 筛选，最新在前（Id 降序），JOIN 题目名
public async Task<SubmissionListResponse> ListAsync(
    int userId, int? problemId, string? status, int page, int size)
{
    var q = db.Submissions.AsNoTracking().Where(s => s.UserId == userId);
    if (problemId.HasValue) q = q.Where(s => s.ProblemId == problemId);
    if (!string.IsNullOrEmpty(status)) q = q.Where(s => s.Status == status);

    var total = await q.CountAsync();
    var items = await q.OrderByDescending(s => s.Id)
        .Skip((page - 1) * size).Take(size)
        .Join(db.Problems, s => s.ProblemId, p => p.Id,
            (s, p) => new SubmissionListItem(
                s.Id, s.ProblemId, p.Title, s.Language,
                s.Status, s.Score, s.TimeMs, s.CreatedAt))
        .ToListAsync();
    return new SubmissionListResponse(items, total, page, size);
}
```

`ToRead` 末尾补：`Code: s.Code`。

#### 4.1.3 `backend/Acm.Api/Controllers/SubmissionsController.cs`（SubmissionQueryController 追加）

```csharp
// 提交历史：GET /api/v1/submissions?problemId=&status=&page=&size=（仅当前用户）
[HttpGet]
public async Task<ActionResult<SubmissionListResponse>> List(
    [FromQuery] int? problemId,
    [FromQuery] string? status,
    [FromQuery, Range(1, int.MaxValue)] int page = 1,
    [FromQuery, Range(1, 100)] int size = 20) =>
    await judge.ListAsync(this.CurrentUserId(), problemId, status, page, size);
```

### 4.2 前端

#### 4.2.1 `frontend/src/api/index.js`（追加 list）

```js
export const submissionApi = {
  submit: (pid, data) => http.post(`/problems/${pid}/submissions`, data),
  get: (sid) => http.get(`/submissions/${sid}`),
  list: (params) => http.get('/submissions', { params })
}
```

#### 4.2.2 `frontend/src/components/SubmissionResult.vue`（新增：结果区共享组件）

从 ProblemDetail 迁入 statusMeta + 结果区模板，props 只收 `result`：

```vue
<script setup>
// 状态徽章映射：颜色 + 用户语言文案（从 ProblemDetail 迁入，两个页面共用）
const statusMeta = {
  AC: { cls: 'st-ac', text: '通过' }, WA: { cls: 'st-wa', text: '答案错误' },
  TLE: { cls: 'st-tle', text: '超时' }, MLE: { cls: 'st-mle', text: '超内存' },
  RE: { cls: 'st-re', text: '运行错误' }, CE: { cls: 'st-ce', text: '编译错误' },
  PENDING: { cls: 'st-pending', text: '排队中' }, JUDGING: { cls: 'st-pending', text: '评测中' }
}
defineProps({ result: { type: Object, required: true } })
</script>

<template>
  <!-- 原 ProblemDetail 结果区模板原样迁入：
       result-head（徽章/分数/耗时/提交#）
       中间态提示（PENDING/JUDGING）
       CE 分支（compileError）
       逐测试点 chips -->
</template>
```

#### 4.2.3 `frontend/src/views/ProblemDetail.vue`（修改：结果区换组件）

- 删除本地 `statusMeta` 定义（迁入 SubmissionResult）
- 结果区模板整体替换为：

```vue
<SubmissionResult v-if="result" :result="result" />
```

- 空态保留（`v-if="!result"` 显示"提交代码后..."）；提交/轮询逻辑零改动

#### 4.2.4 `frontend/src/views/SubmissionHistory.vue`（新增：历史列表页）

仿 ProblemList 结构：

```vue
<script setup>
// 筛选：题目下拉（problemApi.list({size:100}) 拉全量）+ 状态下拉
// 表格列：#Id | 题目 | 语言 | 状态徽章 | 分数 | 耗时 | 提交时间
// 行点击 → router.push(`/submissions/${row.id}`)
// 分页：沿用 ProblemList 模式（size=20，条件变化回第 1 页）
// 语言显示：langLabel = { cpp17: 'C++17', python3: 'Python3' }
// 时间显示：new Date(row.createdAt).toLocaleString()
// 空态："暂无提交"
</script>
```

状态下拉选项：`''`全部 + AC/WA/TLE/MLE/RE/CE（PENDING/JUDGING 只存在于提交瞬间，不进筛选）。

#### 4.2.5 `frontend/src/views/SubmissionDetail.vue`（新增：提交详情页）

```vue
<script setup>
// onMounted 拉 submissionApi.get(sid)
// 展示：← 返回（router.back()）+ 题目链接（/problems/{pid}）
//       元信息（语言/状态/分数/耗时/提交时间）
//       代码（CodeEditor readonly 模式）
//       SubmissionResult :result
</script>
```

#### 4.2.6 `frontend/src/components/CodeEditor.vue`（修改：加 readonly prop）

```vue
const props = defineProps({
  modelValue: { type: String, default: '' },
  rows: { type: Number, default: 12 },
  readonly: { type: Boolean, default: false }
})
// textarea 加 :readonly="props.readonly" + @input 中 readonly 时不 emit
```

#### 4.2.7 `frontend/src/router/index.js`（追加路由）

```js
{ path: '/submissions', component: SubmissionHistory },
{ path: '/submissions/:sid', component: SubmissionDetail }
```

（默认受守卫保护，无需 meta）

#### 4.2.8 `frontend/src/App.vue`（导航加链接）

```vue
<router-link to="/problems">题目</router-link>
<router-link to="/submissions">提交记录</router-link>
```

### 4.3 测试

**`backend/Acm.Api.Tests/M3/SubmissionHistoryTests.cs`**（新增，连真实 DB + Worker）：

| 用例 | 步骤 | 断言 |
|------|------|------|
| 列表只看自己 | 用户 A、B 各提交 1 条（都等终态） | A 查列表 total=1 且为 A 的提交；B 同样互不可见 |
| 最新在前 | 同一用户连交 3 条 | items 按 Id 降序 |
| problemId 筛选 | 两个题各交 1 条，按某题筛 | 只返回该题提交 |
| status 筛选 | 交 1 AC + 1 WA，按 WA 筛 | 只返回 WA 那条 |
| 分页 | 交 3 条，size=1 查第 2 页 | total=3，第 2 页为第 2 新提交 |
| 列表项含题目名 | 列表任一项 | ProblemTitle 非空且等于题目标题 |
| 详情含 Code | GET /submissions/{sid} | Code 与提交内容一致 |

---

## 五、验证方式

### 5.1 构建

```powershell
dotnet build backend/Acm.sln
cd frontend; npm run build
```

### 5.2 后端集成测试（前置：db + redis(6380) + Worker 在跑）

```powershell
$env:ConnectionStrings__Default="Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"
$env:ConnectionStrings__Redis="localhost:6380"
dotnet test
```

### 5.3 Playwright UI 验收（环境同 5.2 + 后端 8000 + 前端 5173）

前置：登录态 + 测试题（P9001，2 个测试点）+ 造 3 条不同状态提交（AC/WA/CE）。

| 场景 | 操作 | 断言 |
|------|------|------|
| ① 导航可达 | 顶部导航点"提交记录" | 进入 /submissions，列表显示 3 条，最新在前（Id 降序） |
| ② 徽章/语言/时间渲染 | 查看列表行 | AC 行绿色"通过"、CE 行黄色"编译错误"；语言列显示 C++17/Python3；时间列非空 |
| ③ 题目筛选 | 选题目 → 查询 | 只显示该题提交；重置后恢复全部 |
| ④ 状态筛选 | 选"编译错误" → 查询 | 只显示 CE 提交 |
| ⑤ 详情页 | 点某行 | /submissions/{sid}：代码只读展示且内容一致、结果区徽章/chips 正确、题目链接可跳转 |
| ⑥ 空态 | 新建用户登录 → 提交记录 | "暂无提交" |
| ⑦ 回归 | 回题目详情页提交 AC 代码 | M3.2 轮询流程不回归（排队中→自动变通过） |

### 5.4 验收清单

1. `dotnet build` + `npm run build` 全绿
2. `dotnet test` 全绿（含新增 SubmissionHistoryTests + M1/M2/M3.2 回归）
3. Playwright 7 场景全过
4. ProblemDetail 结果区抽组件后 UI 行为与 M3.2 一致（⑦覆盖）

---

## 六、风险与对策

| 风险 | 对策 |
|------|------|
| `SubmissionRead` 加字段影响现有调用 | 放 record 末尾 + 默认值；JSON 按属性名反序列化，M2/M3.2 测试与前端零改动；编译错误全量兜底 |
| ProblemDetail 换 SubmissionResult 组件回归 | statusMeta/模板原样迁入不改变逻辑；Playwright ⑦ + dotnet test 双兜底 |
| CodeEditor 加 readonly 破坏编辑 | 默认 false 向后兼容；提交区行为由 Playwright ⑦ 验证 |
| 题目下拉数据源量级增长 | 个人自用题目量小；将来可换远程搜索（不在本期） |
| 历史页 JOIN 性能 | 数据量小 + AsNoTracking + 分页 Take 限制，无问题 |
| 测试依赖 Worker 在跑 | M3.4 自动化前置（既定计划），本期沿用 M3.2 手动前置 |

---

## 七、交付物与后续衔接

| 交付物 | 衔接 |
|--------|------|
| 列表 API + SubmissionRead.Code | M3.4 总验收黑盒用例（历史列表/筛选） |
| SubmissionHistory / SubmissionDetail 页面 | M4 排名统计可复用列表 API 聚合 |
| SubmissionResult 共享组件 | 未来任何展示评测结果的页面直接复用 |
| CodeEditor readonly | 详情页只读场景通用 |

> M3.3 完成后：提交历史闭环（提交 → 轮询出结果 → 历史列表 → 详情回看代码与结果）。M3.4 收尾（测试自动化 + 总验收 + AGENTS.md 更新）。
