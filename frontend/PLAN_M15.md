# 前端 M1.5 题目详情 + 表单设计方案

> **目标**：实现题目详情页 + 创建/编辑共用表单组件，闭环完整 CRUD
> **范围**：`ProblemDetail.vue`（重写）、`components/ProblemForm.vue`（新增）、`ProblemList.vue`（新建按钮接真）、`style.css`（补模态框样式）
> **前置**：M1.4 完成（列表/筛选/分页、5 道测试数据）

---

## 一、页面功能清单

| 区域 | 功能 |
|------|------|
| ProblemDetail | 顶部：返回列表 + 标题 + slug + 难度徽章 |
| ProblemDetail | 元信息：时间/内存限制、作者 ID、标签 |
| ProblemDetail | 题面：Description / InputDesc / OutputDesc（纯文本展示，M1 不做 Markdown 渲染） |
| ProblemDetail | 样例区：SampleInputs/SampleOutputs 按 `---` 切分成多个样例块展示 |
| ProblemDetail | 操作：编辑（打开表单）、删除（confirm → 删除 → 回列表） |
| ProblemDetail | 提交区占位："评测功能 M2 开放" |
| ProblemForm | 创建模式：从列表页"新建题目"打开，提交 POST 后回列表 |
| ProblemForm | 更新模式：从详情页"编辑"打开，预填原值，提交 PUT 后回详情 |
| ProblemForm | 模态框 UI：遮罩 + 居中卡片 + 关闭按钮 |

---

## 二、核心设计决策

### D1. 表单用"模态框"而不是独立路由页

| 方案 | 对比 |
|------|------|
| 模态框（选中） | 不用加路由（/problems/new、/problems/:pid/edit），列表页和详情页共用同一组件，零路由改动 |
| 独立路由页 | 需要新增 2 条路由 + 2 个页面壳，还要处理"编辑模式直接刷新"的守卫 |

**实现**：`ProblemForm.vue` 渲染遮罩 + 表单卡片；`v-if` 由父页面控制开关，`props` 传"模式 + 初始数据"，`emit('close')` 通知父页面关闭，`emit('saved')` 通知父页面刷新。

### D2. 表单组件 props/emit 契约

```js
// 父页面（列表/详情）：
<ProblemForm
  v-if="showForm"
  :mode="formMode"        // 'create' | 'edit'
  :initial="formInitial"  // 编辑模式的题目原值（创建模式为 null）
  @close="showForm = false"
  @saved="onSaved"        // 创建：跳详情；编辑：重新加载详情
/>
```

**组件内部**：
- `mode === 'create'` → 调 `problemApi.create`；`'edit'` → 调 `problemApi.update(initial.id, payload)`
- 关闭按钮 / 遮罩点击 → `emit('close')`

### D3. 部分更新语义的处理（关键坑）

后端 `ProblemUpdate` 全部 nullable，**null = 不修改**。编辑提交时若直接传 null 表示"没改"而非"清空"。

**方案**：编辑保存时把表单所有字段**有值即传**：

```js
// 编辑：tags 空数组也传（后端接收后覆盖为空）
const payload = {
  title, description, inputDesc, outputDesc,
  timeLimit, memoryLimit, tags, difficulty,   // difficulty 空 → null → 后端不修改（已知限制）
  sampleInputs, sampleOutputs
}
```

**已知限制**（个人自用接受）：编辑时**无法把难度从 3 改为"未评级"**（传 null = 不修改）。要支持需后端改 DTO 语义（如加 `clearDifficulty` 标志），M1 不做。文档记录，将来再说。

### D4. tags 的字符串 ↔ 数组转换

后端是 `List<string>`，表单输入是逗号分隔文本：

```js
// 表单 → 提交：'入门,DP' → ['入门','DP']
tags: tagsInput.value.split(',').map(s => s.trim()).filter(Boolean)
// 预填：['入门','DP'] → '入门,DP'
tagsInput.value = initial.tags.join(',')
```

### D5. slug 编辑模式只读

后端 `ProblemUpdate` **没有 Slug 字段**（slug 创建后不可改）。所以：
- 创建模式：slug 可输入（正则提示 `^P\d{3,5}$`，后端校验兜底）
- 编辑模式：slug 只读展示（disabled），不提交

### D6. 样例按 `---` 切分展示

