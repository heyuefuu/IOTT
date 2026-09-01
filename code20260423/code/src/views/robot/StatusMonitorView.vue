<template>
	<div class="robot-status-monitor-view">
		<h2 class="page-title">机器人状态监控</h2>

		<el-card class="status-card">
			<template #header>
				<div class="card-header">
					<span>状态监控</span>
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
				<!-- 状态概览 -->
				<div class="status-overview">
					<el-row :gutter="20">
						<el-col :span="6">
							<el-statistic
								:title="'总设备数'"
								:value="robots.length"
							/>
						</el-col>
						<el-col :span="6">
							<el-statistic
								:title="'运行中'"
								:value="runningRobotsCount"
							/>
						</el-col>
						<el-col :span="6">
							<el-statistic
								:title="'待机中'"
								:value="idleRobotsCount"
							/>
						</el-col>
						<el-col :span="6">
							<el-statistic
								:title="'故障'"
								:value="errorRobotsCount"
							/>
						</el-col>
					</el-row>
				</div>

				<!-- 机器人状态列表 -->
				<div class="robot-status-list">
					<h3>机器人状态详情</h3>
					<el-table :data="robots" style="width: 100%" border>
						<el-table-column
							prop="id"
							label="机器人ID"
							width="100"
						/>
						<el-table-column prop="name" label="机器人名称" />
						<el-table-column
							prop="model"
							label="型号"
							width="120"
						/>
						<el-table-column prop="status" label="状态" width="100">
							<template #default="scope">
								<el-tag :type="getStatusType(scope.row.status)">
									{{ scope.row.status }}
								</el-tag>
							</template>
						</el-table-column>
						<el-table-column
							prop="latency"
							label="连接延迟"
							width="110"
						>
							<template #default="scope">
								{{ scope.row.latency || "-" }}
							</template>
						</el-table-column>
						<el-table-column
							prop="errorMessage"
							label="错误信息"
							min-width="140"
							show-overflow-tooltip
						>
							<template #default="scope">
								{{ scope.row.errorMessage || "-" }}
							</template>
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

		<!-- 机器人详情对话框 -->
		<el-dialog
			v-model="detailDialogVisible"
			:title="`机器人详情: ${selectedRobot?.name || ''}`"
			width="700px"
		>
			<div v-if="selectedRobot" class="robot-detail">
				<el-row :gutter="20">
					<el-col :span="12">
						<el-form :model="selectedRobot" label-width="100px">
							<el-form-item label="机器人ID">
								<el-input v-model="selectedRobot.id" disabled />
							</el-form-item>
							<el-form-item label="机器人名称">
								<el-input
									v-model="selectedRobot.name"
									disabled
								/>
							</el-form-item>
							<el-form-item label="型号">
								<el-input
									v-model="selectedRobot.model"
									disabled
								/>
							</el-form-item>
							<el-form-item label="状态">
								<el-tag
									:type="getStatusType(selectedRobot.status)"
								>
									{{ selectedRobot.status }}
								</el-tag>
							</el-form-item>
							<el-form-item label="连接延迟">
								<el-input v-model="selectedRobot.latency" disabled />
							</el-form-item>
							<el-form-item label="错误信息">
								<el-input
									v-model="selectedRobot.errorMessage"
									disabled
								/>
							</el-form-item>
						</el-form>
					</el-col>
					<el-col :span="12">
						<h4>最近状态记录</h4>
						<el-timeline>
							<el-timeline-item
								v-for="(log, index) in selectedRobot.statusLogs"
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

interface Robot {
	id: string;
	name: string;
	model: string;
	status: string;
	protocol: string;
	errorMessage: string;
	latency: string;
	statusLogs: Array<{ time: string; message: string }>;
	lastUpdate: string;
}

const robots = ref<Robot[]>([]);
const isMonitoring = ref(false);
const monitoringInterval = ref<number | null>(null);
const detailDialogVisible = ref(false);
const selectedRobot = ref<Robot | null>(null);

const nowText = () => new Date().toLocaleString();
const mapStatus = (status: string) => {
	if (status === "Online") return "运行中";
	if (status === "Error") return "故障";
	return "待机中";
};
const toRobot = (device: DeviceDto): Robot => ({
	id: device.id,
	name: device.name,
	model: device.model,
	status: mapStatus(device.status),
	protocol: device.protocol,
	errorMessage: "",
	latency: "",
	statusLogs: [{ time: nowText(), message: `后端状态：${device.status}` }],
	lastUpdate: device.lastSeenAt || device.createdAt || nowText(),
});

const loadRobots = async () => {
	try {
		const list = await machineConnectionDevicesApi.list("Robot");
		robots.value = list.map(toRobot);
	} catch (error) {
		ElMessage.error(error instanceof Error ? error.message : "加载机器人设备失败");
	}
};

const runningRobotsCount = computed(() => robots.value.filter((r) => r.status === "运行中").length);
const idleRobotsCount = computed(() => robots.value.filter((r) => r.status === "待机中").length);
const errorRobotsCount = computed(() => robots.value.filter((r) => r.status === "故障").length);

const getStatusType = (status: string) => {
	switch (status) {
		case "运行中": return "success";
		case "待机中": return "info";
		case "故障": return "danger";
		default: return "warning";
	}
};

const refreshConnectionStatus = async () => {
	await Promise.all(robots.value.map(async (robot) => {
		try {
			const result = await machineConnectionDevicesApi.testConnection(robot.id);
			robot.status = result.success ? "运行中" : "故障";
			robot.errorMessage = result.errorMessage || "";
			robot.latency = result.latency ?? "";
			robot.lastUpdate = nowText();
			robot.statusLogs.unshift({
				time: robot.lastUpdate,
				message: result.success ? "连接测试成功" : result.errorMessage || "连接测试失败",
			});
			robot.statusLogs = robot.statusLogs.slice(0, 5);
		} catch (error) {
			robot.status = "故障";
			robot.errorMessage = error instanceof Error ? error.message : "连接测试失败";
			robot.lastUpdate = nowText();
			robot.statusLogs.unshift({ time: robot.lastUpdate, message: robot.errorMessage });
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
	ElMessage.success("已开始按真实连接状态监控机器人设备");
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
const viewDetails = (robot: Robot) => {
	selectedRobot.value = JSON.parse(JSON.stringify(robot));
	detailDialogVisible.value = true;
};

onMounted(loadRobots);
onUnmounted(() => {
	if (monitoringInterval.value) clearInterval(monitoringInterval.value);
});
</script>

<style lang="scss" scoped>
.robot-status-monitor-view {
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

	.robot-status-list {
		margin-bottom: 30px;
		width: 100%;
	}

	.robot-status-list .el-table {
		width: 100% !important;
	}

	.robot-detail {
		padding: 20px;
	}
}
</style>
