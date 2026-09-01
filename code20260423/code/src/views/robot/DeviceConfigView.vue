<template>
	<div class="robot-device-config-view">
		<h2 class="page-title">机器人设备管理</h2>

		<div class="device-layout">
			<el-card class="tree-panel" shadow="never">
				<template #header>
					<div class="tree-title">设备</div>
				</template>
				<el-input v-model="treeKeyword" placeholder="按设备名称或制造商搜索" class="tree-search" />
				<el-tree ref="treeRef" :data="deviceTree" node-key="id" :default-expanded-keys="['all']"
					:expand-on-click-node="false" :filter-node-method="filterTreeNode" @node-click="handleTreeNodeClick" />
			</el-card>

			<div>
				<div class="action-bar">
					<el-button @click="loadDevices">刷新列表</el-button>
					<el-button type="primary" @click="openAddDeviceDialog">
						<el-icon><Plus /></el-icon>
						新增设备
					</el-button>
					<el-input v-model="searchQuery" placeholder="搜索设备名称/编号/IP/协议"
						style="width: 300px; margin-left: auto" prefix-icon="Search" />
				</div>

				<div v-if="filteredDevices.length === 0" class="empty-hint">暂无机器人设备。</div>
				<div class="device-cards">
					<el-card v-for="device in filteredDevices" :key="device.id" :body-style="{ padding: '20px' }"
						class="device-card">
						<div class="card-header">
							<div class="device-info">
								<el-tag size="small" type="info">{{ device.status || "离线" }}</el-tag>
								<h3 class="device-name">{{ device.name }}</h3>
								<p class="device-code">设备编号：{{ device.deviceCode || "-" }} | {{ device.model || "-" }}</p>
							</div>
							<el-tag :type="getStatusType(device.status)" effect="dark">
								{{ device.status || "离线" }}
							</el-tag>
						</div>
						<div class="card-body">
							<el-descriptions :column="2" size="small">
								<el-descriptions-item label="协议类型">{{ device.protocol }}</el-descriptions-item>
								<el-descriptions-item label="IP地址">{{ device.ip }}</el-descriptions-item>
								<el-descriptions-item label="端口">{{ device.port }}</el-descriptions-item>
								<el-descriptions-item label="制造商">{{ device.manufacturer || "-" }}</el-descriptions-item>
							</el-descriptions>
						</div>
						<div class="card-footer">
							<div class="card-footer-row">
								<el-button type="success" link size="small" @click="testConnection(device)">连接测试</el-button>
								<el-button type="primary" link size="small" @click="goTo('/robot/data')">数据浏览</el-button>
								<el-button type="warning" link size="small" @click="goTo('/robot/rw')">数据读写</el-button>
								<el-button type="info" link size="small" @click="goTo('/robot/status')">状态监控</el-button>
								<el-button
									v-if="usesModbusStation(device.protocol)"
									type="warning"
									link
									size="small"
									@click="goTo('/robot/modbus')"
								>Modbus寄存器</el-button>
								<el-button v-if="device.protocol === 'EstunRobot'" type="primary" link size="small"
									@click="goTo('/robot/estun')">埃斯顿面板</el-button>
							</div>
							<div class="card-footer-row">
								<el-button type="primary" link size="small" @click="openEditDeviceDialog(device)">编辑</el-button>
								<el-button type="danger" link size="small" @click="deleteDevice(device.id)">删除</el-button>
							</div>
						</div>
					</el-card>
				</div>

				<div class="pagination-bar">
					<el-pagination v-model:current-page="currentPage" v-model:page-size="pageSize"
						:page-sizes="[10, 20, 50, 100]" layout="total, sizes, prev, pager, next, jumper"
						:total="filteredTotal" @size-change="handleSizeChange" @current-change="handleCurrentChange" />
				</div>
			</div>
		</div>

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
						<el-option label="Modbus TCP" value="ModbusTCP" />
						<el-option label="FANUC机器人" value="FanucRobot" />
						<el-option label="华中机器人" value="HuazhongRobot" />
						<el-option label="埃斯顿机器人" value="EstunRobot" />
					</el-select>
				</el-form-item>
				<el-form-item
					v-if="usesModbusStation(currentDevice.protocol)"
					label="Modbus站号"
					prop="station"
				>
					<el-input-number v-model="currentDevice.station" :min="1" :max="255" :step="1"
						style="width: 200px" />
					<span class="field-hint">控制器 Modbus/TCP 从站号，默认 1</span>
				</el-form-item>
				<el-form-item label="机器人型号" prop="model" required>
					<el-input
						v-model="currentDevice.model"
						placeholder="请输入机器人型号"
					/>
				</el-form-item>
				<el-form-item label="制造商" prop="manufacturer">
					<el-select
						v-model="currentDevice.manufacturer"
						placeholder="请选择制造商"
					>
						<el-option label="ABB" value="ABB" />
						<el-option label="KUKA" value="KUKA" />
						<el-option label="FANUC" value="FANUC" />
						<el-option label="YASKAWA" value="YASKAWA" />
						<el-option label="ESTUN（埃斯顿）" value="ESTUN" />
						<el-option label="其他" value="Other" />
					</el-select>
				</el-form-item>
				<el-form-item label="描述" prop="description">
					<el-input
						v-model="currentDevice.description"
						type="textarea"
						placeholder="请输入设备描述"
					/>
				</el-form-item>
			</el-form>
			<template #footer>
				<span class="dialog-footer">
					<el-button @click="deviceDialogVisible = false"
						>取消</el-button
					>
					<el-button type="primary" @click="saveDevice"
						>保存</el-button
					>
				</span>
			</template>
		</el-dialog>

		<!-- 连接测试对话框 -->
		<el-dialog v-model="testDialogVisible" title="连接测试" width="400px">
			<div class="test-connection">
				<div v-if="isTesting" class="testing">
					<el-icon class="is-loading"><Loading /></el-icon>
					<span style="margin-left: 10px">正在测试连接...</span>
				</div>
				<div v-else-if="testResult" class="test-result">
					<el-alert
						:title="testResult.success ? '连接成功' : '连接失败'"
						:description="testResult.message"
						:type="testResult.success ? 'success' : 'error'"
						show-icon
					/>
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
import { ref, reactive, computed, onMounted, watch } from "vue";
import { useRouter } from "vue-router";
import { Plus, Loading } from "@element-plus/icons-vue";
import { ElMessage, ElMessageBox } from "element-plus";
import {
	machineConnectionDevicesApi,
	type DeviceDto,
} from "@/api/machineConnectionDevices";

