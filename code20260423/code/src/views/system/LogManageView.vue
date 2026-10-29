<template>
	<div class="system-log-manage-view">
		<h2 class="page-title">日志管理</h2>
		<HostRuntimeLogs />

		<el-card class="log-manage-card">
			<template #header>
				<div class="card-header">
					<span>日志管理</span>
					<el-button type="primary" :loading="exporting" :disabled="logsLoading || !!logsError" @click="exportLogs">
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
						<el-form-item label="历史日志">
							<el-switch v-model="filterForm.includeArchived" active-text="包含归档" @change="searchLogs" />
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
				<p v-if="retention">活跃日志最多 {{ retention.activeLimit }} 条；归档按 UTC 日期{{ retention.archiveRetentionDays ? `保留 ${retention.archiveRetentionDays} 天` : '长期保留' }}。</p>
				<el-alert v-if="logsError" :title="logsError" type="error" :closable="false" />
				<div class="log-list">
					<el-table v-loading="logsLoading" :data="filteredLogs" style="width: 100%" border>
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
							:total="totalLogs"
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
import HostRuntimeLogs from "./HostRuntimeLogs.vue";
import { ref, reactive, computed, onMounted } from "vue";
import { Download, Search } from "@element-plus/icons-vue";
import { ElMessage } from "element-plus";
import {
	businessSystemApi,
	type SystemLogDto,
	type SystemLogQuery,
	type SystemLogRetention,
} from "@/api/businessSystem";
import { downloadBlob } from "@/api/browserDownload";

type Log = SystemLogDto;

const logs = ref<Log[]>([]);
const totalLogs = ref(0);
const logsLoading = ref(false);
const logsError = ref("");
const exporting = ref(false);
const retention = ref<SystemLogRetention | null>(null);
const appliedQuery = ref<SystemLogQuery>({ includeArchived: true });
let queryVersion = 0;

// 分页
const currentPage = ref(1);
const pageSize = ref(10);

// 筛选表单
const filterForm = reactive({
	logType: "",
	user: "",
	keyword: "",
	dateRange: [] as Date[],
	includeArchived: true,
});

// 日志详情
const detailDialogVisible = ref(false);
const selectedLog = ref<Log | null>(null);

const logErrorMessage = (error: unknown, fallback: string): string => {
	const detail = error as { response?: { data?: { error?: string; detail?: string } }; message?: string } | null;
	return detail?.response?.data?.error || detail?.response?.data?.detail || detail?.message || fallback;
};

const buildQuery = (): SystemLogQuery => {
	const [first, last] = filterForm.dateRange ?? [];
	const from = first ? new Date(first) : undefined;
	const to = last ? new Date(last) : undefined;
	from?.setHours(0, 0, 0, 0);
	to?.setHours(23, 59, 59, 999);
	return {
		type: filterForm.logType || undefined,
		user: filterForm.user || undefined,
		keyword: filterForm.keyword || undefined,
		includeArchived: filterForm.includeArchived,
		from: from?.toISOString(), to: to?.toISOString(),
	};
};

const loadLogs = async () => {
	const version = ++queryVersion;
	const query = buildQuery();
	logsLoading.value = true;
	logsError.value = "";
	try {
		const result = await businessSystemApi.queryLogs({ ...query, page: currentPage.value, pageSize: pageSize.value });
		if (version !== queryVersion) return;
		const lastPage = Math.max(1, Math.ceil(result.total / pageSize.value));
		if (currentPage.value > lastPage) {
			currentPage.value = lastPage;
			void loadLogs();
			return;
		}
		logs.value = result.items;
		totalLogs.value = result.total;
		appliedQuery.value = query;
	} catch (error) {
		if (version !== queryVersion) return;
		logs.value = [];
		totalLogs.value = 0;
		logsError.value = logErrorMessage(error, "日志查询失败，请检查查询条件、服务及归档文件");
	} finally {
		if (version === queryVersion) logsLoading.value = false;
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
const filteredLogs = computed(() => logs.value);

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
		includeArchived: true,
	});
	currentPage.value = 1;
	void loadLogs();
};

// 导出日志
const exportLogs = async () => {
	if (exporting.value || logsLoading.value || logsError.value) return;
	exporting.value = true;
	const query = { ...appliedQuery.value };
	try {
		const blob = await businessSystemApi.exportLogs(query);
		downloadBlob(blob, `system-logs-${Date.now()}.csv`);
	} catch (error) {
		ElMessage.error(logErrorMessage(error, "日志导出失败，请检查业务后端服务"));
	} finally {
		exporting.value = false;
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
	void loadLogs();
};

const handleCurrentChange = (current: number) => {
	currentPage.value = current;
	void loadLogs();
};

onMounted(() => {
	void loadLogs();
	void businessSystemApi.logRetention().then(value => { retention.value = value; }).catch(() => { retention.value = null; });
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
