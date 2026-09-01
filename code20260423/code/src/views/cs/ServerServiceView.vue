<template>
	<div class="cs-server-service-view">
		<h2 class="page-title">服务器服务管理</h2>

		<el-card class="service-manage-card">
			<template #header>
				<div class="card-header">
					<span>服务管理</span>
					<el-button type="primary" @click="openAddServiceDialog">
						<el-icon><Plus /></el-icon>
						新增服务
					</el-button>
				</div>
			</template>

			<!-- 服务列表 -->
			<div class="service-list">
				<el-table :data="services" style="width: 100%" border>
					<el-table-column prop="id" label="服务ID" width="100" />
					<el-table-column prop="name" label="服务名称" />
					<el-table-column prop="type" label="服务类型" width="120" />
					<el-table-column prop="port" label="端口" width="100" />
					<el-table-column prop="status" label="状态" width="100">
						<template #default="scope">
							<el-tag :type="getStatusType(scope.row.status)">
								{{ scope.row.status }}
							</el-tag>
						</template>
					</el-table-column>
					<el-table-column
						prop="clientCount"
						label="客户端数"
						width="100"
					/>
					<el-table-column
						prop="lastAccess"
						label="最后访问"
						width="180"
					/>
					<el-table-column label="操作" width="320">
						<template #default="scope">
							<el-button
								type="primary"
								size="small"
								@click="openEditServiceDialog(scope.row)"
							>
								编辑
							</el-button>
							<el-button
								type="success"
								size="small"
								@click="startService(scope.row)"
								style="margin-left: 5px"
							>
								启动
							</el-button>
							<el-button
								type="danger"
								size="small"
								@click="stopService(scope.row)"
								style="margin-left: 5px"
							>
								停止
							</el-button>
							<el-button
								size="small"
								@click="openConnections(scope.row)"
								style="margin-left: 5px"
							>
								连接
							</el-button>
							<el-button
								type="danger"
								size="small"
								@click="deleteService(scope.row)"
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
						:total="services.length"
						@size-change="handleSizeChange"
						@current-change="handleCurrentChange"
					/>
				</div>
			</div>
		</el-card>

		<!-- 新增/编辑服务对话框 -->
		<el-dialog
			v-model="serviceDialogVisible"
			:title="isEditing ? '编辑服务' : '新增服务'"
			width="500px"
		>
			<el-form :model="currentService" label-width="120px">
				<el-form-item label="服务名称" prop="name" required>
					<el-input
						v-model="currentService.name"
						placeholder="请输入服务名称"
					/>
				</el-form-item>
				<el-form-item label="服务类型" prop="type" required>
					<el-select
						v-model="currentService.type"
						placeholder="请选择服务类型"
					>
						<el-option label="Modbus服务器（TCP 回显验证）" value="ModbusServer" />
						<el-option label="OPC UA服务器（TCP 回显验证）" value="OPCUAServer" />
						<el-option label="MQTT代理（TCP 回显验证）" value="MQTTBroker" />
						<el-option label="REST服务器（TCP 回显验证）" value="RESTServer" />
						<el-option label="FTP 服务器（真实 FTP 协议）" value="FtpServer" />
					</el-select>
					<div v-if="currentService.type !== 'FtpServer'" class="type-hint">
						非 FTP 类型仅验证 TCP 连通性（接受连接并回显数据），不实现对应协议语义，请勿用真实协议客户端做业务测试。
					</div>
				</el-form-item>
				<el-form-item label="端口" prop="port" required>
					<el-input-number
						v-model="currentService.port"
						:min="1"
						:max="65535"
						:step="1"
						style="width: 200px"
					/>
				</el-form-item>
				<template v-if="currentService.type === 'FtpServer'">
					<el-form-item label="FTP 用户名" prop="username" required>
						<el-input
							v-model="currentService.username"
							placeholder="必填，匿名登录已禁用"
						/>
					</el-form-item>
					<el-form-item label="FTP 密码" prop="password" :required="!isEditing">
						<el-input
							v-model="currentService.password"
							type="password"
							show-password
							:placeholder="isEditing ? '留空保持原密码' : '必填'"
						/>
					</el-form-item>
				</template>
				<el-form-item label="服务描述" prop="description">
					<el-input
						v-model="currentService.description"
						type="textarea"
						placeholder="请输入服务描述"
					/>
				</el-form-item>
				<el-form-item label="最大客户端数" prop="maxClients">
					<el-input-number
						v-model="currentService.maxClients"
						:min="1"
						:max="1000"
						:step="1"
						style="width: 200px"
					/>
				</el-form-item>
			</el-form>
			<template #footer>
				<span class="dialog-footer">
					<el-button @click="serviceDialogVisible = false"
						>取消</el-button
					>
					<el-button type="primary" @click="saveService"
						>保存</el-button
					>
				</span>
			</template>
		</el-dialog>

		<!-- 在线客户端连接对话框 -->
		<el-dialog
			v-model="connectionsDialogVisible"
			:title="`在线客户端 - ${connectionsServiceName}`"
			width="640px"
		>
			<el-table :data="connections" border>
				<el-table-column
					prop="remoteEndpoint"
					label="客户端地址"
					min-width="180"
				/>
				<el-table-column
					prop="connectedAt"
					label="接入时间"
					width="180"
				/>
				<el-table-column
					prop="bytesReceived"
					label="接收字节"
					width="120"
				/>
			</el-table>
			<template #footer>
				<el-button @click="connectionsDialogVisible = false"
					>关闭</el-button
				>
				<el-button type="primary" @click="refreshConnections"
					>刷新</el-button
				>
			</template>
		</el-dialog>
	</div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted, onUnmounted } from "vue";
