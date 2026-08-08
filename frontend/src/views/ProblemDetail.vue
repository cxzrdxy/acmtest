<script setup>
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { problemApi, submissionApi } from '../api'
import ProblemForm from '../components/ProblemForm.vue'
import CodeEditor from '../components/CodeEditor.vue'
import SubmissionResult from '../components/SubmissionResult.vue'

const route = useRoute()
const router = useRouter()

const problem = ref(null)
const loading = ref(true)
const showForm = ref(false)
const formMode = ref('edit')
const formInitial = ref(null)

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

// 样例切分：按 "---" 分隔，输入输出按下标一一对应
const samples = computed(() => {
  if (!problem.value) return []
  const ins = (problem.value.sampleInputs || '').split('---').map((s) => s.trim())
  const outs = (problem.value.sampleOutputs || '').split('---').map((s) => s.trim())
  return ins.map((input, i) => ({ input, output: outs[i] || '' }))
})

async function load() {
  loading.value = true
  try {
    problem.value = await problemApi.get(route.params.pid)
  } catch {
    // 404 已由拦截器 alert
  } finally {
    loading.value = false
  }
}

async function handleSubmit() {
  if (submitting.value) return
  if (!code.value.trim()) {
    alert('代码不能为空')
    return
  }
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

async function handleDelete() {
  if (!confirm(`确定删除题目 ${problem.value.title} 吗？`)) return
  try {
    await problemApi.remove(problem.value.id)
    router.push('/problems')
  } catch {
    // 拦截器已 alert
  }
}

// 编辑：把当前题目原值交给表单（预填），切到 edit 模式
function openEdit() {
  formInitial.value = problem.value
  formMode.value = 'edit'
  showForm.value = true
}

// 编辑保存成功：关弹窗 + 重新加载详情
function onSaved() {
  showForm.value = false
  load()
}

onMounted(load)

// 组件销毁：停止轮询，避免已销毁组件继续发请求
onUnmounted(stopPolling)
</script>

<template>
  <div v-if="problem" class="card">
    <button class="btn btn-small btn-secondary" @click="router.push('/problems')">← 返回</button>
    <h2 class="mt-1">
      {{ problem.title }}
      <span v-if="problem.difficulty" :class="`badge badge-${problem.difficulty}`">
        {{ problem.difficulty }}★
      </span>
    </h2>
    <p class="text-muted">{{ problem.slug }}</p>
    <p class="mt-1 text-muted">
      时间 {{ problem.timeLimit }}ms · 内存 {{ problem.memoryLimit }}MB · 作者 #{{ problem.authorId }}
    </p>
    <p v-if="problem.tags.length" class="text-muted">
      标签：
      <span v-for="t in problem.tags" :key="t">{{ t }} </span>
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

    <section v-if="samples.length" class="mt-1">
      <h3>样例</h3>
      <div v-for="(s, i) in samples" :key="i" class="sample-block">
        <h4>样例 {{ i + 1 }}</h4>
        <pre>输入：{{ s.input }}</pre>
        <pre>输出：{{ s.output }}</pre>
      </div>
    </section>

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

    <!-- 评测结果区 -->
    <section class="mt-1">
      <h3>评测结果</h3>
      <!-- 无提交：空态 -->
      <p v-if="!result" class="text-muted result-empty">
        提交代码后，评测结果将显示在这里
      </p>
      <!-- 有提交：中间态（排队中/评测中）与终态同一渲染路径，自动流转 -->
      <SubmissionResult v-else :result="result" />
    </section>

    <div class="mt-1">
      <button class="btn btn-small" @click="openEdit">编辑</button>
      <button class="btn btn-small btn-danger" @click="handleDelete">删除</button>
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
