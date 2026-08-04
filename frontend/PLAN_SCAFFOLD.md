# 前端脚手架设计方案（M1.1）

> **目标**：把 `frontend/` 从"0 字节空占位"变成可运行、可 `npm run dev` 的最小 Vue 项目骨架
> **前置**：本机 Node v22.16.0 / npm 10.9.2（已验证）
> **范围**：只搭骨架，页面逻辑在 M1.2+ 实现

---

## 一、现状

`frontend/` 下 14 个文件全部是 0 字节空文件（含 package.json、vite.config.js），目录结构已按 PROJECT_OVERVIEW.md 规划建好。没有任何依赖可装、没有 lockfile。

## 二、方案选择：手动写文件（不跑 `npm create vite`）

| 方案 | 结论 | 理由 |
|------|------|------|
| `npm create vite@latest . -- --template vue` | ❌ 不用 | 会覆盖现有空文件并生成额外文件（public/、HelloWorld.vue 等），结构与规划不一致，还得删 |
| **手动创建全部文件** | ✅ 采用 | 目录结构已存在且与规划一致，逐个写文件完全可控，零覆盖风险 |

## 三、文件清单与内容设计

### 3.1 `package.json`（新建，覆盖空文件）

```json
{
  "name": "acm-frontend",
  "private": true,
  "version": "0.1.0",
  "type": "module",
  "scripts": {
    "dev": "vite",
    "build": "vite build",
    "preview": "vite preview"
  },
  "dependencies": {
    "axios": "^1.7.0",
    "pinia": "^2.2.0",
    "vue": "^3.5.0",
    "vue-router": "^4.4.0"
  },
  "devDependencies": {
    "@vitejs/plugin-vue": "^5.1.0",
    "vite": "^5.4.0"
  }
}
```

版本选择依据：
- **Node 22 兼容**：Vite 5 官方支持 Node 18+，稳定可靠；不追最新版（Vite 6/7）降低未知风险
- **Vue 3.5**：当前稳定线；`<script setup>` 特性完备
- **Pinia 2.x**：稳定线，满足"存 token"这种小场景
- 全部用 `^`（允许小版本升级），安装时 npm 会自动解析 lockfile

### 3.2 `vite.config.js`（新建）

```js
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:8000', changeOrigin: true }
    }
  }
})
```

要点：
- 端口 5173（与后端 CORS 白名单一致，虽然走代理后不再触发 CORS）
- `/api` 代理到后端 8000：开发期前端请求 `http://localhost:5173/api/v1/...` 直接转发到后端，浏览器零跨域问题
- 不配置 build.outDir（默认 `dist/`，已 gitignore）

### 3.3 `index.html`（新建）

```html
<!DOCTYPE html>
<html lang="zh-CN">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>ACM OJ</title>
  </head>
  <body>
    <div id="app"></div>
    <script type="module" src="/src/main.js"></script>
  </body>
</html>
```

要点：`#app` 挂载点 + module script 入口（Vite 约定）。

### 3.4 基础骨架文件（脚手架阶段只写"能跑的最小版"，M1.2 再填充）

| 文件 | 本阶段内容 | 后续补充（M1.2） |
|------|-----------|------------------|
| `src/main.js` | `createApp(App)` + `mount('#app')`（先不挂 router/pinia，骨架期零依赖跑通） | 挂 router + pinia + 全局样式 |
| `src/App.vue` | 最小根组件：`<router-view/>` 占位 + 一行标题 | 顶部导航、退出按钮、布局 |
| `src/router/index.js` | 空路由表（导出 `createRouter` + `createWebHistory`，先不注册任何路由） | 4 条路由 + 登录守卫 |
| `src/stores/auth.js` | 空 store 骨架（`defineStore` 空壳） | token/user 状态与 actions |
| `src/api/index.js` | 空 axios 实例骨架（`axios.create({ baseURL: '/api/v1' })`） | 拦截器 + 业务方法 |
| `src/assets/style.css` | 空文件（保留） | 全局样式 |
| `src/views/*.vue` × 4 | 空文件（保留，不建） | M1.3-M1.5 逐页实现 |
| `src/components/CodeEditor.vue` | 空文件（保留） | M2+ 实现，不动 |

> 决策：**router/store/api 只写空壳不写逻辑**——脚手架阶段的验收标准是"项目能起、能热更新"，业务层留到 M1.2 一次性实现，避免跨阶段半成品。

### 3.5 不新建的文件

- `public/`：无静态资源需求，不建
- `src/assets/logo.*`：无品牌图，不建
- `.gitignore`：根目录已有（覆盖 node_modules/、dist/），不需要前端级

## 四、执行步骤（命令序列）

```bash
# ① 写文件（3.1-3.4 共 7 个：package.json / vite.config.js / index.html
#    / src/main.js / src/App.vue / src/router/index.js / src/stores/auth.js / src/api/index.js）
# ② 安装依赖（workdir: frontend）
npm install
# ③ 验证启动（Ctrl+C 退出）
npm run dev
```

预计：`npm install` 1-2 分钟（国内网络可能需换 registry，npm 默认即可，失败再议）。

## 五、验收标准

1. `npm run dev` 启动无报错，控制台显示 `http://localhost:5173`
2. 浏览器打开 5173 → 页面渲染根组件标题（如 "ACM OJ"），无白屏/报错
3. Vite 热更新生效（改 App.vue 文本 → 浏览器即时刷新）
4. `npm run build` 能产出 dist/（可选验证，构建期不报错）
5. `frontend/node_modules`、`frontend/package-lock.json` 生成（lockfile 建议提交，保证依赖可复现）

## 六、风险与回滚

| 风险 | 影响 | 对策 |
|------|------|------|
| 依赖版本解析失败（registry 网络） | install 报错 | 换国内镜像：`npm config set registry https://registry.npmmirror.com` 后重试 |
| Vite 5 与 Node 22 意外不兼容（概率极低） | dev 启动报错 | 升级 vite 到 ^6/^7 重试（Node 22 完全支持） |
| 覆盖空文件 | 无损失 | 全部目标文件当前为 0 字节，git 可随时回滚（如有提交） |
| package-lock.json 被 gitignore？ | 无 | 根 .gitignore 只忽略 node_modules/ 和 dist/，lockfile 正常跟踪 |

## 七、里程碑确认

M1.1 完成后，进入 M1.2（基础层：拦截器 + store + 守卫 + 全局样式），再 M1.3-M1.5 页面，最后 M1.6 验收清单。每阶段结束给你审阅结果。
