<template>
	<div class="metric-manage-view">
		<h2 class="page-title">指标管理</h2>

		<el-alert
			type="info"
			:closable="false"
			show-icon
			style="margin-bottom: 16px"
			title="本页验收指标定义已接入业务后端，新增、编辑、删除会持久化到业务服务。"
		/>

		<!-- 指标列表 -->
		<el-card class="metric-list-card">
			<template #header>
				<div class="card-header">
					<span>指标列表</span>
					<el-button type="primary" @click="openAddMetricDialog">
						<el-icon><Plus /></el-icon>
						新增指标
					</el-button>
				</div>
			</template>

			<!-- 搜索和筛选 -->
			<div class="search-filter-bar">
				<el-input
					v-model="searchQuery"
					placeholder="搜索指标名称或描述"
					prefix-icon="Search"
					style="width: 300px; margin-right: 10px"
				/>
				<el-select
					v-model="filterCategory"
					placeholder="筛选类别"
					style="width: 150px; margin-right: 10px"
				>
					<el-option label="全部" value="" />
					<el-option label="性能" value="performance" />
					<el-option label="功能" value="function" />
					<el-option label="稳定性" value="stability" />
				</el-select>
				<el-button type="primary" @click="refreshMetricList">
					<el-icon><Refresh /></el-icon>
					刷新
				</el-button>
			</div>

			<!-- 指标表格 -->
			<el-table :data="filteredMetrics" style="width: 100%" border>
				<el-table-column prop="code" label="指标ID" width="80" />
				<el-table-column prop="name" label="指标名称" />
				<el-table-column prop="category" label="类别" width="120">
					<template #default="scope">
						<el-tag :type="getCategoryType(scope.row.category)">
							{{ getCategoryLabel(scope.row.category) }}
						</el-tag>
					</template>
				</el-table-column>
				<el-table-column prop="unit" label="单位" width="100" />
				<el-table-column label="达标阈值" width="110">
					<template #default="scope">
						{{ scope.row.threshold ?? "-" }}
					</template>
				</el-table-column>
				<el-table-column label="当前状态" width="120">
					<template #default="scope">
						<el-tag :type="scope.row.statusType">{{ scope.row.statusLabel }}</el-tag>
					</template>
				</el-table-column>
				<el-table-column prop="description" label="对齐说明" min-width="360" />
				<el-table-column
					prop="createdAt"
					label="创建时间"
					width="180"
				/>
				<el-table-column label="操作" width="180">
					<template #default="scope">
						<el-button
							type="primary"
							size="small"
							@click="openEditMetricDialog(scope.row)"
						>
							编辑
						</el-button>
						<el-button
							type="danger"
							size="small"
							@click="deleteMetric(scope.row.id)"
						>
							删除
						</el-button>
					</template>
				</el-table-column>
			</el-table>

			<!-- 分页 -->
			<div class="pagination-bar">
				<el-pagination
					v-model:current-page="currentPage"
					v-model:page-size="pageSize"
					:page-sizes="[10, 20, 50, 100]"
					layout="total, sizes, prev, pager, next, jumper"
					:total="metrics.length"
					@size-change="handleSizeChange"
					@current-change="handleCurrentChange"
				/>
			</div>
		</el-card>

		<!-- 新增/编辑指标对话框 -->
		<el-dialog
			v-model="metricDialogVisible"
			:title="isEditing ? '编辑指标' : '新增指标'"
			width="500px"
		>
			<el-form :model="currentMetric" label-width="120px">
				<el-form-item label="指标名称" prop="name" required>
					<el-input
						v-model="currentMetric.name"
						placeholder="请输入指标名称"
					/>
				</el-form-item>
				<el-form-item label="指标编码" prop="code">
					<el-input
						v-model="currentMetric.code"
						placeholder="如 5.2.3（与自动验证联动的关键，留空自动生成）"
					/>
				</el-form-item>
				<el-form-item label="指标类别" prop="category" required>
					<el-select
						v-model="currentMetric.category"
						placeholder="请选择指标类别"
					>
						<el-option label="性能" value="performance" />
						<el-option label="功能" value="function" />
						<el-option label="稳定性" value="stability" />
						<el-option label="兼容性" value="compatibility" />
					</el-select>
				</el-form-item>
				<el-form-item label="单位" prop="unit">
					<el-input
						v-model="currentMetric.unit"
						placeholder="请输入单位"
					/>
				</el-form-item>
				<el-form-item label="达标阈值" prop="threshold">
					<el-input-number
						v-model="currentMetric.threshold"
						:min="0"
						:step="1"
						:precision="2"
						placeholder="实测值 ≥ 阈值判达标"
						style="width: 220px"
					/>
					<span style="margin-left: 8px; font-size: 12px; color: var(--el-text-color-secondary)">
						留空则自动验证用内置默认判据；编码需与 5.2.x 对应
					</span>
				</el-form-item>
				<el-form-item label="参考值说明" prop="reference">
					<el-input
						v-model="currentMetric.reference"
						placeholder="如：≥200 并发为优（报表中展示的评价参考标准）"
					/>
				</el-form-item>
				<el-form-item label="描述" prop="description">
					<el-input
						v-model="currentMetric.description"
						type="textarea"
						:rows="3"
						placeholder="请输入指标描述"
					/>
				</el-form-item>
			</el-form>
			<template #footer>
				<span class="dialog-footer">
					<el-button @click="metricDialogVisible = false"
						>取消</el-button
					>
					<el-button type="primary" @click="saveMetric"
						>保存</el-button
					>
				</span>
			</template>
		</el-dialog>
	</div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted } from "vue";
