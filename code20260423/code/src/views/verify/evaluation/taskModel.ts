import type { EvaluationConfig, EvaluationItem } from '@/api/evaluation';
import type { MetricDto } from '@/api/businessValidation';
import type { VerifyRunResponse, VerifyTaskDto } from '@/api/machineConnectionVerify';

export interface TaskMetricOption { id: string; name: string; code: string; reference: string; description: string; automation?: EvaluationItem['automation'] }
export const automaticMetricIds = ['industrial-protocol', 'communication-stability', 'max-connections', 'transfer-protocol', 'file-integrity', 'transfer-speed', 'file-size'];
export const taskStatusLabels: Record<string, string> = { pending: '待执行', running: '执行中', completed: '执行完成', passed: '达标', failed: '执行失败', unrated: '未判定', error: '执行错误', skipped: '已跳过' };
export const metricExecutionLabels: Record<string, string> = { pending: '等待中', running: '执行中', completed: '已完成', error: '执行错误', skipped: '已跳过' };
export function taskStatusType(status: string) {
  return status === 'running' ? 'warning' : ['completed', 'passed'].includes(status) ? 'success' : ['failed', 'error'].includes(status) ? 'danger' : 'info';
}
export function taskTime(value?: string | null) {
  if (!value) return '—';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString('zh-CN', { hour12: false });
}
export function taskMetricOptions(config: EvaluationConfig, metrics: MetricDto[]): TaskMetricOption[] {
  return config.indicators.flatMap(section => section.children.flatMap(subcategory => subcategory.items))
    .filter(item => item.metricId && automaticMetricIds.includes(item.metricId))
    .map(item => {
      const metric = metrics.find(candidate => candidate.id === item.metricId);
      const pass = item.automation?.passRule;
      const reference = pass ? `${pass.comparison} ${pass.threshold} ${pass.unit}` : '达标线未配置';
      return { id: item.metricId!, name: item.name, code: metric?.code ?? '', reference, description: item.method || item.desc, automation: item.automation };
    }).filter((item, index, items) => items.findIndex(candidate => candidate.id === item.id) === index);
}
export function newTask(metricIds: string[]): VerifyTaskDto {
  return { id: '', name: '', type: 'performance', status: 'pending', priority: '中', deviceId: '', machineId: '',
    metricIds: [...metricIds], params: '', description: '', createdAt: new Date().toISOString(), completedAt: null,
    executionTime: '', result: '', detail: '', scheduleType: 'none', scheduleTime: '', lastRunJson: '', currentRunJson: '', activeRunId: null,
    options: { allowFileWrites: false, concurrentDeviceIds: [] } };
}
export function taskRun(task: VerifyTaskDto, source: 'display' | 'history' = 'display'): VerifyRunResponse | null {
  const current = source === 'display' && task.status === 'running';
  const json = current ? task.currentRunJson : task.lastRunJson;
  if (!json) return null;
  try {
    const parsed: unknown = JSON.parse(json);
    if (!parsed || typeof parsed !== 'object' || !('metrics' in parsed) || !Array.isArray(parsed.metrics)) return null;
    if (!parsed.metrics.every(metric => metric && typeof metric === 'object' && typeof metric.metricId === 'string')) return null;
    const run = parsed as VerifyRunResponse;
    if (current && task.activeRunId && run.runId !== task.activeRunId) return null;
    return run;
  } catch { return null; }
}
export function taskProgress(task: VerifyTaskDto): number {
  const value = taskRun(task)?.progressPercent;
  return typeof value === 'number' && Number.isFinite(value) ? Math.max(0, Math.min(100, value)) : 0;
}
export function taskScore(value?: number | null): string {
  return typeof value === 'number' && Number.isFinite(value) ? `${Number(value.toFixed(2))} 分` : '待评分';
}
export function taskMetricSummary(task: VerifyTaskDto, metrics: TaskMetricOption[]) {
  const previous = taskRun(task)?.metrics ?? [];
  return task.metricIds.map(id => metrics.find(metric => metric.id === id)?.name ?? previous.find(metric => metric.metricId === id)?.name ?? id).join('、') || '—';
}
