<script setup>
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { problemApi, submissionApi } from '../api'
import ProblemForm from '../components/ProblemForm.vue'
import CodeEditor from '../components/CodeEditor.vue'

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
const result = ref(null)

// 状态徽章映射：颜色 + 用户语言文案
const statusMeta = {
  AC: { cls: 'st-ac', text: '通过' },
  WA: { cls: 'st-wa', text: '答案错误' },
  TLE: { cls: 'st-tle', text: '超时' },
  MLE: { cls: 'st-mle', text: '超内存' },
  RE: { cls: 'st-re', text: '运行错误' },
  CE: { cls: 'st-ce', text: '编译错误' },
  PENDING: { cls: 'st-pending', text: '排队中' },
  JUDGING: { cls: 'st-pending', text: '评测中' }
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
      <p v-if="!result && !submitting" class="text-muted result-empty">
        提交代码后，评测结果将显示在这里
      </p>
      <p v-else-if="submitting" class="text-muted result-empty">正在评测，请稍候...</p>
      <template v-else>
        <div class="result-head">
          <span :class="`badge ${statusMeta[result.status]?.cls ?? 'st-unknown'}`">
            {{ statusMeta[result.status]?.text ?? result.status }}
          </span>
          <span class="text-muted">分数 {{ result.score }}/100</span>
          <span v-if="result.timeMs != null" class="text-muted">最大耗时 {{ result.timeMs }}ms</span>
          <span class="text-muted">提交 #{{ result.id }}</span>
        </div>

        <!-- 编译错误：单独区域展示 -->
        <div v-if="result.status === 'CE'">
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
