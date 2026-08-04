# 前端 M1.2 基础层设计方案

> **目标**：打通"请求 → 鉴权 → 状态 → 路由守卫 → 样式"全部基础设施
> **范围**：api 拦截器 + auth store + 路由表/守卫 + 全局样式 + 入口装配 + 导航骨架
> **前置**：M1.1 脚手架已完成（Vue 3.5 + Vite 5.4 + Pinia + Router + axios 已装）

---

## 一、本阶段 6 个文件（全部覆盖脚手架阶段的空壳）

| 文件 | 当前 | 本阶段产出 |
|------|------|-----------|
| `src/api/index.js` | 空 axios 实例 | 拦截器 + 全部业务方法（authApi/problemApi） |
| `src/stores/auth.js` | 空 store | token/user 状态 + login/register/logout actions |
| `src/router/index.js` | 空路由表 | 5 条路由 + 登录守卫 |
| `src/App.vue` | 一行标题 | 顶部导航（标题/用户名/退出）+ router-view |
| `src/main.js` | 仅挂载 App | 挂 pinia + router + 全局样式 |
| `src/assets/style.css` | 空文件 | 全局基础样式 |

---

## 二、核心设计决策

### D1. api 层不依赖 store，直接读写 localStorage

**问题**：如果 `api/index.js` 里 `import { useAuthStore }`，而 store 里 `import authApi`，会形成**循环依赖**（api → store → api），Vite 下运行时易出错。

**方案**：分层解耦——
- **api 层**：直接读写 `localStorage`（键 `acm_token`），401 时清 token + 跳登录页。不 import store、不 import router（跳转用 `window.location`，避免依赖 router 实例）。
- **store 层**：只负责 UI 状态（Vue 组件里的响应式 token/user），内部同样读写 localStorage。

数据流：组件 → store（状态）→ api（发请求，自管 token）→ 后端。两层不互相 import。

### D2. 401 用 `window.location.href` 硬跳转

**问题**：拦截器里想用 `router.push('/login')` 需要 import router 实例，同样有循环依赖风险。

**方案**：`window.location.href = '/login'`。硬跳转会整页刷新，代价可接受（401 本身就是异常路径，一年碰不了几次），换来的是 api 模块完全独立、零循环依赖。

### D3. 错误提示统一 `alert()`

- 业务错误（`{detail}`）→ `alert(detail)`
- 400 校验错误（`{errors: {字段: [..]}}`）→ 取第一条消息 `alert`
- 网络错误/5xx → `alert('网络错误或服务器异常')`
- 个人自用项目，`alert` 足够；将来要美化再统一换组件

### D4. 路由默认需登录（白名单制）

**方案**：默认所有路由需登录，公共页显式标记 `meta: { public: true }`：

```js
routes: [
  { path: '/', redirect: '/problems' },
  { path: '/login', component: Login, meta: { public: true } },
  { path: '/register', component: Register, meta: { public: true } },
  { path: '/problems', component: ProblemList },
  { path: '/problems/:pid', component: ProblemDetail }
]
```

守卫逻辑（白名单制更安全，新增页面忘加 meta 时自动被保护）：

```js
router.beforeEach((to) => {
  const hasToken = !!localStorage.getItem('acm_token')
  if (!to.meta.public && !hasToken) return '/login'   // 未登录访问受保护页 → 登录
  if (to.meta.public && hasToken) return '/problems'  // 已登录访问登录/注册页 → 列表
})
```

### D5. 刷新恢复登录态不调 /me

登录时 store 已把 user 存进 localStorage，刷新后直接读回，省一次请求。`/me` 接口留给将来需要校验 token 有效性时用（M1 不做）。

### D6. store 用 Option 写法（与 M1.1 骨架一致）

```js
export const useAuthStore = defineStore('auth', {
  state: () => ({ token: null, user: null }),
  getters: { isLoggedIn: (s) => !!s.token },
  actions: { login(), register(), logout() }
})
```

---

## 三、各文件详细设计

### 3.1 `src/api/index.js`

```js
import axios from 'axios'

const http = axios.create({ baseURL: '/api/v1' })

// 请求拦截器：自动附加 JWT
http.interceptors.request.use((config) => {
  const token = localStorage.getItem('acm_token')
  if (token) config.headers.Authorization = `Bearer ${token}`
  return config
})

// 响应拦截器：统一错误处理
http.interceptors.response.use(
  (resp) => resp.data,                      // 成功：直接返回 data
  (err) => {
    const { response } = err
    if (response) {
      if (response.status === 401) {        // 未登录/过期：清 token 跳登录
        localStorage.removeItem('acm_token')
        localStorage.removeItem('acm_user')
        window.location.href = '/login'
        return Promise.reject(err)
      }
      const detail = response.data?.detail           // 业务错误 {detail}
        ?? Object.values(response.data?.errors ?? {}).flat()[0]  // 校验错误第一条
        ?? '请求失败'
      alert(detail)
    } else {
      alert('网络错误或服务器异常')
    }
    return Promise.reject(err)
  }
)

// 鉴权接口
export const authApi = {
  register: (data) => http.post('/auth/register', data),
  login:    (data) => http.post('/auth/login', data),
  me:       ()     => http.get('/auth/me'),
  changePassword: (data) => http.put('/auth/change-password', data)
}

// 题目接口（所有参数对齐后端 DTO）
export const problemApi = {
  list:  (params) => http.get('/problems', { params }),   // {keyword,tag,difficulty,page,size}
  get:   (pid)    => http.get(`/problems/${pid}`),
  getBySlug: (slug) => http.get(`/problems/by-slug/${slug}`),
  create: (data)  => http.post('/problems', data),
  update: (pid, data) => http.put(`/problems/${pid}`, data),
  remove: (pid)   => http.delete(`/problems/${pid}`)
}

export default http
```

