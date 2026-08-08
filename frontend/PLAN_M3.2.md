# M3.2 前端轮询评测结果 详细设计方案

> **所属里程碑**：M3 异步评测与提交历史（`DESIGN_M3.md`）
> **任务边界**：M3.2 前端轮询 —— 题目详情页提交后秒回 PENDING，前端 1.5s 间隔轮询直至终态展示（总设计"四、4.10 前端"）
> **前置**：M3.1 已落地（提交秒回 PENDING + Worker 后台出结果 + `GET /submissions/{sid}` 轮询接口）✅
> **本阶段不涉及**：提交历史页（M3.3）、SignalR 推送（M4）、Monaco 高亮

---

## 一、目标

把 M2.5 的"提交→阻塞等结果"改成**异步轮询**：提交后立即显示"排队中"徽章，前端自动以 1.5s 间隔轮询 `GET /submissions/{sid}`，状态流转 PENDING→JUDGING→终态（AC/WA/TLE/MLE/RE/CE）时自动切换展示，全程用户无感刷新。

## 二、范围

| 做 | 不做 |
|----|------|
| `ProblemDetail.vue` 提交逻辑改轮询 | 提交历史页（M3.3） |
| 轮询生命周期管理（卸载清理/超时上限/异常停止） | 状态徽章样式调整（M2.5 已有 PENDING/JUDGING 映射） |
| 结果区渲染 PENDING/JUDGING 中间态 | SignalR 推送（M4） |
| 提交中按钮禁用 + 终态解锁重提 | 多提交并行跟踪 |

## 三、设计决策及理由

| 决策 | 理由 |
|------|------|
| **递归 setTimeout** 而非 setInterval | 每次请求"完成后"再等 1.5s 发起下一次，请求永不堆积（setInterval 在响应慢时会叠请求） |
| 轮询间隔 **1.5s**，上限 **60 次（90s）** | DESIGN_M3 1.2 约定：1.5s 间隔、60 次上限；超过即停 + 提示（提交仍可稍后刷新查看） |
| **onUnmounted 停止轮询** | 用户轮询期间返回列表/切页面，定时器必须清理，否则组件已销毁还发请求（还会触发拦截器 alert） |
| 提交后 `result` 立即赋 PENDING 对象，结果区直接渲染中间态徽章 | 比"正在评测，请稍候..."文案更精确；终态到达时同一区域无缝切换，无需两套 UI |
| 轮询期间 `submitting=true` 锁提交按钮，终态后解锁 | 防重复提交；一次只跟踪一个提交（个人自用简化，不做多提交队列） |
| 轮询请求失败（网络/401）即停 | 拦截器已统一 alert；继续轮询只会连环弹窗 |
| 组件卸载标志位 `polling` 配合每轮检查 | 与递归 setTimeout 配合：卸载即置 false，下一轮自然终止（不强行 clearTimeout 也行，双保险） |
| 按钮文案保持"评测中..." | 中间态细节由徽章承担（排队中/评测中），按钮不需要两级文案 |
| api/index.js **零改动** | `submissionApi.get(sid)` M2.5 已存在，直接复用 |

## 四、逐文件内容

### 4.1 `frontend/src/views/ProblemDetail.vue`（修改，核心）

**script 部分改动**：

```js
// 提交区状态
const language = ref('cpp17')
const code = ref('')
const submitting = ref(false)
const result = ref(null)          // 提交后立即为 PENDING 对象，轮询直至终态

// 轮询控制：递归 setTimeout + 卸载标志
let pollTimer = null
let polling = false               // 组件是否存活（onUnmounted 置 false）
const POLL_INTERVAL_MS = 1500     // 与 DESIGN_M3 约定一致
const POLL_MAX_COUNT = 60         // 60 次 × 1.5s = 90s 上限

// 终态集合：命中即停
const FINAL_STATUS = ['AC', 'WA', 'TLE', 'MLE', 'RE', 'CE']

function stopPolling() {
  polling = false
  if (pollTimer) { clearTimeout(pollTimer); pollTimer = null }
}

// 轮询一轮：GET 结果；终态/超时/异常即停
function pollOnce(sid, count) {
  if (!polling) return
  submissionApi
    .get(sid)
    .then((cur) => {
      if (!polling) return                     // 卸载竞态
      result.value = cur                       // PENDING/JUDGING 也渲染（徽章流转）
      if (FINAL_STATUS.includes(cur.status)) { // 终态：收工
        submitting.value = false
        return
      }
      if (count >= POLL_MAX_COUNT) {           // 超时：停止 + 提示
        submitting.value = false
        alert('评测超时，请稍后刷新页面查看结果')
        return
      }
      pollTimer = setTimeout(() => pollOnce(sid, count + 1), POLL_INTERVAL_MS)
    })
    .catch(() => {
      // 拦截器已 alert（网络/401），轮询停止
      submitting.value = false
    })
}

async function handleSubmit() {
  if (submitting.value) return
  if (!code.value.trim()) { alert('代码不能为空'); return }
  submitting.value = true
  result.value = null
  try {
    // M3.1：提交秒回 PENDING，不阻塞
    const sub = await submissionApi.submit(problem.value.id, {
      language: language.value,
      code: code.value
    })
    result.value = sub
    polling = true
    pollTimer = setTimeout(() => pollOnce(sub.id, 1), POLL_INTERVAL_MS)
  } catch {
    // 拦截器已 alert（含 503 队列不可用）；解锁重试
    submitting.value = false
  }
}

// 组件销毁：停止轮询，避免已销毁组件继续发请求
onUnmounted(stopPolling)
```

**template 改动（结果区）**——原三段式（空态/进行中/结果）改为两段式：无提交显示空态，有提交（含中间态）直接渲染徽章：

