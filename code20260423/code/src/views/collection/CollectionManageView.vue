<template>
	<div class="collection-manage-view">
		<h2 class="page-title">采集任务管理</h2>

		<!-- 设备与采集配置 -->
		<el-card class="profiles-card">
			<template #header>
				<div class="card-header">
					<span>采集配置（Profile）</span>
					<div>
						<el-select
							v-model="selectedDeviceId"
							placeholder="请选择设备"
							filterable
							style="width: 320px; margin-right: 10px"
							@change="handleDeviceChange"
						>
							<el-option
								v-for="d in devices"
								:key="d.id"
								:label="`${d.name}（${d.type} · ${d.protocol} · ${d.host}:${d.port}）`"
								:value="d.id"
							/>
						</el-select>
						<el-button
							type="primary"
							:loading="loadingProfiles"
							@click="loadProfiles"
						>
							<el-icon><Refresh /></el-icon>
							刷新配置
						</el-button>
					</div>
				</div>
			</template>

			<el-table
				v-if="selectedDeviceId"
				:data="profiles"
				style="width: 100%"
				border
			>
				<el-table-column prop="name" label="配置名称" min-width="160" />
				<el-table-column label="启用" width="90">
					<template #default="scope">
						<el-switch
							:model-value="scope.row.isEnabled"
							@change="toggleProfileEnabled(scope.row)"
						/>
					</template>
				</el-table-column>
				<el-table-column label="分组数" width="90">
					<template #default="scope">{{ scope.row.groups.length }}</template>
				</el-table-column>
				<el-table-column label="点位数" width="90">
					<template #default="scope">{{ countTags(scope.row) }}</template>
				</el-table-column>
				<el-table-column label="采集周期" min-width="140">
					<template #default="scope">
						{{ formatIntervals(scope.row) }}
					</template>
				</el-table-column>
				<el-table-column label="操作" width="320">
					<template #default="scope">
						<el-button
							type="success"
							size="small"
							:loading="startingProfileId === scope.row.id"
							@click="startProfile(scope.row)"
						>
							启动采集
						</el-button>
						<el-button
							size="small"
							@click="showProfileDetail(scope.row)"
						>
							详情
						</el-button>
						<el-button size="small" @click="renameProfile(scope.row)">
							重命名
						</el-button>
						<el-button
							type="danger"
							size="small"
							@click="deleteProfile(scope.row)"
						>
							删除
						</el-button>
					</template>
				</el-table-column>
			</el-table>
			<el-empty
				v-if="selectedDeviceId && !profiles.length && !loadingProfiles"
				description="该设备暂无采集配置，可在「PLC监控 → 采集配置导入」中创建或批量导入"
				:image-size="80"
			/>
			<el-empty
				v-if="!selectedDeviceId"
				description="请先选择设备"
				:image-size="80"
			/>
		</el-card>

		<!-- 活跃采集任务 -->
		<el-card class="tasks-card">
			<template #header>
				<div class="card-header">
					<span>活跃采集任务（全部设备）</span>
					<el-button
						type="primary"
						:loading="loadingTasks"
						@click="loadTasks"
					>
						<el-icon><Refresh /></el-icon>
						刷新状态
					</el-button>
				</div>
			</template>
			<el-table :data="tasks" style="width: 100%" border>
				<el-table-column prop="taskId" label="任务ID" min-width="220" show-overflow-tooltip />
				<el-table-column label="设备" min-width="160">
					<template #default="scope">
						{{ deviceName(scope.row.deviceId) }}
					</template>
				</el-table-column>
				<el-table-column label="状态" width="90">
					<template #default="scope">
						<el-tag :type="scope.row.isRunning ? 'success' : 'info'">
							{{ scope.row.isRunning ? "运行中" : "已停止" }}
						</el-tag>
					</template>
				</el-table-column>
				<el-table-column label="最近采集时间" min-width="170">
					<template #default="scope">
						{{ formatTime(scope.row.lastCollectedAt) }}
					</template>
				</el-table-column>
				<el-table-column prop="totalCollections" label="采集次数" width="100" />
				<el-table-column prop="totalErrors" label="错误次数" width="100" />
				<el-table-column label="操作" width="110">
					<template #default="scope">
						<el-button
							type="danger"
							size="small"
							:disabled="!scope.row.isRunning"
							@click="stopTask(scope.row)"
						>
							停止
						</el-button>
					</template>
				</el-table-column>
			</el-table>
			<el-empty
				v-if="!tasks.length && !loadingTasks"
				description="当前没有活跃采集任务"
				:image-size="80"
			/>
		</el-card>

		<!-- SignalR 实时数据 -->
		<el-card class="live-card">
			<template #header>
				<div class="card-header">
					<span>
						实时数据推送（SignalR /hubs/device-data）
						<el-tag
							:type="hubStateTagType"
							size="small"
							style="margin-left: 8px"
						>
							{{ hubStateText }}
						</el-tag>
					</span>
					<div>
						<el-button
							type="primary"
							:disabled="!selectedDeviceId || subscribedDeviceId === selectedDeviceId"
							@click="subscribeSelected"
						>
							订阅当前设备
						</el-button>
						<el-button
							:disabled="!subscribedDeviceId"
							@click="unsubscribe"
						>
							取消订阅
						</el-button>
						<el-button @click="liveRows = []">清空</el-button>
					</div>
				</div>
			</template>
			<el-alert
				v-if="subscribedDeviceId"
				type="success"
				:closable="false"
				show-icon
				:title="`已订阅 ${deviceName(subscribedDeviceId)}，采集任务运行时数据将实时推送（无需轮询）`"
				style="margin-bottom: 12px"
			/>
			<el-table :data="liveRows" style="width: 100%" border max-height="420">
				<el-table-column prop="groupName" label="分组" width="130" />
				<el-table-column prop="address" label="地址" min-width="160" />
				<el-table-column label="值" min-width="120">
					<template #default="scope">{{ formatValue(scope.row.value) }}</template>
				</el-table-column>
				<el-table-column label="质量" width="100">
					<template #default="scope">
						<el-tag
							:type="scope.row.quality === 'Good' ? 'success' : 'danger'"
							size="small"
						>
							{{ scope.row.quality }}
						</el-tag>
					</template>
				</el-table-column>
				<el-table-column label="时间戳" min-width="170">
					<template #default="scope">{{ formatTime(scope.row.timestamp) }}</template>
				</el-table-column>
				<el-table-column prop="errorMessage" label="错误" min-width="140" show-overflow-tooltip />
			</el-table>
			<el-empty
				v-if="!liveRows.length"
				description="暂无实时数据：请先启动采集任务并订阅设备"
				:image-size="80"
			/>
		</el-card>

		<!-- 配置详情对话框 -->
		<el-dialog
			v-model="detailDialogVisible"
			:title="`配置详情：${detailProfile?.name ?? ''}`"
			width="720px"
		>
			<template v-if="detailProfile">
				<div
					v-for="group in detailProfile.groups"
					:key="group.groupName"
					class="group-detail"
				>
					<h4>
						分组 {{ group.groupName }}
						<el-tag size="small" style="margin-left: 8px">
							周期 {{ group.intervalMs }}ms
						</el-tag>
					</h4>
					<el-table :data="group.tags" size="small" border>
						<el-table-column prop="address" label="地址" min-width="160" />
						<el-table-column prop="dataType" label="数据类型" width="110" />
						<el-table-column prop="displayName" label="显示名" min-width="120" />
						<el-table-column prop="unit" label="单位" width="90" />
					</el-table>
				</div>
			</template>
			<template #footer>
				<el-button @click="detailDialogVisible = false">关闭</el-button>
			</template>
		</el-dialog>
	</div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from "vue";