import { Plus } from "@element-plus/icons-vue";
import { ElMessage, ElMessageBox } from "element-plus";
import {
	csApi,
	type CsServerService,
	type CsServerConnection,
} from "@/api/cs";

// 服务列表（数据来自 MachineConnectionApi → CsConnectivityService）
const services = ref<CsServerService[]>([]);

const loadServers = async () => {
	try {
		services.value = await csApi.listServers();
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载服务列表失败"));
	}
};

// 周期刷新，便于观察真实在线客户端数变化
let refreshTimer: number | undefined;
onMounted(() => {
	void loadServers();
	refreshTimer = window.setInterval(loadServers, 5000);
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

// 服务对话框
const serviceDialogVisible = ref(false);
const isEditing = ref(false);
const currentService = reactive<CsServerService>({
	id: "",
	name: "",
	type: "ModbusServer",
	username: "",
	password: "",
	port: 502,
	description: "",
	maxClients: 100,
	status: "停止",
	clientCount: 0,
	lastAccess: "",
});

// 获取状态类型
const getStatusType = (status: string) => {
	switch (status) {
		case "运行中":
			return "success";
		case "停止":
			return "danger";
		case "启动中":
			return "warning";
		case "停止中":
			return "warning";
		default:
			return "info";
	}
};

// 打开新增服务对话框
const openAddServiceDialog = () => {
	isEditing.value = false;
	Object.assign(currentService, {
		id: "",
		name: "",
		type: "ModbusServer",
		username: "",
		password: "",
		port: 502,
		description: "",
		maxClients: 100,
		status: "停止",
		clientCount: 0,
		lastAccess: "",
	});
	serviceDialogVisible.value = true;
};

// 打开编辑服务对话框
const openEditServiceDialog = (service: CsServerService) => {
	isEditing.value = true;
	Object.assign(currentService, { ...service });
	serviceDialogVisible.value = true;
};

// 保存服务
const saveService = async () => {
	if (!currentService.name || !currentService.type || !currentService.port) {
		ElMessage.warning("请填写必填字段");
		return;
	}
	// 后端已强制 FTP 服务端禁止匿名（启动时校验），提交前先在表单层拦截
	if (currentService.type === "FtpServer" &&
		(!currentService.username?.trim() || (!isEditing.value && !currentService.password))) {
		ElMessage.warning("FTP 服务端必须配置用户名和密码（匿名登录已禁用）");
		return;
	}

	try {
		if (isEditing.value) {
			await csApi.updateServer(currentService.id, { ...currentService });
			ElMessage.success("服务编辑成功");
		} else {
			await csApi.createServer({ ...currentService });
			ElMessage.success("服务添加成功");
		}
		serviceDialogVisible.value = false;
		await loadServers();
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "保存服务失败"));
	}
};

// 启动服务 = 真实 TcpListener 监听端口
const startService = (service: CsServerService) => {
	ElMessageBox.confirm(
		`确定要启动服务: ${service.name}吗？将真实监听端口 ${service.port}`,
		"确认",
		{ confirmButtonText: "确定", cancelButtonText: "取消", type: "warning" },
	)
		.then(async () => {
			try {
				await csApi.startServer(service.id);
				ElMessage.success(
					`服务 ${service.name} 启动成功，监听端口 ${service.port}`,
				);
				await loadServers();
			} catch (e: unknown) {
				ElMessage.error(getErr(e, "启动失败（端口可能被占用）"));
			}
		})
		.catch(() => {
			// 取消
		});
};

// 停止服务 = 关闭监听并断开所有客户端
const stopService = (service: CsServerService) => {
	ElMessageBox.confirm(`确定要停止服务: ${service.name}吗？`, "确认", {
		confirmButtonText: "确定",
		cancelButtonText: "取消",
		type: "warning",
	})
		.then(async () => {
			try {
				await csApi.stopServer(service.id);
				ElMessage.success(`服务 ${service.name} 停止成功`);
				await loadServers();
			} catch (e: unknown) {
				ElMessage.error(getErr(e, "停止失败"));
			}
		})
		.catch(() => {
			// 取消
		});
};

const deleteService = (service: CsServerService) => {
	ElMessageBox.confirm(`确定删除服务: ${service.name}？`, "删除确认", {
		confirmButtonText: "删除",
		cancelButtonText: "取消",
		type: "warning",
	})
		.then(async () => {
			try {
				await csApi.deleteServer(service.id);
				ElMessage.success(`服务 ${service.name} 已删除`);
				await loadServers();
			} catch (e: unknown) {
				ElMessage.error(getErr(e, "删除服务失败"));
			}
		})
		.catch(() => {
			// 取消
		});
};

// 在线客户端连接查看
const connectionsDialogVisible = ref(false);
const connectionsServiceName = ref("");
const connectionsServiceId = ref("");
const connections = ref<CsServerConnection[]>([]);

const openConnections = async (service: CsServerService) => {
	connectionsServiceId.value = service.id;
	connectionsServiceName.value = service.name;
	connectionsDialogVisible.value = true;
	await refreshConnections();
};

const refreshConnections = async () => {
	if (!connectionsServiceId.value) return;
	try {
		connections.value = await csApi.serverConnections(
			connectionsServiceId.value,
		);
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "获取连接列表失败"));
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
</script>

<style lang="scss" scoped>
.cs-server-service-view {
	.type-hint {
		margin-top: 4px;
		font-size: 12px;
		line-height: 1.5;
		color: var(--el-text-color-secondary);
	}

	.service-manage-card {
		margin-bottom: 20px;
	}

	.card-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
	}

	.service-list {
		padding: 20px;
	}

	.pagination-bar {
		margin-top: 20px;
		display: flex;
		justify-content: flex-end;
	}
}
</style>
