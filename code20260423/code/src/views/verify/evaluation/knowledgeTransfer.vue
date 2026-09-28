<template>
  <el-dialog :model-value="taskVisible" title="选择测试任务" width="min(880px, 94vw)" :close-on-click-modal="false" @update:model-value="$emit('update:taskVisible', $event)">
    <el-input v-model="taskSearch" clearable placeholder="搜索任务名称、机床名称或编号" />
    <div v-loading="tasksLoading" class="task-list">
      <el-empty v-if="!tasksLoading && !filteredTasks.length" description="暂无可同步的已执行任务" />
      <button v-for="task in filteredTasks" :key="task.id" class="task-card" :class="{ selected: task.id === selectedTask }" @click="selectedTask = task.id">
        <div class="task-heading"><strong>{{ task.name }}</strong><el-tag size="small">{{ statusLabels[task.status] || task.status }}</el-tag></div>
        <p>{{ task.machineName || '未关联机床' }} / {{ task.machineModel || '—' }} / {{ task.controlSystem || '—' }}</p>
        <p>任务编号：{{ task.id }} · 测试日期：{{ task.testDate || '—' }}</p>
      </button>
    </div>
    <template #footer>
      <el-button @click="$emit('update:taskVisible', false)">取消</el-button>
      <el-button type="primary" :disabled="!selectedTask" :loading="syncing" @click="$emit('sync', selectedTask)">确定同步</el-button>
    </template>
  </el-dialog>
  <el-dialog :model-value="importVisible" title="Excel批量导入" width="min(650px, 94vw)" :close-on-click-modal="false" @update:model-value="$emit('update:importVisible', $event)">
    <p class="import-help">请使用系统提供的模板填写数据，保持表头结构不变。</p>
    <el-upload ref="uploadRef" drag accept=".xlsx" :auto-upload="false" :limit="1" :on-change="selectFile" :on-remove="() => selectedFile = null" :on-exceed="() => ElMessage.warning('请移除已选文件后重新选择')">
      <el-icon class="el-icon--upload"><UploadFilled /></el-icon>
      <div class="el-upload__text">拖拽 Excel 文件到此处，或<em>点击选择</em></div>
      <template #tip><div class="el-upload__tip">支持 .xlsx，单次建议不超过100条记录</div></template>
    </el-upload>
    <el-alert class="import-note" type="info" :closable="false" title="得分填写0–100之间的数字或留空待评分，权重填写百分比数值；必填字段请完整填写。" />
    <div v-if="importResult" class="import-result">
      <el-alert :type="importResult.failed ? 'warning' : 'success'" :closable="false" :title="`导入结果：成功 ${importResult.success} 条，失败 ${importResult.failed} 条`" />
      <ul v-if="importResult.errors.length"><li v-for="(error, index) in importResult.errors" :key="index">{{ error }}</li></ul>
    </div>
    <template #footer>
      <el-button @click="$emit('update:importVisible', false)">关闭</el-button>
      <el-button type="primary" :disabled="!selectedFile || imported" :loading="importing" @click="importFile">开始导入</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { ElMessage, type UploadFile, type UploadInstance } from 'element-plus';
import { UploadFilled } from '@element-plus/icons-vue';
import { evaluationApi, type EvaluationCategory, type KnowledgeTask } from '@/api/evaluation';
const props = defineProps<{ taskVisible: boolean; importVisible: boolean; syncing: boolean; category?: EvaluationCategory }>();
const emit = defineEmits<{ 'update:taskVisible': [value: boolean]; 'update:importVisible': [value: boolean]; sync: [id: string]; imported: [] }>();
const tasks = ref<KnowledgeTask[]>([]);
const tasksLoading = ref(false);
const taskSearch = ref('');
const selectedTask = ref('');
const selectedFile = ref<File | null>(null);
const uploadRef = ref<UploadInstance>();
const importing = ref(false);
const imported = ref(false);
const importResult = ref<{ total: number; success: number; failed: number; errors: string[] } | null>(null);
const statusLabels: Record<string, string> = { completed: '已完成', failed: '失败', running: '执行中', pending: '待执行' };
const filteredTasks = computed(() => {
  const search = taskSearch.value.trim().toLowerCase();
  return tasks.value.filter(task => (!props.category || (task.category ?? 'machine') === props.category) && `${task.name} ${task.machineName} ${task.machineNo}`.toLowerCase().includes(search));
});
watch(() => props.taskVisible, async visible => {
  if (!visible) return;
  selectedTask.value = '';
  taskSearch.value = '';
  tasksLoading.value = true;
  try { tasks.value = await evaluationApi.listTasks(); }
  catch (error) { tasks.value = []; ElMessage.error(error instanceof Error ? error.message : '加载测试任务失败'); }
  finally { tasksLoading.value = false; }
});
watch(() => props.importVisible, visible => {
  if (!visible) return;
  selectedFile.value = null;
  imported.value = false;
  importResult.value = null;
  uploadRef.value?.clearFiles();
});
function selectFile(file: UploadFile) {
  imported.value = false;
  importResult.value = null;
  if (!file.raw || !file.name.toLowerCase().endsWith('.xlsx')) {
    uploadRef.value?.clearFiles();
    selectedFile.value = null;
    return void ElMessage.warning('请选择 .xlsx 格式的 Excel 文件');
  }
  selectedFile.value = file.raw;
}
async function importFile() {
  if (!selectedFile.value) return;
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
.task-list { min-height: 180px; max-height: 55vh; overflow-y: auto; margin-top: 16px; }
.task-card { display: block; width: 100%; text-align: left; font: inherit; color: var(--el-text-color-primary); background: var(--el-bg-color); border: 1px solid var(--el-border-color); border-radius: 6px; padding: 16px; margin-bottom: 12px; cursor: pointer; }
.task-card:hover, .task-card.selected { border-color: var(--el-color-primary); background: var(--el-color-primary-light-9); }
.task-heading { display: flex; justify-content: space-between; align-items: center; gap: 12px; }
.task-card p { font-size: 13px; color: var(--el-text-color-secondary); margin-top: 8px; overflow-wrap: anywhere; }
.import-help { color: var(--el-text-color-secondary); margin-bottom: 16px; }
.import-note, .import-result { margin-top: 16px; }
.import-result ul { max-height: 180px; overflow: auto; padding-left: 20px; margin-top: 12px; color: var(--el-color-danger); }
</style>
