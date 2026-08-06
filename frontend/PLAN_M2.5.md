# M2.5 前端提交评测 详细设计方案

> **所属里程碑**：M2 评测核心（`DESIGN_M2.md`）
> **任务边界**：M2.5 前端 —— 题目详情页提交区 + CodeEditor + 结果展示（总设计"六、前端设计"一章）
> **前置**：M2.3 提交 API（POST/GET submission）✅；M2.4 测试点接口 ✅
> **本阶段不涉及**：提交历史列表页（M2.6+）、测试点管理前端页面、语法高亮（M3 可选）

---

## 一、目标

在题目详情页实现**提交评测闭环**：选手选语言 → 写代码 → 提交 → 等待结果 → 展示状态徽章/耗时/逐测试点/分数。完成后整个 M2 的"可提交可判题可见结果"目标达成。

## 二、范围

| 做 | 不做 |
|----|------|
| `CodeEditor.vue`（带行号 textarea） | 语法高亮 / Monaco（M3） |
| `ProblemDetail.vue` 提交区 + 结果展示 | 提交历史列表（M2.6） |
| `api/index.js` 加 submissionApi | 测试点管理前端 UI |
| 结果状态徽章（AC 绿/WA 红/TLE 橙/CE 黄...） | 轮询/推送（M2 同步，一次返回） |

## 三、设计决策及理由

| 决策 | 理由 |
|------|------|
| CodeEditor 用**行号 + textarea 同步滚动** | M2 不做 Monaco；行号提升体验，实现简单（~80 行） |
| 提交后**同步阻塞等结果**（axios 默认超时内） | M2 同步评测，POST 返回即是最终结果，无需轮询 |
| 提交中禁用按钮 + 显示"评测中" | 防重复提交；同步评测阻塞期间用户无反馈会困惑 |
| 状态徽章用**颜色映射表** | AC=绿/WA=红/TLE=橙/MLE=紫/RE=粉/CE=黄，与难度徽章风格统一 |
| 结果区显示：徽章 + 分数 + 耗时/内存 + 逐测试点行 | DESIGN 6.1 UI 草图：整体状态 + 明细 |
| CE 时显示 compileError（textarea 只读展示） | 编译错误是文本不是测试点，单独区域展示 |
| 编辑器初始值 = 空（或按语言给个小模板） | 空即可，选手自己写；不预填答案 |
| 默认语言 C++17 | 最常用，减少一步选择 |

## 四、逐文件内容

### 4.1 `frontend/src/components/CodeEditor.vue`（新增，~80 行）

```vue
<template>
  <div class="code-editor">
    <!-- 行号列 + 输入区（同步滚动） -->
    <div ref="gutterRef" class="code-gutter" aria-hidden="true">
      <div v-for="n in lineCount" :key="n">{{ n }}</div>
    </div>
    <textarea
      ref="taRef"
      class="code-input"
      :value="modelValue"
      @input="onInput"
      @scroll="syncScroll"
      :spellcheck="false"
    ></textarea>
  </div>
</template>

<script setup>
import { ref, computed } from 'vue'

const props = defineProps({
  modelValue: { type: String, default: '' },
  rows: { type: Number, default: 12 }
})

const emit = defineEmits(['update:modelValue'])

const taRef = ref(null)
const gutterRef = ref(null)

const lineCount = computed(() => (props.modelValue.match(/\n/g)?.length ?? 0) + 1)

function onInput(e) {
  emit('update:modelValue', e.target.value)
}

function syncScroll(e) {
  if (gutterRef.value) gutterRef.value.scrollTop = e.target.scrollTop
}
</script>

<style scoped>
.code-editor {
  display: flex;
  border: 1px solid var(--border);
  border-radius: 6px;
  overflow: hidden;
  background: #1e1e1e;   /* 深色编辑器背景 */
  font-family: Consolas, Monaco, monospace;
  font-size: 0.9rem;
  line-height: 1.5;
}
.code-gutter {
  flex-shrink: 0;
  padding: 0.5rem 0.6rem;
  text-align: right;
  color: #6b7280;
  background: #252526;
  user-select: none;
  overflow: hidden;      /* 行号不滚动，内容区滚 */
}
.code-input {
  flex: 1;
  min-height: 240px;
  resize: vertical;
  border: none;
  background: transparent;
  color: #d4d4d4;
  padding: 0.5rem 0.6rem;
  font-family: inherit;
  font-size: inherit;
  line-height: inherit;
  outline: none;
}
.code-input:focus { outline: none; }   /* 深色背景不套蓝色边框 */
</style>
```

