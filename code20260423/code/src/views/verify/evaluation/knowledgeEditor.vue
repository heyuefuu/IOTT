<template>
  <el-dialog :model-value="visible" :title="draft?.id ? '编辑评价记录' : '新增评价记录'" width="min(1400px, 96vw)" top="4vh" :close-on-click-modal="false" :close-on-press-escape="!saving && !busy" :show-close="!saving && !busy" @update:model-value="$emit('update:visible', $event)">
    <div v-if="draft" v-loading="busy || saving" class="editor-body">
      <div class="section-header">
        <h3>评价类别</h3>
        <el-select :model-value="draft.category" :disabled="Boolean(draft.id) || busy || saving" aria-label="评价类别" style="width: 160px" @change="$emit('category-change', $event)">
          <el-option v-for="(label, category) in categoryLabels" :key="category" :value="category" :label="label" />
        </el-select>
      </div>
      <p class="help-text">{{ draft.id ? '历史记录保留原类别及指标快照；同步新任务将另存新评价，原记录不变。' : '切换类别会保留各自未保存的草稿，切回后可继续编辑。' }}</p>
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
        <el-button type="success" plain :disabled="saving || busy" @click="$emit('sync')">{{ draft.id ? '同步测试任务结果（另存新评价）' : '同步测试任务结果' }}</el-button>
      </div>
      <p class="help-text">按评价方法填写 0–100 分，未完成的项目可留空。各级权重之和应为 100%。</p>
      <knowledge-scores :record="draft" @sync="$emit('sync')" />
      <div class="score-summary">
        <strong>综合加权得分</strong>
        <span>{{ formatScore(summary.total) }}</span>
      </div>
      <el-form label-position="top" class="conclusion-form">
        <el-form-item label="评价结论"><el-input v-model="draft.conclusion" type="textarea" :rows="3" maxlength="5000" show-word-limit placeholder="请输入综合评价结论" /></el-form-item>
        <el-form-item label="问题与改进建议"><el-input v-model="draft.suggestion" type="textarea" :rows="3" maxlength="5000" show-word-limit placeholder="请输入问题与改进建议" /></el-form-item>
      </el-form>
      <p v-if="draft.syncTaskId" class="help-text">关联测试任务：{{ draft.syncTaskId }} · 运行：{{ draft.syncRunId || '历史记录未保存运行标识' }}。任务分数及未评分状态来自对应运行，请核实并完成评价。</p>
    </div>
    <template #footer>
      <el-button :disabled="saving || busy" @click="$emit('update:visible', false)">取消</el-button>
      <el-button type="primary" :loading="saving" :disabled="busy" @click="save">保存评价记录</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { ElMessage, type FormInstance } from 'element-plus';
import type { EvaluationCategory, KnowledgeRecord } from '@/api/evaluation';
import knowledgeScores from './knowledgeScores.vue';
import { basicFields, categoryLabels, formatScore, scoreSummary, weightErrors } from './knowledgeModel';
const props = defineProps<{ visible: boolean; draft: KnowledgeRecord | null; saving: boolean; busy?: boolean }>();
const emit = defineEmits<{ 'update:visible': [value: boolean]; save: []; sync: []; 'category-change': [category: EvaluationCategory] }>();
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
.section-header { display: flex; align-items: center; gap: 12px; margin-top: 8px; flex-wrap: wrap; }
.help-text { color: var(--el-text-color-secondary); font-size: 13px; margin-top: 8px; }
.score-summary { display: flex; justify-content: space-between; align-items: center; padding: 18px 20px; margin-top: 20px; border-radius: 6px; background: var(--el-color-primary-light-9); }
.score-summary span { color: var(--el-color-primary); font-size: 28px; font-weight: 600; }
.conclusion-form { margin-top: 20px; }
@media (max-width: 760px) { .basic-form { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@media (max-width: 480px) { .basic-form { grid-template-columns: 1fr; } }
</style>
