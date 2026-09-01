<template>
	<div class="home-view-content">
		<!-- 页面头部 -->
		<div class="page-header">
			<div class="header-title">
				<h1>机床验证系统</h1>
				<p class="header-subtitle">实时监控与管理平台</p>
			</div>
			<div class="header-actions">
				<el-button
					type="primary"
					@click="handleRefresh"
					:icon="Refresh"
				>
					刷新数据
				</el-button>
				<el-button @click="handleQuickConfig" :icon="Setting">
					快速配置
				</el-button>
				<el-dropdown>
					<el-button :icon="More">
						更多<el-icon class="el-icon--right"
							><ArrowDown
						/></el-icon>
					</el-button>
					<template #dropdown>
						<el-dropdown-menu>
							<el-dropdown-item @click="navigateTo('/collection/manage')">
								采集任务管理
							</el-dropdown-item>
							<el-dropdown-item @click="navigateTo('/transfer/browser')">
								程序传输
							</el-dropdown-item>
							<el-dropdown-item @click="navigateTo('/system/log')">
								系统日志
							</el-dropdown-item>
						</el-dropdown-menu>
					</template>
				</el-dropdown>
			</div>
		</div>

		<!-- 系统概览 -->
		<div class="overview-section">
			<el-row :gutter="20">
				<el-col :xs="24" :sm="12" :md="8" :lg="6">
					<el-card shadow="hover" class="overview-card">
						<template #header>
							<div class="overview-card-header">
								<el-icon><Monitor /></el-icon>
								<span>总设备数</span>
							</div>
						</template>
						<div class="overview-card-content">
							<div class="overview-value">{{ totalDevices }}</div>
							<div class="overview-desc">
								在线: {{ onlineDevices }} / 离线:
								{{ offlineDevices }}
							</div>
						</div>
					</el-card>
				</el-col>
				<el-col :xs="24" :sm="12" :md="8" :lg="6">
					<el-card shadow="hover" class="overview-card">
						<template #header>
							<div class="overview-card-header">
								<el-icon><PieChart /></el-icon>
								<span>验证任务</span>
							</div>
						</template>
						<div class="overview-card-content">
							<div class="overview-value">{{ totalTasks }}</div>
							<div class="overview-desc">
								运行中: {{ runningTasks }} / 已完成:
								{{ completedTasks }}
							</div>
						</div>
					</el-card>
				</el-col>
				<el-col :xs="24" :sm="12" :md="8" :lg="6">
					<el-card shadow="hover" class="overview-card">
						<template #header>
							<div class="overview-card-header">
								<el-icon><Warning /></el-icon>
								<span>告警信息</span>
							</div>
						</template>
						<div class="overview-card-content">
							<div class="overview-value">{{ totalAlarms }}</div>
							<div class="overview-desc">
								严重: {{ criticalAlarms }} / 普通:
								{{ normalAlarms }}
							</div>
						</div>
					</el-card>
				</el-col>
				<el-col :xs="24" :sm="12" :md="8" :lg="6">
					<el-card shadow="hover" class="overview-card">
						<template #header>
							<div class="overview-card-header">
								<el-icon><Timer /></el-icon>
								<span>系统运行</span>
							</div>
						</template>
						<div class="overview-card-content">
							<div class="overview-value">{{ systemUptime }}</div>
							<div class="overview-desc">
								最后重启: {{ lastRestartTime }}
							</div>
						</div>
					</el-card>
				</el-col>
			</el-row>
		</div>

		<!-- 快速访问 -->
		<div class="quick-access-section">
			<el-card shadow="hover" class="quick-access-card">
				<template #header>
					<div class="section-header">
						<el-icon><Setting /></el-icon>
						<span>快速访问</span>
					</div>
				</template>
				<el-row :gutter="20">
					<el-col
						:xs="24"
						:sm="12"
						:md="8"
						:lg="6"
						v-for="(item, index) in quickAccessItems"
						:key="index"
					>
						<el-card
							shadow="hover"
							class="quick-access-item"
							@click="handleQuickAccess(item)"
						>
							<div
								class="quick-access-icon"
								:style="{ backgroundColor: item.color }"
							>
								<el-icon v-if="item.title === '设备配置'"
									><Setting
								/></el-icon>
								<el-icon v-else-if="item.title === '数据采集'"
									><Monitor
								/></el-icon>
								<el-icon v-else-if="item.title === '状态监控'"
									><Monitor
								/></el-icon>
								<el-icon v-else-if="item.title === '任务管理'"
									><PieChart
								/></el-icon>
							</div>
							<div class="quick-access-title">
								{{ item.title }}
							</div>
							<div class="quick-access-desc">
								{{ item.description }}
							</div>
						</el-card>
					</el-col>
				</el-row>
			</el-card>
		</div>

		<!-- 模块导航 -->
		<div class="modules-section">
			<el-card shadow="hover" class="modules-card">
				<template #header>
					<div class="section-header">
						<el-icon><Grid /></el-icon>
						<span>功能模块</span>
					</div>
				</template>
				<el-row :gutter="20">
					<el-col
						:xs="24"
						:sm="12"
						:md="8"
						:lg="6"
						v-for="(module, index) in modules"
						:key="index"
					>
						<el-card
							shadow="hover"
							class="module-card"
							@click="handleModuleNavigate(module)"
						>
							<template #header>
								<div class="module-card-header">
									<el-icon
										v-if="
											module.title === 'PLC管理' ||
											module.title === '机器人管理'
										"
										><Cpu
									/></el-icon>
									<el-icon
										v-else-if="
											module.title === '工业设备' ||
											module.title === '验证管理' ||
											module.title === '核心服务'
										"
										><Monitor
									/></el-icon>
									<el-icon
										v-else-if="
											module.title === '系统管理' ||
											module.title === '数据传输' ||
											module.title === '核心服务'
										"
										><Setting
									/></el-icon>
									<el-icon
										v-else-if="module.title === '数据传输'"
										><Upload
									/></el-icon>
									<el-icon
										v-else-if="module.title === '验证管理'"
										><PieChart
									/></el-icon>
									<el-icon
										v-else-if="module.title === '工具中心'"
										><Tools
									/></el-icon>
									<span>{{ module.title }}</span>
								</div>
							</template>
							<div class="module-card-content">
								<div class="module-desc">
									{{ module.description }}
								</div>
								<div class="module-features">
									<el-tag
										size="small"
										v-for="(
											feature, idx
										) in module.features"
										:key="idx"
									>
										{{ feature }}
									</el-tag>
								</div>
							</div>
						</el-card>
					</el-col>
				</el-row>
			</el-card>
		</div>

		<!-- 最近活动 -->
		<div class="activities-section">
			<el-card shadow="hover" class="activities-card">
				<template #header>
					<div class="section-header">
						<el-icon><Bell /></el-icon>
						<span>最近活动</span>
						<el-button
							link
							size="small"
							@click="handleViewAllActivities"
						>
							查看全部
						</el-button>
					</div>
				</template>
				<el-timeline v-if="recentActivities.length">
					<el-timeline-item
						v-for="(activity, index) in recentActivities"
						:key="index"
						:timestamp="activity.time"
						:type="activity.type"
					>
						<div class="activity-content">
							<div class="activity-title">
								{{ activity.title }}
							</div>
							<div class="activity-desc">
								{{ activity.description }}
							</div>
							<div class="activity-user">{{ activity.user }}</div>
						</div>
					</el-timeline-item>
				</el-timeline>
				<el-empty
					v-else
					description="暂无操作记录（设备增删改、任务执行等操作会记录在这里）"
					:image-size="60"
				/>
			</el-card>
		</div>

		<!-- 设备状态监控 -->
		<div class="devices-section">
			<el-card shadow="hover" class="devices-card">
				<template #header>
					<div class="section-header">
						<el-icon><Monitor /></el-icon>
						<span>设备状态监控</span>
						<el-button
							link
							size="small"
							@click="handleViewAllDevices"
						>
							查看全部
						</el-button>
					</div>
				</template>
				<el-row :gutter="20">
					<el-col
						:xs="24"
						:sm="12"
						:md="8"
						:lg="6"
						v-for="(device, index) in recentDevices"
						:key="index"
					>
						<DeviceCard
							:device="device"
							@config="handleDeviceConfig"
							@report="handleDeviceReport"
						/>
					</el-col>
				</el-row>
			</el-card>
		</div>
	</div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from "vue";
