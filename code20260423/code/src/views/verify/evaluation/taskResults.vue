<template>
  <div class="task-results">
    <div class="result-heading">
      <strong>{{ historicalMachine?.name || machine?.name || '未关联机床' }}</strong>
      <el-tag :type="taskStatusType(task.status)">{{ taskStatusLabels[task.status] || task.status }}</el-tag>
      <span v-if="run">{{ isRunning ? '本次运行开始' : '最近已结束运行' }}：{{ taskTime(run.completedAt || run.startedAt) }}</span>
    </div>
    <el-descriptions :column="3" border size="small" class="machine-config">
      <el-descriptions-item label="配置来源">{{ historicalMachine ? '所展示运行的机床快照' : '当前机床配置（非实测证据）' }}</el-descriptions-item>
      <el-descriptions-item label="数控系统">{{ historicalMachine ? historicalMachine.controlSystem || '—' : [machine?.brand, machine?.model].filter(Boolean).join(' ') || '—' }}</el-descriptions-item>
      <el-descriptions-item label="通讯协议">{{ historicalMachine ? historicalMachine.protocol || '—' : machine?.protocol || '—' }}</el-descriptions-item>
      <el-descriptions-item label="IP地址">{{ historicalMachine ? historicalMachine.host || '—' : machine?.host || '—' }}</el-descriptions-item>
      <el-descriptions-item label="端口">{{ historicalMachine?.port ?? machine?.port ?? '—' }}</el-descriptions-item>
      <el-descriptions-item label="连接超时">{{ historicalMachine?.connectTimeoutMs ?? machine?.connectTimeoutMs ?? '—' }} ms</el-descriptions-item>
      <el-descriptions-item v-if="task.scheduleType === 'daily'" label="执行计划">每日 {{ task.scheduleTime }}</el-descriptions-item>
      <el-descriptions-item v-if="run?.evaluationSnapshot" label="评价指标版本">{{ run.evaluationSnapshot.version }}</el-descriptions-item>
      <el-descriptions-item v-if="run?.optionsSnapshot" label="本轮文件写入">{{ run.optionsSnapshot.allowFileWrites ? '已显式许可（不执行 NC）' : '未许可，只读' }}</el-descriptions-item>
      <el-descriptions-item v-if="run?.optionsSnapshot" label="本轮并发目标">{{ run.optionsSnapshot.concurrentDeviceIds?.join('、') || '未配置' }}</el-descriptions-item>
    </el-descriptions>
    <el-alert v-if="isRunning" type="info" :closable="false" show-icon title="任务在后台执行，下面展示本轮真实进度；关闭页面不会中断测试。" description="上一轮完整结果保留至本轮结束，不用旧记录填充本轮实测。" />
    <el-alert v-if="task.status === 'pending' && run" type="warning" :closable="false" title="当前展示上次运行结果；修改后的任务配置需重新执行才会产生新结果。" />
    <template v-if="run || isRunning">
      <div class="run-summary" aria-live="polite">
        <strong v-if="isRunning">总体进度 {{ percent(run?.progressPercent) }}% · 已处理 {{ run?.completedMetricCount ?? 0 }} / {{ run?.totalMetricCount ?? task.metricIds.length }} 项</strong>
        <strong v-else>验收结论：{{ run?.result || '待判定' }} · 达标 {{ passedCount }} / {{ displayMetrics.length }} 项</strong>
        <span>任务总分：{{ taskScore(run?.totalScore) }}（所选测试项均分，非综合评价分）</span>
      </div>
      <p class="score-summary">{{ run?.scoreSummary || '任一所选测试项未评分，总分即为待评分；0 分是有效评分。' }}</p>
      <el-table :data="displayMetrics" border class="result-table">
        <el-table-column type="expand">
          <template #default="scope">
            <div class="metric-detail">
              <p>{{ scope.row.detail || '暂无补充说明' }}</p>
              <p>评分依据：{{ scope.row.scoreReason || '未记录，待评分' }}</p>
              <template v-if="metricRule(scope.row.metricId)">
                <p>评分模式：{{ metricRule(scope.row.metricId)?.scoringMode }}；档位：{{ bands(scope.row.metricId) }}</p>
                <p>读取地址：{{ metricRule(scope.row.metricId)?.test.readAddress || '回退设备已有点位；无点位不猜测' }}；数据类型：{{ metricRule(scope.row.metricId)?.test.readDataType || '未记录' }}；测试目录：{{ metricRule(scope.row.metricId)?.test.targetDirectory || '/' }}</p>
                <p>持续 {{ metricRule(scope.row.metricId)?.test.durationMinutes }} min；采样间隔 {{ metricRule(scope.row.metricId)?.test.sampleIntervalSeconds }} s；并发上限 {{ metricRule(scope.row.metricId)?.test.maxConnections }}；失败上限 {{ metricRule(scope.row.metricId)?.test.failureLimit }}</p>
              </template>
              <p v-else>未保存自动测试规则快照；不使用当前配置或旧指标库阈值补判历史结果。</p>
              <strong>本轮证据</strong>
              <ul v-if="scope.row.evidence?.length"><li v-for="(evidence, index) in scope.row.evidence" :key="index">{{ evidence }}</li></ul>
              <p v-else>暂无本轮证据</p>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="测试项 / 实测值" min-width="240">
          <template #default="scope">
            <div class="metric-label">{{ scope.row.code }} {{ metricName(scope.row.metricId, scope.row.name) }}</div>
            <strong class="measured-value">{{ measurement(scope.row) }}</strong>
            <p class="reference">来源：{{ measurementSource(scope.row.measurementSource) }}</p>
          </template>
        </el-table-column>
        <el-table-column label="执行状态 / 进度" min-width="145">
          <template #default="scope"><el-tag :type="taskStatusType(scope.row.executionStatus || '')" size="small">{{ metricExecutionLabels[scope.row.executionStatus] || '历史未记录' }}</el-tag><p v-if="scope.row.executionStatus" class="reference">{{ percent(scope.row.progressPercent) }}%</p></template>
        </el-table-column>
        <el-table-column label="验收判定（非执行状态）" min-width="235">
          <template #default="scope"><el-tag :type="taskStatusType(scope.row.status)" size="small">{{ judgment(scope.row.status) }}</el-tag><p class="reference">达标线：{{ passRule(scope.row.metricId) }}</p></template>
        </el-table-column>
        <el-table-column label="单项得分" min-width="170"><template #default="scope"><strong>{{ taskScore(scope.row.score) }}</strong><p class="reference">{{ scope.row.scoreReason || '未记录评分依据' }}</p></template></el-table-column>
      </el-table>
      <p class="run-detail">{{ run?.detail || (isRunning ? '等待后台实测快照。' : task.detail) }}</p>
    </template>
    <el-empty v-else :description="task.lastRunJson ? '运行结果数据异常，请重新执行任务' : '任务尚未执行，点击执行开始测试'" :image-size="65" />
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import type { VerifyMetricResult, VerifyTaskDto } from '@/api/machineConnectionVerify';
import type { DeviceDto } from '@/api/machineConnectionDevices';
import { metricExecutionLabels, taskRun, taskScore, taskStatusLabels, taskStatusType, taskTime } from './taskModel';
const props = defineProps<{ task: VerifyTaskDto; machine?: DeviceDto }>();
const isRunning = computed(() => props.task.status === 'running');
const run = computed(() => taskRun(props.task));
const historicalMachine = computed(() => run.value?.machineSnapshot);
const displayMetrics = computed<VerifyMetricResult[]>(() => run.value?.metrics ?? (isRunning.value ? props.task.metricIds.map(metricId => ({
  metricId, name: metricId, code: '', status: 'unrated', result: '', value: '', reference: '', detail: '', evidence: [],
  executionStatus: 'pending', progressPercent: 0, score: null, scoreReason: '等待本轮实测',
})) : []));
const passedCount = computed(() => displayMetrics.value.filter(metric => metric.status === 'passed').length);
function metricItem(id: string) {
  return run.value?.evaluationSnapshot?.indicators.flatMap(category => category.children.flatMap(sub => sub.items)).find(item => item.metricId === id);
}
function metricName(id: string, fallback: string) { return metricItem(id)?.name || fallback; }
function metricRule(id: string) { return metricItem(id)?.automation; }
function passRule(id: string) {
  const rule = metricRule(id);
  if (!rule) return isRunning.value ? '等待本轮配置快照' : '历史未记录（不补判）';
  const pass = rule.passRule;
  if (!pass) return '未配置，不能自动判通过';
  return `${({ gte: '≥', eq: '=', lte: '≤' } as Record<string, string>)[pass.comparison] || pass.comparison} ${pass.threshold} ${pass.unit}`;
}
function bands(id: string) { return metricRule(id)?.scoreBands.map(band => `≥${band.min} → ${band.score}分`).join('；') || '无档位'; }
function percent(value?: number) { return typeof value === 'number' && Number.isFinite(value) ? Number(Math.max(0, Math.min(100, value)).toFixed(2)) : 0; }
function judgment(status: string) { return ({ passed: '达标', failed: '未达标', error: '执行错误', unrated: '未判定', pending: '待判定', running: '待判定' } as Record<string, string>)[status] || '待判定'; }
function measurement(metric: VerifyMetricResult) {
  return typeof metric.measurement === 'number' && Number.isFinite(metric.measurement) ? `${metric.measurement} ${metric.unit || ''}` : metric.value || '暂无可靠实测';
}
function measurementSource(source?: string) { return source === 'current-run' ? '本轮实测' : source === 'configuration' ? '配置统计（非通信实测）' : source || '未记录'; }
</script>

