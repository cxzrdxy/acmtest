# 前端 M1.3 认证页设计方案

> **目标**：实现登录页 + 注册页，打通"表单 → store → api → 后端 → 跳转"完整认证闭环
> **范围**：`src/views/Login.vue` + `src/views/Register.vue`（各 ~80 行，纯页面）
> **前置**：M1.2 已完成（api 拦截器 / auth store / 路由守卫 / 全局样式全就位）

---

## 一、页面功能清单

| 页面 | 功能 | 边界 |
|------|------|------|
| Login.vue | 用户名+密码登录 → 跳列表页 | 失败提示（拦截器 alert） |
| Login.vue | 底部"去注册"链接 | — |
| Register.vue | 用户名+密码(+可选邮箱)注册 → 自动登录跳列表页 | 失败提示（拦截器 alert） |
| Register.vue | 底部"去登录"链接 | — |

> 已登录用户访问这两个页面会被路由守卫弹回 `/problems`（M1.2 已实现），页面无需自处理。

---

## 二、核心设计决策

### D1. 前端只做"空值拦截"，格式校验交给后端

```js
// 前端：只检查必填
if (!username.value || !password.value) {
  alert('请输入用户名和密码')
  return
}
// 其余（长度、slug 格式等）由后端 DTO 校验 → 400 {errors} → 拦截器 alert 第一条
```

**理由**：后端已是校验的权威（DataAnnotations 强校验），前端重复实现 `MaxLength` 等纯属双份维护。前端只做**防无效请求**（空值不发请求），规则校验后端兜底。个人自用够用。

### D2. 提交中禁用按钮（防重复提交）

```js
const loading = ref(false)
async function submit() {
  if (loading.value) return
  loading.value = true
  try { ... } finally { loading.value = false }
}
```

按钮 `:disabled="loading"`。理由：登录是慢操作（bcrypt 验证毫秒级+网络），连点会发两个请求。

### D3. 成功跳转用 `router.push`（SPA 内跳转）

```js
await auth.login({ username: ..., password: ... })
router.push('/problems')
```

和 401 拦截器的 `window.location.href` 区别：这是**正常流程**，用 SPA 路由平滑切换，无整页刷新；硬跳转只留给异常路径（401）。

### D4. 复用全局样式，页面内不写 style 块

页面只用全局类（`.card` `.form-group` `.btn` `.text-center` 等），**不带 `<style>` 块**。理由：两个页面 4 个控件，全局样式完全覆盖；页面私有样式留给 M1.4/M1.5 的复杂布局。

### D5. 不用第三方表单库

不引 vee-validate / element-plus 等。理由：`<script setup>` + ref + v-model 原生写法 20 行搞定，第三方库反而引入学习成本。若将来表单复杂化再引入。

---

## 三、Login.vue 详细设计

```vue
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
  // D1：空值拦截
  if (!username.value || !password.value) {
    alert('请输入用户名和密码')
    return
  }
  // D2：防重复提交
  if (loading.value) return
  loading.value = true
  try {
    await auth.login({ username: username.value, password: password.value })
    router.push('/problems')   // D3：成功 → 列表页
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="card login-card">
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
```

设计要点：
- `@submit.prevent`：表单提交不刷新页面，走 JS 逻辑
- `v-model` 双向绑定：输入实时进 ref
- 失败处理**不写在这**：拦截器统一 alert（M1.2 D3），页面只管成功路径
- `login-card` 宽度居中——唯一需要微调的地方，用全局类组合（`max-width: 400px; margin: 4rem auto`）不行的话给全局样式加一个 `.narrow-card` 工具类（在 style.css 里加 2 行，比页面私有 style 更符合 D4）

### 3.1 对全局样式的小补充（style.css 加 3 行）

```css
/* 窄卡片（登录/注册页居中） */
.narrow-card { max-width: 400px; margin: 3rem auto; }
```

