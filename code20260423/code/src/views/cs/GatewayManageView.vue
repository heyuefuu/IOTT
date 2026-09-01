<template>
	<div class="cs-gateway-manage-view">
		<h2 class="page-title">网关管理</h2>

		<el-card class="gateway-manage-card">
			<template #header>
				<div class="card-header">
					<span>网关管理</span>
					<el-button type="primary" @click="openAddGatewayDialog">
						<el-icon><Plus /></el-icon>
						新增网关
					</el-button>
				</div>
			</template>

			<!-- 网关列表 -->
			<div class="gateway-list">
				<el-table :data="gateways" style="width: 100%" border>
					<el-table-column prop="id" label="网关ID" width="100" />
					<el-table-column prop="name" label="网关名称" />
					<el-table-column prop="ip" label="IP地址" width="150" />
					<el-table-column prop="port" label="端口" width="100" />
					<el-table-column prop="type" label="网关类型" width="120" />
					<el-table-column prop="status" label="状态" width="100">
						<template #default="scope">
							<el-tag :type="getStatusType(scope.row.status)">
								{{ scope.row.status }}
							</el-tag>
						</template>
					</el-table-column>
					<el-table-column
						prop="lastHeartbeat"
						label="最后心跳"
						width="180"
					/>
					<el-table-column label="操作" width="270">
						<template #default="scope">
							<el-button
								type="primary"
								size="small"
								@click="openEditGatewayDialog(scope.row)"
							>
								编辑
							</el-button>
							<el-button
								type="success"
								size="small"
								@click="startGateway(scope.row)"
								style="margin-left: 5px"
							>
								连通探测
							</el-button>
							<el-button
								type="warning"
								size="small"
								@click="stopGateway(scope.row)"
								style="margin-left: 5px"
							>
								标记停用
							</el-button>
							<el-button
								type="danger"
								size="small"
								@click="deleteGateway(scope.row)"
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
						:total="gateways.length"
						@size-change="handleSizeChange"
						@current-change="handleCurrentChange"
					/>
				</div>
			</div>
		</el-card>

		<!-- 新增/编辑网关对话框 -->
		<el-dialog
			v-model="gatewayDialogVisible"
			:title="isEditing ? '编辑网关' : '新增网关'"
			width="500px"
		>
			<el-form :model="currentGateway" label-width="120px">
				<el-form-item label="网关名称" prop="name" required>
					<el-input
						v-model="currentGateway.name"
						placeholder="请输入网关名称"
					/>
				</el-form-item>
				<el-form-item label="IP地址" prop="ip" required>
					<el-input
						v-model="currentGateway.ip"
						placeholder="请输入IP地址"
					/>
				</el-form-item>
				<el-form-item label="端口" prop="port" required>
					<el-input-number
						v-model="currentGateway.port"
						:min="1"
						:max="65535"
						:step="1"
						style="width: 200px"
					/>
				</el-form-item>
				<el-form-item label="网关类型" prop="type" required>
					<el-select
						v-model="currentGateway.type"
						placeholder="请选择网关类型"
					>
						<el-option label="Modbus网关" value="Modbus" />
						<el-option label="OPC UA网关" value="OPCUA" />
						<el-option label="MQTT网关" value="MQTT" />
						<el-option label="REST网关" value="REST" />
						<el-option label="FTP" value="FTP" />
					</el-select>
				</el-form-item>
				<template v-if="currentGateway.type === 'FTP'">
					<el-form-item label="FTP 用户名" prop="username">
						<el-input
							v-model="currentGateway.username"
							placeholder="留空为匿名登录"
						/>
					</el-form-item>
					<el-form-item label="FTP 密码" prop="password">
						<el-input
							v-model="currentGateway.password"
							type="password"
							show-password
						/>
					</el-form-item>
					<el-form-item label="远程目录" prop="remotePath">
						<el-input
							v-model="currentGateway.remotePath"
							placeholder="默认根目录 /"
						/>
					</el-form-item>
				</template>
				<el-form-item label="描述" prop="description">
					<el-input
						v-model="currentGateway.description"
						type="textarea"
						placeholder="请输入网关描述"
					/>
				</el-form-item>
			</el-form>
			<template #footer>
				<span class="dialog-footer">
					<el-button @click="gatewayDialogVisible = false"
						>取消</el-button
					>
					<el-button type="primary" @click="saveGateway"
						>保存</el-button
					>
				</span>
			</template>
		</el-dialog>
	</div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from "vue";
import { Plus } from "@element-plus/icons-vue";
import { ElMessage, ElMessageBox } from "element-plus";
import { csApi, type CsGateway } from "@/api/cs";

