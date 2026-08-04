<script setup>
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '../stores/auth'

const router = useRouter()
const auth = useAuthStore()

const username = ref('')
const password = ref('')
const loading = ref(false)

async function handleLogin() {
  // 空值拦截：不发无效请求，其余校验由后端 DTO 兜底（拦截器 alert）
  if (!username.value || !password.value) {
    alert('请输入用户名和密码')
    return
  }
  // 防重复提交
  if (loading.value) return
  loading.value = true
  try {
    await auth.login({ username: username.value, password: password.value })
    router.push('/problems')
  } catch {
    // 错误已由拦截器 alert，这里吞掉避免未处理 Promise
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="card narrow-card">
    <h2 class="text-center">登录</h2>
    <form @submit.prevent="handleLogin">
      <div class="form-group">
        <label>用户名</label>
        <input v-model="username" type="text" autocomplete="username" />
      </div>
      <div class="form-group">
        <label>密码</label>
        <input v-model="password" type="password" autocomplete="current-password" />
      </div>
      <button class="btn" type="submit" :disabled="loading">
        {{ loading ? '登录中...' : '登录' }}
      </button>
    </form>
    <p class="text-center mt-1 text-muted">
      还没有账号？<router-link to="/register">去注册</router-link>
    </p>
  </div>
</template>
