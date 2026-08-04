# M1 前端设计方案（Vue 3）

> **目标产出**：登录 / 注册 / 题目列表 / 题目详情 四个页面，支撑 M1 验收标准
> **技术栈**：Vue 3 (Composition API) + Vite 5 + Vue Router 4 + Pinia 2 + axios
> **风格决策**：不引入 UI 框架，纯手写 CSS（个人自用、页面少，零额外依赖）
> **后端契约来源**：DESIGN_M1.md 十四、十五章 + 后端实际路由（代码为准）

---

## 一、技术选型与理由

| 技术 | 版本 | 职责 | 理由 |
|------|------|------|------|
| Vue 3 | 3.4+ | UI 框架 | 项目既定；`<script setup>` 组合式 API |
| Vite | 5.x | 构建/开发服务器 | 项目既定；秒级热更新 |
| Vue Router | 4.x | 路由 | 4 个页面 + 路由守卫 |
| Pinia | 2.x | 状态管理 | 只存 token/用户信息，比 Vuex 简洁 |
| axios | 1.x | HTTP | 拦截器天然适配"自动带 JWT + 统一错误" |

> 不用 element-plus / Naive UI：4 个页面手写 CSS 完全够用，减少依赖与心智负担。

## 二、目录结构（与 PROJECT_OVERVIEW.md 规划一致）

```
frontend/
├── index.html              # HTML 入口（引入 /src/main.js）
├── package.json            # 依赖清单（vite/vue/router/pinia/axios）
├── vite.config.js          # dev server 代理：/api → http://localhost:8000
└── src/
    ├── main.js             # 入口：createApp + router + pinia + 全局样式
    ├── App.vue             # 根组件：顶部导航 + <router-view>
    ├── router/index.js     # 路由表 + 登录守卫
    ├── stores/auth.js      # token/user 状态、登录/注册/退出 action
    ├── api/index.js        # axios 实例 + 请求/响应拦截器
    ├── assets/style.css    # 全局样式（布局/表单/表格/按钮）
    ├── components/
    │   └── ProblemForm.vue # 题目创建/编辑共用表单（新增，M1 需要）
    └── views/
        ├── Login.vue           # 登录页
        ├── Register.vue        # 注册页
        ├── ProblemList.vue     # 题目列表（分页/搜索/筛选）
        └── ProblemDetail.vue   # 题目详情（题面/样例/编辑/删除）
```

> `CodeEditor.vue` 为 M2+ 提交评测用，本期不实现。

## 三、路由设计

| 路径 | 页面 | 守卫 |
|------|------|------|
| `/` | 重定向 → `/problems` | 需登录 |
| `/login` | 登录 | 已登录访问则跳 `/problems` |
| `/register` | 注册 | 同上 |
| `/problems` | 题目列表 | 需登录 |
| `/problems/:pid` | 题目详情（按 Id，支持编辑/删除） | 需登录 |

**守卫逻辑**（`router.beforeEach`）：无 token 且目标页需登录 → 跳 `/login`；有 token 访问 `/login`、`/register` → 跳 `/problems`。

## 四、API 封装（api/index.js）

### 4.1 请求层
- axios 实例 `baseURL: '/api/v1'`（走 Vite 代理，规避 CORS）
- 请求拦截器：从 localStorage 取 token，有则加 `Authorization: Bearer <token>`
- 响应拦截器：统一处理三类错误
  - `401` → 清除 token → 跳 `/login`
  - 业务错误（`{detail: "..."}`）→ 弹 `alert(detail)`（或页面内错误提示）
  - 校验错误（400 `{errors: {字段: [...]}}`）→ 提取第一条展示
- 导出方法：`authApi`（register/login/me/changePassword）+ `problemApi`（list/get/getBySlug/create/update/delete）

### 4.2 与后端接口对照表

| 前端调用 | 后端端点 | 说明 |
|---------|---------|------|
| `authApi.login({username, password})` | POST `/auth/login` | 返回 `{accessToken, user, tokenType}` |
| `authApi.register({username, password, email})` | POST `/auth/register` | 同上（201） |
| `problemApi.list({keyword, tag, difficulty, page, size})` | GET `/problems` | 返回 `{items, total, page, size}` |
| `problemApi.get(pid)` | GET `/problems/{pid}` | 详情 |
| `problemApi.create(data)` | POST `/problems` | 创建（201），body 见 DTO |
| `problemApi.update(pid, data)` | PUT `/problems/{pid}` | 部分更新：null 字段不传 |
| `problemApi.remove(pid)` | DELETE `/problems/{pid}` | 204 |

> 详情页用 `{pid}`（Id）还是 slug：M1 用 Id（`{pid:int}` 路由更简单）；slug 路由 `by-slug/{slug}` 留给 M2 URL 分享。

## 五、状态管理（stores/auth.js）

