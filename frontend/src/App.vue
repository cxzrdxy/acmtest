<script setup>
import { useAuthStore } from './stores/auth'
import { useRouter } from 'vue-router'

const auth = useAuthStore()
const router = useRouter()

function handleLogout() {
  auth.logout()
  router.push('/login')
}
</script>

<template>
  <header class="nav">
    <span class="brand">ACM OJ</span>
    <nav class="nav-links">
      <template v-if="auth.isLoggedIn">
        <router-link to="/problems">题目</router-link>
        <span class="username">{{ auth.user?.username }}</span>
        <button class="btn btn-small" @click="handleLogout">退出</button>
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
