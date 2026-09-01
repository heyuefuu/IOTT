<template>
	<div class="system-log-manage-view">
		<h2 class="page-title">日志管理</h2>

		<el-card class="log-manage-card">
			<template #header>
				<div class="card-header">
					<span>日志管理</span>
					<el-button type="primary" @click="exportLogs">
						<el-icon><Download /></el-icon>
						导出日志
					</el-button>
				</div>
			</template>

			<div class="log-manage-content">
				<!-- 日志搜索和筛选 -->
				<div class="log-filter">
					<el-form :inline="true" :model="filterForm">
						<el-form-item label="日志类型">
							<el-select
								v-model="filterForm.logType"
								placeholder="请选择日志类型"
							>
								<el-option label="全部" value="" />
								<el-option label="操作日志" value="operation" />
								<el-option label="系统日志" value="system" />
								<el-option label="错误日志" value="error" />
								<el-option label="警告日志" value="warning" />
							</el-select>
						</el-form-item>
						<el-form-item label="用户">
							<el-input
								v-model="filterForm.user"
								placeholder="请输入用户名"
								style="width: 150px"
							/>
						</el-form-item>
						<el-form-item label="关键词">
							<el-input
								v-model="filterForm.keyword"
								placeholder="请输入关键词"
								style="width: 200px"
							/>
						</el-form-item>
						<el-form-item label="时间范围">
							<el-date-picker
								v-model="filterForm.dateRange"
								type="daterange"
								range-separator="至"
								start-placeholder="开始日期"
								end-placeholder="结束日期"
								style="width: 300px"
							/>
						</el-form-item>
						<el-form-item>
							<el-button type="primary" @click="searchLogs">
								<el-icon><Search /></el-icon>
								搜索
							</el-button>
						</el-form-item>
						<el-form-item>
							<el-button @click="resetFilter"> 重置 </el-button>
						</el-form-item>
					</el-form>
				</div>

				<!-- 日志列表 -->
				<div class="log-list">
					<el-table :data="filteredLogs" style="width: 100%" border>
						<el-table-column prop="id" label="日志ID" width="100" />
						<el-table-column
							prop="type"
							label="日志类型"
							width="100"
						>
							<template #default="scope">
								<el-tag :type="getLogTypeTag(scope.row.type)">
									{{ getLogTypeName(scope.row.type) }}
								</el-tag>
							</template>
						</el-table-column>
						<el-table-column
							prop="user"
							label="操作用户"
							width="120"
						/>
						<el-table-column prop="action" label="操作内容" />
						<el-table-column prop="ip" label="IP地址" width="150" />
						<el-table-column
							prop="timestamp"
							label="操作时间"
							width="180"
						/>
						<el-table-column label="操作" width="120">
							<template #default="scope">
								<el-button
									type="primary"
									size="small"
									@click="viewLogDetail(scope.row)"
								>
									详情
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
							:total="logs.length"
							@size-change="handleSizeChange"
							@current-change="handleCurrentChange"
						/>
					</div>
				</div>
			</div>
		</el-card>

		<!-- 日志详情对话框 -->
		<el-dialog v-model="detailDialogVisible" title="日志详情" width="600px">
			<div v-if="selectedLog" class="log-detail">
				<el-descriptions :column="1" border>
					<el-descriptions-item label="日志ID">{{
						selectedLog.id
					}}</el-descriptions-item>
					<el-descriptions-item label="日志类型">{{
						getLogTypeName(selectedLog.type)
					}}</el-descriptions-item>
					<el-descriptions-item label="操作用户">{{
						selectedLog.user
					}}</el-descriptions-item>
					<el-descriptions-item label="操作内容">{{
						selectedLog.action
					}}</el-descriptions-item>
					<el-descriptions-item label="IP地址">{{
						selectedLog.ip
					}}</el-descriptions-item>
					<el-descriptions-item label="操作时间">{{
						selectedLog.timestamp
					}}</el-descriptions-item>
					<el-descriptions-item label="详细信息">{{
						selectedLog.detail
					}}</el-descriptions-item>
				</el-descriptions>
			</div>
			<template #footer>
				<span class="dialog-footer">
					<el-button @click="detailDialogVisible = false"
						>关闭</el-button
					>
				</span>
			</template>
		</el-dialog>
	</div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted } from "vue";
