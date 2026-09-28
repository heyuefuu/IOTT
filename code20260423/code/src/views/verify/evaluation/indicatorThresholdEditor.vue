<template>
	<section class="threshold-editor" aria-label="指标达标阈值">
		<div class="threshold-heading">
			<strong>达标阈值</strong>
			<el-tag :type="pass ? 'success' : 'info'" size="small">{{ pass ? '已配置' : '未配置' }}</el-tag>
			<el-button v-if="pass" link type="danger" @click="setThreshold(undefined)">清除阈值</el-button>
		</div>
		<p class="threshold-help">直接输入该项的达标数值，留空表示不设置。阈值用于判断是否达标，评分档位用于计算得分，两者互不替代。</p>
		<el-alert v-if="automatic && !enabled" type="info" :closable="false">
			本项目自动规则已关闭。
			<el-button link type="primary" @click="enableAutomation">启用规则并配置阈值</el-button>
		</el-alert>
		<div class="threshold-fields">
			<el-form-item label="比较方式">
				<el-select v-model="comparison" :disabled="!enabled" aria-label="阈值比较方式" @change="updateCriterion">
					<el-option label="≥ 大于等于" value="gte" />
					<el-option label="= 等于" value="eq" />
					<el-option label="≤ 小于等于" value="lte" />
				</el-select>
			</el-form-item>
			<el-form-item label="达标数值（可留空）">
				<!-- Element Plus initializes aria-disabled only when this input mounts. -->
				<el-input-number :key="enabled ? 'enabled' : 'disabled'" :model-value="pass?.threshold" :disabled="!enabled" :min="automatic ? 0 : undefined"
					:max="automatic && unit === '%' ? 100 : undefined" :precision="automatic && unit === 'count' ? 0 : undefined"
					:controls="false" placeholder="请输入阈值" aria-label="指标达标阈值数值" @update:model-value="setThreshold" />
			</el-form-item>
			<el-form-item label="阈值单位">
				<el-select v-if="automatic" v-model="unit" :disabled="!enabled" aria-label="阈值单位" @change="updateCriterion">
					<el-option v-for="option in units" :key="option" :label="unitLabel(option)" :value="option" />
				</el-select>
				<el-input v-else v-model="unit" maxlength="50" placeholder="如 mm、%、台、℃" aria-label="人工阈值单位" @update:model-value="updateCriterion" />
			</el-form-item>
		</div>
		<div class="threshold-preview">当前判据：<strong>{{ thresholdSummary(item) }}</strong></div>
		<p v-if="automatic" class="threshold-help">{{ measurementHint }} 保存后用于新执行任务；已有任务和评价记录保留原阈值快照。</p>
		<p v-else class="threshold-help">人工评价项：阈值随指标及评价快照保存，供人工对照实测数据核验；不会自动执行测试或自动给分。定性项目可以留空。</p>
	</section>
</template>

<script setup lang="ts">
import { computed, ref, watch } from "vue";
import type { EvaluationItem, EvaluationPassRule, EvaluationUnit } from "@/api/evaluation";
import { compatibleUnits, defaultFor, thresholdSummary } from "./automationRules";

const props = defineProps<{ item: EvaluationItem }>();
const defaults = computed(() => defaultFor(props.item.metricId));
const automatic = computed(() => Boolean(defaults.value));
const enabled = computed(() => !automatic.value || Boolean(props.item.automation));
const pass = computed(() => automatic.value ? props.item.automation?.passRule : props.item.manualPassRule);
const comparison = ref<EvaluationPassRule["comparison"]>("gte");
const unit = ref("");
const units = computed(() => compatibleUnits(defaults.value?.unit ?? "%"));
watch(() => props.item, () => {
	comparison.value = pass.value?.comparison ?? "gte";
	unit.value = pass.value?.unit ?? props.item.automation?.unit ?? defaults.value?.unit ?? "";
}, { immediate: true });
watch(pass, value => {
	if (value) { comparison.value = value.comparison; unit.value = value.unit; }
});
const measurementHint = computed(() => {
	if (props.item.metricId === "industrial-protocol") return "工控协议按覆盖率（%）判定，不把协议数量当成百分比。";
	if (props.item.metricId === "transfer-protocol") return "本项按有实测依据的传输协议种数判定。";
	return "自动测试会按兼容单位换算后比较。";
});
function unitLabel(value: EvaluationUnit): string {
	if (value === "count") return props.item.metricId === "transfer-protocol" ? "种（协议数量）" : "个（并发数量）";
	return ({ min: "min（分钟）", s: "s（秒）", h: "h（小时）", "%": "%（百分比）" } as Record<string, string>)[value] ?? value;
}
function enableAutomation() {
	props.item.automation = defaultFor(props.item.metricId);
	unit.value = props.item.automation?.unit ?? "";
}
function setThreshold(value: number | null | undefined) {
	if (!enabled.value) return;
	if (automatic.value && props.item.automation) {
		props.item.automation.passRule = value == null ? null
			: { comparison: comparison.value, threshold: value, unit: unit.value as EvaluationUnit };
	} else if (!automatic.value) {
		props.item.manualPassRule = value == null ? null
			: { comparison: comparison.value, threshold: value, unit: unit.value.trim() };
	}
}
function updateCriterion() {
	if (pass.value) setThreshold(pass.value.threshold);
}
</script>

<style scoped>
.threshold-editor { margin: 0 0 20px; padding: 16px; border: 1px solid var(--el-color-primary-light-5); border-radius: 8px; background: var(--el-color-primary-light-9); }
.threshold-heading { display: flex; align-items: center; gap: 12px; }
.threshold-heading > .el-button { margin-left: auto; }
.threshold-help { margin: 10px 0; color: var(--el-text-color-secondary); font-size: 13px; line-height: 1.7; }
.threshold-fields { display: grid; grid-template-columns: 1fr 1.2fr 1fr; gap: 16px; margin-top: 12px; }
.threshold-fields :deep(.el-input-number), .threshold-fields :deep(.el-select) { width: 100%; }
.threshold-preview { font-size: 13px; color: var(--el-text-color-regular); overflow-wrap: anywhere; }
@media (max-width: 600px) { .threshold-fields { grid-template-columns: 1fr; gap: 0; } }
</style>