import { useRouter } from "vue-router";
import {
	Monitor,
	Connection,
	Warning,
	Cpu,
	Operation,
	Files,
	Document,
	Setting,
	Timer,
	Refresh,
	More,
	ArrowDown,
	PieChart,
	Grid,
	Upload,
	Tools,
	Bell,
} from "@element-plus/icons-vue";
import DeviceCard from "@/components/DeviceCard.vue";
import { machineConnectionDevicesApi, type DeviceDto } from "@/api/machineConnectionDevices";
import { machineConnectionCollectionApi } from "@/api/machineConnectionCollection";
import { businessSystemApi, type SystemLogDto } from "@/api/businessSystem";

const router = useRouter();
const devices = ref<DeviceDto[]>([]);
const collectionStatus = ref<Record<string, { isRunning: boolean; totalErrors: number }>>({});

const loadDashboard = async () => {
	const [deviceList, status, systemStatus, logs] = await Promise.all([
		machineConnectionDevicesApi.list(),
		machineConnectionCollectionApi.status().catch(() => ({})),
		businessSystemApi.getStatus().catch(() => null),
		businessSystemApi.listLogs().catch(() => []),
	]);
	devices.value = deviceList;
	collectionStatus.value = status;
	if (systemStatus) {
		systemUptime.value = systemStatus.uptime;
		lastRestartTime.value = new Date(systemStatus.startedAt).toLocaleString();
	}
	recentLogs.value = logs.slice(0, 6);
};

