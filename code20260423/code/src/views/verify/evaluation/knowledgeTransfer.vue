<template>
  <el-dialog :model-value="taskVisible" :title="resyncing ? '选择任务，另存新评价' : '选择测试任务'" width="min(880px, 94vw)" :close-on-click-modal="false" :close-on-press-escape="!syncing" :show-close="!syncing" @update:model-value="$emit('update:taskVisible', $event)">
    <el-alert v-if="resyncing" class="task-note" type="info" :closable="false" title="同步将生成新评价草稿，原记录、历史快照及运行来源保持不变。保存时仍校验运行去重及版本冲突。" />
    <div class="task-filters">
      <el-select v-model="taskCategory" :disabled="syncing" aria-label="任务评价类别" @change="selectedTask = ''">
        <el-option v-for="(label, key) in categoryLabels" :key="key" :label="label" :value="key" />
      </el-select>
      <el-input v-model="taskSearch" clearable :disabled="syncing" placeholder="搜索任务名称、机床名称或编号" />
    </div>
    <p class="import-help">只展示已结束且保存了历史快照的运行；若任务正重跑，展示的是上一次已结束结果，不是本轮进度。任务均分为所选项简单平均，与知识库的分类加权综合分不同。</p>
    <div v-loading="tasksLoading" class="task-list">
      <el-empty v-if="!tasksLoading && !filteredTasks.length" description="暂无此类别可同步的已结束任务" />
      <button v-for="task in filteredTasks" :key="task.id" type="button" class="task-card" :class="{ selected: task.id === selectedTask }" :aria-pressed="task.id === selectedTask" :disabled="syncing" @click="selectedTask = task.id">
        <div class="task-heading"><strong>{{ task.name }}</strong><el-tag size="small">{{ statusLabels[task.status] || task.status }}</el-tag></div>
        <p>{{ task.machineName || '未关联机床' }} / {{ task.machineModel || '—' }} / {{ task.controlSystem || '—' }}</p>
        <p>任务编号：{{ task.id }} · 测试日期：{{ task.testDate || '—' }}</p>
        <p>所选 {{ task.metrics?.length ?? 0 }} 项 · 任务均分：{{ formatScore(task.totalScore, 2) }} · 通过率：{{ formatPassRate(task.passRate) }}</p>
        <ul v-if="task.metrics?.length" class="metric-preview">
          <li v-for="(metric, index) in task.metrics" :key="`${metric.metricId}-${index}`" :title="metric.scoreReason || metric.detail">
            <span>{{ metric.name || metric.metricId }}</span>
            <span>{{ metric.value || '无测量值' }} · {{ metric.score == null ? '未评分' : `${formatScore(metric.score, 2)} 分` }} · {{ statusLabels[metric.status] || metric.status }}</span>
          </li>
        </ul>
        <p v-else>历史记录未保存分项分数，不推算得分。</p>
      </button>
    </div>
    <template #footer>
      <el-button :disabled="syncing" @click="$emit('update:taskVisible', false)">取消</el-button>
      <el-button type="primary" :disabled="!selectedTaskRecord || tasksLoading" :loading="syncing" @click="confirmTask">{{ resyncing ? '生成新评价草稿' : '确定同步' }}</el-button>
    </template>
  </el-dialog>
  <el-dialog :model-value="importVisible" title="Excel批量导入" width="min(650px, 94vw)" :close-on-click-modal="false" :close-on-press-escape="!importing" :show-close="!importing" @update:model-value="$emit('update:importVisible', $event)">
    <p class="import-help">请使用系统提供的机床评价或加工评价模板填写数据，保持表头结构不变；每行的评价类别以文件内容为准。</p>
    <el-upload ref="uploadRef" drag accept=".xls,.xlsx" :disabled="importing" :auto-upload="false" :limit="1" :on-change="selectFile" :on-remove="() => selectedFile = null" :on-exceed="() => ElMessage.warning('请移除已选文件后重新选择')">
      <el-icon class="el-icon--upload"><UploadFilled /></el-icon>
      <div class="el-upload__text">拖拽 Excel 文件到此处，或<em>点击选择</em></div>
      <template #tip><div class="el-upload__tip">支持 .xls / .xlsx，单个工作簿不超过 5 MiB，最多 1000 条记录；与测试附件的 200 MiB 上限不同</div></template>
    </el-upload>
    <el-alert class="import-note" type="info" :closable="false" title="得分填写0–100之间的数字或留空待评分，权重填写百分比数值；必填字段请完整填写。逐行导入，失败行不影响成功行，请仅修正并重传失败行，避免重复。" />
    <div v-if="importResult" class="import-result">
      <el-alert :type="importResult.failed ? 'warning' : 'success'" :closable="false" :title="`导入结果：成功 ${importResult.success} 条，失败 ${importResult.failed} 条`" />
      <ul v-if="importResult.errors.length"><li v-for="(error, index) in importResult.errors" :key="index">{{ error }}</li></ul>
    </div>
    <template #footer>
      <el-button :disabled="importing" @click="$emit('update:importVisible', false)">关闭</el-button>
      <el-button type="primary" :disabled="!selectedFile || imported" :loading="importing" @click="importFile">开始导入</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { ElMessage, type UploadFile, type UploadInstance } from 'element-plus';
