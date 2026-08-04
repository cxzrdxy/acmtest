<script setup>
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { problemApi } from '../api'
import ProblemForm from '../components/ProblemForm.vue'

const router = useRouter()

// 筛选条件（表单输入）
const keyword = ref('')
const tag = ref('')
const difficulty = ref('') // '' = 全部

// 列表数据
const items = ref([])
const total = ref(0)
const page = ref(1)
const size = 20
const loading = ref(false)

// 总页数（空库时兜底为 1 页）
const totalPages = computed(() => Math.ceil(total.value / size) || 1)

// 组装参数 + 请求：空条件不进 params（对齐后端可空参数语义）
async function load() {
  loading.value = true
  try {
    const params = { page: page.value, size }
    if (keyword.value.trim()) params.keyword = keyword.value.trim()
    if (tag.value.trim()) params.tag = tag.value.trim()
    if (difficulty.value) params.difficulty = difficulty.value
    const data = await problemApi.list(params) // {items, total, page, size}
    items.value = data.items
    total.value = data.total
  } catch {
    // 错误已由拦截器 alert
  } finally {
    loading.value = false
  }
}

// 查询：条件变了必须回到第 1 页
function doSearch() {
  page.value = 1
  load()
}

// 重置：清空条件再查
function reset() {
  keyword.value = ''
  tag.value = ''
  difficulty.value = ''
  doSearch()
}

// 翻页：保留当前筛选条件
function prev() {
  if (page.value > 1) {
    page.value--
    load()
  }
}

function next() {
  if (page.value < totalPages.value) {
    page.value++
    load()
  }
}

function goDetail(row) {
  router.push(`/problems/${row.id}`)
}

// 新建模态框状态
const showForm = ref(false)
const formMode = ref('create')
const formInitial = ref(null)

function newProblem() {
  formMode.value = 'create'
  formInitial.value = null
  showForm.value = true
}

// 创建成功：关弹窗 + 刷新列表
function onSaved() {
  showForm.value = false
  load()
}

onMounted(load)
</script>

<template>
  <div>
    <!-- 筛选工具栏 -->
    <div class="toolbar mb-1">
      <input v-model="keyword" type="text" placeholder="搜索标题" class="toolbar-input" />
      <input v-model="tag" type="text" placeholder="标签（如 入门）" class="toolbar-input" />
      <select v-model="difficulty" class="toolbar-input">
        <option value="">全部难度</option>
        <option v-for="d in [1, 2, 3, 4, 5]" :key="d" :value="d">{{ d }}★</option>
      </select>
      <button class="btn btn-small" @click="doSearch">查询</button>
      <button class="btn btn-small btn-secondary" @click="reset">重置</button>
      <button class="btn btn-small" @click="newProblem">新建题目</button>
    </div>

    <!-- 题目表格 -->
    <table>
      <thead>
        <tr>
          <th>Slug</th>
          <th>标题</th>
          <th>难度</th>
          <th>时间限制</th>
          <th>内存限制</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="p in items" :key="p.id" @click="goDetail(p)" style="cursor: pointer">
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
    <p v-if="loading" class="text-center text-muted mt-1">加载中...</p>
    <p v-else-if="items.length === 0" class="text-center text-muted mt-1">暂无题目</p>

    <!-- 分页 -->
    <div class="pagination">
      <button class="btn btn-small btn-secondary" :disabled="page === 1" @click="prev">上一页</button>
      <span>共 {{ total }} 题 · 第 {{ page }} / {{ totalPages }} 页</span>
      <button class="btn btn-small btn-secondary" :disabled="page === totalPages" @click="next">下一页</button>
    </div>

    <!-- 新建模态框 -->
    <ProblemForm
      v-if="showForm"
      :mode="formMode"
      :initial="formInitial"
      @close="showForm = false"
      @saved="onSaved"
    />
  </div>
</template>
