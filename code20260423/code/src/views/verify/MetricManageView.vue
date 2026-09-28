<template>
	<div class="metric-manage-view">
		<h2 class="page-title">评价指标管理</h2>
		<div class="metric-topbar">
			<div><p class="page-description">三级指标体系 · 权重自动校验 · 测试依据管理</p></div>
			<div class="toolbar-actions">
				<el-button :loading="loading" :disabled="saving" @click="refresh">刷新</el-button>
				<el-button :icon="Download" :disabled="!config" @click="exportConfig">导出配置 JSON</el-button>
				<el-button type="primary" :loading="saving" :disabled="!config || !dirty[category]" @click="saveConfig">保存配置</el-button>
			</div>
		</div>
		<el-card shadow="never" class="metric-category-card">
			<div class="category-toolbar">
				<el-radio-group :model-value="category" :disabled="saving || loading" @change="switchCategory">
					<el-radio-button value="machine">机床类</el-radio-button>
					<el-radio-button value="machining">加工中心类</el-radio-button>
				</el-radio-group>
				<span class="muted">{{ category === 'machine' ? '适用于普通数控机床评价' : '适用于加工中心设备评价' }}</span>
				<el-tag v-if="dirty[category]" type="warning">有未保存修改</el-tag>
			</div>
		</el-card>
		<el-alert v-if="errorMessage" :title="errorMessage" type="error" show-icon :closable="false" class="metric-alert" />
		<div v-loading="loading" class="metric-content">
			<div class="metric-stats">
				<el-card v-for="stat in stats" :key="stat.label" shadow="never" :class="['stat-card', stat.status]">
					<div class="stat-value">{{ stat.value }}</div><div class="muted">{{ stat.label }}</div>
				</el-card>
			</div>
			<el-card shadow="never" class="metric-table-card">
				<template #header><div class="card-header">
					<div class="toolbar-actions">
						<el-button type="primary" :icon="Plus" :disabled="!config || saving" @click="add('section')">添加一级分类</el-button>
						<el-button :disabled="!config" @click="setExpanded(true)">全部展开</el-button>
						<el-button :disabled="!config" @click="setExpanded(false)">全部折叠</el-button>
					</div><span class="muted">同级权重合计应为 100%</span>
				</div></template>
				<el-alert v-if="weightIssues.length" type="warning" :closable="false" show-icon class="weight-alert"
					:title="`${weightIssues.length} 组同级权重未达到 100%，请调整后用于评价`">
					{{ weightIssues.join('；') }}
				</el-alert>
				<el-table ref="table" :data="rows" row-key="id" default-expand-all border
					:row-class-name="({ row }: { row: MetricRow }) => `metric-${row.level}`" empty-text="暂无评价指标，请添加一级分类">
					<el-table-column label="分类 / 项目名称" min-width="260">
						<template #default="{ row }"><span :class="{ 'category-name': row.level !== 'item' }">{{ row.node.name }}</span></template>
					</el-table-column>
					<el-table-column label="权重 (%)" width="150">
						<template #default="{ row }">
							<el-input-number v-model="row.node.weight" :min="0" :max="100" :precision="1" :step="0.1" controls-position="right"
								:disabled="saving" :aria-label="`${row.node.name}权重`" class="weight-input" @change="weightChanged(row)" />
							<div v-if="!weightBalanced(row.siblingWeight)" class="weight-warning">同级合计 {{ row.siblingWeight }}%</div>
						</template>
					</el-table-column>
					<el-table-column label="项目描述 / 评价方法" min-width="340">
						<template #default="{ row }">
							<template v-if="row.level === 'item'"><div class="metric-description">{{ row.node.desc || '—' }}</div><div class="scoring-method"><strong>评分标准：</strong>{{ row.node.method || '—' }}</div></template>
							<span v-else class="muted">{{ row.level === 'section' ? '一级分类' : `二级分类（归属：${row.parentName}）` }}</span>
						</template>
					</el-table-column>
					<el-table-column label="测试依据" min-width="230">
						<template #default="{ row }">
							<el-tag v-if="row.level !== 'item'" type="info">{{ row.children?.length || 0 }} 个{{ row.level === 'section' ? '子项' : '项目' }}</el-tag>
							<template v-else>
								<el-tag :type="row.node.evidenceType === 'protocol' ? 'primary' : row.node.evidenceType === 'file' ? 'success' : 'info'" size="small">{{ evidenceLabels[row.node.evidenceType as EvaluationItem['evidenceType']] }}</el-tag>
								<div v-if="row.node.evidenceType === 'protocol'" class="evidence-tags"><el-tag v-for="protocol in row.node.protocols" :key="protocol" size="small" effect="plain">{{ protocol }}</el-tag></div>
								<div v-if="row.node.evidenceType === 'file'" class="evidence-files"><div v-for="(file, index) in row.node.files" :key="file.id || index">
									<el-button v-if="file.id" type="primary" link @click="download(file)">{{ file.name }}</el-button><span v-else>{{ file.name }}</span>
									<small class="muted"> ({{ file.size }}){{ file.id ? '' : ' · 未上传' }}</small>
								</div></div>
								<div class="evidence-standard">{{ row.node.standards || '未填写标准规范' }}</div>
							</template>
						</template>
					</el-table-column>
					<el-table-column label="操作" width="194" fixed="right">
						<template #default="{ row }"><div class="row-actions">
							<el-button v-if="row.level === 'item'" type="primary" link @click="viewStandard(row.node)">查看标准</el-button>
							<el-button v-else type="primary" link :disabled="saving" @click="add(row.level === 'section' ? 'subcategory' : 'item', row.id)">{{ row.level === 'section' ? '添加子项' : '添加项目' }}</el-button>
							<el-button type="warning" link :disabled="saving" @click="edit(row)">编辑</el-button>
							<el-button type="danger" link :disabled="saving" @click="remove(row)">删除</el-button>
						</div></template>
					</el-table-column>
				</el-table>
			</el-card>
		</div>
		<MetricEditor :target="editing" @close="editing = null" @save="saveEdit" />
		<el-dialog v-model="standardVisible" title="程序识别标准（JSON）" width="min(800px, 94vw)">
			<pre class="standard-json">{{ standardJson }}</pre>
			<template #footer><el-button @click="standardVisible = false">关闭</el-button><el-button type="primary" @click="copyStandard">复制 JSON</el-button></template>
		</el-dialog>
	</div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from "vue";
