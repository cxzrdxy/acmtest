import { createRouter, createWebHistory } from 'vue-router'
import Login from '../views/Login.vue'
import Register from '../views/Register.vue'
import ProblemList from '../views/ProblemList.vue'
import ProblemDetail from '../views/ProblemDetail.vue'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', redirect: '/problems' },
    { path: '/login', component: Login, meta: { public: true } },
    { path: '/register', component: Register, meta: { public: true } },
    { path: '/problems', component: ProblemList },
    { path: '/problems/:pid', component: ProblemDetail }
  ]
})

// 登录守卫（白名单制）：默认全部路由需登录，公共页显式标记 public
router.beforeEach((to) => {
  const hasToken = !!localStorage.getItem('acm_token')
  if (!to.meta.public && !hasToken) return '/login' // 未登录访问受保护页 → 登录
  if (to.meta.public && hasToken) return '/problems' // 已登录访问登录/注册页 → 列表
})

export default router
