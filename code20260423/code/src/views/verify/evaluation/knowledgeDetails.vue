<template>
  <el-dialog :model-value="visible" title="评价报告详情" width="min(1400px, 96vw)" top="4vh" @update:model-value="$emit('update:visible', $event)">
    <div v-if="record" class="report-body">
      <h3>一、基础信息</h3>
      <el-descriptions :column="3" border>
        <el-descriptions-item v-for="field in basicFields" :key="field.key" :label="field.label">{{ record[field.key] || '—' }}</el-descriptions-item>
        <el-descriptions-item label="综合得分"><strong class="score">{{ formatScore(record.totalScore) }}</strong><el-tag class="grade" :type="gradeType">{{ gradeLabel(record.totalScore) }}</el-tag></el-descriptions-item>
        <el-descriptions-item label="数据来源">{{ sourceLabels[record.dataSource] }}</el-descriptions-item>
        <el-descriptions-item label="指标版本">{{ record.snapshot.version }}</el-descriptions-item>
        <el-descriptions-item v-if="record.syncTaskId" label="关联任务" :span="3">{{ record.syncTaskId }}</el-descriptions-item>
      </el-descriptions>
      <h3>二、评价项目得分明细</h3>
      <knowledge-scores :record="record" readonly />
      <h3>三、评价结论</h3>
      <div class="report-text">{{ record.conclusion || '无' }}</div>
      <h3>四、问题与改进建议</h3>
      <div class="report-text">{{ record.suggestion || '无' }}</div>
    </div>
    <template #footer>
      <el-button @click="$emit('update:visible', false)">关闭</el-button>
      <el-button :loading="exporting" @click="$emit('export', 'xlsx')">导出 Excel</el-button>
      <el-button type="primary" :loading="exporting" @click="$emit('export', 'pdf')">导出 PDF</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import type { KnowledgeRecord } from '@/api/evaluation';
import knowledgeScores from './knowledgeScores.vue';
import { basicFields, formatScore, gradeLabel, sourceLabels } from './knowledgeModel';
const props = defineProps<{ visible: boolean; record: KnowledgeRecord | null; exporting: boolean }>();
defineEmits<{ 'update:visible': [value: boolean]; export: [format: 'xlsx' | 'pdf'] }>();
const gradeType = computed(() => {
  const score = props.record?.totalScore;
  return score == null ? 'info' : score >= 90 ? 'success' : score >= 80 ? 'primary' : score >= 60 ? 'warning' : 'danger';
});
</script>

<style scoped>
.report-body { max-height: 72vh; overflow-y: auto; padding: 0 8px; }
h3 { font-size: 16px; color: var(--el-text-color-primary); margin: 22px 0 14px; }
h3:first-child { margin-top: 0; }
.report-text { white-space: pre-wrap; overflow-wrap: anywhere; background: var(--el-fill-color-light); border-radius: 6px; padding: 16px; }
.score { font-size: 20px; color: var(--el-color-primary); }
.grade { margin-left: 10px; }
</style>
