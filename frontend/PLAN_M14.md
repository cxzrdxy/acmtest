# 前端 M1.4 题目列表页设计方案

> **目标**：实现题目列表页——筛选、分页、列表展示、新建入口
> **范围**：`src/views/ProblemList.vue` + `style.css` 补 ~6 行工具类
> **前置**：M1.2/M1.3 完成（problemApi.list 已封装、路由/样式就位、M1.3 拦截器 401 修复）

---

## 一、页面功能清单

| 区域 | 功能 | 细节 |
|------|------|------|
| 筛选区 | keyword 搜索 | 标题模糊匹配（后端 ILIKE，大小写不敏感） |
| 筛选区 | tag 筛选 | 单 tag 文本输入（后端数组包含查询） |
| 筛选区 | difficulty 下拉 | 全部 / 1~5 星（不传 = 不限） |
| 筛选区 | 查询 / 重置按钮 | 查询=带条件重新加载；重置=清空条件 |
| 主区 | 题目表格 | slug、标题、难度（徽章）、时间/内存限制 |
| 主区 | 行点击 | 跳转 `/problems/:pid` 详情页（M1.5 实现） |
| 主区 | 空列表提示 | "暂无题目" |
| 分页区 | 上一页/下一页 + 页码 | "共 X 题 · 第 Y / Z 页" |
| 工具栏 | 新建题目按钮 | 本阶段占位（alert 提示 M1.5 实现） |

---

## 二、核心设计决策

### D1. 筛选触发用"查询按钮"，不做实时搜索

**不做** keyword 输入防抖实时请求（那是 element-plus + 复杂交互场景）。个人自用：输入条件 → 点查询 → 请求。简化且意图明确。

### D2. 筛选条件变化时重置页码

```js
async function doSearch() {
  page.value = 1            // 换条件必须回到第 1 页
  await load()
}
```

翻页按钮只改 page 不重置（保留当前筛选条件，状态在同一组件内天然保留）。

### D3. difficulty 空值处理（对齐后端）

```js
const params = { page: page.value, size: 20 }
if (keyword.value.trim())  params.keyword = keyword.value.trim()
if (tag.value.trim())      params.tag = tag.value.trim()
if (difficulty.value)      params.difficulty = difficulty.value
// 空值不传 → 后端 [FromQuery] 收不到 → 不加筛选条件（与 M1 后端逻辑对齐）
```

下拉"全部"的 value 用空字符串 `''`，`if (difficulty.value)` 自动跳过 → **空值不进入 params**，而不是传 `difficulty=''`（那会触发 Range 校验 400）。

### D4. 分页计算

```js
const totalPages = computed(() => Math.ceil(total.value / size) || 1)
// 上一页 disabled: page===1；下一页 disabled: page===totalPages
```

后端返回 `{items, total, page, size}`，前端只负责翻页 UI，不做任何计算逻辑之外的事。

### D5. 行点击跳详情，URL 用 Id

```js
function goDetail(row) { router.push(`/problems/${row.id}`) }
```

路由 `/problems/:pid` 已在 M1.2 建好；详情页 M1.5 实现。M1 用 Id（自增主键），slug 路由留给 M2 URL 分享。

### D6. 页面不写 style 块（延续 D4 约定）

筛选区布局需要 flex——全局样式补一个 `.toolbar` 工具类（6 行），表格/徽章/分页/按钮全部复用已有类。

### D7. 不写 URL query 同步

刷新页面后筛选条件丢失（个人自用可接受）。M2 再考虑把 keyword/page 同步到 URL（支持分享/回退）。

---

## 三、页面骨架

```
┌──────────────────────────────────────────────┐
│ 工具栏: [搜索框] [tag输入] [难度▼] [查询][重置] [新建题目] │
├──────────────────────────────────────────────┤
│ 表格:                                          │
│  ID  Slug   标题       难度  时间  内存       │
│  1   P1000  A+B        ★1   1000ms  256MB    │
│  2   P1001  最长子序列  ★3   2000ms  512MB    │
│  ...                                          │
│  （空时显示"暂无题目"）                          │
├──────────────────────────────────────────────┤
│ 分页: 共 137 题 · 第 2/7 页  [上一页][下一页]     │
└──────────────────────────────────────────────┘
```

## 四、ProblemList.vue 详细设计