const totalDevices = computed(() => devices.value.length);
const onlineDevices = computed(() => devices.value.filter((d) => d.status === "Online").length);
const offlineDevices = computed(() => Math.max(totalDevices.value - onlineDevices.value, 0));
const totalTasks = computed(() => Object.keys(collectionStatus.value).length);
const activeTasks = computed(() => Object.values(collectionStatus.value).filter((s) => s.isRunning).length);
const runningTasks = activeTasks;
const completedTasks = computed(() => Math.max(totalTasks.value - activeTasks.value, 0));
const totalAlarms = computed(() =>
	devices.value.filter((d) => d.status === "Error").length +
	Object.values(collectionStatus.value).reduce((sum, s) => sum + (s.totalErrors || 0), 0),
);
const criticalAlarms = computed(() => devices.value.filter((d) => d.status === "Error").length);
const normalAlarms = computed(() => Math.max(totalAlarms.value - criticalAlarms.value, 0));
const systemUptime = ref("加载中");
const lastRestartTime = ref("加载中");

const quickAccessItems = ref([
	{ title: "设备配置", description: "PLC 真实设备配置", icon: Cpu, path: "/plc/device", color: "#409EFF" },
	{ title: "状态监控", description: "机器人真实设备状态", icon: Operation, path: "/robot/status", color: "#67C23A" },
	{ title: "数据采集", description: "CNC 设备、点位、读写和采集", icon: Monitor, path: "/industrial/device", color: "#E6A23C" },
	{ title: "任务管理", description: "连接并发能力验证", icon: Connection, path: "/parallel", color: "#F56C6C" },
]);

