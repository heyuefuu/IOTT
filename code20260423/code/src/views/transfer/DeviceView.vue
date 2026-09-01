<template>
	<div class="transfer-device-view">
		<h2 class="page-title">数控设备管理</h2>

		<!-- 设备列表 -->
		<el-card class="device-list-card">
			<template #header>
				<div class="card-header">
					<span>设备列表</span>
					<el-button type="primary" @click="openAddDeviceDialog">
						<el-icon><Plus /></el-icon>
						新增设备
					</el-button>
				</div>
			</template>

			<!-- 搜索和筛选 -->
			<div class="search-filter-bar">
				<el-input
					v-model="searchQuery"
					placeholder="搜索设备名称、编号或IP"
					prefix-icon="Search"
					style="width: 300px; margin-right: 10px"
				/>
				<el-select
					v-model="filterProtocol"
					placeholder="筛选协议"
					style="width: 150px; margin-right: 10px"
				>
					<el-option label="全部" value="" />
					<el-option label="FTP" value="FTP" />
					<el-option label="SMB" value="SMB" />
					<el-option label="NFS" value="NFS" />
				</el-select>
				<el-button type="primary" @click="refreshDeviceList">
					<el-icon><Refresh /></el-icon>
					刷新
				</el-button>
			</div>

			<!-- 设备表格 -->
			<el-table :data="filteredDevices" style="width: 100%" border>
				<el-table-column prop="id" label="设备ID" width="80" />
				<el-table-column prop="deviceCode" label="设备编号" width="120" />
				<el-table-column prop="name" label="设备名称" />
				<el-table-column prop="ip" label="IP地址" width="150" />
				<el-table-column prop="port" label="端口" width="100" />
				<el-table-column prop="protocol" label="协议" width="100" />
				<el-table-column prop="status" label="状态" width="100">
					<template #default="scope">
						<el-tag :type="getStatusType(scope.row.status)">
							{{ scope.row.status }}
						</el-tag>
					</template>
				</el-table-column>
				<el-table-column label="操作" width="200">
					<template #default="scope">
						<el-button
							type="primary"
							size="small"
							@click="openEditDeviceDialog(scope.row)"
						>
							编辑
						</el-button>
						<el-button
							type="danger"
							size="small"
							@click="deleteDevice(scope.row.id)"
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
					:total="devices.length"
					@size-change="handleSizeChange"
					@current-change="handleCurrentChange"
				/>
			</div>
		</el-card>

		<!-- 新增/编辑设备对话框 -->
		<el-dialog
			v-model="deviceDialogVisible"
			:title="isEditing ? '编辑设备' : '新增设备'"
			width="500px"
		>
			<el-form :model="currentDevice" label-width="120px">
				<el-form-item label="设备编号" prop="deviceCode" required>
					<el-input
						v-model="currentDevice.deviceCode"
						placeholder="请输入设备编号"
					/>
				</el-form-item>
				<el-form-item label="设备名称" prop="name" required>
					<el-input
						v-model="currentDevice.name"
						placeholder="请输入设备名称"
					/>
				</el-form-item>
				<el-form-item label="IP地址" prop="ip" required>
					<el-input
						v-model="currentDevice.ip"
						placeholder="请输入IP地址"
					/>
				</el-form-item>
				<el-form-item label="端口" prop="port" required>
					<el-input-number
						v-model="currentDevice.port"
						:min="1"
						:max="65535"
						:step="1"
						style="width: 200px"
					/>
				</el-form-item>
				<el-form-item label="协议类型" prop="protocol" required>
					<el-select
						v-model="currentDevice.protocol"
						placeholder="请选择协议类型"
					>
						<el-option label="FTP" value="FTP" />
						<el-option label="SMB" value="SMB" />
						<el-option label="NFS" value="NFS" />
					</el-select>
				</el-form-item>
				<el-form-item label="用户名" prop="username">
					<el-input
						v-model="currentDevice.username"
						placeholder="请输入用户名"
					/>
				</el-form-item>
				<el-form-item label="密码" prop="password">
					<el-input
						v-model="currentDevice.password"
						type="password"
						placeholder="请输入密码"
					/>
				</el-form-item>
				<el-form-item label="设备路径" prop="devicePath">
					<el-input
						v-model="currentDevice.devicePath"
						placeholder="请输入设备程序存储路径"
					/>
				</el-form-item>
				<el-form-item
					v-if="currentDevice.protocol === 'SMB'"
					label="共享名"
					prop="shareName"
					required
				>
					<el-input
						v-model="currentDevice.shareName"
						placeholder="SMB 共享名（必填，对应 ShareName）"
					/>
				</el-form-item>
				<el-form-item
					v-if="currentDevice.protocol === 'FTP'"
					label="FTPS 加密"
					prop="ftpsMode"
				>
					<el-select
						v-model="currentDevice.ftpsMode"
						placeholder="请选择 FTPS 加密模式"
					>
						<el-option label="不加密" value="None" />
						<el-option label="显式 FTPS (Explicit)" value="Explicit" />
						<el-option label="隐式 FTPS (Implicit)" value="Implicit" />
					</el-select>
				</el-form-item>
			</el-form>
			<template #footer>
				<span class="dialog-footer">
					<el-button @click="deviceDialogVisible = false"
						>取消</el-button
					>
					<el-button type="success" @click="testConnection"
						>测试连接</el-button
					>
					<el-button type="primary" @click="saveDevice"
						>保存</el-button
					>
				</span>
			</template>
		</el-dialog>

		<!-- 测试连接对话框 -->
		<el-dialog
			v-model="testDialogVisible"
			title="端口测试结果"
			width="400px"
		>
			<div class="test-connection">
				<div v-if="isTesting" class="testing">
					<el-icon class="is-loading"><Loading /></el-icon>
					<span style="margin-left: 10px">正在测试连接...</span>
				</div>
				<div v-else-if="testResult" class="test-result">
					<el-alert
						:title="testResult.success ? '测试成功' : '测试失败'"
						:description="testResult.message"
						:type="testResult.success ? 'success' : 'error'"
						show-icon
					/>
					<div v-if="testResult.details" class="test-details">
						<h4>测试详情</h4>
						<el-descriptions :column="1" border>
							<el-descriptions-item label="设备地址">{{
								testResult.details.address
							}}</el-descriptions-item>
							<el-descriptions-item label="端口">{{
								testResult.details.port
							}}</el-descriptions-item>
							<el-descriptions-item label="协议">{{
								testResult.details.protocol
							}}</el-descriptions-item>
							<el-descriptions-item
								label="FTP模式"
								v-if="testResult.details.ftpMode"
								>{{
									testResult.details.ftpMode
								}}</el-descriptions-item
							>
							<el-descriptions-item label="响应时间"
								>{{
									testResult.details.responseTime
								}}ms</el-descriptions-item
							>
						</el-descriptions>
					</div>
				</div>
			</div>
			<template #footer>
				<span class="dialog-footer">
					<el-button @click="testDialogVisible = false"
						>关闭</el-button
					>
				</span>
			</template>
		</el-dialog>
	</div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted } from "vue";
