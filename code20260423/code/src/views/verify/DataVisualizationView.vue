<template>
	<div class="data-visualization-view">
		<h2 class="page-title">数据可视化</h2>

		<!-- 数据选择和配置 -->
		<el-card class="config-card">
			<template #header>
				<div class="card-header">
					<span>数据配置</span>
				</div>
			</template>

			<div class="config-content">
				<div class="config-row">
					<el-form label-width="120px" :inline="true">
						<el-form-item label="设备">
							<el-select
								v-model="deviceId"
								placeholder="请选择设备"
								filterable
								style="width: 220px"
							>
								<el-option
									v-for="d in devices"
									:key="d.id"
									:label="d.name"
									:value="d.id"
								/>
							</el-select>
						</el-form-item>
						<el-form-item label="点位路径">
							<el-input
								v-model="tagPath"
								placeholder="可选，留空查全部点位"
								style="width: 220px"
							/>
						</el-form-item>
						<el-form-item label="时间范围">
							<el-date-picker
								v-model="dateRange"
								type="datetimerange"
								range-separator="至"
								start-placeholder="开始时间"
								end-placeholder="结束时间"
								style="width: 360px"
							/>
						</el-form-item>
						<el-form-item>
							<el-button type="primary" :loading="loading" @click="loadData">
								<el-icon><Refresh /></el-icon>
								加载数据
							</el-button>
						</el-form-item>
					</el-form>
				</div>

				<div class="config-row">
					<el-form label-width="120px" :inline="true">
						<el-form-item label="图表类型">
							<el-select
								v-model="chartType"
								placeholder="请选择图表类型"
								style="width: 140px"
							>
								<el-option label="折线图" value="line" />
								<el-option label="柱状图" value="bar" />
							</el-select>
						</el-form-item>
						<el-form-item>
							<el-button type="success" @click="generateChart">
								<el-icon><View /></el-icon>
								生成图表
							</el-button>
						</el-form-item>
					</el-form>
				</div>
			</div>
		</el-card>

		<!-- 图表展示 -->
		<el-card class="chart-card">
			<template #header>
				<div class="card-header">
					<span>数据可视化（时间范围内趋势，最多 {{ CHART_MAX_POINTS }} 点）</span>
					<div class="chart-actions">
						<el-button size="small" @click="exportChart">
							<el-icon><Download /></el-icon>
							导出图表
						</el-button>
						<el-button size="small" @click="refreshChart">
							<el-icon><Refresh /></el-icon>
							刷新
						</el-button>
					</div>
				</div>
			</template>

			<div class="chart-container" v-if="chartSeriesData.length > 0">
			<el-empty
				v-if="loading"
				description="数据加载中..."
				image-size="120"
			/>
			<div v-else class="chart-wrapper">
				<div
					ref="chartCanvas"
					class="chart-item"
					style="width: 100%; height: 400px"
				></div>
			</div>
		</div>
			<el-empty
				v-else
				description="暂无数据，请先加载数据"
				image-size="120"
			>
				<template #description>
					<p>暂无数据，请先配置数据源并加载数据</p>
				</template>
				<el-button type="primary" @click="loadData">
					<el-icon><Refresh /></el-icon>
					加载数据
				</el-button>
			</el-empty>
		</el-card>

		<!-- 数据表格 -->
		<el-card class="data-table-card">
			<template #header>
				<div class="card-header">
					<span>数据详情（{{ loadedRangeText }}）</span>
					<el-button size="small" type="info" @click="exportData">
						<el-icon><Download /></el-icon>
						导出数据
					</el-button>
				</div>
			</template>

			<el-table :data="displayData" style="width: 100%" border>
				<el-table-column
					v-for="column in tableColumns"
					:key="column.prop"
					:prop="column.prop"
					:label="column.label"
				/>
			</el-table>

			<!-- 分页 -->
			<div class="pagination-bar" v-if="chartData.length > 0">
				<el-pagination
					v-model:current-page="currentPage"
					v-model:page-size="pageSize"
					:page-sizes="[10, 20, 50, 100]"
					layout="total, sizes, prev, pager, next, jumper"
					:total="total"
					@size-change="handleSizeChange"
					@current-change="handleCurrentChange"
				/>
			</div>
		</el-card>
	</div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, watch, nextTick } from "vue";