后端约定：多个样例以 `---` 分隔（`Models/Problem.cs` 注释）。展示逻辑：

```js
const samples = computed(() => {
  const ins = initial.sampleInputs.split('---').map(s => s.trim())
  const outs = initial.sampleOutputs.split('---').map(s => s.trim())
  // 按 index 一一对应，循环输出 样例 1/2/3...
})
```

### D7. 删除用原生 `confirm()`

```js
if (!confirm(`确定删除题目 ${p.title} 吗？`)) return
await problemApi.remove(p.id)
router.push('/problems')
```

个人自用项目，原生 confirm 够用，不引入弹窗库。

### D8. 错误处理延续约定

所有失败由 api 拦截器统一 alert，页面只走成功路径（`catch {}` 吞掉）。

---

## 三、ProblemDetail.vue 详细设计

```vue
<script setup>
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { problemApi } from '../api'
import ProblemForm from '../components/ProblemForm.vue'

const route = useRoute()
const router = useRouter()

const problem = ref(null)
const loading = ref(true)
const showForm = ref(false)

// D6 样例切分
const samples = computed(() => {
  if (!problem.value) return []
  const ins = (problem.value.sampleInputs || '').split('---').map(s => s.trim())
  const outs = (problem.value.sampleOutputs || '').split('---').map(s => s.trim())
  return ins.map((input, i) => ({ input, output: outs[i] || '' }))
})

async function load() {
  loading.value = true
  try {
    problem.value = await problemApi.get(route.params.pid)
  } catch {
    // 拦截器已 alert（404 题目不存在）
  } finally {
    loading.value = false
  }
}

// D7 删除
async function handleDelete() {
  if (!confirm(`确定删除题目 ${problem.value.title} 吗？`)) return
  try {
    await problemApi.remove(problem.value.id)
    router.push('/problems')
  } catch { /* 拦截器已 alert */ }
}

function openEdit() {
  formInitial.value = problem.value
  formMode.value = 'edit'
  showForm.value = true
}
function onSaved() { showForm.value = false; load() }  // 编辑保存后刷新详情

onMounted(load)
</script>

<template>
  <div v-if="problem" class="card">
    <button class="btn btn-small btn-secondary" @click="router.push('/problems')">← 返回</button>
    <h2 class="mt-1">
      {{ problem.title }}
      <span v-if="problem.difficulty" :class="`badge badge-${problem.difficulty}`">{{ problem.difficulty }}★</span>
    </h2>
    <p class="text-muted">{{ problem.slug }}</p>

    <p class="mt-1 text-muted">
      时间 {{ problem.timeLimit }}ms · 内存 {{ problem.memoryLimit }}MB · 作者 #{{ problem.authorId }}
    </p>
    <p class="text-muted" v-if="problem.tags.length">
      标签：<span v-for="t in problem.tags" :key="t">{{ t }} </span>
    </p>

    <section class="mt-1">
      <h3>题目描述</h3>
      <pre class="problem-text">{{ problem.description }}</pre>
    </section>
    <section class="mt-1">
      <h3>输入格式</h3>
      <pre class="problem-text">{{ problem.inputDesc }}</pre>
    </section>
    <section class="mt-1">
      <h3>输出格式</h3>
      <pre class="problem-text">{{ problem.outputDesc }}</pre>
    </section>

    <section class="mt-1" v-if="samples.length">
      <h3>样例</h3>
      <div v-for="(s, i) in samples" :key="i" class="sample-block">
        <h4>样例 {{ i + 1 }}</h4>
        <pre>输入：{{ s.input }}</pre>
        <pre>输出：{{ s.output }}</pre>
      </div>
    </section>

    <div class="mt-1">
      <button class="btn btn-small" @click="openEdit">编辑</button>
      <button class="btn btn-small btn-danger" @click="handleDelete">删除</button>
    </div>

    <div class="submit-placeholder mt-1">
      提交评测功能 M2 开放
    </div>
  </div>
  <p v-else-if="loading" class="text-center text-muted">加载中...</p>

  <!-- 编辑模态框 -->
  <ProblemForm
    v-if="showForm"
    :mode="formMode"
    :initial="formInitial"
    @close="showForm = false"
    @saved="onSaved"
  />
</template>
```

## 四、ProblemForm.vue 详细设计