设计要点：
- 成功拦截器 `return resp.data`——所有调用方直接拿 `{accessToken, user}` / `{items, total}`，不碰 `resp.data.data` 嵌套
- 后端错误契约两类都处理：`{detail}`（业务）与 `{errors}`（校验 400）
- 401 清双键（token + user），保证 store 和 localStorage 一致

### 3.2 `src/stores/auth.js`

```js
import { defineStore } from 'pinia'

export const useAuthStore = defineStore('auth', {
  state: () => ({
    token: localStorage.getItem('acm_token'),
    user: JSON.parse(localStorage.getItem('acm_user') || 'null')
  }),
  getters: {
    isLoggedIn: (s) => !!s.token
  },
  actions: {
    async login(credentials) {
      const data = await authApi.login(credentials)   // {accessToken, user, tokenType}
      this.token = data.accessToken
      this.user = data.user
      localStorage.setItem('acm_token', data.accessToken)
      localStorage.setItem('acm_user', JSON.stringify(data.user))
    },
    async register(payload) { ...同 login 逻辑... },
    logout() {
      this.token = null
      this.user = null
      localStorage.removeItem('acm_token')
      localStorage.removeItem('acm_user')
    }
  }
})
```

设计要点：
- **state 初始化直接读 localStorage**：页面刷新后 store 自动恢复登录态（无需额外代码）
- `isLoggedIn` 由 token 推导，页面守卫/导航都用它
- actions 里调用 api → 成功后同步写 store + localStorage（两处一致）

### 3.3 `src/router/index.js`

```js
import { createRouter, createWebHistory } from 'vue-router'
import Login from '../views/Login.vue'
import Register from '../views/Register.vue'
import ProblemList from '../views/ProblemList.vue'
import ProblemDetail from '../views/ProblemDetail.vue'

const router = createRouter({
  history: createWebHistory(),
  routes: [ /* D4 的 5 条路由 */ ]
})

router.beforeEach((to) => { /* D4 的守卫 */ })
export default router
```

> 页面组件此时还是空文件——守卫逻辑先行就位，页面 M1.3+ 填充。**空组件 import 进来也能编译**（Vue 空 SFC 合法），导航已可跑通。

### 3.4 `src/App.vue`（导航骨架）

```vue
<script setup>
import { useAuthStore } from './stores/auth'
import { useRouter } from 'vue-router'
const auth = useAuthStore()
const router = useRouter()
function handleLogout() { auth.logout(); router.push('/login') }
</script>

<template>
  <header class="nav">
    <span class="brand">ACM OJ</span>
    <template v-if="auth.isLoggedIn">
      <span>{{ auth.user?.username }}</span>
      <button @click="handleLogout">退出</button>
    </template>
    <template v-else>
      <router-link to="/login">登录</router-link>
      <router-link to="/register">注册</router-link>
    </template>
  </header>
  <main class="content"><router-view /></main>
</template>
```

### 3.5 `src/main.js`

```js
import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import router from './router'
import './assets/style.css'

createApp(App).use(createPinia()).use(router).mount('#app')
```

### 3.6 `src/assets/style.css`（全局基础样式）

```css
:root {
  --bg: #f5f6f8; --card: #fff; --primary: #2563eb;
  --border: #e2e5e9; --text: #1f2937; --danger: #dc2626;
}
* { margin: 0; padding: 0; box-sizing: border-box; }
body { font-family: system-ui, sans-serif; background: var(--bg); color: var(--text); }
/* .nav（顶部条）+ .content（居中 900px）+ 表单/表格/按钮/徽章/错误提示 基础类 */
```

覆盖：导航条、内容容器、input/button/表格/徽章（难度色块）、禁用态、分页按钮。

---

## 四、验证方式（后端需要跑起来）

```bash
# 后端（本地需设连接串环境变量，见 AGENTS.md）
$env:ConnectionStrings__Default="Host=localhost;Port=5432;Database=acm;Username=acm;Password=acm"
docker compose up -d db        # 数据库
dotnet run                     # workdir: backend/Acm.Api

# 前端
npm run dev                    # workdir: frontend
```

手动验证清单（M1.2 阶段，页面未实现也能验一半）：
1. 未登录访问 `/` → 被守卫重定向 `/login`（页面空白但 URL 正确，M1.3 才有表单）
2. 已登录（手动往 localStorage 塞 token）访问 `/login` → 被守卫弹回 `/problems`
3. 伪造 token 调 `/api/v1/problems` → 401 → 拦截器清 token → 跳登录
4. 导航栏登录/注册/退出按钮状态切换正确
5. 无后端时（后端没起）请求 → 提示"网络错误或服务器异常"

> 完整联调（表单登录等）留到 M1.3 页面实现后统一验收。

---

## 五、风险与回滚

| 风险 | 对策 |
|------|------|
| 循环依赖（api/store/router 互相 import） | D1/D2 已从设计上杜绝：api 只碰 localStorage + window.location |
| 空 SFC import 导致构建报错 | 验证过：Vue 空 SFC 合法，M1.1 build 已含空组件通过 |
| 守卫死循环（跳转来回弹） | 白名单制 + 已登录访问 public 页重定向，路径无环 |
| localStorage JSON 解析失败（手改坏数据） | `|| 'null'` 兜底，解析失败得到 null |

---

## 六、完成后进入 M1.3（登录/注册页）

M1.2 验收通过后，M1.3 实现 Login.vue + Register.vue（表单 + 调 store），届时 2、3、4 条手动验证项闭环。
