import { defineStore } from 'pinia'
import { authApi } from '../api'

export const useAuthStore = defineStore('auth', {
  // 初始化直接读 localStorage：刷新后自动恢复登录态
  state: () => ({
    token: localStorage.getItem('acm_token'),
    user: JSON.parse(localStorage.getItem('acm_user') || 'null')
  }),
  getters: {
    isLoggedIn: (s) => !!s.token
  },
  actions: {
    // 登录/注册成功后：内存 + localStorage 双写（保持两处一致）
    async login(credentials) {
      const data = await authApi.login(credentials) // {accessToken, user, tokenType}
      this.saveAuth(data)
    },
    async register(payload) {
      const data = await authApi.register(payload)
      this.saveAuth(data)
    },
    saveAuth(data) {
      this.token = data.accessToken
      this.user = data.user
      localStorage.setItem('acm_token', data.accessToken)
      localStorage.setItem('acm_user', JSON.stringify(data.user))
    },
    // 退出：内存 + localStorage 双清
    logout() {
      this.token = null
      this.user = null
      localStorage.removeItem('acm_token')
      localStorage.removeItem('acm_user')
    }
  }
})