<style scoped>
.task-results { padding: 20px; background: var(--el-fill-color-lighter); border-radius: 6px; }
.result-heading { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; margin-bottom: 16px; }
.result-heading strong { font-size: 16px; }
.result-heading span:last-child { color: var(--el-text-color-secondary); font-size: 13px; }
.machine-config { margin-bottom: 16px; }
.run-summary { display: flex; justify-content: space-between; gap: 14px; flex-wrap: wrap; margin: 18px 0 12px; color: var(--el-text-color-regular); }
.score-summary { font-size: 13px; color: var(--el-text-color-secondary); margin-bottom: 12px; }
.metric-label { color: var(--el-text-color-secondary); font-size: 13px; }
.measured-value { display: block; color: var(--el-text-color-primary); font-size: 16px; margin-top: 7px; overflow-wrap: anywhere; }
.reference { margin-top: 7px; color: var(--el-text-color-secondary); white-space: pre-wrap; overflow-wrap: anywhere; }
.metric-detail { white-space: pre-wrap; padding: 16px 30px; overflow-wrap: anywhere; }
.metric-detail p { margin-bottom: 10px; }
.metric-detail ul { margin-top: 12px; padding-left: 20px; }
.run-detail { margin-top: 14px; color: var(--el-text-color-secondary); white-space: pre-wrap; }
</style>
