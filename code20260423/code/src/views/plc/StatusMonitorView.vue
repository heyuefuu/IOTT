<template>
	<div class="plc-status-monitor-view">
		<h2 class="page-title">PLC状态监控</h2>

		<el-card class="status-card">
			<template #header>
				<div class="card-header">
					<span>设备状态监控</span>
					<el-button type="primary" @click="startMonitoring">
						<el-icon><VideoCamera /></el-icon>
						开始监控
					</el-button>
					<el-button
						@click="stopMonitoring"
						style="margin-left: 10px"
					>
						<el-icon><VideoCamera /></el-icon>
						停止监控
					</el-button>
				</div>
			</template>

			<div class="status-content">
				<!-- 设备状态概览 -->
				<div class="status-overview">
					<el-row :gutter="20">
						<el-col :span="6">
							<el-statistic
								:title="'总设备数'"
								:value="devices.length"
							/>
						</el-col>
						<el-col :span="6">
							<el-statistic
								:title="'在线设备'"
								:value="onlineDevicesCount"
							/>
						</el-col>
						<el-col :span="6">
							<el-statistic
								:title="'离线设备'"
								:value="offlineDevicesCount"
							/>
						</el-col>
						<el-col :span="6">
							<el-statistic
								:title="'告警设备'"
								:value="alarmDevicesCount"
							/>
						</el-col>
					</el-row>
				</div>

				<!-- 设备状态列表 -->
				<div class="status-list">
					<h3>设备状态详情</h3>
					<el-table :data="devices" style="width: 100%" border>
						<el-table-column prop="id" label="设备ID" width="100" />
						<el-table-column prop="name" label="设备名称" />
						<el-table-column prop="ip" label="IP地址" width="150" />
						<el-table-column prop="status" label="状态" width="100">
							<template #default="scope">
								<el-tag :type="getStatusType(scope.row.status)">
									{{ scope.row.status }}
								</el-tag>
							</template>
						</el-table-column>
						<el-table-column prop="latency" label="连接延迟" width="120">
							<template #default="scope">{{ scope.row.latency || "-" }}</template>
						</el-table-column>
						<el-table-column
							prop="lastUpdate"
							label="最后更新"
							width="180"
						/>
						<el-table-column label="操作" width="150">
							<template #default="scope">
								<el-button
									type="primary"
									size="small"
									@click="viewDetails(scope.row)"
								>
									查看详情
								</el-button>
							</template>
						</el-table-column>
					</el-table>
				</div>

			</div>
		</el-card>

		<!-- 设备详情对话框 -->
		<el-dialog
			v-model="detailDialogVisible"
			:title="`设备详情: ${selectedDevice?.name || ''}`"
			width="700px"
		>
			<div v-if="selectedDevice" class="device-detail">
				<el-row :gutter="20">
					<el-col :span="12">
						<el-form :model="selectedDevice" label-width="100px">
							<el-form-item label="设备ID">
								<el-input
									v-model="selectedDevice.id"
									disabled
								/>
							</el-form-item>
							<el-form-item label="设备名称">
								<el-input
									v-model="selectedDevice.name"
									disabled
								/>
							</el-form-item>
							<el-form-item label="IP地址">
								<el-input
									v-model="selectedDevice.ip"
									disabled
								/>
							</el-form-item>
							<el-form-item label="状态">
								<el-tag
									:type="getStatusType(selectedDevice.status)"
								>
									{{ selectedDevice.status }}
								</el-tag>
							</el-form-item>
						</el-form>
					</el-col>
					<el-col :span="12">
						<h4>最近5次状态更新</h4>
						<el-timeline>
							<el-timeline-item
								v-for="(
									log, index
								) in selectedDevice.statusLogs"
								:key="index"
								:timestamp="log.time"
							>
								{{ log.message }}
							</el-timeline-item>
						</el-timeline>
					</el-col>
				</el-row>
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
import { ref, computed, onMounted, onUnmounted } from "vue";
import { VideoCamera } from "@element-plus/icons-vue";
import { ElMessage } from "element-plus";
import { machineConnectionDevicesApi, type DeviceDto } from "@/api/machineConnectionDevices";