import { Refresh } from "@element-plus/icons-vue";
import { ElMessage, ElMessageBox } from "element-plus";
import {
	machineConnectionDevicesApi,
	type DeviceDto,
} from "@/api/machineConnectionDevices";
import {
	machineConnectionCollectionApi,
	type CollectionProfileDto,
	type CollectionTaskStatus,
} from "@/api/machineConnectionCollection";
import {
	useDeviceDataHub,
	type CollectedDataBatch,
} from "@/composables/useDeviceDataHub";

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

// ---------- 设备 ----------
const devices = ref<DeviceDto[]>([]);
const selectedDeviceId = ref("");

const deviceName = (id: string) =>
	devices.value.find((d) => d.id === id)?.name ?? id;

const loadDevices = async () => {
	try {
		devices.value = await machineConnectionDevicesApi.list();
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载设备列表失败"));
	}
};

// ---------- 采集配置 ----------
const profiles = ref<CollectionProfileDto[]>([]);
const loadingProfiles = ref(false);
const detailDialogVisible = ref(false);
const detailProfile = ref<CollectionProfileDto | null>(null);
const startingProfileId = ref("");

const countTags = (p: CollectionProfileDto) =>
	p.groups.reduce((sum, g) => sum + g.tags.length, 0);

const formatIntervals = (p: CollectionProfileDto) =>
	[...new Set(p.groups.map((g) => `${g.intervalMs}ms`))].join(" / ") || "-";