### 3.2 页面宽度处理（可选微调）

Login/Register 共用 `.card.narrow-card` 类即可。

---

## 四、Register.vue 详细设计

与 Login 几乎同构，差异点：

```vue
<script setup>
// 相同结构 + 一个可选邮箱
const email = ref('')

async function handleRegister() {
  if (!username.value || !password.value) {
    alert('请输入用户名和密码')
    return
  }
  if (loading.value) return
  loading.value = true
  try {
    // 后端注册成功即返回 token → 自动登录（无需再调 login）
    await auth.register({
      username: username.value,
      password: password.value,
      email: email.value || null   // 可选：空串转 null
    })
    router.push('/problems')
  } finally {
    loading.value = false
  }
}
</script>
```

差异点说明：
1. **多一个邮箱输入框**（可选，label 标注"选填"）
2. **调 `auth.register` 而非 login**——后端注册接口（POST /auth/register）成功就直接返回 `{accessToken, user}`，store 的 `saveAuth` 双写后即为登录态，"注册即自动登录"零额外请求
3. `email: email.value || null`：空串转 null，对齐后端 `string? Email = null` 可空语义
4. 表单：用户名/密码/邮箱 + 提交按钮 + "已有账号？去登录"

---

## 五、与已有模块的对接点（全部已就绪）

| 依赖 | 文件 | 状态 |
|------|------|------|
| `auth.login / auth.register` | stores/auth.js | ✅ M1.2 已实现 |
| 错误 alert | api/index.js 拦截器 | ✅ M1.2 已实现 |
| 成功跳转目标 `/problems` | router | ✅ 路由已存在 |
| 守卫（已登录弹回） | router.beforeEach | ✅ 已实现 |
| 全局样式类 | style.css | ✅ 需补 `.narrow-card` |

> 本阶段**零后端改动、零新增依赖文件**，只写 2 个页面 + 3 行 CSS。

---

## 六、验证方式（需后端 + 数据库跑起来）

```bash
# 后端
docker compose up -d db        # 已启动
$env:ConnectionStrings__Default="Host=localhost;Port=5433;Database=acm;Username=acm;Password=acm"
dotnet run                     # workdir: backend/Acm.Api

# 前端
npm run dev                    # workdir: frontend
```

手动验收清单：
1. 未登录访问 `/` → 弹回 `/login` → 显示登录表单
2. 空表单点登录 → alert "请输入用户名和密码"（不发请求）
3. 输入错误密码 → 后端 401 → 拦截器 alert "用户名或密码错误"
4. 输入正确 → 跳 `/problems` → 导航栏显示用户名 + 退出按钮
5. 已登录访问 `/login` → 弹回 `/problems`
6. 点退出 → 回登录页，导航栏变"登录/注册"链接
7. 注册页：空邮箱可注册成功；重复用户名 → alert "用户名已存在"
8. 注册成功自动登录跳列表页（无需再登录一次）
9. 提交中按钮禁用，连点不重复发请求

> 列表页此时还是占位模板（M1.4 实现），跳转后显示"题目列表页（M1.4 实现）"即视为跳转成功。

---

## 七、风险与回滚

| 风险 | 对策 |
|------|------|
| 后端未启动时点提交 | 拦截器兜底 alert"网络错误或服务器异常"，无崩溃 |
| 401 被拦截器硬跳 + 页面内也处理 → 双重跳转 | 页面内**不写**错误处理，只走成功路径（D3 已约定） |
| 邮箱填了格式错的内容 | 后端不强校验（设计如此），直接接受 |
| 样式轻微不居中 | `.narrow-card` 工具类统一处理，不写页面私有 style |

---

## 八、完成后进入 M1.4（题目列表页）

M1.4 实现 ProblemList.vue：keyword/tag/difficulty 筛选 + 分页 + 新建入口，届时用上 problemApi.list + `{params}` 查询参数语法。
