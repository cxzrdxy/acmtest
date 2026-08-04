<script setup>
import { ref, computed } from 'vue'
import { problemApi } from '../api'

// props：父页面告诉表单"什么模式 + 编辑原值"
const props = defineProps({
  mode: { type: String, required: true }, // 'create' | 'edit'
  initial: { type: Object, default: null } // 编辑模式的题目原值（创建为 null）
})
// events：向父页面汇报
const emit = defineEmits(['close', 'saved'])

// 表单状态（创建默认值 / 编辑预填）
const slug = ref(props.initial?.slug ?? '')
const title = ref(props.initial?.title ?? '')
const description = ref(props.initial?.description ?? '')
const inputDesc = ref(props.initial?.inputDesc ?? '')
const outputDesc = ref(props.initial?.outputDesc ?? '')
const timeLimit = ref(props.initial?.timeLimit ?? 1000)
const memoryLimit = ref(props.initial?.memoryLimit ?? 256)
const tagsInput = ref(props.initial?.tags.join(',') ?? '') // 数组 → 逗号分隔文本
const difficulty = ref(props.initial?.difficulty ?? '')
const sampleInputs = ref(props.initial?.sampleInputs ?? '')
const sampleOutputs = ref(props.initial?.sampleOutputs ?? '')
const loading = ref(false)

const isEdit = computed(() => props.mode === 'edit')

async function submit() {
  // 必填拦截（其余校验由后端 DTO 兜底）
  if (!title.value.trim()) {
    alert('请输入标题')
    return
  }
  if (!description.value.trim()) {
    alert('请输入题目描述')
    return
  }
  if (!isEdit.value && !slug.value.trim()) {
    alert('请输入 slug（如 P1000）')
    return
  }

  loading.value = true
  try {
    const payload = {
      title: title.value.trim(),
      description: description.value.trim(),
      inputDesc: inputDesc.value,
      outputDesc: outputDesc.value,
      timeLimit: Number(timeLimit.value),
      memoryLimit: Number(memoryLimit.value),
      // 逗号分隔文本 → 数组；空串清空为 []
      tags: tagsInput.value.split(',').map((s) => s.trim()).filter(Boolean),
      difficulty: difficulty.value || null, // 空串 → null（部分更新语义：null=不改）
      sampleInputs: sampleInputs.value,
      sampleOutputs: sampleOutputs.value
    }
    if (isEdit.value) {
      // 编辑：部分更新，全字段有值即传（null=不改，难度清空为已知限制）
      await problemApi.update(props.initial.id, payload)
    } else {
      payload.slug = slug.value.trim()
      await problemApi.create(payload)
    }
    emit('saved')
  } catch {
    // 错误已由拦截器 alert
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <!-- 遮罩 + 居中卡片；@click.self 只响应遮罩本体点击 -->
  <div class="modal-mask" @click.self="emit('close')">
    <div class="modal-card">
      <div class="modal-head">
        <h3>{{ isEdit ? '编辑题目' : '新建题目' }}</h3>
        <button class="btn btn-small btn-secondary" @click="emit('close')">✕</button>
      </div>
      <div class="modal-body">
        <div class="form-group">
          <label>Slug（如 P1000）</label>
          <input v-model="slug" type="text" :disabled="isEdit" />
        </div>
        <div class="form-group">
          <label>标题 *</label>
          <input v-model="title" type="text" />
        </div>
        <div class="form-group">
          <label>题目描述 *</label>
          <textarea v-model="description" rows="5"></textarea>
        </div>
        <div class="form-group">
          <label>输入格式</label>
          <textarea v-model="inputDesc" rows="2"></textarea>
        </div>
        <div class="form-group">
          <label>输出格式</label>
          <textarea v-model="outputDesc" rows="2"></textarea>
        </div>
        <div class="form-group">
          <label>时间限制（ms）</label>
          <input v-model="timeLimit" type="number" />
        </div>
        <div class="form-group">
          <label>内存限制（MB）</label>
          <input v-model="memoryLimit" type="number" />
        </div>
        <div class="form-group">
          <label>标签（逗号分隔）</label>
          <input v-model="tagsInput" type="text" placeholder="如 入门,DP" />
        </div>
        <div class="form-group">
          <label>难度</label>
          <select v-model="difficulty">
            <option value="">未评级</option>
            <option v-for="d in [1, 2, 3, 4, 5]" :key="d" :value="d">{{ d }}★</option>
          </select>
        </div>
        <div class="form-group">
          <label>样例输入（多个用 --- 分隔）</label>
          <textarea v-model="sampleInputs" rows="2" placeholder="1 2&#10;---&#10;3 4"></textarea>
        </div>
        <div class="form-group">
          <label>样例输出（多个用 --- 分隔）</label>
          <textarea v-model="sampleOutputs" rows="2"></textarea>
        </div>
      </div>
      <div class="modal-foot">
        <button class="btn btn-small btn-secondary" @click="emit('close')">取消</button>
        <button class="btn btn-small" :disabled="loading" @click="submit">
          {{ loading ? '保存中...' : '保存' }}
        </button>
      </div>
    </div>
  </div>
</template>