// 网关列表（数据来自 MachineConnectionApi → CsConnectivityService）
const gateways = ref<CsGateway[]>([]);

const loadGateways = async () => {
	try {
		gateways.value = await csApi.listGateways();
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载网关列表失败"));
	}
};

onMounted(loadGateways);

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

// 网关对话框
const gatewayDialogVisible = ref(false);
const isEditing = ref(false);
const currentGateway = reactive<CsGateway>({
	id: "",
	name: "",
	ip: "",
	port: 502,
	type: "Modbus",
	username: "",
	password: "",
	remotePath: "",
	description: "",
	status: "停止",
	lastHeartbeat: "",
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

// 打开新增网关对话框
const openAddGatewayDialog = () => {
	isEditing.value = false;
	Object.assign(currentGateway, {
		id: "",
		name: "",
		ip: "",
		port: 502,
		type: "Modbus",
		username: "",
		password: "",
		remotePath: "",
		description: "",
		status: "停止",
		lastHeartbeat: "",
	});
	gatewayDialogVisible.value = true;
};

// 打开编辑网关对话框
const openEditGatewayDialog = (gateway: CsGateway) => {
	isEditing.value = true;
	Object.assign(currentGateway, { ...gateway });
	gatewayDialogVisible.value = true;
};

// 保存网关
const saveGateway = async () => {
	if (
		!currentGateway.name ||
		!currentGateway.ip ||
		!currentGateway.port ||
		!currentGateway.type
	) {
		ElMessage.warning("请填写必填字段");
		return;
	}

	try {
		if (isEditing.value) {
			await csApi.updateGateway(currentGateway.id, { ...currentGateway });
			ElMessage.success("网关编辑成功");
		} else {
			await csApi.createGateway({ ...currentGateway });
			ElMessage.success("网关添加成功");
		}
		gatewayDialogVisible.value = false;
		await loadGateways();
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "保存网关失败"));
	}
};

// 连通探测：对网关地址真实 TCP connect 并测 RTT（客户端模式没有可“启动”的常驻进程）
const startGateway = (gateway: CsGateway) => {
	ElMessageBox.confirm(
		`对网关 ${gateway.name}（${gateway.ip}:${gateway.port}）发起 TCP 连通探测？`,
		"连通探测",
		{ confirmButtonText: "探测", cancelButtonText: "取消", type: "info" },
	)
		.then(async () => {
			try {
				const r = await csApi.probeGateway(gateway.id);
				if (r.success) {
					ElMessage.success(`网关 ${gateway.name} 连通，RTT ${r.rttMs}ms`);
				} else {
					ElMessage.warning(`网关 ${gateway.name} 不可达：${r.message}`);
				}
				await loadGateways();
			} catch (e: unknown) {
				ElMessage.error(getErr(e, "探测失败"));
			}
		})
		.catch(() => {
			// 取消
		});
};

// 标记停用：仅更新状态记录（客户端探测无常驻连接，没有真实进程可停止）
const stopGateway = (gateway: CsGateway) => {
	ElMessageBox.confirm(
		`将网关 ${gateway.name} 标记为停用？（仅更新状态记录，不影响远端服务）`,
		"标记停用",
		{
			confirmButtonText: "确定",
			cancelButtonText: "取消",
			type: "warning",
		},
	)
		.then(async () => {
			try {
				await csApi.updateGateway(gateway.id, {
					...gateway,
					status: "停止",
					lastHeartbeat: "",
				});
				ElMessage.success(`网关 ${gateway.name} 已标记为停用`);
				await loadGateways();
			} catch (e: unknown) {
				ElMessage.error(getErr(e, "操作失败"));
			}
		})
		.catch(() => {
			// 取消
		});
};

const deleteGateway = (gateway: CsGateway) => {
	ElMessageBox.confirm(`确定删除网关: ${gateway.name}？`, "删除确认", {
		confirmButtonText: "删除",
		cancelButtonText: "取消",
		type: "warning",
	})
		.then(async () => {
			try {
				await csApi.deleteGateway(gateway.id);
				ElMessage.success(`网关 ${gateway.name} 已删除`);
				await loadGateways();
			} catch (e: unknown) {
				ElMessage.error(getErr(e, "删除网关失败"));
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
.cs-gateway-manage-view {
	.gateway-manage-card {
		margin-bottom: 20px;
	}

	.card-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
	}

	.gateway-list {
		padding: 20px;
	}

	.pagination-bar {
		margin-top: 20px;
		display: flex;
		justify-content: flex-end;
	}
}
</style>
