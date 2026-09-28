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
  const executingIds = ref(new Set<string>());
  const selectedTaskIds = ref<string[]>([]);
  const maxParallelTasks = 7;
  const runningTaskCount = computed(() => new Set([
    ...executingIds.value, ...tasks.value.filter(task => task.status === 'running').map(task => task.id),
  ]).size);
  let taskRevision = 0;
  const exportingId = ref('');
  const editorVisible = ref(false);
  const resultVisible = ref(false);
  const draft = ref<VerifyTaskDto | null>(null);
  const metrics = computed(() => categoryMetrics.value[draft.value?.evaluationCategory ?? 'machine']);
  const selectedTask = ref<VerifyTaskDto | null>(null);
  const expandedRows = ref<string[]>([]);
  const machineFor = (task: VerifyTaskDto) => machines.value.find(machine => machine.id === (task.machineId || task.deviceId));
  const metricsFor = (task: VerifyTaskDto) => categoryMetrics.value[task.evaluationCategory ?? 'machine'];
  const isTaskRunning = (task: VerifyTaskDto) => executingIds.value.has(task.id) || task.status === 'running';
  function targetKeys(task: VerifyTaskDto) {
    const machine = machineFor(task);
    const keys = [`device:${task.machineId || task.deviceId}`];
    if (machine?.host) keys.push(`endpoint:${machine.host.trim().toLowerCase()}:${machine.port}`);
    return keys;
  }
  function executionConflict(candidates: VerifyTaskDto[]) {
    if (!candidates.length) return '请先选择需要执行的任务';
    if (candidates.some(isTaskRunning)) return '所选任务正在执行，请等待完成后重试';
    if (runningTaskCount.value + candidates.length > maxParallelTasks)
      return `最多同时执行 ${maxParallelTasks} 个任务，当前运行 ${runningTaskCount.value} 个，请减少所选任务`;
    const targets = new Set(tasks.value.filter(isTaskRunning).flatMap(targetKeys));
    for (const task of candidates) {
      const keys = targetKeys(task);
      if (keys.some(key => targets.has(key))) return `“${task.name}”与其他任务使用同一目标，请分开执行`;
      keys.forEach(key => targets.add(key));
    }
    return '';
  }
  function onSelectionChange(selection: VerifyTaskDto[]) { selectedTaskIds.value = selection.map(task => task.id); }
  async function executeSelectedTasks() {
    const selected = new Set(selectedTaskIds.value);
    await executeTasks(tasks.value.filter(task => selected.has(task.id)), true);
  }
  async function loadPage() {
    const revision = taskRevision;
    loading.value = true;
    try {
      const [devices, config, machiningConfig, autoMetrics, taskList] = await Promise.all([
        machineConnectionDevicesApi.list('CNC'), evaluationApi.getConfig('machine'),
        evaluationApi.getConfig('machining'),
        businessValidationApi.listMetrics(), machineConnectionVerifyApi.listTasks(),
      ]);
      machines.value = devices;
      categoryMetrics.value = { machine: taskMetricOptions(config, autoMetrics), machining: taskMetricOptions(machiningConfig, autoMetrics) };
      if (revision === taskRevision) {
        tasks.value = taskList.map(task => executingIds.value.has(task.id) ? { ...task, status: 'running' } : task);
      }
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
    taskRevision++;
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
  async function executeTask(task: VerifyTaskDto) { await executeTasks([task], false); }
  async function executeTasks(candidates: VerifyTaskDto[], batch: boolean) {
    const conflict = executionConflict(candidates);
    if (conflict) return void ElMessage.warning(conflict);
    const oldStates = new Map(candidates.map(task => [task.id, { status: task.status, completedAt: task.completedAt }]));
    const ownedIds = new Set(candidates.map(task => task.id));
    const failedIds = new Set<string>();
    for (const task of candidates) {
      executingIds.value.add(task.id);
      task.status = 'running';
      task.completedAt = null;
      if (!expandedRows.value.includes(task.id)) expandedRows.value.push(task.id);
    }
    taskRevision++;
    try {
      const first = candidates[0]!;
      const results = batch ? await machineConnectionVerifyApi.runTasks(candidates.map(task => task.id))
        : [{ taskId: first.id, task: await machineConnectionVerifyApi.runTask(first.id), error: null }];
      const byId = new Map(results.map(result => [result.taskId, result]));
      for (const task of candidates) {
        const outcome = byId.get(task.id);
        if (!outcome?.task) {
          failedIds.add(task.id);
          ElMessage.error(`“${task.name}”：${outcome?.error || '未收到执行结果，请刷新后确认'}`);
          continue;
        }
        replaceTask(outcome.task);
        ownedIds.delete(task.id);
        executingIds.value.delete(task.id);
        outcome.task.status === 'completed' ? ElMessage.success(`“${task.name}”自动验证执行完成`)
          : ElMessage.warning(`“${task.name}”：${outcome.task.detail || '自动验证完成，存在未达标项'}`);
      }
    } catch (error) {
      ownedIds.forEach(id => failedIds.add(id));
      reportError(error, '执行验证任务失败');
    } finally {
      if (failedIds.size) {
        try {
          const refreshed = await machineConnectionVerifyApi.listTasks();
          for (const task of refreshed.filter(task => failedIds.has(task.id))) {
            replaceTask(task);
            failedIds.delete(task.id);
          }
        } catch { /* 保留现有任务以供刷新 */ }
      }
      for (const id of failedIds) {
        const current = tasks.value.find(task => task.id === id);
        if (current) Object.assign(current, oldStates.get(id) ?? { status: 'pending' });
      }
      ownedIds.forEach(id => executingIds.value.delete(id));
      taskRevision++;
    }
  }
  async function deleteTask(task: VerifyTaskDto) {
    try {
      await ElMessageBox.confirm(`确定删除任务“${task.name}”吗？`, '删除任务', { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' });
      await machineConnectionVerifyApi.deleteTask(task.id);
      taskRevision++;
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
  return { tasks, machines, metrics, loading, saving, executingIds, selectedTaskIds, runningTaskCount, maxParallelTasks,
    isTaskRunning, onSelectionChange, executeSelectedTasks, exportingId, editorVisible, resultVisible,
    draft, selectedTask, expandedRows, machineFor, metricsFor, selectCategory, loadPage, addTask, editTask, saveTask, executeTask, deleteTask, viewResult, exportTask, canImport, importKnowledge };
}