// 设备类型定义
interface Device {
	id: string;
	deviceCode: string;
	name: string;
	ip: string;
	port: number;
	protocol: string;
	model: string;
	manufacturer: string;
	description: string;
	status: string;
	/** 埃斯顿等 Modbus 系机器人的从站号，落到 extendedProperties.Station */
	station: number;
}

// 设备列表（来自后端 /api/devices?type=Robot）
const devices = ref<Device[]>([]);
const router = useRouter();

const goTo = (path: string) => {
	void router.push(path);
};

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

// 后端 DeviceDto → 页面 Device（制造商=brand，编号/描述存 extendedProperties）
function mapToRobot(d: DeviceDto): Device {
	return {
		id: d.id,
		deviceCode: d.extendedProperties?.deviceCode ?? d.model ?? "",
		name: d.name,
		ip: d.host,
		port: d.port,
		protocol: d.protocol,
		model: d.model,
		manufacturer: d.brand,
		description: d.extendedProperties?.description ?? "",
		status: d.status,
		station: Number(d.extendedProperties?.Station ?? 1) || 1,
	};
}

const loadDevices = async () => {
	try {
		const list = await machineConnectionDevicesApi.list("Robot");
		devices.value = list.map(mapToRobot);
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载设备列表失败"));
	}
};

// 分页
const currentPage = ref(1);
const pageSize = ref(10);
const searchQuery = ref("");
const treeKeyword = ref("");
const selectedTreeNodeId = ref("all");
const treeRef = ref();

const deviceTree = [
	{
		id: "all",
		label: "全部设备",
		children: [
			{ id: "maker-ABB", label: "ABB" },
			{ id: "maker-KUKA", label: "KUKA" },
			{ id: "maker-FANUC", label: "FANUC" },
			{ id: "maker-YASKAWA", label: "YASKAWA" },
			{ id: "maker-ESTUN", label: "ESTUN（埃斯顿）" },
			{ id: "maker-Other", label: "其他" },
		],
	},
];