**关键点**：
- `v-model` 双向绑定（props modelValue + emit update:modelValue）
- 行号 = 换行数 + 1（`computed`），跟随内容自动增减
- `@scroll` 同步：textarea 滚动时行号列同步（行号列自身不滚动，靠 scrollTop 对齐）
- 深色主题：编辑器常规模样，代码可读性好

### 4.2 `frontend/src/api/index.js`（修改，追加）

```js
// 提交接口（对齐后端 /api/v1）
export const submissionApi = {
  submit: (pid, data) => http.post(`/problems/${pid}/submissions`, data),
  get: (sid) => http.get(`/submissions/${sid}`)
}
```

### 4.3 `frontend/src/views/ProblemDetail.vue`（修改，核心）

**script 追加**：

```js
import CodeEditor from '../components/CodeEditor.vue'
import { submissionApi } from '../api'

const language = ref('cpp17')
const code = ref('')
const submitting = ref(false)
const result = ref(null)          // 最近一次提交结果（SubmissionRead）

const statusMeta = {
  AC:  { cls: 'st-ac',   text: '通过' },
  WA:  { cls: 'st-wa',   text: '答案错误' },
  TLE: { cls: 'st-tle',  text: '超时' },
  MLE: { cls: 'st-mle',  text: '超内存' },
  RE:  { cls: 'st-re',   text: '运行错误' },
  CE:  { cls: 'st-ce',   text: '编译错误' },
  PENDING: { cls: 'st-pending', text: '排队中' },
  JUDGING: { cls: 'st-pending', text: '评测中' }
}

async function handleSubmit() {
  if (submitting.value) return
  if (!code.value.trim()) { alert('代码不能为空'); return }
  submitting.value = true
  result.value = null
  try {
    result.value = await submissionApi.submit(problem.value.id, {
      language: language.value,
      code: code.value
    })
  } catch {
    // 拦截器已 alert
  } finally {
    submitting.value = false
  }
}
```

**template 追加（替换 `.submit-placeholder` 占位）**：

```vue
<!-- 提交评测区 -->
<section class="mt-1">
  <h3>提交评测</h3>
  <div class="submit-bar">
    <select v-model="language" :disabled="submitting" class="toolbar-input">
      <option value="cpp17">C++17</option>
      <option value="python3">Python3</option>
    </select>
    <button class="btn" :disabled="submitting" @click="handleSubmit">
      {{ submitting ? '评测中...' : '提交' }}
    </button>
  </div>
  <div class="mt-1">
    <CodeEditor v-model="code" :rows="12" />
  </div>
</section>

<!-- 结果区 -->
<section v-if="result" class="mt-1">
  <h3>评测结果</h3>
  <div class="result-head">
    <span :class="`badge ${statusMeta[result.status]?.cls ?? 'st-unknown'}`">
      {{ statusMeta[result.status]?.text ?? result.status }}
    </span>
    <span class="text-muted">分数 {{ result.score }}/100</span>
    <span v-if="result.timeMs != null" class="text-muted">最大耗时 {{ result.timeMs }}ms</span>
    <span class="text-muted">提交 #{{ result.id }}</span>
  </div>

  <!-- CE：显示编译错误 -->
  <pre v-if="result.status === 'CE' && result.compileError" class="compile-error">{{ result.compileError }}</pre>

  <!-- 逐测试点 -->
  <div v-else-if="result.detail.length" class="result-detail">
    <span v-for="tc in result.detail" :key="tc.id" :class="`tc-chip ${statusMeta[tc.status]?.cls}`">
      点{{ tc.id }} {{ statusMeta[tc.status]?.text ?? tc.status }}
      <template v-if="tc.timeMs != null">· {{ tc.timeMs }}ms</template>
    </span>
  </div>
</section>
```

### 4.4 `frontend/src/assets/style.css`（修改，追加）

