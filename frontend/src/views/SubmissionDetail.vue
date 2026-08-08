<script setup>
import { ref, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { submissionApi } from '../api'
import CodeEditor from '../components/CodeEditor.vue'
import SubmissionResult from '../components/SubmissionResult.vue'

const route = useRoute()
const router = useRouter()

const submission = ref(null)
const loading = ref(true)

const langLabel = { cpp17: 'C++17', python3: 'Python3' }

onMounted(async () => {
  try {
    submission.value = await submissionApi.get(route.params.sid)
  } catch {
    // 404 已由拦截器 alert
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <div v-if="submission" class="card">
    <button class="btn btn-small btn-secondary" @click="router.back()">← 返回</button>
    <h2 class="mt-1">
      提交 #{{ submission.id }}
      <router-link :to="`/problems/${submission.problemId}`" class="text-muted link-title">
        {{ submission.problemId }}
      </router-link>
    </h2>
    <p class="text-muted">
      语言 {{ langLabel[submission.language] ?? submission.language }} ·
      提交时间 {{ new Date(submission.createdAt).toLocaleString() }}
    </p>

    <section class="mt-1">
      <h3>提交代码</h3>
      <CodeEditor :model-value="submission.code" :rows="14" readonly />
    </section>

    <section class="mt-1">
      <h3>评测结果</h3>
      <SubmissionResult :result="submission" />
    </section>
  </div>
  <p v-else-if="loading" class="text-center text-muted">加载中...</p>
</template>

<style scoped>
.link-title {
  font-size: 0.85rem;
}
</style>