const filteredSourceDevices = computed(() => {
	let result = [...devices.value];
	if (selectedTreeNodeId.value.startsWith("maker-")) {
		const maker = selectedTreeNodeId.value.replace("maker-", "");
		result = result.filter((device) => device.manufacturer === maker);
	}
	if (searchQuery.value) {
		const query = searchQuery.value.toLowerCase();
		result = result.filter(
			(device) =>
				device.name.toLowerCase().includes(query) ||
				device.deviceCode.toLowerCase().includes(query) ||
				device.ip.includes(query) ||
				device.protocol.toLowerCase().includes(query),
		);
	}
	return result;
});

const filteredTotal = computed(() => filteredSourceDevices.value.length);
const filteredDevices = computed(() => {
	const startIndex = (currentPage.value - 1) * pageSize.value;
	return filteredSourceDevices.value.slice(startIndex, startIndex + pageSize.value);
});

const handleTreeNodeClick = (data: { id: string }) => {
	selectedTreeNodeId.value = data.id;
	currentPage.value = 1;
};

watch(treeKeyword, (value) => {
	treeRef.value?.filter(value);
});

const filterTreeNode = (value: string, data: { label: string }) => {
	if (!value) return true;
	return data.label.includes(value);
};

// 设备对话框
const deviceDialogVisible = ref(false);
const isEditing = ref(false);
const currentDevice = reactive<Device>({
	id: "",
	deviceCode: "",
	name: "",
	ip: "",
	port: 502,
	protocol: "ModbusTCP",
	model: "",
	manufacturer: "",
	description: "",
	status: "离线",
	station: 1,
});

// 测试连接
const testDialogVisible = ref(false);
const isTesting = ref(false);
const testResult = ref<any>(null);

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

// 打开新增设备对话框
const openAddDeviceDialog = () => {
	isEditing.value = false;
	Object.assign(currentDevice, {
		id: "",
		deviceCode: "",
		name: "",
		ip: "",
		port: 502,
		protocol: "ModbusTCP",
		model: "",
		manufacturer: "",
		description: "",
		status: "离线",
		station: 1,
	});
	deviceDialogVisible.value = true;
};

// 打开编辑设备对话框
const openEditDeviceDialog = (device: Device) => {
	isEditing.value = true;
	Object.assign(currentDevice, { ...device });
	deviceDialogVisible.value = true;
};

/**
 * 决定发给后端的 brand。后端 DriverRegistry.Resolve 顺序是 model → brand → "*" 通配，
 * 而机器人各协议注册时都没有 "*"（EstunRobot 只认 埃斯顿/ESTUN/ER/ProNet，
 * HuazhongRobot 只认 华中数控/华中机器人/HSR/HR/HC/HNC-Robot），
 * 直接把「制造商」下拉的值当 brand 送出去，一旦用户没选或选了"其他"，
 * brand 会退化成协议名而匹配不上任何注册项 → No driver registered。
 * 这些协议本身就是厂商专属的，故按协议钉死规范品牌；只有 ModbusTCP 是厂商无关（且注册了 "*"），沿用制造商。
 */
function resolveBrand(protocol: string, manufacturer: string): string {
	const canonical: Record<string, string> = {
		EstunRobot: "ESTUN",
		FanucRobot: "FANUC",
		HuazhongRobot: "华中数控",
	};
	return canonical[protocol] ?? (manufacturer || protocol);
}

/** 底层走 Modbus/TCP、需要从站号的协议。ModbusTCP 是通用 Modbus 主站，
 * 埃斯顿控制器本身就是 Modbus/TCP 服务端，两者都要填站号。 */
function usesModbusStation(protocol: string): boolean {
	return protocol === "EstunRobot" || protocol === "ModbusTCP";
}

