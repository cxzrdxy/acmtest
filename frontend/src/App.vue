<script setup>
import { useAuthStore } from './stores/auth'
import { useRouter } from 'vue-router'

const auth = useAuthStore()
const router = useRouter()

// 头像首字母（取用户名首字符大写）
const avatarLetter = () => (auth.user?.username?.[0] ?? '?').toUpperCase()

function handleLogout() {
  auth.logout()
  router.push('/login')
}
</script>

<template>
  <header class="nav">
    <span class="brand">ACM<span class="accent">OJ</span></span>
    <nav class="nav-links">
      <template v-if="auth.isLoggedIn">
        <router-link to="/problems">题目</router-link>
        <router-link to="/submissions">提交记录</router-link>
        <span class="username">
          <span class="avatar">{{ avatarLetter() }}</span>
          {{ auth.user?.username }}
        </span>
        <button class="btn btn-small btn-secondary" @click="handleLogout">退出</button>
      </template>
      <template v-else>
        <router-link to="/login">登录</router-link>
        <router-link to="/register">注册</router-link>
      </template>
    </nav>
  </header>
  <main class="content">
    <router-view />
  </main>
</template>