import { UploadFilled } from '@element-plus/icons-vue';
import { evaluationApi, type EvaluationCategory, type KnowledgeTask } from '@/api/evaluation';
import { categoryLabels, formatScore } from './knowledgeModel';
const props = defineProps<{ taskVisible: boolean; importVisible: boolean; syncing: boolean; category?: EvaluationCategory; resyncing?: boolean }>();
const emit = defineEmits<{ 'update:taskVisible': [value: boolean]; 'update:importVisible': [value: boolean]; sync: [id: string, category: EvaluationCategory]; imported: [] }>();
const tasks = ref<KnowledgeTask[]>([]);
const tasksLoading = ref(false);
const taskCategory = ref<EvaluationCategory>('machine');
const taskSearch = ref('');
const selectedTask = ref('');
const selectedFile = ref<File | null>(null);
const uploadRef = ref<UploadInstance>();
const importing = ref(false);
const imported = ref(false);
const importResult = ref<{ total: number; success: number; failed: number; errors: string[] } | null>(null);
const statusLabels: Record<string, string> = { completed: '已完成', failed: '不通过/失败', passed: '通过', error: '执行错误', skipped: '未执行', unrated: '未判定', running: '执行中', pending: '待执行', cancelled: '已取消', canceled: '已取消' };
const filteredTasks = computed(() => {
  const search = taskSearch.value.trim().toLowerCase();
  return tasks.value.filter(task => (task.category ?? 'machine') === taskCategory.value && `${task.name} ${task.machineName} ${task.machineNo}`.toLowerCase().includes(search));
});
const selectedTaskRecord = computed(() => filteredTasks.value.find(task => task.id === selectedTask.value));
function formatPassRate(value: number | null | undefined) { return value == null ? '未配置判据或未确定' : `${value.toFixed(1)}%`; }
function confirmTask() {
  if (selectedTaskRecord.value && !props.syncing) emit('sync', selectedTaskRecord.value.id, taskCategory.value);
}
let taskRequest = 0;
watch(() => props.taskVisible, async visible => {
  const request = ++taskRequest;
  if (!visible) return;
  selectedTask.value = '';
  taskSearch.value = '';
  taskCategory.value = props.category ?? 'machine';
  tasksLoading.value = true;
  try {
    const loaded = await evaluationApi.listTasks();
    if (request === taskRequest) tasks.value = loaded;
  } catch (error) {
    if (request === taskRequest) { tasks.value = []; ElMessage.error(error instanceof Error ? error.message : '加载测试任务失败'); }
  } finally { if (request === taskRequest) tasksLoading.value = false; }
});
watch(() => props.importVisible, visible => {
  if (!visible || importing.value) return;
  selectedFile.value = null;
  imported.value = false;
  importResult.value = null;
  uploadRef.value?.clearFiles();
});
function selectFile(file: UploadFile) {
  imported.value = false;
  importResult.value = null;
  if (!file.raw || !/\.(xls|xlsx)$/i.test(file.name)) {
    uploadRef.value?.clearFiles();
    selectedFile.value = null;
    return void ElMessage.warning('请选择 .xls 或 .xlsx 格式的 Excel 文件');
  }
  selectedFile.value = file.raw;
}
async function importFile() {
  if (!selectedFile.value || importing.value || imported.value) return;
  importing.value = true;
  try {
    importResult.value = await evaluationApi.importRecords(selectedFile.value);
    imported.value = true;
    if (importResult.value.success) emit('imported');
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '导入失败'); }
  finally { importing.value = false; }
}
</script>

<style scoped>
.task-note { margin-bottom: 16px; }
.task-filters { display: grid; grid-template-columns: 150px 1fr; gap: 12px; }
.task-list { min-height: 180px; max-height: 55vh; overflow-y: auto; margin-top: 16px; }
.task-card { display: block; width: 100%; text-align: left; font: inherit; color: var(--el-text-color-primary); background: var(--el-bg-color); border: 1px solid var(--el-border-color); border-radius: 6px; padding: 16px; margin-bottom: 12px; cursor: pointer; }
.task-card:hover, .task-card.selected { border-color: var(--el-color-primary); background: var(--el-color-primary-light-9); }
.task-heading { display: flex; justify-content: space-between; align-items: center; gap: 12px; }
.task-card p { font-size: 13px; color: var(--el-text-color-secondary); margin-top: 8px; overflow-wrap: anywhere; }
.metric-preview { list-style: none; padding: 0; margin: 12px 0 0; font-size: 13px; }
.metric-preview li { display: flex; flex-wrap: wrap; justify-content: space-between; gap: 8px; padding: 6px 0; border-top: 1px solid var(--el-border-color-lighter); }
.import-help { color: var(--el-text-color-secondary); margin-bottom: 16px; font-size: 13px; line-height: 1.6; }
.import-note, .import-result { margin-top: 16px; }
.import-result ul { max-height: 180px; overflow: auto; padding-left: 20px; margin-top: 12px; color: var(--el-color-danger); }
</style>
