import { computed, ref, toRaw } from 'vue';
import { useRouter } from 'vue-router';
import { isAxiosError } from 'axios';
import { ElMessage, ElMessageBox } from 'element-plus';
import { evaluationApi, type EvaluationCategory } from '@/api/evaluation';
import { businessValidationApi } from '@/api/businessValidation';
import { machineConnectionDevicesApi, type DeviceDto } from '@/api/machineConnectionDevices';
import { machineConnectionVerifyApi, type VerifyTaskDto } from '@/api/machineConnectionVerify';
import { downloadBlob } from '@/api/browserDownload';
import { newTask, taskMetricOptions, taskRun, type TaskMetricOption } from './taskModel';

function reportError(error: unknown, fallback: string) {
  const message = isAxiosError(error) ? error.response?.data?.error || error.response?.data?.message || error.message : error instanceof Error ? error.message : fallback;
  ElMessage.error(typeof message === 'string' ? message : fallback);
}
export function useTaskWorkbench() {
  const router = useRouter();
  const tasks = ref<VerifyTaskDto[]>([]);
  const machines = ref<DeviceDto[]>([]);
  const categoryMetrics = ref<Record<EvaluationCategory, TaskMetricOption[]>>({ machine: [], machining: [] });
  const loading = ref(false);
  const saving = ref(false);
  const executingId = ref('');
  const exportingId = ref('');
  const editorVisible = ref(false);
  const resultVisible = ref(false);
  const draft = ref<VerifyTaskDto | null>(null);
  const metrics = computed(() => categoryMetrics.value[draft.value?.evaluationCategory ?? 'machine']);
  const selectedTask = ref<VerifyTaskDto | null>(null);
  const expandedRows = ref<string[]>([]);
  const machineFor = (task: VerifyTaskDto) => machines.value.find(machine => machine.id === (task.machineId || task.deviceId));
  const metricsFor = (task: VerifyTaskDto) => categoryMetrics.value[task.evaluationCategory ?? 'machine'];
  async function loadPage() {
    loading.value = true;
    try {
      const [devices, config, machiningConfig, autoMetrics, taskList] = await Promise.all([
        machineConnectionDevicesApi.list('CNC'), evaluationApi.getConfig('machine'),
        evaluationApi.getConfig('machining'),
        businessValidationApi.listMetrics(), machineConnectionVerifyApi.listTasks(),
      ]);
      machines.value = devices;
      categoryMetrics.value = { machine: taskMetricOptions(config, autoMetrics), machining: taskMetricOptions(machiningConfig, autoMetrics) };
      tasks.value = taskList;
    } catch (error) { reportError(error, '加载验证任务失败'); }
    finally { loading.value = false; }
  }
  function addTask() {
    draft.value = newTask(categoryMetrics.value.machine.map(metric => metric.id));
    draft.value.evaluationCategory = 'machine';
    editorVisible.value = true;
  }
  function editTask(task: VerifyTaskDto) {
    draft.value = structuredClone(toRaw(task));
    draft.value.evaluationCategory ??= 'machine';
    editorVisible.value = true;
  }
  function selectCategory(category: EvaluationCategory) {
    if (!draft.value) return;
    draft.value.evaluationCategory = category;
    draft.value.metricIds = categoryMetrics.value[category].map(metric => metric.id);
  }
  function replaceTask(task: VerifyTaskDto) {
    const index = tasks.value.findIndex(existing => existing.id === task.id);
    if (index >= 0) tasks.value.splice(index, 1, task); else tasks.value.unshift(task);
    if (selectedTask.value?.id === task.id) selectedTask.value = task;
  }
  async function saveTask() {
    if (!draft.value || saving.value) return;
    saving.value = true;
    try {
      const payload: VerifyTaskDto = { ...draft.value, name: draft.value.name.trim(), deviceId: draft.value.machineId };
      const existing = tasks.value.find(task => task.id === payload.id);
      const changed = existing && (existing.machineId !== payload.machineId || (existing.evaluationCategory ?? 'machine') !== payload.evaluationCategory || existing.metricIds.join(',') !== payload.metricIds.join(','));
      if (changed) Object.assign(payload, { status: 'pending', completedAt: null, executionTime: '', result: '', detail: '' });
      const saved = payload.id ? await machineConnectionVerifyApi.updateTask(payload.id, payload) : await machineConnectionVerifyApi.createTask(payload);
      replaceTask(saved);
      editorVisible.value = false;
      ElMessage.success(changed ? '任务已保存，请重新执行更新后的测试配置' : '任务已保存');
    } catch (error) { reportError(error, '保存验证任务失败'); }
    finally { saving.value = false; }
  }
  async function executeTask(task: VerifyTaskDto) {
    if (executingId.value || task.status === 'running') return;
    executingId.value = task.id;
    const oldStatus = task.status;
    task.status = 'running';
    if (!expandedRows.value.includes(task.id)) expandedRows.value.push(task.id);
    try {
      const result = await machineConnectionVerifyApi.runTask(task.id);
      replaceTask(result);
      result.status === 'completed' ? ElMessage.success('自动验证执行完成') : ElMessage.warning(result.detail || '自动验证完成，存在未达标项');
    } catch (error) {
      task.status = oldStatus;
      reportError(error, '执行验证任务失败');
      try { tasks.value = await machineConnectionVerifyApi.listTasks(); } catch { /* 保留现有任务以供刷新 */ }
    } finally { executingId.value = ''; }
  }
  async function deleteTask(task: VerifyTaskDto) {
    try {
      await ElMessageBox.confirm(`确定删除任务“${task.name}”吗？`, '删除任务', { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' });
      await machineConnectionVerifyApi.deleteTask(task.id);
      tasks.value = tasks.value.filter(existing => existing.id !== task.id);
      ElMessage.success('任务已删除');
    } catch (error) { if (error !== 'cancel' && error !== 'close') reportError(error, '删除任务失败'); }
  }
  function viewResult(task: VerifyTaskDto) { selectedTask.value = task; resultVisible.value = true; }
  async function exportTask(task: VerifyTaskDto, format: 'xlsx' | 'pdf' = 'xlsx') {
    exportingId.value = task.id;
    try {
      const file = format === 'pdf' ? await machineConnectionVerifyApi.exportTaskPdf(task.id) : await machineConnectionVerifyApi.exportTaskResult(task.id);
      downloadBlob(file.blob, file.fileName);
    } catch (error) { reportError(error, '导出验证报告失败'); }
    finally { exportingId.value = ''; }
  }
  function canImport(task: VerifyTaskDto) {
    const run = taskRun(task);
    return Boolean(run?.machineSnapshot && run.evaluationSnapshot && run.metrics.length);
  }
  function importKnowledge(task: VerifyTaskDto) {
    if (!canImport(task)) return void ElMessage.warning('该任务旧结果未保存评价指标和机床快照，请重新执行后导入');
    void router.push({ path: '/verify/knowledge', query: { taskId: task.id } });
  }
  return { tasks, machines, metrics, loading, saving, executingId, exportingId, editorVisible, resultVisible,
    draft, selectedTask, expandedRows, machineFor, metricsFor, selectCategory, loadPage, addTask, editTask, saveTask, executeTask, deleteTask, viewResult, exportTask, canImport, importKnowledge };
}