```vue
<script setup>
import { ref, computed } from 'vue'
import { problemApi } from '../api'

const props = defineProps({
  mode: { type: String, required: true },   // 'create' | 'edit'
  initial: { type: Object, default: null }  // 编辑模式的题目原值
})
const emit = defineEmits(['close', 'saved'])

// 表单状态（创建默认值 / 编辑预填，D4/D5）
const slug = ref(props.initial?.slug ?? '')
const title = ref(props.initial?.title ?? '')
const description = ref(props.initial?.description ?? '')
const inputDesc = ref(props.initial?.inputDesc ?? '')
const outputDesc = ref(props.initial?.outputDesc ?? '')
const timeLimit = ref(props.initial?.timeLimit ?? 1000)
const memoryLimit = ref(props.initial?.memoryLimit ?? 256)
const tagsInput = ref(props.initial?.tags.join(',') ?? '')
const difficulty = ref(props.initial?.difficulty ?? '')
const sampleInputs = ref(props.initial?.sampleInputs ?? '')
const sampleOutputs = ref(props.initial?.sampleOutputs ?? '')
const loading = ref(false)
const isEdit = computed(() => props.mode === 'edit')

async function submit() {
  // 必填拦截（其余校验后端 DTO 兜底）
  if (!title.value.trim()) { alert('请输入标题'); return }
  if (!description.value.trim()) { alert('请输入题目描述'); return }
  if (!isEdit.value && !slug.value.trim()) { alert('请输入 slug（如 P1000）'); return }

  loading.value = true
  try {
    // D4：tags 转数组；难度空串 → null
    const payload = {
      title: title.value.trim(),
      description: description.value.trim(),
      inputDesc: inputDesc.value,
      outputDesc: outputDesc.value,
      timeLimit: Number(timeLimit.value),
      memoryLimit: Number(memoryLimit.value),
      tags: tagsInput.value.split(',').map(s => s.trim()).filter(Boolean),
      difficulty: difficulty.value || null,
      sampleInputs: sampleInputs.value,
      sampleOutputs: sampleOutputs.value
    }
    if (isEdit.value) {
      await problemApi.update(props.initial.id, payload)  // 部分更新：全字段有值即传
      emit('saved')
    } else {
      payload.slug = slug.value.trim()
      await problemApi.create(payload)
      emit('saved')
    }
  } catch {
    // 拦截器已 alert
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <!-- 遮罩 + 居中卡片 -->
  <div class="modal-mask" @click.self="emit('close')">
    <div class="modal-card">
      <div class="modal-head">
        <h3>{{ isEdit ? '编辑题目' : '新建题目' }}</h3>
        <button class="btn btn-small btn-secondary" @click="emit('close')">✕</button>
      </div>
      <div class="modal-body">
        <!-- slug：编辑只读（D5） -->
        <div class="form-group">
          <label>Slug（如 P1000）</label>
          <input v-model="slug" type="text" :disabled="isEdit" />
        </div>
        <div class="form-group">
          <label>标题 *</label>
          <input v-model="title" type="text" />
        </div>
        <div class="form-group">
          <label>题目描述 *</label>
          <textarea v-model="description" rows="5"></textarea>
        </div>
        <div class="form-group">
          <label>输入格式</label>
          <textarea v-model="inputDesc" rows="2"></textarea>
        </div>
        <div class="form-group">
          <label>输出格式</label>
          <textarea v-model="outputDesc" rows="2"></textarea>
        </div>
        <div class="form-group">
          <label>时间限制（ms）</label>
          <input v-model="timeLimit" type="number" />
        </div>
        <div class="form-group">
          <label>内存限制（MB）</label>
          <input v-model="memoryLimit" type="number" />
        </div>
        <div class="form-group">
          <label>标签（逗号分隔）</label>
          <input v-model="tagsInput" type="text" placeholder="如 入门,DP" />
        </div>
        <div class="form-group">
          <label>难度</label>
          <select v-model="difficulty">
            <option value="">未评级</option>
            <option v-for="d in [1,2,3,4,5]" :key="d" :value="d">{{ d }}★</option>
          </select>
        </div>
        <div class="form-group">
          <label>样例输入（多个用 --- 分隔）</label>
          <textarea v-model="sampleInputs" rows="2" placeholder="1 2&#10;---&#10;3 4"></textarea>
        </div>
        <div class="form-group">
          <label>样例输出（多个用 --- 分隔）</label>
          <textarea v-model="sampleOutputs" rows="2"></textarea>
        </div>
      </div>
      <div class="modal-foot">
        <button class="btn btn-small btn-secondary" @click="emit('close')">取消</button>
        <button class="btn btn-small" :disabled="loading" @click="submit">
          {{ loading ? '保存中...' : '保存' }}
        </button>
      </div>
    </div>
  </div>
</template>
```