```vue
<!-- 评测结果区 -->
<section class="mt-1">
  <h3>评测结果</h3>
  <!-- 无提交：空态 -->
  <p v-if="!result" class="text-muted result-empty">
    提交代码后，评测结果将显示在这里
  </p>
  <!-- 有提交：中间态（排队中/评测中）与终态同一渲染路径，自动流转 -->
  <template v-else>
    <div class="result-head">
      <span :class="`badge ${statusMeta[result.status]?.cls ?? 'st-unknown'}`">
        {{ statusMeta[result.status]?.text ?? result.status }}
      </span>
      <span class="text-muted">分数 {{ result.score }}/100</span>
      <span v-if="result.timeMs != null" class="text-muted">最大耗时 {{ result.timeMs }}ms</span>
      <span class="text-muted">提交 #{{ result.id }}</span>
    </div>

    <!-- 中间态提示（PENDING/JUDGING 时 detail 为空，且尚未有可展示结果） -->
    <p v-if="result.status === 'PENDING' || result.status === 'JUDGING'" class="text-muted">
      正在评测，请稍候...
    </p>

    <!-- 编译错误：单独区域展示 -->
    <div v-else-if="result.status === 'CE'">
      <h4 class="ce-title">编译错误</h4>
      <pre class="compile-error">{{ result.compileError }}</pre>
    </div>

    <!-- 逐测试点 -->
    <div v-else-if="result.detail.length" class="result-detail">
      <span
        v-for="tc in result.detail"
        :key="tc.id"
        :class="`tc-chip ${statusMeta[tc.status]?.cls ?? 'st-unknown'}`"
      >
        点{{ tc.id }} {{ statusMeta[tc.status]?.text ?? tc.status }}
        <template v-if="tc.timeMs != null">· {{ tc.timeMs }}ms</template>
      </span>
    </div>
  </template>
</section>
```

**按钮区零改动**（现有 `:disabled="submitting"` + "评测中..." 文案已符合需求）。

> 注：`statusMeta` 已含 `PENDING: 排队中 / JUDGING: 评测中`（M2.5 就定义了），无需新增样式；`result.detail` 在中间态为空数组，`v-else-if="result.detail.length"` 天然跳过。

### 4.2 `frontend/src/api/index.js`

**零改动**。`submissionApi.get(sid)` 已存在（M2.5）。

---

## 五、验证方式

### 5.1 构建

```powershell
cd frontend
npm run build
```

### 5.2 Playwright UI 验收

前置（M3.1 环境全套）：db(5433) + redis(compose 6380) + Worker 进程 + 后端(8000) + 前端(5173)；测试题 P9001 + 2 个测试点已备。

| 场景 | 操作 | 断言 |
|------|------|------|
| ① 提交秒回排队中 | 提交 AC 代码 | 提交按钮立即变"评测中..."且 disabled；结果区出现**灰色"排队中"徽章** |
| ② 自动流转终态 | 不手动刷新，等待 2-5s | 徽章自动变绿色"通过" + 分数 100 + 逐测试点 chips |
| ③ TLE 流转 | 提交死循环代码 | 排队中 → 自动变橙色"超时" |
| ④ 轮询中离开页面 | 提交后立刻点"← 返回" | 无报错、无残留 alert；回列表页正常 |
| ⑤ 终态后再次提交 | 终态后改代码再提交 | 按钮解锁可再次提交，结果区重置 → 重新流转 |
| ⑥ 空代码拦截（回归） | 清空代码点提交 | alert"代码不能为空" |
| ⑦ CE 展示（回归） | 提交语法错误代码 | 排队中 → 黄色"编译错误" + compileError 框 |

> 关键断言点：**不手动刷新**，验证"排队中"自动变终态（这正是轮询生效的证明）。PENDING 阶段可先 `browser_find` 断言"排队中"，等待后用 `browser_find` 断言"通过"。

### 5.3 回归

```powershell
dotnet test   # 后端零改动，跑一遍保险（需 Worker 在跑）
```

验收清单：
1. 提交后秒回"排队中"，无手动刷新自动变终态
2. 轮询期间按钮禁用、终态解锁
3. 页面离开无报错（轮询已清理）
4. `npm run build` 通过
5. 空代码拦截、CE 展示等 M2.5 行为不回归

---

## 六、风险与对策

| 风险 | 对策 |
|------|------|
| 组件卸载后轮询仍在跑 | `onUnmounted` 置 `polling=false` + `clearTimeout`；每轮开头再查标志（双保险） |
| 请求堆积 / 竞态 | 递归 setTimeout（完成后再等 1.5s）；`polling` 标志拦截卸载竞态 |
| 轮询无限 | 60 次上限（90s）即停 + alert 提示可刷新查看 |
| Worker 没起导致永远 PENDING | 60 次后停止提示，不无限弹窗；不阻塞其他操作 |
| 中间态 detail 为空数组 | 模板 `v-else-if="detail.length"` 天然跳过；CE 分支只在终态命中 |
| 快速重复点提交 | `submitting` 锁；终态才解锁 |
| 轮询中网络断 | 拦截器 alert 一次 + 轮询停止 + 解锁重试，不连环弹窗 |

---

## 七、交付物与后续衔接

| 交付物 | 衔接 |
|--------|------|
| ProblemDetail 异步轮询提交 | M3.3 提交历史页复用 `submissionApi.get`；M3.4 总验收 Playwright 全流程 |
| 轮询生命周期工具代码 | 可提取复用（M3.3 历史页若需自动刷新可参照） |

> M3.2 完成后：提交→排队中→自动出结果，M3 用户侧闭环打通。M3.3 提交历史页，M3.4 收尾（测试自动化 + 总验收）。
