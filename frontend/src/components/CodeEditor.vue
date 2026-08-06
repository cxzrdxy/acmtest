<template>
  <div class="code-editor">
    <!-- 行号列 + 输入区（同步滚动） -->
    <div ref="gutterRef" class="code-gutter" aria-hidden="true">
      <div v-for="n in lineCount" :key="n">{{ n }}</div>
    </div>
    <textarea
      ref="taRef"
      class="code-input"
      :value="modelValue"
      @input="onInput"
      @scroll="syncScroll"
      :spellcheck="false"
    ></textarea>
  </div>
</template>

<script setup>
import { ref, computed } from 'vue'

const props = defineProps({
  modelValue: { type: String, default: '' },
  rows: { type: Number, default: 12 }
})

const emit = defineEmits(['update:modelValue'])

const taRef = ref(null)
const gutterRef = ref(null)

const lineCount = computed(() => (props.modelValue.match(/\n/g)?.length ?? 0) + 1)

function onInput(e) {
  emit('update:modelValue', e.target.value)
}

function syncScroll(e) {
  if (gutterRef.value) gutterRef.value.scrollTop = e.target.scrollTop
}
</script>

<style scoped>
/* 浅色现代工具风编辑器 */
.code-editor {
  display: flex;
  border: 1px solid var(--border);
  border-radius: 6px;
  overflow: hidden;
  background: #fafbfc;
  font-family: ui-monospace, "SF Mono", Consolas, "Courier New", monospace;
  font-size: 0.88rem;
  line-height: 1.55;
}
.code-gutter {
  flex-shrink: 0;
  padding: 0.55rem 0.7rem;
  text-align: right;
  color: #9ca3af;
  background: #f1f3f5;
  border-right: 1px solid var(--border);
  user-select: none;
  overflow: hidden;
}
.code-input {
  flex: 1;
  min-height: 240px;
  resize: vertical;
  border: none;
  background: transparent;
  color: #1f2328;
  padding: 0.55rem 0.7rem;
  font-family: inherit;
  font-size: inherit;
  line-height: inherit;
  outline: none;
}
.code-input:focus {
  outline: none;
}
</style>