## 五、ProblemList.vue 改动（替换 M1.4 占位）

```js
// 删掉 newProblem 的 alert 占位，改为：
const showForm = ref(false)
const formMode = ref('create')
const formInitial = ref(null)

function newProblem() {
  formMode.value = 'create'
  formInitial.value = null
  showForm.value = true
}
function onSaved() { showForm.value = false; load() }  // 创建成功刷新列表
```

模板末尾加 `<ProblemForm v-if="showForm" :mode="formMode" :initial="formInitial" @close="showForm = false" @saved="onSaved" />`。

## 六、style.css 补充（模态框 + 详情展示）

```css
/* 模态框 */
.modal-mask { position: fixed; inset: 0; background: rgba(0,0,0,.4);
              display: flex; align-items: center; justify-content: center; z-index: 100; }
.modal-card { background: #fff; border-radius: 10px; width: 640px; max-width: 95vw;
              max-height: 85vh; display: flex; flex-direction: column; }
.modal-head { display: flex; justify-content: space-between; align-items: center;
              padding: 1rem 1.5rem; border-bottom: 1px solid var(--border); }
.modal-body { padding: 1rem 1.5rem; overflow-y: auto; }
.modal-foot { display: flex; justify-content: flex-end; gap: .6rem;
              padding: 1rem 1.5rem; border-top: 1px solid var(--border); }

/* 详情页题面 */
.problem-text { white-space: pre-wrap; font-family: inherit;
                background: #f9fafb; border: 1px solid var(--border);
                border-radius: 6px; padding: .8rem; }
.sample-block { margin-bottom: .8rem; }
.sample-block pre { background: #f9fafb; border-left: 3px solid var(--primary);
                    padding: .5rem .8rem; margin: .3rem 0; white-space: pre-wrap; }
.submit-placeholder { border: 1px dashed var(--border); border-radius: 6px;
                      padding: 1.5rem; text-align: center; color: var(--muted); }
```

---

## 七、验收清单

1. 列表行点击 → 详情页完整展示（标题/徽章/slug/元信息/标签/题面/格式）
2. 多样例题（P1000 只有 1 个样例——测试数据需补一道含 `---` 多样例的题）→ 分块展示"样例 1/样例 2"
3. 新建题目：列表页按钮 → 模态框 → 填表单（slug 新号 P1005）→ 保存 → 列表出现
4. 表单校验：空标题/空描述/空 slug 被前端拦截 alert
5. slug 格式错（如 P1）→ 后端 400 → 拦截器 alert
6. 编辑：详情页"编辑"→ 预填原值 → 改标题 → 保存 → 详情刷新显示新标题
7. 编辑时 slug 输入框禁用（只读）
8. 删除：确认框 → 确定 → 回列表，题目消失；取消 → 不删
9. 遮罩点击 / ✕ / 取消 → 关闭模态框不保存
10. 提交中按钮禁用防重复
11. 返回按钮回列表

---

## 八、风险与回滚

| 风险 | 对策 |
|------|------|
| 编辑时难度清空无效（后端部分更新语义） | D3 已知限制，文档记录，M1 不做 |
| tags 空串转换 | `filter(Boolean)` 兜底，空标签不进数组 |
| 模态框内容超高 | `.modal-body` overflow-y: auto + max-height 85vh |
| 详情 404（题目被删） | 拦截器 alert，页面停留空白，返回按钮可退出 |
| 遮罩点击误关 | `@click.self` 只响应遮罩本体点击 |

---

## 九、完成后 M1 前端闭环

M1.5 验收通过后，前端 4 页面 + 创建/编辑 + 删除全部完成，对照 DESIGN_FRONTEND_M1.md 第九节 9 项验收清单做最终总验，M1 整体收尾。
