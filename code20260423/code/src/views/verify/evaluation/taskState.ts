import { computed, onScopeDispose, ref, toRaw } from 'vue';
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
  // Linked transfer devices may be PLC/Robot rows: do not resolve links from a CNC-only list.
  const targetDevices = ref<DeviceDto[]>([]);
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
  let taskFetchVersion = 0;
  let pollTimer: ReturnType<typeof setTimeout> | undefined;
  let pollController: AbortController | undefined;
  let pollGeneration = 0;
  let polling = false;
  let pollInFlight = false;
  let disposed = false;
  const progressError = ref('');
  const lastPolledAt = ref('');
  function schedulePoll() {
    if (!polling || disposed || pollTimer !== undefined || typeof setTimeout !== 'function') return;
    pollTimer = setTimeout(async () => {
      pollTimer = undefined;
      await refreshTasks();
      schedulePoll();
    }, runningTaskCount.value ? 2000 : 15000);
  }
  function startPolling() { if (!disposed) { polling = true; schedulePoll(); } }
  function stopPolling() {
    polling = false;
    pollGeneration++;
    if (pollTimer !== undefined) clearTimeout(pollTimer);
    pollTimer = undefined;
    pollController?.abort();
  }
  if (typeof onScopeDispose === 'function') onScopeDispose(() => { disposed = true; stopPolling(); });
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
    const registry = targetDevices.value.length ? targetDevices.value : machines.value;
    const keys = new Set<string>();
    function endpoint(host: string | undefined, port: number) {
      if (!host?.trim() || !Number.isInteger(port) || port < 0 || port > 65535)
        throw new Error('设备通信端点无效，请核对设备配置');
      keys.add(`endpoint:${host.trim().replace(/\.$/, '').toLowerCase()}:${port}`);
    }
    function lookup(id: string) {
      const matches = registry.filter(device => device.id.toLowerCase() === id.toLowerCase());
      if (matches.length !== 1 || matches[0]!.id !== id)
        throw new Error(`设备 ${id} 不存在或标识不唯一，请分别核对网关与 IoT 后端设备库`);
      return matches[0]!;
    }
    function linkedDevice(device: DeviceDto) {
      if (device.transfer) return undefined;
      const linked = device.extendedProperties?.transferDeviceId;
      if (!linked?.trim()) return undefined;
      if (linked !== linked.trim()) throw new Error('transferDeviceId 格式无效');
      return lookup(linked);
    }
    function validateGraph(device: DeviceDto, path: Set<string>) {
      const normalized = device.id.toLowerCase();
      if (path.has(normalized)) throw new Error('transferDeviceId 存在循环引用，请核对设备配置');
      path.add(normalized);
      if (path.size > 100) throw new Error('transferDeviceId 引用层级过深');
      const linked = linkedDevice(device);
      if (linked) validateGraph(linked, path);
      path.delete(normalized);
    }
    function addPrimary(device: DeviceDto) {
      keys.add(`device:${device.id.toLowerCase()}`);
      endpoint(device.host, device.port);
    }
    for (const id of new Set([task.machineId || task.deviceId, ...(task.options?.concurrentDeviceIds ?? [])])) {
      if (!id?.trim()) throw new Error('请明确选择测试目标；不会自动测试全部设备');
      const device = lookup(id);
      validateGraph(device, new Set());
      addPrimary(device);
      // Inline wins; otherwise Host connects the linked row's primary endpoint, not its Transfer.
      if (device.transfer) endpoint(device.transfer.host, device.transfer.port);
      else {
        const linked = linkedDevice(device);
        if (linked) addPrimary(linked);
      }
    }
    return [...keys];
  }
  function executionConflict(candidates: VerifyTaskDto[]) {
    if (!candidates.length) return '请先选择需要执行的任务';
    if (candidates.some(task => !(task.machineId || task.deviceId)?.trim())) return '请明确选择测试机床；不会自动测试全部设备';
    if (candidates.some(isTaskRunning)) return '所选任务正在执行，请等待完成后重试';
    if (runningTaskCount.value + candidates.length > maxParallelTasks)
      return `最多同时执行 ${maxParallelTasks} 个任务，当前运行 ${runningTaskCount.value} 个，请减少所选任务`;
    try {
      const targets = new Set(tasks.value.filter(isTaskRunning).flatMap(targetKeys));
      for (const task of candidates) {
        const keys = targetKeys(task);
        if (keys.some(key => targets.has(key))) return `“${task.name}”与其他任务使用同一目标，请分开执行`;
        keys.forEach(key => targets.add(key));
      }
    } catch (error) { return error instanceof Error ? error.message : '无法确认实际执行目标，请刷新设备列表'; }
    // This is advisory local preflight only. Server leases also inspect the actual IoT registry;
    // a prior upstreamSynced flag must never bypass that independent safety check.
    return '';
  }
  function onSelectionChange(selection: VerifyTaskDto[]) { selectedTaskIds.value = selection.map(task => task.id); }
  async function executeSelectedTasks() {
    const selected = new Set(selectedTaskIds.value);
    await executeTasks(tasks.value.filter(task => selected.has(task.id)), true);
  }
  function applyTaskList(taskList: VerifyTaskDto[]) {
    if (disposed) return;
    const previous = new Map(tasks.value.map(task => [task.id, task]));
    tasks.value = taskList.map(task => executingIds.value.has(task.id) ? previous.get(task.id) ?? { ...task, status: 'running' } : task);
    for (const task of tasks.value) {
      if (previous.get(task.id)?.status === 'running' && task.status !== 'running') {
        if (task.status === 'completed') ElMessage.success(`“${task.name}”执行已完成；验收结论：${task.result || '待判定'}`);
        else if (task.status === 'failed') ElMessage.warning(`“${task.name}”执行失败或中断：${task.detail}`);
      }
    }
    if (selectedTask.value) selectedTask.value = tasks.value.find(task => task.id === selectedTask.value?.id) ?? null;
    taskRevision++;
    progressError.value = '';
    lastPolledAt.value = new Date().toLocaleTimeString('zh-CN', { hour12: false });
  }
  async function refreshTasks() {
    if (disposed || pollInFlight) return;
    const revision = taskRevision;
    const version = ++taskFetchVersion;
    const generation = pollGeneration;
    pollInFlight = true;
    pollController = typeof AbortController === 'function' ? new AbortController() : undefined;
    try {
      const taskList = await machineConnectionVerifyApi.listTasks(pollController?.signal);
      if (generation === pollGeneration && revision === taskRevision && version === taskFetchVersion) applyTaskList(taskList);
    } catch {
      if (generation === pollGeneration && !disposed) progressError.value = '进度暂时无法刷新，后台测试不会因浏览器断开而停止；保留最后一次实测快照，稍后自动重试。';
    } finally { pollInFlight = false; pollController = undefined; }
  }
  async function loadPage() {
    const generation = pollGeneration;
    const revision = taskRevision;
    const version = ++taskFetchVersion;
    loading.value = true;
    try {
      const [devices, config, machiningConfig, autoMetrics, taskList] = await Promise.all([
        machineConnectionDevicesApi.list(), evaluationApi.getConfig('machine'),
        evaluationApi.getConfig('machining'),
        businessValidationApi.listMetrics(), machineConnectionVerifyApi.listTasks(),
      ]);
      targetDevices.value = devices;
      machines.value = devices.filter(device => !device.type || device.type === 'CNC');
      categoryMetrics.value = { machine: taskMetricOptions(config, autoMetrics), machining: taskMetricOptions(machiningConfig, autoMetrics) };
      if (generation === pollGeneration && revision === taskRevision && version === taskFetchVersion) applyTaskList(taskList);
    } catch (error) { reportError(error, '加载验证任务失败'); }
    finally { loading.value = false; if (generation === pollGeneration) startPolling(); }
  }
  function addTask() {
    draft.value = newTask(categoryMetrics.value.machine.map(metric => metric.id));
    draft.value.evaluationCategory = 'machine';
    editorVisible.value = true;
  }
  function editTask(task: VerifyTaskDto) {
    draft.value = structuredClone(toRaw(task));
    draft.value.evaluationCategory ??= 'machine';
    draft.value.options ??= { allowFileWrites: false, concurrentDeviceIds: [] };
    draft.value.options.concurrentDeviceIds ??= [];
    editorVisible.value = true;
  }
  function selectCategory(category: EvaluationCategory) {
    if (!draft.value) return;
    draft.value.evaluationCategory = category;
    draft.value.metricIds = categoryMetrics.value[category].map(metric => metric.id);
  }
  function replaceTask(task: VerifyTaskDto) {
    if (disposed) return;
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
      const changed = existing && (existing.machineId !== payload.machineId || (existing.evaluationCategory ?? 'machine') !== payload.evaluationCategory || existing.metricIds.join(',') !== payload.metricIds.join(',') || JSON.stringify(existing.options) !== JSON.stringify(payload.options));
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
    const generation = pollGeneration;
    const conflict = executionConflict(candidates);
    if (conflict) return void ElMessage.warning(conflict);
    const oldStates = new Map(candidates.map(task => [task.id, { status: task.status, completedAt: task.completedAt, currentRunJson: task.currentRunJson, activeRunId: task.activeRunId }]));
    const ownedIds = new Set(candidates.map(task => task.id));
    const failedIds = new Set<string>();
    for (const task of candidates) {
      executingIds.value.add(task.id);
      task.status = 'running';
      task.completedAt = null;
      task.currentRunJson = '';
      task.activeRunId = null;
      if (!expandedRows.value.includes(task.id)) expandedRows.value.push(task.id);
    }
    taskRevision++;
    try {
      const first = candidates[0]!;
      // The current API reserves in the background. Keep synchronous adapters compatible,
      // but never retry an uncertain /start POST as /run (that could launch a second test).
      const results = batch ? await (machineConnectionVerifyApi.startTasks ?? machineConnectionVerifyApi.runTasks)(candidates.map(task => task.id))
        : [{ taskId: first.id, task: await (machineConnectionVerifyApi.startTask ?? machineConnectionVerifyApi.runTask)(first.id), error: null }];
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
        if (outcome.task.status === 'running') ElMessage.success(`“${task.name}”已在后台启动，可离开页面，进度将自动刷新`);
        else if (outcome.task.status === 'completed') ElMessage.success(`“${task.name}”执行已完成；验收结论：${outcome.task.result || '待判定'}`);
        else ElMessage.warning(`“${task.name}”执行失败或中断：${outcome.task.detail}`);
      }
    } catch (error) {
      ownedIds.forEach(id => failedIds.add(id));
      reportError(error, '启动响应未确认，请刷新核实后台状态；不会将浏览器超时当成测试失败');
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
      // Switch an idle 15-second poll to the active 2-second cadence, without touching
      // a polling lifecycle started after this HTTP request (e.g. KeepAlive reactivation).
      if (generation === pollGeneration) {
        if (pollTimer !== undefined) clearTimeout(pollTimer);
        pollTimer = undefined;
        startPolling();
      }
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
    return Boolean(task.status !== 'running' && run?.completedAt && ['completed', 'failed'].includes(run.status)
      && run.machineSnapshot && run.evaluationSnapshot && run.metrics.length);
  }
  function importKnowledge(task: VerifyTaskDto) {
    if (!canImport(task)) return void ElMessage.warning('该任务旧结果未保存评价指标和机床快照，请重新执行后导入');
    void router.push({ path: '/verify/knowledge', query: { taskId: task.id } });
  }
  return { tasks, machines, metrics, loading, saving, executingIds, selectedTaskIds, runningTaskCount, maxParallelTasks,
    isTaskRunning, onSelectionChange, executeSelectedTasks, exportingId, editorVisible, resultVisible,
    progressError, lastPolledAt, refreshTasks, startPolling, stopPolling,
    draft, selectedTask, expandedRows, machineFor, metricsFor, selectCategory, loadPage, addTask, editTask, saveTask, executeTask, deleteTask, viewResult, exportTask, canImport, importKnowledge };
}