import { onBeforeRouteLeave } from "vue-router";
import { ElMessage, ElMessageBox, type TableInstance } from "element-plus";
import { Download, Plus } from "@element-plus/icons-vue";
import { isAxiosError } from "axios";
import { evaluationApi, type EvaluationCategory, type EvaluationConfig, type EvaluationFile, type EvaluationItem } from "@/api/evaluation";
import MetricEditor from "./evaluation/metricEditor.vue";
import { clone, createMetric, metricRows, removeMetric, sumWeights, updateMetric, weightBalanced, type MetricEdit, type MetricLevel, type MetricRow } from "./evaluation/metricTree";

const category = ref<EvaluationCategory>("machine");
const configs = ref<Partial<Record<EvaluationCategory, EvaluationConfig>>>({});
const dirty = ref<Partial<Record<EvaluationCategory, boolean>>>({});
const config = computed(() => configs.value[category.value]);
const loading = ref(false);
const saving = ref(false);
const errorMessage = ref("");
const table = ref<TableInstance>();
const editing = ref<MetricEdit | null>(null);
const standardVisible = ref(false);
const standardJson = ref("");
const rows = computed(() => metricRows(config.value?.indicators ?? []));
const evidenceLabels = { standard: "标准规范", protocol: "勾选协议", file: "测试文件" };
const sections = computed(() => config.value?.indicators ?? []);
const subcategories = computed(() => sections.value.flatMap(section => section.children));
const stats = computed(() => [
	{ label: "一级分类权重总和", value: `${sumWeights(sections.value)}%`, status: weightBalanced(sumWeights(sections.value)) ? "balanced" : "unbalanced" },
	{ label: "一级分类数量", value: sections.value.length, status: "" },
	{ label: "二级分类数量", value: subcategories.value.length, status: "" },
	{ label: "评价项目总数", value: subcategories.value.reduce((total, child) => total + child.items.length, 0), status: "" },
]);
const weightIssues = computed(() => {
	if (!config.value) return [];
	const groups = [{ name: "一级分类", nodes: sections.value }, ...sections.value.map(section => ({ name: section.name, nodes: section.children })), ...subcategories.value.map(child => ({ name: child.name, nodes: child.items }))];
	return groups.filter(group => !weightBalanced(sumWeights(group.nodes))).map(group => `${group.name} ${sumWeights(group.nodes)}%`);
});

