<template>
  <div class="task-results">
    <div class="result-heading">
      <strong>{{ historicalMachine?.name || machine?.name || '未关联机床' }}</strong>
      <el-tag :type="taskStatusType(task.status)">{{ taskStatusLabels[task.status] || task.status }}</el-tag>
      <span v-if="run">最近一次执行：{{ taskTime(run.completedAt || run.startedAt) }}</span>
    </div>
    <el-descriptions :column="3" border size="small" class="machine-config">
      <el-descriptions-item label="配置来源">{{ historicalMachine ? '本次运行机床快照' : '当前机床配置' }}</el-descriptions-item>
      <el-descriptions-item label="数控系统">{{ historicalMachine ? historicalMachine.controlSystem || '—' : [machine?.brand, machine?.model].filter(Boolean).join(' ') || '—' }}</el-descriptions-item>
      <el-descriptions-item label="通讯协议">{{ historicalMachine ? historicalMachine.protocol || '—' : machine?.protocol || '—' }}</el-descriptions-item>
      <el-descriptions-item label="IP地址">{{ historicalMachine ? historicalMachine.host || '—' : machine?.host || '—' }}</el-descriptions-item>
      <el-descriptions-item label="端口">{{ historicalMachine?.port ?? machine?.port ?? '—' }}</el-descriptions-item>
      <el-descriptions-item label="连接超时">{{ historicalMachine?.connectTimeoutMs ?? machine?.connectTimeoutMs ?? '—' }} ms</el-descriptions-item>
      <el-descriptions-item v-if="task.scheduleType === 'daily'" label="执行计划">每日 {{ task.scheduleTime }}</el-descriptions-item>
      <el-descriptions-item v-if="run?.evaluationSnapshot" label="评价指标版本">{{ run.evaluationSnapshot.version }}</el-descriptions-item>
    </el-descriptions>
    <el-alert v-if="task.status === 'running'" type="info" :closable="false" show-icon title="任务正在执行，完成后显示本次实测结果。" />
    <template v-else-if="run">
      <div class="run-summary"><strong>达标 {{ passedCount }} / {{ run.metrics.length }} 项</strong><span>评分：待评价</span></div>
      <el-table :data="run.metrics" border class="result-table">
        <el-table-column type="expand">
          <template #default="scope"><div class="metric-detail"><p>{{ scope.row.detail || '无补充说明' }}</p><ul v-if="scope.row.evidence?.length"><li v-for="(evidence, index) in scope.row.evidence" :key="index">{{ evidence }}</li></ul></div></template>
        </el-table-column>
        <el-table-column label="真实数据（实测）" min-width="270">
          <template #default="scope"><div class="metric-label">{{ scope.row.code }} {{ metricName(scope.row.metricId, scope.row.name) }}</div><strong class="measured-value">{{ scope.row.value || scope.row.result || '—' }}</strong></template>
        </el-table-column>
        <el-table-column label="条例判定" min-width="240">
          <template #default="scope"><el-tag :type="taskStatusType(scope.row.status)" size="small">{{ scope.row.status === 'passed' ? '达标' : scope.row.status === 'failed' ? '未达标' : taskStatusLabels[scope.row.status] || scope.row.status }}</el-tag><p class="reference">参考：{{ scope.row.reference || '—' }}</p></template>
        </el-table-column>
        <el-table-column label="得分" width="100"><template #default><span class="pending-score">待评价</span></template></el-table-column>
      </el-table>
      <p class="run-detail">{{ run.detail || task.detail }}</p>
    </template>
    <el-empty v-else :description="task.lastRunJson ? '运行结果数据异常，请重新执行任务' : '任务尚未执行，点击执行开始测试'" :image-size="65" />
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import type { VerifyTaskDto } from '@/api/machineConnectionVerify';
import type { DeviceDto } from '@/api/machineConnectionDevices';
import { taskRun, taskStatusLabels, taskStatusType, taskTime } from './taskModel';
const props = defineProps<{ task: VerifyTaskDto; machine?: DeviceDto }>();
const run = computed(() => taskRun(props.task));
const historicalMachine = computed(() => run.value?.machineSnapshot);
const passedCount = computed(() => run.value?.metrics.filter(metric => metric.status === 'passed').length ?? 0);
function metricName(id: string, fallback: string) {
  return run.value?.evaluationSnapshot?.indicators.flatMap(category => category.children.flatMap(sub => sub.items)).find(item => item.metricId === id)?.name || fallback;
}
</script>

<style scoped>
.task-results { padding: 20px; background: var(--el-fill-color-lighter); border-radius: 6px; }
.result-heading { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; margin-bottom: 16px; }
.result-heading strong { font-size: 16px; }
.result-heading span:last-child { color: var(--el-text-color-secondary); font-size: 13px; }
.machine-config { margin-bottom: 16px; }
.run-summary { display: flex; justify-content: space-between; margin: 18px 0 12px; color: var(--el-text-color-regular); }
.run-summary span, .pending-score { color: var(--el-text-color-secondary); }
.metric-label { color: var(--el-text-color-secondary); font-size: 13px; }
.measured-value { display: block; color: var(--el-text-color-primary); font-size: 16px; margin-top: 7px; overflow-wrap: anywhere; }
.reference { margin-top: 7px; color: var(--el-text-color-secondary); white-space: pre-wrap; }
.metric-detail { white-space: pre-wrap; padding: 16px 30px; overflow-wrap: anywhere; }
.metric-detail ul { margin-top: 12px; padding-left: 20px; }
.run-detail { margin-top: 14px; color: var(--el-text-color-secondary); white-space: pre-wrap; }
</style>
