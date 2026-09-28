<template>
  <el-dialog :model-value="visible" :title="draft?.id ? '编辑评价记录' : '新增评价记录'" width="min(1400px, 96vw)" top="4vh" :close-on-click-modal="false" @update:model-value="$emit('update:visible', $event)">
    <div v-if="draft" class="editor-body">
      <h3>基础信息</h3>
      <el-form ref="formRef" :model="draft" label-position="top" class="basic-form">
        <el-form-item v-for="field in basicFields" :key="field.key" :label="field.label" :prop="field.key" :rules="field.required ? [{ required: true, message: `请输入${field.label}`, trigger: 'blur' }, { validator: validateText, trigger: 'blur' }] : []">
          <el-date-picker v-if="field.key === 'testDate'" v-model="draft.testDate" type="date" value-format="YYYY-MM-DD" placeholder="请选择测试日期" />
          <el-input v-else v-model="draft[field.key]" :placeholder="`请输入${field.label}`" maxlength="200" />
        </el-form-item>
      </el-form>
      <div class="section-header">
        <h3>评价项目评分</h3>
        <el-tag type="info">指标版本 {{ draft.snapshot.version }}</el-tag>
      </div>
      <p class="help-text">按评价方法填写 0–100 分，未完成的项目可留空。各级权重之和应为 100%。</p>
      <knowledge-scores :record="draft" :can-sync="!draft.id" @sync="$emit('sync')" />
      <div class="score-summary">
        <strong>综合加权得分</strong>
        <span>{{ formatScore(summary.total) }}</span>
      </div>
      <el-form label-position="top" class="conclusion-form">
        <el-form-item label="评价结论"><el-input v-model="draft.conclusion" type="textarea" :rows="3" maxlength="5000" show-word-limit placeholder="请输入综合评价结论" /></el-form-item>
        <el-form-item label="问题与改进建议"><el-input v-model="draft.suggestion" type="textarea" :rows="3" maxlength="5000" show-word-limit placeholder="请输入问题与改进建议" /></el-form-item>
      </el-form>
      <p v-if="draft.syncTaskId" class="help-text">关联测试任务：{{ draft.syncTaskId }}。任务实测结果已同步，请核实并完成评分。</p>
    </div>
    <template #footer>
      <el-button :disabled="saving" @click="$emit('update:visible', false)">取消</el-button>
      <el-button type="primary" :loading="saving" @click="save">保存评价记录</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { ElMessage, type FormInstance } from 'element-plus';
import type { KnowledgeRecord } from '@/api/evaluation';
import knowledgeScores from './knowledgeScores.vue';
import { basicFields, formatScore, scoreSummary, weightErrors } from './knowledgeModel';
const props = defineProps<{ visible: boolean; draft: KnowledgeRecord | null; saving: boolean }>();
const emit = defineEmits<{ 'update:visible': [value: boolean]; save: []; sync: [] }>();
const formRef = ref<FormInstance>();
const summary = computed(() => props.draft ? scoreSummary(props.draft) : { total: null });
function validateText(_rule: unknown, value: unknown, callback: (error?: Error) => void) {
  callback(typeof value === 'string' && value.trim() ? undefined : new Error('内容不能为空'));
}
async function save() {
  if (!props.draft || !await formRef.value?.validate().catch(() => false)) return;
  const errors = weightErrors(props.draft);
  if (errors.length) return void ElMessage.warning(errors.join('；'));
  for (const score of Object.values(props.draft.scores)) {
    if (score != null && (!Number.isFinite(score) || score < 0 || score > 100)) {
      return void ElMessage.warning('得分需为0–100之间的数字');
    }
  }
  emit('save');
}
</script>

<style scoped>
.editor-body { max-height: 72vh; overflow-y: auto; padding: 0 8px; }
h3 { font-size: 16px; font-weight: 600; color: var(--el-text-color-primary); }
.basic-form { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 0 20px; margin-top: 16px; }
.basic-form :deep(.el-date-editor) { width: 100%; }
.section-header { display: flex; align-items: center; gap: 12px; margin-top: 8px; }
.help-text { color: var(--el-text-color-secondary); font-size: 13px; margin-top: 8px; }
.score-summary { display: flex; justify-content: space-between; align-items: center; padding: 18px 20px; margin-top: 20px; border-radius: 6px; background: var(--el-color-primary-light-9); }
.score-summary span { color: var(--el-color-primary); font-size: 28px; font-weight: 600; }
.conclusion-form { margin-top: 20px; }
@media (max-width: 760px) { .basic-form { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@media (max-width: 480px) { .basic-form { grid-template-columns: 1fr; } }
</style>