const loadProfiles = async () => {
	if (!selectedDeviceId.value) return;
	loadingProfiles.value = true;
	try {
		profiles.value = await machineConnectionCollectionApi.listProfiles(
			selectedDeviceId.value,
		);
	} catch (e: unknown) {
		profiles.value = [];
		ElMessage.error(getErr(e, "加载采集配置失败"));
	} finally {
		loadingProfiles.value = false;
	}
};

const handleDeviceChange = () => {
	profiles.value = [];
	void loadProfiles();
};

const toggleProfileEnabled = async (profile: CollectionProfileDto) => {
	try {
		const updated = await machineConnectionCollectionApi.updateProfile(
			profile.id,
			{ isEnabled: !profile.isEnabled },
		);
		profile.isEnabled = updated.isEnabled;
		ElMessage.success(updated.isEnabled ? "已启用" : "已停用");
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "更新采集配置失败"));
	}
};

const renameProfile = async (profile: CollectionProfileDto) => {
	try {
		const { value } = (await ElMessageBox.prompt("新的配置名称", "重命名", {
			inputValue: profile.name,
			confirmButtonText: "保存",
			cancelButtonText: "取消",
			inputValidator: (v: string) => (v.trim() ? true : "名称不能为空"),
		})) as { value: string };
		const updated = await machineConnectionCollectionApi.updateProfile(
			profile.id,
			{ name: value.trim() },
		);
		profile.name = updated.name;
		ElMessage.success("重命名成功");
	} catch (e: unknown) {
		if (e !== "cancel") ElMessage.error(getErr(e, "重命名失败"));
	}
};

const deleteProfile = (profile: CollectionProfileDto) => {
	ElMessageBox.confirm(
		`确定删除采集配置「${profile.name}」吗？`,
		"警告",
		{ confirmButtonText: "确定", cancelButtonText: "取消", type: "warning" },
	)
		.then(async () => {
			try {
				await machineConnectionCollectionApi.deleteProfile(profile.id);
				ElMessage.success("删除成功");
				await loadProfiles();
			} catch (e: unknown) {
				ElMessage.error(getErr(e, "删除失败"));
			}
		})
		.catch(() => {
			/* 取消 */
		});
};

const showProfileDetail = (profile: CollectionProfileDto) => {
	detailProfile.value = profile;
	detailDialogVisible.value = true;
};

const startProfile = async (profile: CollectionProfileDto) => {
	if (!profile.groups.length || !countTags(profile)) {
		ElMessage.warning("该配置没有可采集的点位");
		return;
	}
	startingProfileId.value = profile.id;
	try {
		const { taskId } = await machineConnectionCollectionApi.startCollection(
			profile.deviceId,
			profile.groups,
		);
		ElMessage.success(`采集任务已启动：${taskId}`);
		await loadTasks();
		// 启动后自动订阅该设备的实时推送
		await subscribeDeviceById(profile.deviceId);
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "启动采集失败"));
	} finally {
		startingProfileId.value = "";
	}
};

// ---------- 采集任务状态 ----------
const tasks = ref<CollectionTaskStatus[]>([]);
const loadingTasks = ref(false);
let tasksTimer: number | undefined;

const loadTasks = async () => {
	loadingTasks.value = true;
	try {
		const map = await machineConnectionCollectionApi.status();
		tasks.value = Object.entries(map).map(([taskId, s]) => ({
			...s,
			taskId: s.taskId || taskId,
		}));
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载采集任务状态失败"));
	} finally {
		loadingTasks.value = false;
	}
};

