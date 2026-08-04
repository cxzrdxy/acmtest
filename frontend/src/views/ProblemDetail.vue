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
const formMode = ref('edit')
const formInitial = ref(null)

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

    <div class="mt-1">
      <button class="btn btn-small" @click="openEdit">编辑</button>
      <button class="btn btn-small btn-danger" @click="handleDelete">删除</button>
    </div>

    <div class="submit-placeholder mt-1">提交评测功能 M2 开放</div>
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
