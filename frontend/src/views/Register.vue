<script setup>
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '../stores/auth'

const router = useRouter()
const auth = useAuthStore()

const username = ref('')
const password = ref('')
const email = ref('')
const loading = ref(false)

async function handleRegister() {
  // 空值拦截：用户名/密码必填，邮箱选填
  if (!username.value || !password.value) {
    alert('请输入用户名和密码')
    return
  }
  // 防重复提交
  if (loading.value) return
  loading.value = true
  try {
    // 后端注册成功即返回 token → 自动登录
    await auth.register({
      username: username.value,
      password: password.value,
      email: email.value || null // 空串转 null，对齐后端可空语义
    })
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
    <h2 class="text-center">注册</h2>
    <form @submit.prevent="handleRegister">
      <div class="form-group">
        <label>用户名</label>
        <input v-model="username" type="text" autocomplete="username" />
      </div>
      <div class="form-group">
        <label>密码</label>
        <input v-model="password" type="password" autocomplete="new-password" />
      </div>
      <div class="form-group">
        <label>邮箱（选填）</label>
        <input v-model="email" type="text" autocomplete="email" />
      </div>
      <button class="btn" type="submit" :disabled="loading">
        {{ loading ? '注册中...' : '注册' }}
      </button>
    </form>
    <p class="text-center mt-1 text-muted">
      已有账号？<router-link to="/login">去登录</router-link>
    </p>
  </div>
</template>
