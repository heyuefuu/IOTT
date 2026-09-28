import type { EvaluationConfig } from '@/api/evaluation';
import type { MetricDto } from '@/api/businessValidation';
import type { VerifyRunResponse, VerifyTaskDto } from '@/api/machineConnectionVerify';

export interface TaskMetricOption { id: string; name: string; code: string; reference: string; description: string }
export const automaticMetricIds = ['industrial-protocol', 'communication-stability', 'max-connections', 'transfer-protocol', 'file-integrity', 'transfer-speed', 'file-size'];
export const taskStatusLabels: Record<string, string> = { pending: '待执行', running: '执行中', completed: '已完成', passed: '达标', failed: '失败' };
export function taskStatusType(status: string) {
  return status === 'running' ? 'warning' : ['completed', 'passed'].includes(status) ? 'success' : status === 'failed' ? 'danger' : 'info';
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
      return { id: item.metricId!, name: item.name, code: metric?.code ?? '', reference: metric?.reference ?? '', description: item.method || item.desc };
    }).filter((item, index, items) => items.findIndex(candidate => candidate.id === item.id) === index);
}
export function newTask(metricIds: string[]): VerifyTaskDto {
  return { id: '', name: '', type: 'performance', status: 'pending', priority: '中', deviceId: '', machineId: '',
    metricIds: [...metricIds], params: '', description: '', createdAt: new Date().toISOString(), completedAt: null,
    executionTime: '', result: '', detail: '', scheduleType: 'none', scheduleTime: '', lastRunJson: '' };
}
export function taskRun(task: VerifyTaskDto): VerifyRunResponse | null {
  if (!task.lastRunJson) return null;
  try {
    const parsed: unknown = JSON.parse(task.lastRunJson);
    if (!parsed || typeof parsed !== 'object' || !('metrics' in parsed) || !Array.isArray(parsed.metrics)) return null;
    if (!parsed.metrics.every(metric => metric && typeof metric === 'object' && typeof metric.metricId === 'string')) return null;
    return parsed as VerifyRunResponse;
  } catch { return null; }
}
export function taskMetricSummary(task: VerifyTaskDto, metrics: TaskMetricOption[]) {
  const previous = taskRun(task)?.metrics ?? [];
  return task.metricIds.map(id => metrics.find(metric => metric.id === id)?.name ?? previous.find(metric => metric.metricId === id)?.name ?? id).join('、') || '—';
}