interface Device {
	id: string;
	name: string;
	ip: string;
	status: string;
	latency: string;
	lastUpdate: string;
	statusLogs: Array<{ time: string; message: string }>;
}

const devices = ref<Device[]>([]);
const isMonitoring = ref(false);
const monitoringInterval = ref<number | null>(null);
const detailDialogVisible = ref(false);
const selectedDevice = ref<Device | null>(null);

const nowText = () => new Date().toLocaleString();
const mapStatus = (status: string) => {
	if (status === "Online") return "在线";
	if (status === "Error") return "告警";
	return "离线";
};
const toDevice = (device: DeviceDto): Device => ({
	id: device.id,
	name: device.name,
	ip: device.host,
	status: mapStatus(device.status),
	latency: "",
	lastUpdate: device.lastSeenAt || device.createdAt || nowText(),
	statusLogs: [
		{ time: nowText(), message: `后端状态：${device.status}` },
	],
});

const loadDevices = async () => {
	try {
		const list = await machineConnectionDevicesApi.list("PLC");
		devices.value = list.map(toDevice);
	} catch (error) {
		ElMessage.error(error instanceof Error ? error.message : "加载 PLC 设备失败");
	}
};

const onlineDevicesCount = computed(() => devices.value.filter((d) => d.status === "在线").length);
const offlineDevicesCount = computed(() => devices.value.filter((d) => d.status === "离线").length);
const alarmDevicesCount = computed(() => devices.value.filter((d) => d.status === "告警").length);

const getStatusType = (status: string) => {
	switch (status) {
		case "在线": return "success";
		case "离线": return "danger";
		case "告警": return "warning";
		default: return "info";
	}
};

const refreshConnectionStatus = async () => {
	await Promise.all(devices.value.map(async (device) => {
		try {
			const result = await machineConnectionDevicesApi.testConnection(device.id);
			device.status = result.success ? "在线" : "告警";
			device.latency = result.latency ?? "";
			device.lastUpdate = nowText();
			device.statusLogs.unshift({
				time: device.lastUpdate,
				message: result.success ? "连接测试成功" : result.errorMessage || "连接测试失败",
			});
			device.statusLogs = device.statusLogs.slice(0, 5);
		} catch (error) {
			device.status = "告警";
			device.lastUpdate = nowText();
			device.statusLogs.unshift({
				time: device.lastUpdate,
				message: error instanceof Error ? error.message : "连接测试失败",
			});
		}
	}));
};

const startMonitoring = async () => {
	if (isMonitoring.value) {
		ElMessage.warning("监控已在运行中");
		return;
	}
	isMonitoring.value = true;
	await refreshConnectionStatus();
	monitoringInterval.value = window.setInterval(refreshConnectionStatus, 10000);
	ElMessage.success("已开始按真实连接状态监控 PLC 设备");
};

const stopMonitoring = () => {
	if (!isMonitoring.value) {
		ElMessage.warning("监控未运行");
		return;
	}
	if (monitoringInterval.value) clearInterval(monitoringInterval.value);
	monitoringInterval.value = null;
	isMonitoring.value = false;
	ElMessage.success("已停止监控");
};

const viewDetails = (device: Device) => {
	selectedDevice.value = { ...device, statusLogs: [...device.statusLogs] };
	detailDialogVisible.value = true;
};

onMounted(loadDevices);
onUnmounted(() => {
	if (monitoringInterval.value) clearInterval(monitoringInterval.value);
});
</script>

<style lang="scss" scoped>
.plc-status-monitor-view {
	.status-card {
		margin-bottom: 20px;
	}

	.card-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
	}

	.status-overview {
		margin-bottom: 30px;
	}

	.status-list {
		margin-bottom: 30px;
		width: 100%;
	}

	.status-list .el-table {
		width: 100% !important;
	}

	.realtime-chart {
		margin-top: 30px;
	}

	.chart-container {
		display: flex;
		flex-wrap: wrap;
		gap: 20px;
		margin-top: 20px;
	}

	.device-chart-card {
		flex: 1;
		min-width: 300px;
	}

	.chart-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
	}

	.chart-content {
		padding: 20px;
	}

	.device-detail {
		padding: 20px;
	}
}
</style>