- state：`token`（localStorage 持久化）、`user`（{id, username, email?}）
- getters：`isLoggedIn`
- actions：
  - `login(credentials)`：调 API → 存 token+user → localStorage
  - `register(payload)`：同上
  - `logout()`：清 token+user → 跳 `/login`
  - `fetchMe()`：页面刷新后用 `/me` 恢复用户信息（可选，M1 存了 user 可不调）

## 六、页面设计

### 6.1 Login.vue
- 表单：用户名 + 密码，`[Required]` 对齐后端校验
- 提交 → `auth.login()` → 跳 `/problems`；失败展示 `detail` 错误
- 底部链接：去注册

### 6.2 Register.vue
- 表单：用户名 + 密码（+ 可选邮箱，后端不强校验）
- 成功 → 自动登录状态 → 跳 `/problems`
- 底部链接：去登录

### 6.3 ProblemList.vue
- 顶部：搜索框（keyword）+ tag 输入 + 难度下拉（1-5，可空）+ 查询按钮
- 主区：题目表格/卡片：slug、标题、难度、时间/内存限制
- 分页：上一页/下一页 + 页码 + 总数（后端已返回 total）
- "新建题目"按钮 → 打开 ProblemForm（创建模式）

### 6.4 ProblemDetail.vue
- 标题 + slug + 难度 + 时间/内存限制 + 作者
- 题面（Description / InputDesc / OutputDesc，Markdown 纯文本展示，M1 不做渲染）
- 样例区块：SampleInputs / SampleOutputs（多个样例以 `---` 分隔，前端切分展示）
- 操作按钮（登录用户均可）：
  - 编辑 → 打开 ProblemForm（更新模式，仅传修改字段）
  - 删除 → confirm 确认 → DELETE → 跳回列表
- 提交区：占位"评测功能 M2 开放"

### 6.5 ProblemForm.vue（新增组件）
- 创建/编辑共用一个表单组件，`mode` prop 区分
- 字段对齐 `ProblemCreate` DTO：slug（正则 P\d{3,5} 提示）、title、description、inputDesc、outputDesc、timeLimit、memoryLimit、tags（逗号分隔）、difficulty、sampleInputs、sampleOutputs
- 编辑模式：预填已有值，保存时只把**非空字段**放进 body（部分更新语义）

## 七、全局样式（assets/style.css）

- 极简布局：顶部导航条（logo + 用户名 + 退出按钮）+ 内容区（max-width 900px 居中）
- 表单 / 表格 / 按钮 / 徽章（难度色块）基础样式
- 无 UI 框架，全部原生 CSS，变量统一 `:root` 定义

## 八、开发与构建

```bash
# 脚手架（package.json 等目前是 0 字节空文件）
cd frontend
npm create vite@latest . -- --template vue      # 或手动创建全部文件
npm install
npm install vue-router@4 pinia axios

# 开发（需要后端：本地 dotnet run 或 docker compose up）
npm run dev                                      # 端口 5173，/api 代理到 8000

# 构建（M1 仅开发联调用，构建产物 dist/ 已 gitignore）
npm run build
```

vite.config.js 关键配置：

```js
server: {
  proxy: {
    '/api': { target: 'http://localhost:8000', changeOrigin: true }
  }
}
```

> 走代理后浏览器无跨域问题，后端 CORS 白名单（5173）仍然保留，两者不冲突。

## 九、联调验收清单（对照 M1 验收标准）

1. 注册新用户 → 自动登录跳列表
2. 退出 → 访问 `/problems` 被拦截跳登录
3. 登录 → 创建题目（P1001）→ 列表出现
4. 列表：keyword 搜索、tag 筛选、难度筛选、分页翻页
5. 详情页：题面/样例展示正确
6. 编辑标题 → 列表同步变化
7. 删除题目 → 列表消失
8. 未登录访问任意页面 → 跳登录
9. 无 token 调接口 → 401 被拦截器捕获跳登录

## 十、实现顺序（里程碑）

| 阶段 | 内容 | 验证 |
|------|------|------|
| M1.1 脚手架 | 全部目录文件 + 依赖 + vite 代理 | `npm run dev` 起得来 |
| M1.2 基础层 | api 封装 + auth store + router + 守卫 + 样式 | 空页面可导航 |
| M1.3 认证页 | Login + Register | 注册登录跳转成功 |
| M1.4 题目列表 | ProblemList（分页/搜索/筛选） | 后端数据展示正确 |
| M1.5 题目详情 | ProblemDetail + ProblemForm（创建/编辑/删除） | 全 CRUD 走通 |
| M1.6 验收 | 按第九节清单逐项过 | 全部通过 |

## 十一、M2 预留（本期不实现）

- 提交评测页面、CodeEditor.vue（代码编辑器）
- 题面 Markdown 渲染（marked/markdown-it）
- 按 slug 的 URL 分享路由（`/problems/P1000`）
- 用户改密入口（后端接口已存在）
