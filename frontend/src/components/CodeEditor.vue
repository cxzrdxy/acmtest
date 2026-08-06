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
/* 深色终端风编辑器（评测世界的"世界感"：编译器/终端） */
.code-editor {
  display: flex;
  border: 1px solid var(--border);
  border-radius: 6px;
  overflow: hidden;
  background: #1e1e1e;
  font-family: Consolas, Monaco, monospace;
  font-size: 0.9rem;
  line-height: 1.5;
}
.code-gutter {
  flex-shrink: 0;
  padding: 0.5rem 0.6rem;
  text-align: right;
  color: #6b7280;
  background: #252526;
  user-select: none;
  overflow: hidden;
}
.code-input {
  flex: 1;
  min-height: 240px;
  resize: vertical;
  border: none;
  background: transparent;
  color: #d4d4d4;
  padding: 0.5rem 0.6rem;
  font-family: inherit;
  font-size: inherit;
  line-height: inherit;
  outline: none;
}
.code-input:focus {
  outline: none;
}
</style>