// 保存设备 = 真实创建/更新
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
	// 后端 Model 是 NotEmpty 硬校验，留空会「网关存下了但同步上游失败」——
	// 界面上设备照常显示，可读写却因上游没有该设备而 404
	if (!currentDevice.model) {
		ElMessage.warning("请填写机器人型号");
		return;
	}

	const ext: Record<string, string> = {};
	if (currentDevice.deviceCode) ext.deviceCode = currentDevice.deviceCode;
	if (currentDevice.description) ext.description = currentDevice.description;
	// 从站号：EstunRobot 驱动读 ExtendedProperties["Station"]，
	// ModbusTcpDriver 读 "UnitId"（也兼容 "Station"）。两种协议都需要，缺省 1。
	if (usesModbusStation(currentDevice.protocol)) {
		ext.Station = String(currentDevice.station || 1);
	}

	const brand = resolveBrand(currentDevice.protocol, currentDevice.manufacturer);

	try {
		if (isEditing.value) {
			const saved = await machineConnectionDevicesApi.update(currentDevice.id, {
				name: currentDevice.name,
				brand,
				model: currentDevice.model ?? "",
				protocol: currentDevice.protocol,
				host: currentDevice.ip,
				port: currentDevice.port,
				extendedProperties: ext,
			});
			if (saved.upstreamSynced === false)
				ElMessage.warning(`已保存，但同步采集服务失败：${saved.upstreamError ?? "上游不可用"}`);
			else ElMessage.success("设备编辑成功");
		} else {
			const saved = await machineConnectionDevicesApi.create({
				name: currentDevice.name,
				type: "Robot",
				brand,
				model: currentDevice.model ?? "",
				protocol: currentDevice.protocol,
				host: currentDevice.ip,
				port: currentDevice.port,
				extendedProperties: ext,
			});
			if (saved.upstreamSynced === false)
				ElMessage.warning(`设备已添加，但同步采集服务失败：${saved.upstreamError ?? "上游不可用"}`);
			else ElMessage.success("设备添加成功");
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

// 测试连接 = 调后端真实连通性测试
const testConnection = async (device: Device) => {
	testDialogVisible.value = true;
	isTesting.value = true;
	testResult.value = null;

	try {
		const r = await machineConnectionDevicesApi.testConnection(device.id);
		testResult.value = {
			success: r.success,
			message: r.success
				? `成功连接到设备: ${device.name}`
				: (r.errorMessage ?? `无法连接到设备: ${device.name}`),
		};
	} catch (e: unknown) {
		testResult.value = { success: false, message: getErr(e, "连接测试失败") };
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
.robot-device-config-view {
	.device-layout {
		display: grid;
		grid-template-columns: 260px 1fr;
		gap: 16px;
	}

	.tree-panel {
		.tree-title {
			font-weight: 600;
		}

		.tree-search {
			margin-bottom: 10px;
		}
	}

	.action-bar {
		display: flex;
		align-items: center;
		margin-bottom: 20px;
		gap: 10px;
	}

	.device-cards {
		display: grid;
		grid-template-columns: repeat(auto-fill, minmax(400px, 1fr));
		gap: 20px;
		margin-bottom: 20px;
	}

	.device-card {
		border-radius: 8px;
		box-shadow: 0 2px 12px 0 rgba(0, 0, 0, 0.1);
	}

	.card-header {
		display: flex;
		justify-content: space-between;
		align-items: flex-start;
		margin-bottom: 15px;
		padding-bottom: 10px;
		border-bottom: 1px solid var(--el-border-color);
	}

	.device-name {
		margin: 4px 0 5px;
		font-size: 18px;
		font-weight: 600;
	}

	.device-code {
		margin: 0;
		font-size: 14px;
		color: var(--el-text-color-secondary);
	}

	.card-body {
		margin-bottom: 15px;
	}

	.card-footer {
		display: flex;
		flex-direction: column;
		align-items: flex-start;
		gap: 4px;
		padding-top: 15px;
		border-top: 1px solid var(--el-border-color);
	}

	.card-footer-row {
		display: flex;
		flex-wrap: wrap;
		gap: 10px;
	}

	.empty-hint {
		margin-bottom: 16px;
		color: var(--el-text-color-secondary);
	}

	.field-hint {
		margin-left: 10px;
		font-size: 12px;
		color: var(--el-text-color-secondary);
	}
}

@media (max-width: 1200px) {
	.robot-device-config-view .device-layout {
		grid-template-columns: 1fr;
	}
}
</style>
