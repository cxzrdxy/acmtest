<script setup>
// 状态徽章映射：颜色 + 用户语言文案（ProblemDetail 与 SubmissionDetail 共用）
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

defineProps({
  result: { type: Object, required: true }
})
</script>

<template>
  <div class="result-head">
    <span :class="`badge ${statusMeta[result.status]?.cls ?? 'st-unknown'}`">
      {{ statusMeta[result.status]?.text ?? result.status }}
    </span>
    <span class="text-muted">分数 {{ result.score }}/100</span>
    <span v-if="result.timeMs != null" class="text-muted">最大耗时 {{ result.timeMs }}ms</span>
    <span class="text-muted">提交 #{{ result.id }}</span>
  </div>

  <!-- 中间态提示（PENDING/JUDGING 时 detail 为空，且尚未有可展示结果） -->
  <p v-if="result.status === 'PENDING' || result.status === 'JUDGING'" class="text-muted">
    正在评测，请稍候...
  </p>

  <!-- 编译错误：单独区域展示 -->
  <div v-else-if="result.status === 'CE'">
    <h4 class="ce-title">编译错误</h4>
    <pre class="compile-error">{{ result.compileError }}</pre>
  </div>

  <!-- 逐测试点 -->
  <div v-else-if="result.detail.length" class="result-detail">
    <span
      v-for="tc in result.detail"
      :key="tc.id"
      :class="`tc-chip ${statusMeta[tc.status]?.cls ?? 'st-unknown'}`"
    >
      点{{ tc.id }} {{ statusMeta[tc.status]?.text ?? tc.status }}
      <template v-if="tc.timeMs != null">· {{ tc.timeMs }}ms</template>
    </span>
  </div>
</template>