const modules = ref([
	{ title: "CNC 通讯", description: "设备管理、地址浏览、读写、采集与程序传输", icon: Monitor, path: "/industrial/device", status: "active", features: ["真实接口"] },
	{ title: "PLC 通讯", description: "PLC 设备配置、地址浏览和读写", icon: Cpu, path: "/plc/device", status: "active", features: ["真实接口"] },
	{ title: "机器人通讯", description: "机器人设备配置、数据浏览和读写", icon: Operation, path: "/robot/device", status: "active", features: ["真实接口"] },
	{ title: "Client/Server", description: "网关、客户端数据源和服务端管理", icon: Files, path: "/cs/gateway", status: "active", features: ["真实接口"] },
	{ title: "验证管理", description: "验证任务、指标、模板和数据可视化", icon: Document, path: "/verify/task", status: "active", features: ["任务持久化", "指标管理", "报表模板"] },
	{ title: "系统管理", description: "日志查询、导出与用户权限管理", icon: Setting, path: "/system/log", status: "active", features: ["日志审计", "权限管理"] },
]);

// 最近活动 = 后端系统操作日志（设备增删改 / 任务执行 / 服务启停等真实审计记录）
const recentLogs = ref<SystemLogDto[]>([]);
const activityTagType = (logType: string) => {
	switch (logType) {
		case "error":
			return "danger";
		case "warning":
			return "warning";
		case "operation":
			return "primary";
		default:
			return "info";
	}
};
const recentActivities = computed(() =>
	recentLogs.value.map((log) => ({
		time: new Date(log.timestamp).toLocaleString("zh-CN"),
		type: activityTagType(log.type) as "primary" | "success" | "warning" | "danger" | "info",
		title: log.action,
		description: log.detail,
		user: log.user,
	})),
);

const recentDevices = computed(() =>
	devices.value.slice(0, 6).map((device) => ({
		id: device.id,
		name: device.name,
		type: (device.type === "PLC" ? "plc" : device.type === "Robot" ? "robot" : "nc") as "nc" | "plc" | "robot",
		ip: device.host,
		port: device.port,
		protocol: device.protocol,
		status: (device.status === "Online" ? "running" : device.status === "Error" ? "alarm" : "offline") as "running" | "standby" | "alarm" | "offline",
		parameters: {
			品牌: device.brand,
			型号: device.model,
		},
		lastCommunication: device.lastSeenAt || device.createdAt,
	})),
);

const navigateTo = (path: string) => {
	router.push(path);
};
const handleRefresh = () => void loadDashboard();
const handleQuickConfig = () => router.push("/industrial/device");
const handleQuickAccess = (item: { path: string }) => navigateTo(item.path);
const handleModuleNavigate = (module: { path: string }) => navigateTo(module.path);
const handleViewAllActivities = () => router.push("/system/log");
const handleViewAllDevices = () => router.push("/industrial/device");
const handleDeviceConfig = (device: { type: string }) => {
	if (device.type === "plc") router.push("/plc/device");
	else if (device.type === "robot") router.push("/robot/device");
	else router.push("/industrial/device");
};
const handleDeviceReport = () => router.push("/verify/report");

onMounted(() => {
	void loadDashboard();
});
</script>

<style scoped lang="scss">
.home-view-content {
	padding: 20px;
	background-color: var(--el-bg-color-page);
	overflow-y: auto;
}

/* 页面头部 */
.page-header {
	margin-bottom: 30px;
	display: flex;
	justify-content: space-between;
	align-items: flex-start;
	flex-wrap: wrap;
	gap: 20px;
}

.header-title {
	flex: 1;
}

.header-title h1 {
	font-size: 28px;
	font-weight: 700;
	color: var(--el-text-color-primary);
	margin: 0 0 8px 0;
}

.header-subtitle {
	font-size: 14px;
	color: var(--el-text-color-secondary);
	margin: 0;
}

.header-actions {
	display: flex;
	gap: 10px;
	align-items: center;
}

/* 概览卡片 */
.overview-section {
	margin-bottom: 30px;
}

.overview-card {
	border-radius: var(--el-border-radius-base);
	transition: all 0.3s ease;

	&:hover {
		transform: translateY(-2px);
		box-shadow: 0 10px 20px rgba(0, 0, 0, 0.1);
	}
}