async function load(force = false) {
	if (config.value && !force) return;
	loading.value = true;
	errorMessage.value = "";
	try {
		configs.value[category.value] = await evaluationApi.getConfig(category.value);
		dirty.value[category.value] = false;
	} catch {
		errorMessage.value = "评价指标加载失败，请刷新重试";
	} finally {
		loading.value = false;
	}
}

async function refresh() {
	if (dirty.value[category.value]) {
		try { await ElMessageBox.confirm("刷新将放弃当前分类的未保存修改，是否继续？", "刷新指标", { type: "warning" }); }
		catch { return; }
	}
	await load(true);
}

async function switchCategory(value: string | number | boolean | undefined) {
	if (value !== "machine" && value !== "machining") return;
	category.value = value;
	errorMessage.value = "";
	await load();
}

async function saveConfig() {
	if (!config.value || saving.value) return;
	const currentCategory = category.value;
	saving.value = true;
	try {
		configs.value[currentCategory] = await evaluationApi.saveConfig(clone(config.value));
		dirty.value[currentCategory] = false;
		errorMessage.value = "";
		ElMessage.success("评价指标配置已保存");
	} catch (error) {
		errorMessage.value = isAxiosError(error) && error.response?.status === 409 ? "配置已被其他操作更新，当前草稿已保留；请先导出草稿，再刷新后重新修改" : "配置保存失败，当前草稿已保留，请重试保存";
	} finally { saving.value = false; }
}
function add(level: MetricLevel, parentId?: string) {
	editing.value = { level, parentId, node: createMetric(level), isNew: true };
}

function edit(row: MetricRow) {
	editing.value = { level: row.level, parentId: row.parentId, node: clone(row.node), isNew: false };
}

async function saveEdit(target: MetricEdit) {
	if (!config.value) return;
	updateMetric(config.value.indicators, target);
	editing.value = null;
	dirty.value[category.value] = true;
	await saveConfig();
	await nextTick();
	setExpanded(true);
}

async function weightChanged(row: MetricRow) {
	if (!Number.isFinite(row.node.weight)) row.node.weight = 0;
	dirty.value[category.value] = true;
	await saveConfig();
}

async function remove(row: MetricRow) {
	try {
		await ElMessageBox.confirm(`确定删除“${row.node.name}”${row.level === 'item' ? '' : '及其所有下级项目'}？`, "删除确认", { type: "warning" });
	} catch { return; }
	if (!config.value) return;
	config.value.indicators = removeMetric(config.value.indicators, row.id);
	dirty.value[category.value] = true;
	await saveConfig();
}

function setExpanded(expanded: boolean) {
	for (const row of rows.value) {
		table.value?.toggleRowExpansion(row, expanded);
		for (const child of row.children ?? []) table.value?.toggleRowExpansion(child, expanded);
	}
}

function saveBlob(blob: Blob, name: string) {
	const url = URL.createObjectURL(blob);
	const link = document.createElement("a");
	link.href = url; link.download = name; link.click();
	URL.revokeObjectURL(url);
}
function exportConfig() {
	if (!config.value) return;
	const data = { ...config.value, exportTime: new Date().toISOString() };
	saveBlob(new Blob([JSON.stringify(data, null, 2)], { type: "application/json" }), `评价指标配置_${category.value}_${Date.now()}.json`);
}