const stopTask = (task: CollectionTaskStatus) => {
	ElMessageBox.confirm(
		`确定停止设备「${deviceName(task.deviceId)}」的采集任务吗？`,
		"确认",
		{ confirmButtonText: "停止", cancelButtonText: "取消", type: "warning" },
	)
		.then(async () => {
			try {
				await machineConnectionCollectionApi.stopCollection(task.taskId);
				ElMessage.success("采集任务已停止");
				await loadTasks();
			} catch (e: unknown) {
				ElMessage.error(getErr(e, "停止采集失败"));
			}
		})
		.catch(() => {
			/* 取消 */
		});
};

// ---------- SignalR 实时数据 ----------
interface LiveRow {
	groupName: string;
	address: string;
	value: unknown;
	quality: string;
	timestamp: string;
	errorMessage?: string | null;
}

const liveRows = ref<LiveRow[]>([]);
const subscribedDeviceId = ref("");
const MAX_LIVE_ROWS = 200;

const onBatch = (batch: CollectedDataBatch) => {
	// 按地址合并为「最新值」表：同地址覆盖，新地址插入
	const rows = new Map(
		liveRows.value.map((r) => [`${r.groupName}|${r.address}`, r] as const),
	);
	for (const v of batch.values) {
		rows.set(`${batch.groupName}|${v.address}`, {
			groupName: batch.groupName,
			address: v.address,
			value: v.value,
			quality: v.quality,
			timestamp: v.timestamp,
			errorMessage: v.errorMessage,
		});
	}
	liveRows.value = [...rows.values()].slice(-MAX_LIVE_ROWS);
};

const hub = useDeviceDataHub(onBatch);

const hubStateText = computed(() => {
	switch (hub.state.value) {
		case "connected":
			return "已连接";
		case "connecting":
			return "连接中";
		case "reconnecting":
			return "重连中";
		default:
			return "未连接";
	}
});

const hubStateTagType = computed(() => {
	switch (hub.state.value) {
		case "connected":
			return "success";
		case "connecting":
		case "reconnecting":
			return "warning";
		default:
			return "info";
	}
});

const subscribeDeviceById = async (deviceId: string) => {
	try {
		if (subscribedDeviceId.value && subscribedDeviceId.value !== deviceId) {
			await hub.unsubscribeDevice(subscribedDeviceId.value);
		}
		await hub.subscribeDevice(deviceId);
		subscribedDeviceId.value = deviceId;
		liveRows.value = [];
	} catch (e: unknown) {
		ElMessage.error(
			getErr(e, "实时推送连接失败：请确认 Industrial IoT 服务已启动"),
		);
	}
};

const subscribeSelected = () => {
	if (selectedDeviceId.value) void subscribeDeviceById(selectedDeviceId.value);
};

const unsubscribe = async () => {
	if (!subscribedDeviceId.value) return;
	await hub.unsubscribeDevice(subscribedDeviceId.value);
	subscribedDeviceId.value = "";
};

// ---------- 工具 ----------
const formatTime = (value?: string | null) =>
	value ? new Date(value).toLocaleString("zh-CN") : "-";

const formatValue = (value: unknown) => {
	if (value === null || value === undefined) return "-";
	if (typeof value === "object") return JSON.stringify(value);
	return String(value);
};

onMounted(() => {
	void loadDevices();
	void loadTasks();
	// 任务状态轻量轮询（数据本身走 SignalR 推送）
	tasksTimer = window.setInterval(() => void loadTasks(), 10_000);
});

onUnmounted(() => {
	if (tasksTimer !== undefined) {
		clearInterval(tasksTimer);
		tasksTimer = undefined;
	}
});
</script>

<style lang="scss" scoped>
.collection-manage-view {
	.profiles-card,
	.tasks-card,
	.live-card {
		margin-bottom: 20px;
	}

	.card-header {
		display: flex;
		justify-content: space-between;
		align-items: center;

		> div {
			display: flex;
			align-items: center;
		}
	}

	.group-detail {
		margin-bottom: 16px;

		h4 {
			margin: 0 0 8px;
		}
	}
}
</style>
