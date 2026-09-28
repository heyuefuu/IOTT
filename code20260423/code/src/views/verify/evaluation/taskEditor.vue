<template>
  <el-dialog :model-value="visible" :title="task?.id ? '编辑任务' : '创建任务'" width="min(800px, 94vw)" :close-on-click-modal="false" @update:model-value="$emit('update:visible', $event)">
    <el-form v-if="task" ref="formRef" :model="task" label-position="top">
      <h3>任务基本信息</h3>
      <div class="basic-fields">
        <el-form-item label="任务名称" prop="name" :rules="[{ required: true, whitespace: true, message: '请输入任务名称', trigger: 'blur' }]">
          <el-input v-model="task.name" placeholder="请输入任务名称" maxlength="200" />
        </el-form-item>
        <el-form-item label="测试机床" prop="machineId" :rules="[{ required: true, message: '请选择测试机床', trigger: 'change' }]">
          <el-select v-model="task.machineId" placeholder="请选择测试机床" filterable>
            <el-option v-for="machine in machines" :key="machine.id" :value="machine.id" :label="`${machine.name} (${machine.model || machine.brand} · ${machine.host})`" />
          </el-select>
        </el-form-item>
      </div>
      <h3>测试指标</h3>
      <el-form-item label="评价类别">
        <el-select :model-value="task.evaluationCategory || 'machine'" @change="$emit('category-change', $event)"><el-option label="机床类" value="machine" /><el-option label="加工中心类" value="machining" /></el-select>
      </el-form-item>
      <el-checkbox :model-value="allSelected" :indeterminate="partSelected" @change="toggleAll">全选</el-checkbox>
      <el-form-item prop="metricIds" :rules="[{ type: 'array', min: 1, required: true, message: '请至少选择一个测试指标', trigger: 'change' }]">
        <el-checkbox-group v-model="task.metricIds" class="metric-grid">
          <el-checkbox v-for="metric in metrics" :key="metric.id" :value="metric.id" border>
            <el-tooltip :content="metric.description" placement="top" :show-after="500" :disabled="!metric.description"><span>{{ metric.name }}</span></el-tooltip>
          </el-checkbox>
        </el-checkbox-group>
      </el-form-item>
      <el-empty v-if="!metrics.length" description="评价指标管理中暂无可自动测试的通讯指标" :image-size="70" />
      <el-alert v-if="unknownMetrics.length" type="warning" :closable="false" :title="`原任务包含当前指标配置中已移除的项目：${unknownMetrics.join('、')}。请重新选择测试指标。`" />
      <p v-if="task.scheduleType === 'daily'" class="schedule-note">该任务保留每日 {{ task.scheduleTime }} 的自动执行设置。</p>
    </el-form>
    <template #footer>
      <el-button :disabled="saving" @click="$emit('update:visible', false)">取消</el-button>
      <el-button type="primary" :loading="saving" :disabled="!metrics.length" @click="submit">{{ task?.id ? '保存修改' : '创建任务' }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue';
import { ElMessage, type FormInstance } from 'element-plus';
import type { DeviceDto } from '@/api/machineConnectionDevices';
import type { EvaluationCategory } from '@/api/evaluation';
import type { VerifyTaskDto } from '@/api/machineConnectionVerify';
import type { TaskMetricOption } from './taskModel';
const props = defineProps<{ visible: boolean; task: VerifyTaskDto | null; machines: DeviceDto[]; metrics: TaskMetricOption[]; saving: boolean }>();
const emit = defineEmits<{ 'update:visible': [value: boolean]; save: []; 'category-change': [category: EvaluationCategory] }>();
const formRef = ref<FormInstance>();
const allSelected = computed(() => props.metrics.length > 0 && props.metrics.every(metric => props.task?.metricIds.includes(metric.id)));
const partSelected = computed(() => Boolean(props.task?.metricIds.length) && !allSelected.value);
const unknownMetrics = computed(() => props.task?.metricIds.filter(id => !props.metrics.some(metric => metric.id === id)) ?? []);
function toggleAll(checked: unknown) { if (props.task) props.task.metricIds = checked ? props.metrics.map(metric => metric.id) : []; }
async function submit() {
  if (!await formRef.value?.validate().catch(() => false)) return;
  if (unknownMetrics.value.length) return void ElMessage.warning('请重新选择当前可测试的指标');
  if (!props.machines.some(machine => machine.id === props.task?.machineId)) return void ElMessage.warning('所选机床已不存在，请重新选择');
  emit('save');
}
</script>

<style scoped>
h3 { color: var(--el-text-color-primary); font-size: 16px; font-weight: 600; margin-bottom: 20px; padding-bottom: 12px; border-bottom: 1px solid var(--el-border-color-light); }
.basic-fields { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 20px; margin-bottom: 16px; }
.basic-fields .el-select { width: 100%; }
.metric-grid { display: grid; width: 100%; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; margin-top: 12px; }
.metric-grid .el-checkbox { margin: 0; min-height: 46px; height: auto; white-space: normal; }
.metric-grid :deep(.el-checkbox__label) { white-space: normal; }
.schedule-note { color: var(--el-text-color-secondary); font-size: 13px; margin-top: 12px; }
@media (max-width: 600px) { .basic-fields, .metric-grid { grid-template-columns: 1fr; } }
</style>