async function download(file: EvaluationFile) {
	if (!file.id) return;
	try { saveBlob(await evaluationApi.downloadAttachment(file.id), file.name); }
	catch { ElMessage.error("测试文件下载失败，请重试"); }
}

function viewStandard(item: EvaluationItem) {
	standardJson.value = JSON.stringify({
		indicator_id: item.id, indicator_name: item.name, category: category.value,
		test_type: item.evidenceType, test_files: item.files.map(file => file.name),
		selected_protocols: item.protocols, standards: item.standards,
		scoring_rules: item.scoring, weight: item.weight,
	}, null, 2);
	standardVisible.value = true;
}

async function copyStandard() {
	try { await navigator.clipboard.writeText(standardJson.value); ElMessage.success("JSON 已复制"); }
	catch { ElMessage.error("复制失败，请选择内容后复制"); }
}

onBeforeRouteLeave(async () => {
	if (!Object.values(dirty.value).some(Boolean)) return true;
	try {
		await ElMessageBox.confirm("还有未保存的评价指标修改，确定离开？", "未保存修改", { type: "warning" });
		return true;
	} catch { return false; }
});
onMounted(() => { void load(); });
</script>

<style lang="scss" scoped>
.metric-manage-view { min-width: 0; }
.metric-topbar, .category-toolbar, .card-header, .toolbar-actions { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
.metric-topbar, .card-header { justify-content: space-between; }
.metric-topbar { margin-bottom: 20px; }
.page-description, .muted { color: var(--el-text-color-secondary); font-size: 13px; }
.metric-category-card, .metric-alert { margin-bottom: 20px; }
.metric-category-card :deep(.el-card__body) { padding: 16px 20px; }
.toolbar-actions :deep(.el-button + .el-button) { margin-left: 0; }
.metric-stats { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 20px; margin-bottom: 20px; }
.stat-card { border-left: 3px solid var(--el-color-primary); }
.stat-card.balanced { border-left-color: var(--el-color-success); }
.stat-card.unbalanced { border-left-color: var(--el-color-warning); }
.stat-value { font-size: 28px; font-weight: 600; line-height: 1.4; margin-bottom: 4px; }
.metric-table-card :deep(.el-card__body) { padding: 0; }
.weight-alert { margin: 16px; width: auto; }
.category-name { font-weight: 600; }
.weight-input { width: 115px; }
.weight-warning { color: var(--el-color-warning); font-size: 12px; margin-top: 4px; }
.metric-description { line-height: 1.7; white-space: pre-wrap; overflow-wrap: anywhere; }
.scoring-method { line-height: 1.7; white-space: pre-wrap; margin-top: 8px; padding-top: 8px; border-top: 1px dashed var(--el-border-color-lighter); color: var(--el-text-color-regular); }
.evidence-tags { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 8px; }
.evidence-files, .evidence-standard { font-size: 12px; line-height: 1.7; margin-top: 8px; overflow-wrap: anywhere; }
.evidence-files :deep(.el-button) { white-space: normal; text-align: left; height: auto; }
.evidence-standard { color: var(--el-text-color-secondary); }
.row-actions { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
.row-actions :deep(.el-button + .el-button) { margin-left: 0; }
.standard-json { background: var(--el-fill-color-light); padding: 16px; border-radius: var(--el-border-radius-base); max-height: 60vh; overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere; font: 13px/1.7 Consolas, monospace; }
:deep(.metric-section) { --el-table-tr-bg-color: var(--el-color-primary-light-9); }
:deep(.metric-subcategory) { --el-table-tr-bg-color: var(--el-fill-color-lighter); }
:deep(.el-table .cell) { padding-top: 7px; padding-bottom: 7px; }
@media (max-width: 1000px) { .metric-stats { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; } }
@media (max-width: 600px) { .metric-stats { grid-template-columns: 1fr 1fr; } .stat-value { font-size: 24px; } .metric-topbar { align-items: flex-start; } }
</style>
