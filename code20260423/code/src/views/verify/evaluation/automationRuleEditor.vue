<template>
	<section class="automation-editor" aria-label="结构化自动测试规则">
		<div class="rule-heading">
			<strong>结构化自动测试规则</strong>
			<el-switch v-if="supported" :model-value="Boolean(rule)" active-text="启用" inactive-text="不评分"
				@change="setEnabled" />
		</div>
		<p class="rule-help">仅以下结构化规则参与本轮自动测试；上方描述、评分文字及历史阈值不会被解析为可执行规则。达标线与评分档位分别设置，0 分是有效分数。</p>
		<el-alert v-if="!supported" title="普通项目不自动绑定通讯测试，请由人工评价。" type="info" :closable="false" />
		<template v-else-if="rule">
			<p class="rule-help">关联项目：{{ metricNames[item.metricId!] }}（{{ item.metricId }}）</p>
			<div class="rule-grid">
				<el-form-item label="测量单位">
					<el-select :model-value="rule.unit" @change="setUnit">
						<el-option v-for="unit in unitOptions" :key="unit" :label="unit" :value="unit" />
					</el-select>
				</el-form-item>
				<el-form-item label="评分模式">
					<el-select :model-value="rule.scoringMode" @change="setScoringMode">
						<el-option label="按实测值分档" value="bands" />
						<el-option v-if="rule.unit === '%'" label="百分比线性映射 0–100 分" value="linear" />
					</el-select>
				</el-form-item>
			</div>
			<p class="rule-help">MB / KB 使用二进制换算：1 MiB = 1024 KiB。切换测量单位会同步换算评分档位，达标线保留其独立单位。</p>
			<p class="rule-help">达标阈值在编辑窗口顶部单独填写；此处只设置评分档位与测试参数。</p>
			<template v-if="rule.scoringMode === 'bands'">
				<div class="rule-heading"><strong>评分档位（按不超过实测值的最高下限取分）</strong>
					<el-button link type="primary" :disabled="rule.scoreBands.length >= 100" @click="addBand">添加档位</el-button></div>
				<div v-for="(band, index) in rule.scoreBands" :key="index" class="band-fields">
					<el-form-item :label="`下限 ≥（${rule.unit}）`"><el-input-number v-model="band.min" :min="0"
						:max="rule.unit === '%' ? 100 : undefined" :precision="rule.unit === 'count' ? 0 : undefined" /></el-form-item>
					<el-form-item label="分数"><el-input-number v-model="band.score" :min="0" :max="100" /></el-form-item>
					<el-button link type="danger" :aria-label="`删除第 ${index + 1} 个档位`" @click="rule.scoreBands.splice(index, 1)">删除</el-button>
				</div>
			</template>
			<el-divider content-position="left">测试设置</el-divider>
			<div class="rule-grid">
				<el-form-item label="测试时长（分钟）"><el-input-number v-model="rule.test.durationMinutes" :min="0.001" :max="1440" :step="1" /></el-form-item>
				<el-form-item label="采样间隔（秒）"><el-input-number v-model="rule.test.sampleIntervalSeconds" :min="0.001" :max="86400" :step="1" /></el-form-item>
				<el-form-item label="并发范围"><el-select v-model="rule.test.concurrencyMode">
					<el-option label="显式选择的不同设备" value="devices" /><el-option label="独立连接（当前适配器未支持，不评分）" value="connections" />
						<el-option v-if="rule.test.concurrencyMode === 'sessions'" label="独立会话（历史配置，当前不评分）" value="sessions" />
				</el-select></el-form-item>
				<el-form-item label="并发上限"><el-input-number v-model="rule.test.maxConnections" :min="1" :max="64" :precision="0" /></el-form-item>
				<el-form-item label="停止前连续失败次数"><el-input-number v-model="rule.test.failureLimit" :min="1" :max="100" :precision="0" /></el-form-item>
			</div>
			<div class="rule-grid">
				<el-form-item label="读取地址 / 点位"><el-input v-model="rule.test.readAddress" maxlength="500" placeholder="留空则仅回退设备已有点位，不猜测地址" /></el-form-item>
				<el-form-item label="Host 读取数据类型"><el-select v-model="rule.test.readDataType">
					<el-option v-for="type in readDataTypes" :key="type" :label="type" :value="type" />
				</el-select></el-form-item>
			</div>
			<el-form-item label="文件测试目标目录"><el-input v-model="rule.test.targetDirectory" maxlength="1024" placeholder="/" /></el-form-item>
			<p class="rule-help">读取优先使用规则点位，无点位则不猜测。设备范围需在任务中显式选择；不支持独立会话的协议不会伪装为多连接。文件写入还需逐任务明确授权，且只使用已上传的真实附件，绝不自动执行 NC 程序；不能保证不覆盖已有程序的协议不做写入测试。</p>
			<el-alert :title="ruleSummary(item)" type="info" :closable="false" />
		</template>
	</section>
</template>

<script setup lang="ts">
import { computed } from "vue";
import type { EvaluationItem, EvaluationUnit } from "@/api/evaluation";
import { changeUnit, compatibleUnits, defaultFor, metricNames, readDataTypes, ruleSummary } from "./automationRules";
const props = defineProps<{ item: EvaluationItem }>();
const rule = computed(() => props.item.automation);
const supported = computed(() => Boolean(defaultFor(props.item.metricId)));
const unitOptions = computed(() => compatibleUnits(defaultFor(props.item.metricId)?.unit ?? "%"));
function setEnabled(enabled: boolean | string | number) { props.item.automation = enabled === true ? defaultFor(props.item.metricId) : null; }
function setUnit(unit: EvaluationUnit) { if (rule.value) changeUnit(rule.value, unit); }
function setScoringMode(mode: "linear" | "bands") {
	if (!rule.value) return;
	rule.value.scoringMode = mode;
	rule.value.scoreBands = mode === "linear" ? [] : [{ min: 0, score: 0 }];
}
function addBand() {
	if (!rule.value) return;
	let min = 0;
	while (rule.value.scoreBands.some(band => band.min === min)) min += 1;
	const lower = rule.value.scoreBands.filter(band => band.min < min).sort((a, b) => b.min - a.min)[0];
	rule.value.scoreBands.push({ min, score: lower?.score ?? 0 });
}
</script>

<style scoped lang="scss">
.automation-editor { padding: 16px; border: 1px solid var(--el-border-color); border-radius: 6px; margin-top: 20px; }
.rule-heading { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 8px; margin-bottom: 12px; }
.rule-help { color: var(--el-text-color-secondary); font-size: 13px; line-height: 1.7; margin: 8px 0 16px; }
.rule-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 0 16px; }
.pass-fields { grid-template-columns: 1fr 1fr 1fr; }
.band-fields { display: grid; grid-template-columns: 1fr 1fr auto; gap: 12px; align-items: center; }
.rule-grid :deep(.el-input-number), .band-fields :deep(.el-input-number) { width: 100%; }
@media (max-width: 600px) { .rule-grid { grid-template-columns: 1fr; } .band-fields { grid-template-columns: 1fr 1fr; } .band-fields > .el-button { grid-column: 2; justify-self: end; margin-bottom: 16px; } }
</style>