import { Plus, Refresh } from "@element-plus/icons-vue";
import { ElMessage, ElMessageBox } from "element-plus";
import {
	businessValidationApi,
	type MetricDto,
} from "@/api/businessValidation";

type Metric = MetricDto;

// 指标列表
const metrics = ref<Metric[]>([]);

// 搜索和筛选
const searchQuery = ref("");
const filterCategory = ref("");

// 分页
const currentPage = ref(1);
const pageSize = ref(10);

// 指标对话框
const metricDialogVisible = ref(false);
const isEditing = ref(false);
const currentMetric = reactive<Metric>({
	id: "",
	code: "",
	name: "",
	category: "performance",
	unit: "",
	statusLabel: "待定义",
	statusType: "info",
	description: "",
	reference: "",
	threshold: null,
	createdAt: new Date().toISOString(),
});

const createEmptyMetric = (): Metric => ({
	id: "",
	code: "",
	name: "",
	category: "performance",
	unit: "",
	statusLabel: "待定义",
	statusType: "info",
	description: "",
	reference: "",
	threshold: null,
	createdAt: new Date().toISOString(),
});

const loadMetrics = async () => {
	try {
		metrics.value = await businessValidationApi.listMetrics();
	} catch (error) {
		console.error(error);
		ElMessage.error("指标列表加载失败，请检查业务后端服务");
	}
};

// 过滤后的指标列表
const filteredMetrics = computed(() => {
	let result = [...metrics.value];

	// 搜索
	if (searchQuery.value) {
		const query = searchQuery.value.toLowerCase();
		result = result.filter(
			(metric) =>
				metric.name.toLowerCase().includes(query) ||
				metric.description.toLowerCase().includes(query) ||
				metric.statusLabel.toLowerCase().includes(query),
		);
	}

	// 类别筛选
	if (filterCategory.value) {
		result = result.filter(
			(metric) => metric.category === filterCategory.value,
		);
	}

	// 分页
	const startIndex = (currentPage.value - 1) * pageSize.value;
	const endIndex = startIndex + pageSize.value;
	return result.slice(startIndex, endIndex);
});

// 打开新增指标对话框
const openAddMetricDialog = () => {
	isEditing.value = false;
	Object.assign(currentMetric, createEmptyMetric());
	metricDialogVisible.value = true;
};

// 打开编辑指标对话框
const openEditMetricDialog = (metric: Metric) => {
	isEditing.value = true;
	Object.assign(currentMetric, { ...metric });
	metricDialogVisible.value = true;
};

// 保存指标
const saveMetric = async () => {
	const payload = {
		...currentMetric,
		code: currentMetric.code || `MET-${Date.now()}`,
		createdAt: currentMetric.createdAt || new Date().toISOString(),
	};
	try {
		if (isEditing.value) {
			await businessValidationApi.updateMetric(currentMetric.id, payload);
		} else {
			await businessValidationApi.createMetric(payload);
		}
		metricDialogVisible.value = false;
		await loadMetrics();
		ElMessage.success("指标已保存");
	} catch (error) {
		console.error(error);
		ElMessage.error("指标保存失败，请检查业务后端服务");
	}
};

// 删除指标
const deleteMetric = async (id: string) => {
	try {
		await ElMessageBox.confirm("确定删除该指标？", "删除确认", {
			type: "warning",
		});
		await businessValidationApi.deleteMetric(id);
		await loadMetrics();
		ElMessage.success("指标已删除");
	} catch (error) {
		if (error !== "cancel") {
			console.error(error);
			ElMessage.error("指标删除失败，请检查业务后端服务");
		}
	}
};

// 刷新指标列表
const refreshMetricList = async () => {
	await loadMetrics();
};

// 获取类别类型
const getCategoryType = (category: string) => {
	switch (category) {
		case "performance":
			return "primary";
		case "function":
			return "success";
		case "stability":
			return "warning";
		case "compatibility":
			return "info";
		default:
			return "default";
	}
};

// 获取类别标签
const getCategoryLabel = (category: string) => {
	switch (category) {
		case "performance":
			return "性能";
		case "function":
			return "功能";
		case "stability":
			return "稳定性";
		case "compatibility":
			return "兼容性";
		default:
			return category;
	}
};

// 分页处理
const handleSizeChange = (size: number) => {
	pageSize.value = size;
	currentPage.value = 1;
};

const handleCurrentChange = (current: number) => {
	currentPage.value = current;
};

onMounted(() => {
	void loadMetrics();
});
</script>

<style lang="scss" scoped>
.metric-manage-view {
	.metric-list-card {
		margin-bottom: 20px;
	}

	.card-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
	}

	.search-filter-bar {
		display: flex;
		align-items: center;
		margin-bottom: 20px;
	}

	.pagination-bar {
		margin-top: 20px;
		display: flex;
		justify-content: flex-end;
	}
}
</style>
