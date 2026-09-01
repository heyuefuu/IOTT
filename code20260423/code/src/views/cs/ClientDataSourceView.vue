<template>
	<div class="cs-client-data-source-view">
		<h2 class="page-title">客户端数据源管理</h2>

		<el-card class="data-source-card">
			<template #header>
				<div class="card-header">
					<span>数据源管理</span>
					<el-button type="primary" @click="openAddDataSourceDialog">
						<el-icon><Plus /></el-icon>
						新增数据源
					</el-button>
				</div>
			</template>

			<!-- 数据源列表 -->
			<div class="data-source-list">
				<el-table :data="dataSources" style="width: 100%" border>
					<el-table-column prop="id" label="数据源ID" width="100" />
					<el-table-column prop="name" label="数据源名称" />
					<el-table-column
						prop="type"
						label="数据源类型"
						width="120"
					/>
					<el-table-column
						prop="gatewayId"
						label="关联网关"
						width="150"
					>
						<template #default="scope">
							{{ getGatewayName(scope.row.gatewayId) }}
						</template>
					</el-table-column>
					<el-table-column prop="status" label="状态" width="100">
						<template #default="scope">
							<el-tag :type="getStatusType(scope.row.status)">
								{{ scope.row.status }}
							</el-tag>
						</template>
					</el-table-column>
					<el-table-column
						prop="updateInterval"
						label="更新间隔(s)"
						width="120"
					/>
					<el-table-column
						prop="lastUpdate"
						label="最后更新"
						width="180"
					/>
					<el-table-column
						prop="lastResult"
						label="最近探测结果"
						min-width="160"
					/>
					<el-table-column label="操作" width="260">
						<template #default="scope">
							<el-button
								type="primary"
								size="small"
								@click="openEditDataSourceDialog(scope.row)"
							>
								编辑
							</el-button>
							<el-button
								type="success"
								size="small"
								@click="enableDataSource(scope.row)"
								style="margin-left: 5px"
							>
								启用
							</el-button>
							<el-button
								type="warning"
								size="small"
								@click="disableDataSource(scope.row)"
								style="margin-left: 5px"
							>
								禁用
							</el-button>
							<el-button
								type="danger"
								size="small"
								@click="deleteDataSource(scope.row)"
								style="margin-left: 5px"
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
						:total="dataSources.length"
						@size-change="handleSizeChange"
						@current-change="handleCurrentChange"
					/>
				</div>
			</div>
		</el-card>

		<!-- 新增/编辑数据源对话框 -->
		<el-dialog
			v-model="dataSourceDialogVisible"
			:title="isEditing ? '编辑数据源' : '新增数据源'"
			width="500px"
		>
			<el-form :model="currentDataSource" label-width="120px">
				<el-form-item label="数据源名称" prop="name" required>
					<el-input
						v-model="currentDataSource.name"
						placeholder="请输入数据源名称"
					/>
				</el-form-item>
				<el-form-item label="数据源类型" prop="type" required>
					<el-select
						v-model="currentDataSource.type"
						placeholder="请选择数据源类型"
					>
						<el-option label="Modbus设备" value="Modbus" />
						<el-option label="OPC UA节点" value="OPCUA" />
						<el-option label="MQTT主题" value="MQTT" />
						<el-option label="REST API" value="REST" />
						<el-option label="FTP" value="FTP" />
					</el-select>
				</el-form-item>
				<el-form-item label="关联网关" prop="gatewayId" required>
					<el-select
						v-model="currentDataSource.gatewayId"
						placeholder="请选择关联网关"
					>
						<el-option
							v-for="gateway in gateways"
							:key="gateway.id"
							:label="gateway.name"
							:value="gateway.id"
						/>
					</el-select>
				</el-form-item>
				<el-form-item label="配置信息" prop="config">
					<el-input
						v-model="currentDataSource.config"
						type="textarea"
						placeholder="请输入配置信息"
					/>
				</el-form-item>
				<el-form-item label="更新间隔" prop="updateInterval" required>
					<el-input-number
						v-model="currentDataSource.updateInterval"
						:min="1"
						:max="3600"
						:step="1"
						style="width: 200px"
					/>
					<span style="margin-left: 10px">秒</span>
				</el-form-item>
			</el-form>
			<template #footer>
				<span class="dialog-footer">
					<el-button @click="dataSourceDialogVisible = false"
						>取消</el-button
					>
					<el-button type="primary" @click="saveDataSource"
						>保存</el-button
					>
				</span>
			</template>
		</el-dialog>
	</div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted, onUnmounted } from "vue";
import { Plus } from "@element-plus/icons-vue";
import { ElMessage, ElMessageBox } from "element-plus";
import { csApi, type CsGateway, type CsDataSource } from "@/api/cs";

// 网关列表（用于"关联网关"下拉）
const gateways = ref<CsGateway[]>([]);
// 数据源列表（数据来自 MachineConnectionApi → CsConnectivityService）
const dataSources = ref<CsDataSource[]>([]);

