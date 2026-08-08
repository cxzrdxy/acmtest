<script setup>
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { problemApi, submissionApi } from '../api'

const router = useRouter()

// 筛选条件
const problemId = ref('') // '' = 全部题目
const status = ref('')    // '' = 全部状态

// 题目下拉数据源（个人自用题目量小，拉全量）
const problems = ref([])

// 列表数据
const items = ref([])
const total = ref(0)
const page = ref(1)
const size = 20
const loading = ref(false)

const totalPages = computed(() => Math.ceil(total.value / size) || 1)

// 状态筛选项（PENDING/JUDGING 只存在于提交瞬间，不进筛选）
const statusOptions = [
  { value: '', text: '全部状态' },
  { value: 'AC', text: '通过' },
  { value: 'WA', text: '答案错误' },
  { value: 'TLE', text: '超时' },
  { value: 'MLE', text: '超内存' },
  { value: 'RE', text: '运行错误' },
  { value: 'CE', text: '编译错误' }
]

// 语言显示
const langLabel = { cpp17: 'C++17', python3: 'Python3' }

// 状态徽章（展示用，仅列表列渲染）
const statusMeta = {
  AC: { cls: 'st-ac', text: '通过' },
  WA: { cls: 'st-wa', text: '答案错误' },
  TLE: { cls: 'st-tle', text: '超时' },
  MLE: { cls: 'st-mle', text: '超内存' },
  RE: { cls: 'st-re', text: '运行错误' },
  CE: { cls: 'st-ce', text: '编译错误' },
  PENDING: { cls: 'st-pending', text: '排队中' },
  JUDGING: { cls: 'st-pending', text: '评测中' }
}

function fmtTime(iso) {
  return new Date(iso).toLocaleString()
}

// 组装参数 + 请求：空条件不进 params（对齐后端可空参数语义）
async function load() {
  loading.value = true
  try {
    const params = { page: page.value, size }
    if (problemId.value) params.problemId = problemId.value
    if (status.value) params.status = status.value
    const data = await submissionApi.list(params) // {items, total, page, size}
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
  problemId.value = ''
  status.value = ''
  doSearch()
}

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
  router.push(`/submissions/${row.id}`)
}

onMounted(async () => {
  try {
    const data = await problemApi.list({ size: 100 })
    problems.value = data.items
  } catch {
    // 拦截器已 alert；题目下拉为空不影响列表
  }
  load()
})
</script>

<template>
  <div>
    <!-- 筛选工具栏 -->
    <div class="toolbar mb-1">
      <select v-model="problemId" class="toolbar-input">
        <option value="">全部题目</option>
        <option v-for="p in problems" :key="p.id" :value="p.id">{{ p.slug }} {{ p.title }}</option>
      </select>
      <select v-model="status" class="toolbar-input">
        <option v-for="o in statusOptions" :key="o.value" :value="o.value">{{ o.text }}</option>
      </select>
      <button class="btn btn-small" @click="doSearch">查询</button>
      <button class="btn btn-small btn-secondary" @click="reset">重置</button>
    </div>

    <!-- 提交表格 -->
    <table>
      <thead>
        <tr>
          <th>#Id</th>
          <th>题目</th>
          <th>语言</th>
          <th>状态</th>
          <th>分数</th>
          <th>耗时</th>
          <th>提交时间</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="s in items" :key="s.id" @click="goDetail(s)" style="cursor: pointer">
          <td>{{ s.id }}</td>
          <td>{{ s.problemTitle }}</td>
          <td>{{ langLabel[s.language] ?? s.language }}</td>
          <td>
            <span :class="`badge ${statusMeta[s.status]?.cls ?? 'st-unknown'}`">
              {{ statusMeta[s.status]?.text ?? s.status }}
            </span>
          </td>
          <td>{{ s.score }}/100</td>
          <td>{{ s.timeMs != null ? `${s.timeMs}ms` : '-' }}</td>
          <td class="text-muted">{{ fmtTime(s.createdAt) }}</td>
        </tr>
      </tbody>
    </table>
    <p v-if="loading" class="text-center text-muted mt-1">加载中...</p>
    <p v-else-if="items.length === 0" class="text-center text-muted mt-1">暂无提交</p>

    <!-- 分页 -->
    <div class="pagination">
      <button class="btn btn-small btn-secondary" :disabled="page === 1" @click="prev">上一页</button>
      <span>共 {{ total }} 条 · 第 {{ page }} / {{ totalPages }} 页</span>
      <button class="btn btn-small btn-secondary" :disabled="page === totalPages" @click="next">下一页</button>
    </div>
  </div>
</template>