import { Download, Search } from "@element-plus/icons-vue";
import { ElMessage } from "element-plus";
import {
	businessSystemApi,
	type SystemLogDto,
} from "@/api/businessSystem";
import { downloadBlob } from "@/api/browserDownload";

type Log = SystemLogDto;

const logs = ref<Log[]>([]);

// 分页
const currentPage = ref(1);
const pageSize = ref(10);

// 筛选表单
const filterForm = reactive({
	logType: "",
	user: "",
	keyword: "",
	dateRange: [] as any[],
});

// 日志详情
const detailDialogVisible = ref(false);
const selectedLog = ref<Log | null>(null);

const buildQuery = () => ({
	type: filterForm.logType || undefined,
	user: filterForm.user || undefined,
	keyword: filterForm.keyword || undefined,
});

const loadLogs = async () => {
	try {
		logs.value = await businessSystemApi.listLogs(buildQuery());
	} catch (error) {
		console.error(error);
		ElMessage.error("日志查询失败，请检查业务后端服务");
	}
};

// 获取日志类型标签
const getLogTypeTag = (type: string) => {
	switch (type) {
		case "operation":
			return "info";
		case "system":
			return "primary";
		case "error":
			return "danger";
		case "warning":
			return "warning";
		default:
			return "info";
	}
};

// 获取日志类型名称
const getLogTypeName = (type: string) => {
	switch (type) {
		case "operation":
			return "操作日志";
		case "system":
			return "系统日志";
		case "error":
			return "错误日志";
		case "warning":
			return "警告日志";
		default:
			return "未知日志";
	}
};

// 过滤后的日志
const filteredLogs = computed(() => {
	let result = [...logs.value];

	// 日志类型过滤
	if (filterForm.logType) {
		result = result.filter((log) => log.type === filterForm.logType);
	}

	// 用户过滤
	if (filterForm.user) {
		result = result.filter((log) => log.user.includes(filterForm.user));
	}

	// 关键词过滤
	if (filterForm.keyword) {
		const keyword = filterForm.keyword.toLowerCase();
		result = result.filter(
			(log) =>
				log.action.toLowerCase().includes(keyword) ||
				log.detail.toLowerCase().includes(keyword),
		);
	}

	// 时间范围过滤
	if (filterForm.dateRange && filterForm.dateRange.length === 2) {
		const startDate = new Date(filterForm.dateRange[0]);
		const endDate = new Date(filterForm.dateRange[1]);
		endDate.setHours(23, 59, 59, 999);

		result = result.filter((log) => {
			const logDate = new Date(log.timestamp);
			return logDate >= startDate && logDate <= endDate;
		});
	}

	// 分页
	const startIndex = (currentPage.value - 1) * pageSize.value;
	const endIndex = startIndex + pageSize.value;
	return result.slice(startIndex, endIndex);
});

// 搜索日志
const searchLogs = async () => {
	currentPage.value = 1;
	await loadLogs();
};

// 重置筛选
const resetFilter = () => {
	Object.assign(filterForm, {
		logType: "",
		user: "",
		keyword: "",
		dateRange: [],
	});
	currentPage.value = 1;
	void loadLogs();
};

// 导出日志
const exportLogs = async () => {
	try {
		const blob = await businessSystemApi.exportLogs(buildQuery());
		downloadBlob(blob, `system-logs-${Date.now()}.csv`);
	} catch (error) {
		console.error(error);
		ElMessage.error("日志导出失败，请检查业务后端服务");
	}
};

// 查看日志详情
const viewLogDetail = (log: Log) => {
	selectedLog.value = log;
	detailDialogVisible.value = true;
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
	void loadLogs();
});
</script>

<style lang="scss" scoped>
.system-log-manage-view {
	.log-manage-card {
		margin-bottom: 20px;
	}

	.card-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
	}

	.log-manage-content {
		padding: 20px;
	}

	.log-filter {
		margin-bottom: 20px;
	}

	.log-list {
		margin-top: 20px;
	}

	.pagination-bar {
		margin-top: 20px;
		display: flex;
		justify-content: flex-end;
	}

	.log-detail {
		padding: 20px;
	}
}
</style>