const loadAll = async () => {
	try {
		const [gws, dss] = await Promise.all([
			csApi.listGateways(),
			csApi.listDataSources(),
		]);
		gateways.value = gws;
		dataSources.value = dss;
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载数据源/网关失败"));
	}
};

// 周期刷新，便于观察后台探测的最近结果
let refreshTimer: number | undefined;
onMounted(() => {
	void loadAll();
	refreshTimer = window.setInterval(loadAll, 5000);
});
onUnmounted(() => {
	if (refreshTimer !== undefined) window.clearInterval(refreshTimer);
});

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

// 分页
const currentPage = ref(1);
const pageSize = ref(10);

// 数据源对话框
const dataSourceDialogVisible = ref(false);
const isEditing = ref(false);
const currentDataSource = reactive<CsDataSource>({
	id: "",
	name: "",
	type: "Modbus",
	gatewayId: "",
	config: "",
	updateInterval: 5,
	status: "禁用",
	lastUpdate: "",
});

// 获取网关名称
const getGatewayName = (gatewayId: string) => {
	const gateway = gateways.value.find((g) => g.id === gatewayId);
	return gateway ? gateway.name : "未知";
};

// 获取状态类型
const getStatusType = (status: string) => {
	return status === "启用" ? "success" : "danger";
};

// 打开新增数据源对话框
const openAddDataSourceDialog = () => {
	isEditing.value = false;
	Object.assign(currentDataSource, {
		id: "",
		name: "",
		type: "Modbus",
		gatewayId: gateways.value.length > 0 ? gateways.value[0]?.id : "",
		config: "",
		updateInterval: 5,
		status: "禁用",
		lastUpdate: "",
	});
	dataSourceDialogVisible.value = true;
};

// 打开编辑数据源对话框
const openEditDataSourceDialog = (dataSource: CsDataSource) => {
	isEditing.value = true;
	Object.assign(currentDataSource, { ...dataSource });
	dataSourceDialogVisible.value = true;
};

// 保存数据源
const saveDataSource = async () => {
	if (
		!currentDataSource.name ||
		!currentDataSource.type ||
		!currentDataSource.gatewayId ||
		!currentDataSource.updateInterval
	) {
		ElMessage.warning("请填写必填字段");
		return;
	}

	try {
		if (isEditing.value) {
			await csApi.updateDataSource(currentDataSource.id, {
				...currentDataSource,
			});
			ElMessage.success("数据源编辑成功");
		} else {
			await csApi.createDataSource({ ...currentDataSource });
			ElMessage.success("数据源添加成功");
		}
		dataSourceDialogVisible.value = false;
		await loadAll();
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "保存数据源失败"));
	}
};

// 启用数据源 = 启动后台周期探测循环
const enableDataSource = (dataSource: CsDataSource) => {
	ElMessageBox.confirm(`确定要启用数据源: ${dataSource.name}吗？`, "确认", {
		confirmButtonText: "确定",
		cancelButtonText: "取消",
		type: "warning",
	})
		.then(async () => {
			try {
				await csApi.enableDataSource(dataSource.id);
				ElMessage.success(`数据源 ${dataSource.name} 启用成功`);
				await loadAll();
			} catch (e: unknown) {
				ElMessage.error(getErr(e, "启用失败"));
			}
		})
		.catch(() => {
			// 取消
		});
};

// 禁用数据源 = 停止周期探测循环
const disableDataSource = (dataSource: CsDataSource) => {
	ElMessageBox.confirm(`确定要禁用数据源: ${dataSource.name}吗？`, "确认", {
		confirmButtonText: "确定",
		cancelButtonText: "取消",
		type: "warning",
	})
		.then(async () => {
			try {
				await csApi.disableDataSource(dataSource.id);
				ElMessage.success(`数据源 ${dataSource.name} 禁用成功`);
				await loadAll();
			} catch (e: unknown) {
				ElMessage.error(getErr(e, "禁用失败"));
			}
		})
		.catch(() => {
			// 取消
		});
};

const deleteDataSource = (dataSource: CsDataSource) => {
	ElMessageBox.confirm(`确定删除数据源: ${dataSource.name}？`, "删除确认", {
		confirmButtonText: "删除",
		cancelButtonText: "取消",
		type: "warning",
	})
		.then(async () => {
			try {
				await csApi.deleteDataSource(dataSource.id);
				ElMessage.success(`数据源 ${dataSource.name} 已删除`);
				await loadAll();
			} catch (e: unknown) {
				ElMessage.error(getErr(e, "删除数据源失败"));
			}
		})
		.catch(() => {
			// 取消
		});
};

// 分页处理
const handleSizeChange = (size: number) => {
	pageSize.value = size;
	currentPage.value = 1;
};

const handleCurrentChange = (current: number) => {
	currentPage.value = current;
};
</script>

<style lang="scss" scoped>
.cs-client-data-source-view {
	.data-source-card {
		margin-bottom: 20px;
	}

	.card-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
	}

	.data-source-list {
		padding: 20px;
	}

	.pagination-bar {
		margin-top: 20px;
		display: flex;
		justify-content: flex-end;
	}
}
</style>