import { Refresh, View, Download } from "@element-plus/icons-vue";
import { ElMessage } from "element-plus";
import * as echarts from "echarts";
import { machineConnectionDevicesApi } from "@/api/machineConnectionDevices";
import {
	telemetryInfluxApi,
	type InfluxTelemetryHistoryItem,
} from "@/api/telemetryInflux";

function getErr(e: unknown, fallback: string): string {
	const ax = e as {
		response?: { data?: { error?: string; detail?: string } };
		message?: string;
	};
	return (
		ax.response?.data?.error ??
		ax.response?.data?.detail ??
		ax.message ??
		fallback
	);
}

// 设备列表（真实 /api/devices）
const devices = ref<{ id: string; name: string }[]>([]);
const deviceId = ref("");
const tagPath = ref("");

// 默认近 1 小时
const now = new Date();
const dateRange = ref<[Date, Date]>([
	new Date(now.getTime() - 60 * 60 * 1000),
	now,
]);
const chartType = ref<"line" | "bar">("line");

// 数据状态（来自 InfluxDB 真实遥测历史）
// chartData：表格当前服务端页；chartSeriesData：图表专用，一次拉取时间范围内最多 1000 点。
const chartData = ref<InfluxTelemetryHistoryItem[]>([]);
const chartSeriesData = ref<InfluxTelemetryHistoryItem[]>([]);
const chartSeriesTruncated = ref(false);
const total = ref(0);
const loading = ref(false);
const currentPage = ref(1);
const pageSize = ref(20);
const CHART_MAX_POINTS = 1000;

// 图表引用
const chartCanvas = ref<HTMLDivElement>();
let chartInstance: echarts.ECharts | null = null;
let loadSequence = 0;
let chartLoadSequence = 0;

// 表格列（遥测历史固定列）
const tableColumns = [
	{ prop: "time", label: "时间" },
	{ prop: "name", label: "名称" },
	{ prop: "path", label: "点位路径" },
	{ prop: "dataType", label: "数据类型" },
	{ prop: "value", label: "值" },
	{ prop: "quality", label: "质量" },
	{ prop: "status", label: "状态" },
];

// 服务端已完成分页，当前内存仅保存当前页。
const displayData = computed(() => chartData.value);
const loadedRangeText = computed(() => {
	if (chartData.value.length === 0) return `当前页 0 条 / 共 ${total.value} 条`;
	const start = (currentPage.value - 1) * pageSize.value + 1;
	const end = start + chartData.value.length - 1;
	return `已加载第 ${start}-${end} 条 / 共 ${total.value} 条`;
});

// 加载设备
const loadDevices = async () => {
	try {
		const list = await machineConnectionDevicesApi.list();
		devices.value = list.map((d) => ({ id: d.id, name: d.name }));
		if (devices.value.length > 0 && !deviceId.value)
			deviceId.value = devices.value[0]!.id;
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载设备列表失败"));
	}
};