import { Plus, Refresh, Loading } from "@element-plus/icons-vue";
import { ElMessageBox, ElMessage } from "element-plus";
import {
	machineConnectionDevicesApi,
	type DeviceDto,
	type CreateDeviceRequest,
} from "@/api/machineConnectionDevices";
import { TRANSFER_PROTOCOLS } from "./transferRecordMetrics";

// 设备类型定义（映射后端 Device，type=CNC + 文件传输协议）
interface Device {
	id: string;
	deviceCode: string;
	name: string;
	ip: string;
	port: number;
	protocol: string;
	username?: string;
	password?: string;
	devicePath?: string;
	shareName?: string; // SMB 必填共享名
	ftpsMode?: string; // FTP 加密：None/Explicit/Implicit（驱动读 EncryptionMode）
	status: string;
}

// 设备列表（来自后端 /api/devices?type=CNC，仅文件传输协议）
const devices = ref<Device[]>([]);

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

// 后端 DeviceDto → 页面 Device
function mapToDevice(d: DeviceDto): Device {
	return {
		id: d.id,
		deviceCode: d.extendedProperties?.deviceCode ?? d.model ?? "",
		name: d.name,
		ip: d.host,
		port: d.port,
		protocol: d.protocol,
		username: d.username ?? "",
		password: "",
		devicePath: d.extendedProperties?.devicePath ?? "",
		shareName: d.extendedProperties?.ShareName ?? "",
		ftpsMode: d.extendedProperties?.EncryptionMode ?? "None",
		status: d.status,
	};
}

const loadDevices = async () => {
	try {
		const list = await machineConnectionDevicesApi.list("CNC");
		devices.value = list
			.filter((d) => TRANSFER_PROTOCOLS.includes(d.protocol))
			.map(mapToDevice);
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载设备列表失败"));
	}
};

// 搜索和筛选
const searchQuery = ref("");
const filterProtocol = ref("");

// 分页
const currentPage = ref(1);
const pageSize = ref(10);

// 设备对话框
const deviceDialogVisible = ref(false);
const isEditing = ref(false);
const currentDevice = reactive<Device>({
	id: "",
	deviceCode: "",
	name: "",
	ip: "",
	port: 21,
	protocol: "FTP",
	username: "",
	password: "",
	devicePath: "",
	shareName: "",
	ftpsMode: "None",
	status: "离线",
});

// 测试连接状态
const isTesting = ref(false);
const testResult = ref<any>(null);
const testDialogVisible = ref(false);

// 过滤后的设备列表
const filteredDevices = computed(() => {
	let result = [...devices.value];

	// 搜索
	if (searchQuery.value) {
		const query = searchQuery.value.toLowerCase();
		result = result.filter(
			(device) =>
				device.name.toLowerCase().includes(query) ||
				device.deviceCode.toLowerCase().includes(query) ||
				device.ip.includes(query),
		);
	}

	// 协议筛选
	if (filterProtocol.value) {
		result = result.filter(
			(device) => device.protocol === filterProtocol.value,
		);
	}

	// 分页
	const startIndex = (currentPage.value - 1) * pageSize.value;
	const endIndex = startIndex + pageSize.value;
	return result.slice(startIndex, endIndex);
});