```css
/* 提交区 */
.submit-bar {
  display: flex;
  align-items: center;
  gap: 0.6rem;
}

/* 结果徽章颜色（状态色，区别于难度 badge） */
.st-ac { background: #22c55e; }      /* 绿：通过 */
.st-wa { background: #ef4444; }      /* 红：答案错误 */
.st-tle { background: #f97316; }     /* 橙：超时 */
.st-mle { background: #a855f7; }     /* 紫：超内存 */
.st-re { background: #ec4899; }      /* 粉：运行错误 */
.st-ce { background: #eab308; }      /* 黄：编译错误 */
.st-pending { background: #6b7280; } /* 灰：评测中 */
.st-unknown { background: #6b7280; }

/* 逐测试点 chip */
.result-detail {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
  margin-top: 0.6rem;
}
.tc-chip {
  padding: 0.2rem 0.7rem;
  border-radius: 999px;
  color: #fff;
  font-size: 0.8rem;
}

/* 编译错误框 */
.compile-error {
  background: #fef2f2;
  border: 1px solid #fecaca;
  border-radius: 6px;
  padding: 0.8rem;
  color: #991b1b;
  font-size: 0.85rem;
  white-space: pre-wrap;
  max-height: 240px;
  overflow-y: auto;
  margin-top: 0.6rem;
}

/* 结果头部 */
.result-head {
  display: flex;
  align-items: center;
  gap: 1rem;
  flex-wrap: wrap;
}
```

## 五、验证方式

### 5.1 构建

```powershell
cd frontend
npm run build
```

### 5.2 Playwright UI 验收（三层验证最后一层）

前置：db(5433) + 后端(8000) + 前端(5173) 都在跑，测试题 + 测试点已备。

| 场景 | 操作 | 断言 |
|------|------|------|
| ① 打开详情页 | 导航到题目页 | 看到提交区（语言选择 + 编辑器 + 提交按钮） |
| ② 空代码提交 | 点提交 | alert"代码不能为空" |
| ③ AC 提交 | 选 C++17，输入 A+B 代码，点提交 | 等待 → 绿色"通过"徽章 + 分数 100 + 逐测试点 chips |
| ④ WA 提交 | 代码输出错误值 | 红色"答案错误" |
| ⑤ CE 提交 | 语法错误代码 | 黄色"编译错误" + compileError 框显示错误 |
| ⑥ 提交中禁用 | 提交后立即看按钮 | "评测中..."且 disabled |

> 同步评测阻塞数秒：Playwright 用 `handle_dialog` 处理 alert，`wait_for_timeout` 等结果渲染。

### 5.3 回归

```powershell
$env:ConnectionStrings__Default="Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"
dotnet test   # 后端无改动，回归保险
```

验收清单：
1. 提交区显示正常（语言/编辑器/按钮）
2. AC/WA/CE 三种结果正确展示（徽章颜色 + 分数 + 逐点 + compileError）
3. 空代码拦截、提交中禁用按钮
4. `npm run build` 通过

## 六、风险与对策

| 风险 | 对策 |
|------|------|
| 同步评测阻塞 axios 请求超时 | M2 评测约 1-5s（2 测试点）；axios 默认无超时，安全；M2.6 可调 timeout=30s 保险 |
| 行号与内容行数不同步 | 行号 = 换行数+1 computed，自动跟随；换行符统一 `\n`（textarea 输入天然如此） |
| 结果区在评测后不刷新 | 同步返回，result 直接赋值；重复提交覆盖 result |
| 深色编辑器在浅色主题里突兀 | CodeEditor 内部自带深色（scoped），与页面其他部分无冲突 |
| CE 的 detail 为空数组 | 前端按 status==='CE' 分支显示 compileError，不读 detail |

## 七、交付物与后续衔接

| 交付物 | 衔接 |
|--------|------|
| CodeEditor.vue | M3 可替换成 Monaco（接口 v-model 不变，零改动接入） |
| ProblemDetail 提交区+结果区 | M2.6 提交历史页复用 submissionApi.get |
| submissionApi | 提交历史列表（M2.6）复用 |

> M2.5 完成后 M2 主体功能全部打通。M2.6 收尾：集成测试（AC/WA/CE 用例）+ 提交历史页 + 总验收。