// 加载数据 = 真实 InfluxDB 遥测历史
const loadPage = async () => {
	if (!deviceId.value) {
		ElMessage.warning("请先选择设备");
		return;
	}
	if (!dateRange.value || dateRange.value.length !== 2) {
		ElMessage.warning("请选择时间范围");
		return;
	}
	const requestedDeviceId = deviceId.value;
	const sequence = ++loadSequence;
	loading.value = true;
	try {
		const res = await telemetryInfluxApi.history({
			deviceId: requestedDeviceId,
			startTime: dateRange.value[0].toISOString(),
			endTime: dateRange.value[1].toISOString(),
			path: tagPath.value.trim() || undefined,
			page: currentPage.value,
			pageSize: pageSize.value,
		});
		if (sequence !== loadSequence || deviceId.value !== requestedDeviceId) return;
		chartData.value = res.items ?? [];
		total.value = res.total ?? 0;
		currentPage.value = res.page;
		pageSize.value = res.pageSize;
		if (total.value === 0) {
			ElMessage.info("该时间范围无遥测数据");
		} else {
			ElMessage.success(
				`已加载第 ${currentPage.value} 页 ${chartData.value.length} 条，共 ${total.value} 条`,
			);
		}
	} catch (e: unknown) {
		if (sequence !== loadSequence || deviceId.value !== requestedDeviceId) return;
		chartData.value = [];
		total.value = 0;
		ElMessage.error(getErr(e, "加载数据失败"));
	} finally {
		if (sequence === loadSequence) loading.value = false;
	}
};

// 图表数据独立于表格分页：一次拉取整个时间范围（上限 1000 点），翻页不影响趋势图。
const loadChartSeries = async () => {
	if (!deviceId.value || !dateRange.value || dateRange.value.length !== 2) return;
	const requestedDeviceId = deviceId.value;
	const sequence = ++chartLoadSequence;
	try {
		const res = await telemetryInfluxApi.history({
			deviceId: requestedDeviceId,
			startTime: dateRange.value[0].toISOString(),
			endTime: dateRange.value[1].toISOString(),
			path: tagPath.value.trim() || undefined,
			page: 1,
			pageSize: CHART_MAX_POINTS,
		});
		if (sequence !== chartLoadSequence || deviceId.value !== requestedDeviceId) return;
		chartSeriesData.value = res.items ?? [];
		chartSeriesTruncated.value = (res.total ?? 0) > chartSeriesData.value.length;
		if (chartSeriesData.value.length === 0) {
			chartInstance?.clear();
		} else {
			await nextTick();
			generateChart();
		}
	} catch {
		if (sequence !== chartLoadSequence || deviceId.value !== requestedDeviceId) return;
		// 图表加载失败不打断表格使用；表格错误提示已由 loadPage 负责
		chartSeriesData.value = [];
		chartSeriesTruncated.value = false;
		chartInstance?.clear();
	}
};

const loadData = async () => {
	currentPage.value = 1;
	await loadPage();
	void loadChartSeries();
};

// 初始化图表
const initChart = () => {
	if (!chartCanvas.value) return;
	
	if (chartInstance) {
		chartInstance.dispose();
	}
	
	chartInstance = echarts.init(chartCanvas.value);
	
	// 监听窗口大小变化
	window.addEventListener('resize', handleResize);
};

// 处理图表大小变化
const handleResize = () => {
	chartInstance?.resize();
};

// 生成图表
const generateChart = () => {
	if (chartSeriesData.value.length === 0) {
		ElMessage.warning("请先加载数据");
		return;
	}

	if (!chartInstance) {
		initChart();
	}

	const option = getChartOption();
	chartInstance?.setOption(option, true);
};

// 获取图表配置 = 按点位路径分组的真实时间序列
const getChartOption = () => {
	// 仅取数值型遥测点
	const numeric = chartSeriesData.value.filter(
		(it) => it.value !== null && it.value !== undefined && !isNaN(Number(it.value)),
	);
	const times = Array.from(new Set(numeric.map((it) => it.time))).sort();
	const paths = Array.from(new Set(numeric.map((it) => it.path || it.name)));
	const colors = ["#5470c6", "#91cc75", "#fac858", "#ee6666", "#73c0de", "#3ba272"];

	const series = paths.map((p, idx) => {
		const byTime = new Map(
			numeric
				.filter((it) => (it.path || it.name) === p)
				.map((it) => [it.time, Number(it.value)]),
		);
		return {
			name: p,
			type: chartType.value,
			showSymbol: false,
			data: times.map((t) => byTime.get(t) ?? null),
			connectNulls: true,
			itemStyle: { color: colors[idx % colors.length] },
		};
	});

	return {
		title: {
			text: chartSeriesTruncated.value
				? `遥测历史（最近 ${chartSeriesData.value.length} 条）`
				: "遥测历史",
			left: "center",
		},
		tooltip: { trigger: "axis" },
		legend: { type: "scroll", data: paths, bottom: 0 },
		grid: { bottom: 60 },
		xAxis: {
			type: "category",
			data: times.map((t) => new Date(t).toLocaleString()),
			axisLabel: { rotate: 45 },
		},
		yAxis: { type: "value" },
		series,
	};
};