```vue
<script setup>
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { problemApi } from '../api'

const router = useRouter()

// 筛选条件（表单输入）
const keyword = ref('')
const tag = ref('')
const difficulty = ref('')      // '' = 全部

// 列表数据
const items = ref([])
const total = ref(0)
const page = ref(1)
const size = 20
const loading = ref(false)

// D4 分页计算
const totalPages = computed(() => Math.ceil(total.value / size) || 1)

// D2/D3 组装参数 + 请求
async function load() {
  loading.value = true
  try {
    const params = { page: page.value, size }
    if (keyword.value.trim()) params.keyword = keyword.value.trim()
    if (tag.value.trim()) params.tag = tag.value.trim()
    if (difficulty.value) params.difficulty = difficulty.value
    const data = await problemApi.list(params)   // {items, total, page, size}
    items.value = data.items
    total.value = data.total
  } catch {
    // 错误已由拦截器 alert
  } finally {
    loading.value = false
  }
}

function doSearch() {          // 查询：重置到第 1 页
  page.value = 1
  load()
}
function reset() {             // 重置：清空条件再查
  keyword.value = ''
  tag.value = ''
  difficulty.value = ''
  doSearch()
}
function prev() { if (page.value > 1) { page.value--; load() } }
function next() { if (page.value < totalPages.value) { page.value++; load() } }
function goDetail(row) { router.push(`/problems/${row.id}`) }
function newProblem() { alert('新建题目功能在 M1.5 实现') }

onMounted(load)                // 进入页面加载第一页
</script>

<template>
  <div>
    <!-- 筛选工具栏 -->
    <div class="toolbar mb-1">
      <input v-model="keyword" type="text" placeholder="搜索标题" class="toolbar-input" />
      <input v-model="tag" type="text" placeholder="标签（如 入门）" class="toolbar-input" />
      <select v-model="difficulty" class="toolbar-input">
        <option value="">全部难度</option>
        <option v-for="d in [1,2,3,4,5]" :key="d" :value="d">{{ d }}★</option>
      </select>
      <button class="btn btn-small" @click="doSearch">查询</button>
      <button class="btn btn-small btn-secondary" @click="reset">重置</button>
      <span class="flex-1"></span>
      <button class="btn btn-small" @click="newProblem">新建题目</button>
    </div>

    <!-- 表格 -->
    <table>
      <thead>
        <tr><th>Slug</th><th>标题</th><th>难度</th><th>时间限制</th><th>内存限制</th></tr>
      </thead>
      <tbody>
        <tr v-for="p in items" :key="p.id" @click="goDetail(p)" style="cursor:pointer">
          <td>{{ p.slug }}</td>
          <td>{{ p.title }}</td>
          <td>
            <span v-if="p.difficulty" :class="`badge badge-${p.difficulty}`">{{ p.difficulty }}★</span>
            <span v-else class="text-muted">未评级</span>
          </td>
          <td>{{ p.timeLimit }}ms</td>
          <td>{{ p.memoryLimit }}MB</td>
        </tr>
      </tbody>
    </table>
    <p v-if="!loading && items.length === 0" class="text-center text-muted mt-1">暂无题目</p>
    <p v-if="loading" class="text-center text-muted mt-1">加载中...</p>

    <!-- 分页 -->
    <div class="pagination">
      <button class="btn btn-small btn-secondary" :disabled="page === 1" @click="prev">上一页</button>
      <span>共 {{ total }} 题 · 第 {{ page }} / {{ totalPages }} 页</span>
      <button class="btn btn-small btn-secondary" :disabled="page === totalPages" @click="next">下一页</button>
    </div>
  </div>
</template>
```

### 4.1 style.css 补充（6 行）

```css
/* 筛选工具栏（flex 一行布局） */
.toolbar { display: flex; align-items: center; gap: 0.6rem; }
.toolbar-input { width: auto; min-width: 140px; }
.flex-1 { flex: 1; }
```

> 行点击用 `style="cursor:pointer"` 内联样式（1 处，不另建类）；D6 约定页面无 style 块。

---

## 五、与后端接口对齐（problemApi.list）

| 前端传参 | 后端接收 | 后端行为 |
|---------|---------|---------|
| `keyword`（有值时传） | `[FromQuery] string? keyword` | 标题 ILIKE 模糊匹配 |
| `tag`（有值时传） | `[FromQuery] string? tag` | tags 数组包含 |
| `difficulty`（非空时传） | `[FromQuery, Range(1,5)] short?` | 等值筛选 |
| `page`（1 起） | `[FromQuery] int page = 1` | 页码 |
| `size`（固定 20） | `[FromQuery] int size = 20` | 每页条数 |
| — | 响应 | `{items, total, page, size}` |

---

## 六、验证方式

```bash
# 后端 + 数据库已就绪（5433），前端 npm run dev 已在跑
```

手动验收清单：
1. 登录后进列表页 → 表格显示已有题目（先用 Swagger 或 API 创建 2-3 题测试数据）
2. 空库时显示"暂无题目"
3. keyword 搜索"数字" → 只显示标题含数字的题
4. tag 筛选"入门" → 只显示带该标签的题
5. 难度筛选 1★ → 只显示难度 1
6. 条件组合（keyword + tag + 难度）生效
7. 翻页：第 1 页只有 1 条时"下一页"禁用；共 137 题时第 2 页显示正确
8. 重置按钮清空条件并回到第 1 页
9. 行点击跳转 `/problems/:id`（详情页显示占位即可）
10. 新建按钮 alert 占位提示
11. 未登录访问 → 守卫拦回登录页

> 测试数据准备：登录后用 Swagger（http://localhost:8000/swagger）POST 几道题，覆盖不同难度/标签/标题。

---

## 七、风险与回滚

| 风险 | 对策 |
|------|------|
| difficulty 传空串触发后端 Range 校验 400 | D3 已处理：空值不进 params |
| 筛选条件与翻页状态错乱 | D2：查询重置页码；翻页保留条件 |
| 行点击误触（如点按钮触发跳转） | 表格行绑定 @click，按钮不在表格内 |
| 总页数为 0（空库） | `|| 1` 兜底，显示"第 1 / 1 页" |

---

## 八、完成后进入 M1.5（详情 + 表单）

M1.5 实现 ProblemDetail.vue（题面/样例/编辑/删除）+ ProblemForm.vue（创建/编辑共用表单），届时替换"新建题目"占位按钮、接通行点击跳转的目标页。