.overview-card-header {
	display: flex;
	align-items: center;
	gap: 8px;
	font-size: 14px;
	color: var(--el-text-color-secondary);
}

.overview-card-content {
	margin-top: 20px;
}

.overview-value {
	font-size: 32px;
	font-weight: 700;
	color: var(--el-text-color-primary);
	margin-bottom: 8px;
}

.overview-desc {
	font-size: 14px;
	color: var(--el-text-color-secondary);
}

/* 快速访问 */
.quick-access-section {
	margin-bottom: 30px;
}

.quick-access-card {
	:deep(.el-card__body) {
		padding: 20px;
	}
}

.quick-access-item {
	cursor: pointer;
	transition: all 0.3s ease;
	border-radius: var(--el-border-radius-base);

	&:hover {
		transform: translateY(-2px);
		box-shadow: 0 10px 20px rgba(0, 0, 0, 0.1);
	}

	:deep(.el-card__body) {
		padding: 20px;
	}
}

.quick-access-icon {
	width: 50px;
	height: 50px;
	border-radius: 50%;
	display: flex;
	align-items: center;
	justify-content: center;
	margin-bottom: 16px;

	:deep(.el-icon) {
		font-size: 24px;
		color: white;
	}
}

.quick-access-title {
	font-size: 16px;
	font-weight: 600;
	color: var(--el-text-color-primary);
	margin-bottom: 8px;
}

.quick-access-desc {
	font-size: 14px;
	color: var(--el-text-color-secondary);
}

/* 模块导航 */
.modules-section {
	margin-bottom: 30px;
}

.modules-card {
	:deep(.el-card__body) {
		padding: 20px;
	}
}

.module-card {
	cursor: pointer;
	transition: all 0.3s ease;
	border-radius: var(--el-border-radius-base);

	&:hover {
		transform: translateY(-2px);
		box-shadow: 0 10px 20px rgba(0, 0, 0, 0.1);
	}

	:deep(.el-card__body) {
		padding: 16px;
	}
}

.module-card-header {
	display: flex;
	align-items: center;
	gap: 8px;
	font-size: 16px;
	font-weight: 600;
	color: var(--el-text-color-primary);
}

.module-desc {
	font-size: 14px;
	color: var(--el-text-color-secondary);
	margin: 12px 0;
}

.module-features {
	display: flex;
	flex-wrap: wrap;
	gap: 8px;
}

/* 最近活动 */
.activities-section {
	margin-bottom: 30px;
}

.activities-card {
	:deep(.el-card__body) {
		padding: 20px;
	}
}

.activity-content {
	margin-left: 16px;
}

.activity-title {
	font-size: 14px;
	font-weight: 600;
	color: var(--el-text-color-primary);
	margin-bottom: 4px;
}

.activity-desc {
	font-size: 13px;
	color: var(--el-text-color-secondary);
	margin-bottom: 4px;
}

.activity-user {
	font-size: 12px;
	color: var(--el-text-color-tertiary);
}

/* 设备状态监控 */
.devices-section {
	margin-bottom: 30px;
}

.devices-card {
	:deep(.el-card__body) {
		padding: 20px;
	}
}

/* 分区头部 */
.section-header {
	display: flex;
	align-items: center;
	justify-content: space-between;
	font-size: 16px;
	font-weight: 600;
	color: var(--el-text-color-primary);

	:deep(.el-icon) {
		margin-right: 8px;
	}
}

/* 响应式调整 */
@media (max-width: 1200px) {
	.page-header {
		flex-direction: column;
		align-items: flex-start;
	}

	.header-actions {
		width: 100%;
		justify-content: space-between;
	}
}

@media (max-width: 768px) {
	.home-view-content {
		padding: 10px;
	}

	.header-title h1 {
		font-size: 24px;
	}

	.overview-value {
		font-size: 24px;
	}

	.overview-card,
	.quick-access-card,
	.modules-card,
	.activities-card,
	.devices-card {
		:deep(.el-card__body) {
			padding: 16px;
		}
	}
}
</style>