// 刷新图表
const refreshChart = () => {
	generateChart();
};

// 导出图表 = echarts 当前画布 PNG
const exportChart = () => {
	if (!chartInstance) {
		ElMessage.warning("请先生成图表");
		return;
	}
	const url = chartInstance.getDataURL({ type: "png", pixelRatio: 2, backgroundColor: "#fff" });
	const a = document.createElement("a");
	a.href = url;
	a.download = `telemetry-chart-${Date.now()}.png`;
	a.click();
};

// 导出数据 = 整个时间范围的遥测记录 CSV（与图表同源，上限 CHART_MAX_POINTS 条）
const exportData = () => {
	const source = chartSeriesData.value.length > 0 ? chartSeriesData.value : chartData.value;
	if (source.length === 0) {
		ElMessage.warning("暂无数据可导出");
		return;
	}
	if (chartSeriesTruncated.value) {
		ElMessage.warning(
			`数据超过 ${CHART_MAX_POINTS} 条上限，本次导出前 ${source.length} 条（共 ${total.value} 条），请缩小时间范围导出完整数据`,
		);
	}
	const header = tableColumns.map((c) => c.label).join(",");
	const rows = source.map((it) =>
		tableColumns
			.map((c) => {
				const v = (it as Record<string, unknown>)[c.prop];
				const s = v === null || v === undefined ? "" : String(v);
				return `"${s.replace(/"/g, '""')}"`;
			})
			.join(","),
	);
	const csv = "﻿" + [header, ...rows].join("\r\n");
	const blob = new Blob([csv], { type: "text/csv;charset=utf-8" });
	const url = URL.createObjectURL(blob);
	const a = document.createElement("a");
	a.href = url;
	a.download = `telemetry-data-${Date.now()}.csv`;
	a.click();
	URL.revokeObjectURL(url);
};

// 分页处理
const handleSizeChange = (size: number) => {
	pageSize.value = size;
	currentPage.value = 1;
	void loadPage();
};

const handleCurrentChange = (current: number) => {
	currentPage.value = current;
	void loadPage();
};

// 初始化
onMounted(async () => {
	await loadDevices();
	setTimeout(() => {
		initChart();
	}, 100);
});

// 图表类型变化时重绘
watch(chartType, () => {
	if (chartData.value.length > 0) {
		generateChart();
	}
});

watch(deviceId, () => {
	loadSequence += 1;
	chartLoadSequence += 1;
	chartData.value = [];
	chartSeriesData.value = [];
	chartSeriesTruncated.value = false;
	total.value = 0;
	currentPage.value = 1;
	loading.value = false;
	chartInstance?.clear();
});

// 清理
onUnmounted(() => {
	window.removeEventListener('resize', handleResize);
	chartInstance?.dispose();
});
</script>

<style lang="scss" scoped>
.data-visualization-view {
	.config-card,
	.chart-card,
	.data-table-card {
		margin-bottom: 20px;
	}

	.card-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
	}

	.config-content {
		padding: 20px 0;
	}

	.config-row {
		margin-bottom: 20px;
	}

	.chart-container {
		padding: 20px 0;
	}

	.chart-wrapper {
		position: relative;
		height: 400px;
	}

	.chart-actions {
		display: flex;
		gap: 10px;
	}

	.pagination-bar {
		margin-top: 20px;
		display: flex;
		justify-content: flex-end;
	}
}
</style>