// 打开新增设备对话框
const openAddDeviceDialog = () => {
	isEditing.value = false;
	Object.assign(currentDevice, {
		id: "",
		deviceCode: "",
		name: "",
		ip: "",
		port: 21,
		protocol: "FTP",
		username: "",
		password: "",
		devicePath: "",
		shareName: "",
		ftpsMode: "None",
		status: "离线",
	});
	deviceDialogVisible.value = true;
};

// 打开编辑设备对话框
const openEditDeviceDialog = (device: Device) => {
	isEditing.value = true;
	Object.assign(currentDevice, { ...device });
	deviceDialogVisible.value = true;
};

// 组装 extendedProperties（按协议补必填项）
function buildExt(d: Device): Record<string, string> {
	const ext: Record<string, string> = {};
	if (d.deviceCode) ext.deviceCode = d.deviceCode;
	if (d.devicePath) ext.devicePath = d.devicePath;
	if (d.protocol === "SMB" && d.shareName) ext.ShareName = d.shareName;
	if (d.protocol === "FTP" && d.ftpsMode && d.ftpsMode !== "None")
		ext.EncryptionMode = d.ftpsMode;
	return ext;
}

// 保存设备 = 真实创建/更新（后端 type=CNC + 文件传输协议）
const saveDevice = async () => {
	if (
		!currentDevice.deviceCode ||
		!currentDevice.name ||
		!currentDevice.ip ||
		!currentDevice.port ||
		!currentDevice.protocol
	) {
		ElMessage.warning("请填写必填字段");
		return;
	}
	if (currentDevice.protocol === "SMB" && !currentDevice.shareName) {
		ElMessage.warning("SMB 协议必须填写共享名（ShareName）");
		return;
	}

	const body: CreateDeviceRequest = {
		name: currentDevice.name,
		type: "CNC",
		brand: currentDevice.protocol,
		model: currentDevice.deviceCode,
		protocol: currentDevice.protocol,
		host: currentDevice.ip,
		port: currentDevice.port,
		username: currentDevice.username || null,
		password: currentDevice.password || null,
		extendedProperties: buildExt(currentDevice),
	};

	try {
		if (isEditing.value) {
			await machineConnectionDevicesApi.update(currentDevice.id, body);
			ElMessage.success("设备编辑成功");
		} else {
			await machineConnectionDevicesApi.create(body);
			ElMessage.success("设备添加成功");
		}
		deviceDialogVisible.value = false;
		await loadDevices();
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "保存设备失败"));
	}
};

// 删除设备 = 真实删除
const deleteDevice = (id: string) => {
	ElMessageBox.confirm("确定要删除该设备吗？", "警告", {
		confirmButtonText: "确定",
		cancelButtonText: "取消",
		type: "warning",
	})
		.then(async () => {
			try {
				await machineConnectionDevicesApi.remove(id);
				ElMessage.success("设备删除成功");
				await loadDevices();
			} catch (e: unknown) {
				ElMessage.error(getErr(e, "删除失败"));
			}
		})
		.catch(() => {
			// 取消删除
		});
};

// 刷新设备列表 = 重新从后端加载
const refreshDeviceList = () => {
	void loadDevices();
};

// 获取状态类型
const getStatusType = (status: string) => {
	switch (status) {
		case "在线":
			return "success";
		case "离线":
			return "danger";
		default:
			return "info";
	}
};

// 测试连接 = 调后端真实连通性测试（需设备已保存）
const testConnection = async () => {
	if (!isEditing.value || !currentDevice.id) {
		ElMessage.warning("请先保存设备，再测试连接（后端按设备 ID 建连）");
		return;
	}

	testDialogVisible.value = true;
	isTesting.value = true;
	testResult.value = null;

	const startTime = Date.now();
	try {
		const r = await machineConnectionDevicesApi.testConnection(currentDevice.id);
		testResult.value = {
			success: r.success,
			message: r.success
				? `成功连接到 ${currentDevice.ip}:${currentDevice.port}`
				: (r.errorMessage ?? `无法连接到 ${currentDevice.ip}:${currentDevice.port}`),
			details: {
				address: currentDevice.ip,
				port: currentDevice.port,
				protocol: currentDevice.protocol,
				ftpMode: undefined,
				responseTime: r.latency ?? `${Date.now() - startTime}`,
			},
		};
	} catch (e: unknown) {
		testResult.value = {
			success: false,
			message: getErr(e, "连接测试失败"),
		};
	} finally {
		isTesting.value = false;
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
	void loadDevices();
});
</script>

<style lang="scss" scoped>
.transfer-device-view {
	.device-list-card {
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

	.test-connection {
		padding: 20px;
	}

	.testing {
		display: flex;
		align-items: center;
		justify-content: center;
		padding: 40px 0;
	}

	.test-result {
		margin-top: 20px;
	}

	.test-details {
		margin-top: 20px;
	}

	.test-details h4 {
		margin-top: 0;
		margin-bottom: 15px;
	}
}
</style>
