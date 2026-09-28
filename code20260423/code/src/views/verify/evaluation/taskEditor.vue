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
      <h3>执行范围与安全许可</h3>
      <el-form-item v-if="task.metricIds.includes('max-connections') || concurrentDeviceIds.length" label="并发测试设备（显式目标，不会自动选择全部设备）">
        <el-select v-model="concurrentDeviceIds" multiple filterable clearable placeholder="请选择参与并发实测的设备" class="target-select">
          <el-option v-for="machine in machines" :key="machine.id" :value="machine.id" :label="`${machine.name} · ${machine.host}:${machine.port}`" />
        </el-select>
        <p class="safety-note">此处为单个指标的实测设备组，与最多 7 个任务并行无关。所有选中设备及其文件通道会占用目标锁；请勿选择相同端点的别名设备。</p>
        <p v-if="!concurrentDeviceIds.length" class="safety-note">未配置目标时，并发连接指标将明确标注未评分。</p>
        <p v-else-if="concurrencyLimit && concurrentDeviceIds.length < concurrencyLimit" class="safety-note">当前 {{ concurrentDeviceIds.length }} 个目标少于规则上限 {{ concurrencyLimit }}，只能验证下限，不能作为最大连接数评分。</p>
      </el-form-item>
      <el-checkbox v-model="allowFileWrites">明确许可本任务写入并回读测试文件（默认不许可）</el-checkbox>
      <el-alert type="warning" :closable="false" show-icon class="safety-alert" title="仅向规则指定的测试目录写入隔离测试文件；不自动执行 NC 程序，不覆盖既有程序。" description="请在受控测试环境确认目标与目录后授权。没有许可、没有真实附件，或协议不能保障隔离/不覆盖时，相关指标保持未评分。每日计划任务会沿用此项许可，可随时编辑取消。" />
      <p class="safety-note">读取地址、数据类型、测试目录、达标线与评分档位来自“评价指标管理”的版本化规则；开始运行后按本轮快照执行，不读取旧指标库的隐藏阈值。</p>
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
const concurrentDeviceIds = computed({
  get: () => props.task?.options?.concurrentDeviceIds ?? [],
  set: (value: string[]) => { if (props.task) { props.task.options ??= { allowFileWrites: false }; props.task.options.concurrentDeviceIds = value; } },
});
const allowFileWrites = computed({
  get: () => props.task?.options?.allowFileWrites === true,
  set: (value: boolean) => { if (props.task) { props.task.options ??= {}; props.task.options.allowFileWrites = value; } },
});
const concurrencyLimit = computed(() => props.metrics.find(metric => metric.id === 'max-connections')?.automation?.test.maxConnections);
const allSelected = computed(() => props.metrics.length > 0 && props.metrics.every(metric => props.task?.metricIds.includes(metric.id)));
const partSelected = computed(() => Boolean(props.task?.metricIds.length) && !allSelected.value);
const unknownMetrics = computed(() => props.task?.metricIds.filter(id => !props.metrics.some(metric => metric.id === id)) ?? []);
function toggleAll(checked: unknown) { if (props.task) props.task.metricIds = checked ? props.metrics.map(metric => metric.id) : []; }
async function submit() {
  if (!await formRef.value?.validate().catch(() => false)) return;
  if (unknownMetrics.value.length) return void ElMessage.warning('请重新选择当前可测试的指标');
  if (!props.machines.some(machine => machine.id === props.task?.machineId)) return void ElMessage.warning('所选机床已不存在，请重新选择');
  const endpoints = new Set<string>();
  if (concurrentDeviceIds.value.length > 100) return void ElMessage.warning('并发测试目标最多100个');
  for (const id of concurrentDeviceIds.value) {
    const device = props.machines.find(machine => machine.id === id);
    const endpoint = `${device?.host?.trim().toLowerCase()}:${device?.port}`;
    if (!device?.host || !device.port || endpoints.has(endpoint)) return void ElMessage.warning('并发设备已移除、未配置端点或网络端点重复，请重新选择');
    endpoints.add(endpoint);
  }
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
.target-select { width: 100%; }
.safety-note { color: var(--el-text-color-secondary); font-size: 13px; line-height: 1.6; margin-top: 10px; }
.safety-alert { margin-top: 12px; }
.schedule-note { color: var(--el-text-color-secondary); font-size: 13px; margin-top: 12px; }
@media (max-width: 600px) { .basic-fields, .metric-grid { grid-template-columns: 1fr; } }
</style>
