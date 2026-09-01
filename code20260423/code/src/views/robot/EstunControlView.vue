<template>
	<div class="estun-control-view">
		<h2 class="page-title">埃斯顿机器人控制面板</h2>

		<el-card class="estun-card" shadow="never">
			<template #header>
				<div class="card-header">
					<span>设备连接</span>
					<div class="header-actions">
						<el-button :disabled="!deviceId" :loading="isReading" @click="readOnce">
							<el-icon><Refresh /></el-icon>
							读取一次
						</el-button>
						<el-button v-if="!isPolling" type="primary" :disabled="!deviceId" @click="startPolling">
							<el-icon><VideoPlay /></el-icon>
							开始轮询
						</el-button>
						<el-button v-else type="warning" @click="stopPolling">
							<el-icon><VideoPause /></el-icon>
							停止轮询
						</el-button>
					</div>
				</div>
			</template>

			<el-form :inline="true">
				<el-form-item label="选择设备">
					<el-select v-model="deviceId" placeholder="请选择埃斯顿机器人" style="width: 280px"
						@change="handleDeviceChange">
						<el-option v-for="d in devices" :key="d.id" :label="`${d.name}（${d.host}:${d.port}）`"
							:value="d.id" />
					</el-select>
				</el-form-item>
				<el-form-item label="轮询间隔">
					<el-input-number v-model="pollIntervalMs" :min="200" :max="60000" :step="200"
						style="width: 150px" />
					<span class="unit-hint">ms</span>
				</el-form-item>
				<el-form-item>
					<el-button :disabled="!deviceId" :loading="isTesting" @click="testConnection">连接测试</el-button>
				</el-form-item>
			</el-form>

			<el-alert v-if="devices.length === 0" type="info" show-icon :closable="false"
				title="未找到 EstunRobot 协议的机器人设备"
				description="请先到「机器人设备管理」新增一台设备，协议类型选择「埃斯顿机器人（EstunRobot）」。" />

			<el-alert v-if="lastError" type="error" show-icon :closable="true" :title="lastError"
				style="margin-top: 12px" @close="lastError = ''" />
		</el-card>

		<template v-if="snapshot">
			<!-- 状态指示灯 + 关键数值，对应 HslCommunication 示例的状态面板 -->
			<el-card class="estun-card" shadow="never">
				<template #header>
					<div class="card-header">
						<span>机器人状态</span>
						<span class="read-at">最后更新：{{ readAtText }}</span>
					</div>
				</template>

				<div class="lamp-grid">
					<div v-for="lamp in lamps" :key="lamp.label" class="lamp-item">
						<span class="lamp-dot" :class="lamp.on ? lamp.onClass : 'is-off'" />
						<span class="lamp-label">{{ lamp.label }}</span>
						<span class="lamp-value">{{ lamp.on ? lamp.onText : lamp.offText }}</span>
					</div>
				</div>

				<el-divider />

				<el-descriptions :column="4" border size="small">
					<el-descriptions-item label="运行模式">
						<el-tag :type="modeTagType" size="small">{{ modeText }}</el-tag>
					</el-descriptions-item>
					<el-descriptions-item label="全局速度">{{ snapshot.globalSpeedValue }}</el-descriptions-item>
					<el-descriptions-item label="当前工程">{{ snapshot.projectName || "-" }}</el-descriptions-item>
					<el-descriptions-item label="读写标志位">{{ snapshot.readWriteFlag }}</el-descriptions-item>
					<el-descriptions-item label="命令状态">
						{{ commandStatusHex }}（{{ snapshot.robotCommandStatus }}）
					</el-descriptions-item>
				</el-descriptions>
			</el-card>

			<!-- 机器人操作，对应示例的各个按钮 -->
			<el-card class="estun-card" shadow="never">
				<template #header><span>机器人操作</span></template>

				<div class="cmd-row">
					<el-button v-for="cmd in commandButtons" :key="cmd.command" :type="cmd.type"
						:loading="busyCommand === cmd.command" @click="runCommand(cmd.command)">
						{{ cmd.label }}
					</el-button>
				</div>

				<el-divider />

				<el-form :inline="true" class="cmd-form">
					<el-form-item label="工程名">
						<el-input v-model="projectNameInput" placeholder="如 Project1" style="width: 200px" />
					</el-form-item>
					<el-form-item>
						<el-button type="primary" :loading="busyCommand === 'LoadProject'"
							@click="runLoadProject">装载工程</el-button>
					</el-form-item>
				</el-form>

				<el-form :inline="true" class="cmd-form">
					<el-form-item label="全局速度">
						<el-input-number v-model="globalSpeedInput" :min="0" :max="100" :step="1"
							style="width: 150px" />
					</el-form-item>
					<el-form-item>
						<el-button type="primary" :loading="busyCommand === 'SetSpeed'"
							@click="runSetSpeed">设置速度</el-button>
					</el-form-item>
				</el-form>

				<el-divider />

				<el-collapse>
					<el-collapse-item title="原始寄存器直写（厂商调试，谨慎使用）" name="raw">
						<el-alert type="warning" show-icon :closable="false" style="margin-bottom: 12px"
							title="直写会绕过驱动的语义校验，直接写入机器人 Modbus 寄存器"
							description="对应 HslCommunication 示例中的 estun.Write(&quot;36&quot;, (short)0x801)。请确认寄存器号与机器人当前状态后再执行。" />
						<el-form :inline="true">
							<el-form-item label="寄存器">
								<el-input v-model="rawAddress" placeholder="如 36" style="width: 120px" />
							</el-form-item>
							<el-form-item label="值">
								<el-input v-model="rawValueText" placeholder="如 0x801 或 2049" style="width: 150px" />
							</el-form-item>
							<el-form-item>
								<el-button type="danger" :loading="busyCommand === 'RawWrite'"
									@click="runRawWrite">下发</el-button>
							</el-form-item>
						</el-form>
					</el-collapse-item>
				</el-collapse>
			</el-card>

			<!-- IO 状态 -->
			<el-card class="estun-card" shadow="never">
				<template #header><span>IO 状态</span></template>

				<el-tabs v-model="ioTab">
					<el-tab-pane :label="`SimDI（${snapshot.diBits.length}）`" name="di">
						<div v-if="snapshot.diBits.length === 0" class="empty-hint">无数据</div>
						<div v-else class="bit-grid">
							<div v-for="(bit, i) in snapshot.diBits" :key="`di${i}`" class="bit-item"
								:class="{ 'is-on': bit }" :title="`DI${i} = ${bit}`">
								{{ i }}
							</div>
						</div>
					</el-tab-pane>

					<el-tab-pane :label="`SimDout（${snapshot.doBits.length}）`" name="do">
						<div v-if="snapshot.doBits.length === 0" class="empty-hint">无数据</div>
						<div v-else class="bit-grid">
							<div v-for="(bit, i) in snapshot.doBits" :key="`do${i}`" class="bit-item"
								:class="{ 'is-on': bit }" :title="`DO${i} = ${bit}`">
								{{ i }}
							</div>
						</div>
					</el-tab-pane>

					<el-tab-pane :label="`用户 AI（${snapshot.aiValues.length}）`" name="ai">
						<div v-if="snapshot.aiValues.length === 0" class="empty-hint">无数据</div>
						<div v-else class="analog-grid">
							<div v-for="(v, i) in snapshot.aiValues" :key="`ai${i}`" class="analog-item">
								<span class="analog-index">AI{{ i }}</span>
								<span class="analog-value">{{ formatAnalog(v) }}</span>
							</div>
						</div>
					</el-tab-pane>

					<el-tab-pane :label="`用户 AO（${snapshot.aoValues.length}）`" name="ao">
						<div v-if="snapshot.aoValues.length === 0" class="empty-hint">无数据</div>
						<div v-else class="analog-grid">
							<div v-for="(v, i) in snapshot.aoValues" :key="`ao${i}`" class="analog-item">
								<span class="analog-index">AO{{ i }}</span>
								<span class="analog-value">{{ formatAnalog(v) }}</span>
							</div>
						</div>
					</el-tab-pane>
				</el-tabs>

				<el-divider />

				<el-collapse v-model="rawPanel">
					<el-collapse-item title="驱动原始快照 JSON（ESTUN_DATA）" name="raw">
						<el-button size="small" :loading="isReadingRaw" @click="loadRawSnapshot">拉取原始快照</el-button>
						<pre v-if="rawSnapshotJson" class="raw-json">{{ rawSnapshotJson }}</pre>
					</el-collapse-item>
				</el-collapse>
			</el-card>
		</template>

		<el-empty v-else-if="deviceId && !isReading" description="尚无数据，点击「读取一次」或「开始轮询」" />
	</div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from "vue";
import { Refresh, VideoPlay, VideoPause } from "@element-plus/icons-vue";
import { ElMessage } from "element-plus";
import { machineConnectionDevicesApi } from "@/api/machineConnectionDevices";
import {
	machineConnectionEstunApi,
	ESTUN_COMMAND_LABELS,
	type EstunCommand,
	type EstunSnapshot,
} from "@/api/machineConnectionEstun";

/** 该面板仅适用于 EstunRobot 协议设备 */
const ESTUN_PROTOCOL = "EstunRobot";

interface EstunDevice {
	id: string;
	name: string;
	host: string;
	port: number;
}

const devices = ref<EstunDevice[]>([]);
const deviceId = ref("");
const snapshot = ref<EstunSnapshot | null>(null);
const lastError = ref("");

const isReading = ref(false);
const isReadingRaw = ref(false);
const isTesting = ref(false);
const isPolling = ref(false);
const pollIntervalMs = ref(1000);
const pollTimer = ref<number | null>(null);

const busyCommand = ref<string>("");
const projectNameInput = ref("");
const globalSpeedInput = ref(50);
const rawAddress = ref("36");
const rawValueText = ref("0x801");
const ioTab = ref("di");
const rawPanel = ref<string[]>([]);
const rawSnapshotJson = ref("");
/** 输入框是否已用机器人当前值初始化过（避免轮询覆盖用户输入） */
const hasInitializedInputs = ref(false);

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

const loadDevices = async () => {
	try {
		const list = await machineConnectionDevicesApi.list("Robot");
		devices.value = list
			.filter((d) => d.protocol === ESTUN_PROTOCOL)
			.map((d) => ({ id: d.id, name: d.name, host: d.host, port: d.port }));
		if (devices.value.length === 1 && !deviceId.value) {
			deviceId.value = devices.value[0]!.id;
		}
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载埃斯顿设备列表失败"));
	}
};

const handleDeviceChange = () => {
	stopPolling();
	snapshot.value = null;
	rawSnapshotJson.value = "";
	lastError.value = "";
	hasInitializedInputs.value = false;
};

const readOnce = async () => {
	if (!deviceId.value) {
		ElMessage.warning("请选择设备");
		return;
	}
	// 轮询间隔可能短于一次往返耗时，跳过重入避免请求堆积
	if (isReading.value) return;
	isReading.value = true;
	try {
		snapshot.value = await machineConnectionEstunApi.readSnapshot(deviceId.value);
		// 仅首次读取时把输入框对齐机器人当前值；之后不再覆盖，否则轮询会冲掉用户正在输入的内容
		if (!hasInitializedInputs.value) {
			projectNameInput.value = snapshot.value.projectName;
			globalSpeedInput.value = snapshot.value.globalSpeedValue;
			hasInitializedInputs.value = true;
		}
		lastError.value = "";
	} catch (e: unknown) {
		lastError.value = getErr(e, "读取机器人数据失败");
	} finally {
		isReading.value = false;
	}
};

const startPolling = async () => {
	if (!deviceId.value) {
		ElMessage.warning("请选择设备");
		return;
	}
	if (isPolling.value) return;
	isPolling.value = true;
	await readOnce();
	pollTimer.value = window.setInterval(() => {
		void readOnce();
	}, pollIntervalMs.value);
};

const stopPolling = () => {
	if (pollTimer.value !== null) {
		clearInterval(pollTimer.value);
		pollTimer.value = null;
	}
	isPolling.value = false;
};

const testConnection = async () => {
	if (!deviceId.value) return;
	isTesting.value = true;
	try {
		const r = await machineConnectionDevicesApi.testConnection(deviceId.value);
		if (r.success) ElMessage.success(`连接成功${r.latency ? `（${r.latency}）` : ""}`);
		else ElMessage.error(r.errorMessage ?? "连接失败");
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "连接测试失败"));
	} finally {
		isTesting.value = false;
	}
};

// ---- 指令下发 ----

const commandButtons: ReadonlyArray<{
	command: EstunCommand;
	label: string;
	type: "primary" | "success" | "warning" | "danger" | "info";
}> = [
	{ command: "Start", label: ESTUN_COMMAND_LABELS.Start, type: "success" },
	{ command: "Stop", label: ESTUN_COMMAND_LABELS.Stop, type: "warning" },
	{ command: "ResetError", label: ESTUN_COMMAND_LABELS.ResetError, type: "primary" },
	{ command: "CommandStatusRestart", label: ESTUN_COMMAND_LABELS.CommandStatusRestart, type: "info" },
	{ command: "UnregisterProject", label: ESTUN_COMMAND_LABELS.UnregisterProject, type: "danger" },
];

/** 指令下发后立即回读一次，让界面反映机器人真实状态而非乐观假设 */
async function withCommand(key: string, action: () => Promise<void>, okText: string) {
	if (!deviceId.value) {
		ElMessage.warning("请选择设备");
		return;
	}
	busyCommand.value = key;
	try {
		await action();
		ElMessage.success(okText);
		await readOnce();
	} catch (e: unknown) {
		const msg = getErr(e, `${okText}失败`);
		lastError.value = msg;
		ElMessage.error(msg);
	} finally {
		busyCommand.value = "";
	}
}

const runCommand = (command: EstunCommand) =>
	withCommand(
		command,
		() => machineConnectionEstunApi.sendCommand(deviceId.value, command),
		ESTUN_COMMAND_LABELS[command],
	);

const runLoadProject = () =>
	withCommand(
		"LoadProject",
		() => machineConnectionEstunApi.loadProject(deviceId.value, projectNameInput.value),
		"装载工程",
	);

const runSetSpeed = () =>
	withCommand(
		"SetSpeed",
		() => machineConnectionEstunApi.setGlobalSpeed(deviceId.value, globalSpeedInput.value),
		"设置全局速度",
	);

/** 支持 0x 前缀十六进制与十进制两种输入 */
function parseRawValue(text: string): number | null {
	const t = text.trim();
	if (!t) return null;
	const n = /^0[xX][0-9a-fA-F]+$/.test(t) ? parseInt(t, 16) : Number(t);
	return Number.isFinite(n) ? n : null;
}

const runRawWrite = () => {
	const value = parseRawValue(rawValueText.value);
	if (value === null) {
		ElMessage.warning("值格式不正确，请输入十进制或 0x 前缀的十六进制");
		return;
	}
	if (!rawAddress.value.trim()) {
		ElMessage.warning("请输入寄存器地址");
		return;
	}
	return withCommand(
		"RawWrite",
		() =>
			machineConnectionEstunApi.writeRawRegister(
				deviceId.value,
				rawAddress.value,
				value,
			),
		`写入寄存器 ${rawAddress.value}`,
	);
};

const loadRawSnapshot = async () => {
	if (!deviceId.value) return;
	isReadingRaw.value = true;
	try {
		const json = await machineConnectionEstunApi.readRawSnapshot(deviceId.value);
		// 后端返回的是 JSON 字符串，这里美化后展示；解析失败则原样显示
		try {
			rawSnapshotJson.value = JSON.stringify(JSON.parse(json), null, 2);
		} catch {
			rawSnapshotJson.value = json;
		}
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "拉取原始快照失败"));
	} finally {
		isReadingRaw.value = false;
	}
};

// ---- 展示派生 ----

const lamps = computed(() => {
	const s = snapshot.value;
	if (!s) return [];
	return [
		{ label: "错误", on: s.errorStatus, onText: "有错误", offText: "正常", onClass: "is-error" },
		{ label: "使能", on: s.enableStatus, onText: "已使能", offText: "未使能", onClass: "is-ok" },
		{ label: "运行", on: s.runStatus, onText: "运行中", offText: "停止", onClass: "is-ok" },
		{ label: "程序", on: s.programRunStatus, onText: "运行中", offText: "未运行", onClass: "is-ok" },
		{ label: "动作", on: s.robotMoving, onText: "移动中", offText: "静止", onClass: "is-active" },
	];
});

const modeText = computed(() => {
	const s = snapshot.value;
	if (!s) return "-";
	if (s.autoMode) return "自动";
	if (s.manualMode) return "手动";
	if (s.remoteMode) return "远程";
	return "未知";
});

const modeTagType = computed(() => {
	switch (modeText.value) {
		case "自动":
			return "success";
		case "手动":
			return "warning";
		case "远程":
			return "primary";
		default:
			return "info";
	}
});

const commandStatusHex = computed(() => {
	const s = snapshot.value;
	if (!s) return "-";
	return `0x${(s.robotCommandStatus >>> 0).toString(16).toUpperCase()}`;
});

const readAtText = computed(() =>
	snapshot.value ? snapshot.value.readAt.toLocaleTimeString() : "-",
);

const formatAnalog = (v: number) =>
	Number.isInteger(v) ? String(v) : v.toFixed(3);

onMounted(() => {
	void loadDevices();
});

onUnmounted(() => {
	stopPolling();
});
</script>

<style lang="scss" scoped>
.estun-control-view {
	.estun-card {
		margin-bottom: 16px;
	}

	.card-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
	}

	.header-actions {
		display: flex;
		gap: 10px;
	}

	.read-at {
		font-size: 13px;
		color: var(--el-text-color-secondary);
	}

	.unit-hint {
		margin-left: 6px;
		color: var(--el-text-color-secondary);
	}

	.lamp-grid {
		display: grid;
		grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
		gap: 12px;
	}

	.lamp-item {
		display: flex;
		align-items: center;
		gap: 8px;
		padding: 10px 12px;
		border: 1px solid var(--el-border-color-lighter);
		border-radius: 6px;
	}

	.lamp-dot {
		width: 12px;
		height: 12px;
		border-radius: 50%;
		background: var(--el-border-color);
		flex: 0 0 auto;

		&.is-error {
			background: var(--el-color-danger);
			box-shadow: 0 0 6px var(--el-color-danger);
		}

		&.is-ok {
			background: var(--el-color-success);
			box-shadow: 0 0 6px var(--el-color-success);
		}

		&.is-active {
			background: var(--el-color-warning);
			box-shadow: 0 0 6px var(--el-color-warning);
		}
	}

	.lamp-label {
		font-weight: 600;
	}

	.lamp-value {
		margin-left: auto;
		font-size: 13px;
		color: var(--el-text-color-secondary);
	}

	.cmd-row {
		display: flex;
		flex-wrap: wrap;
		gap: 10px;
	}

	.cmd-form {
		margin-top: 4px;
	}

	.bit-grid {
		display: grid;
		grid-template-columns: repeat(auto-fill, minmax(34px, 1fr));
		gap: 6px;
	}

	.bit-item {
		display: flex;
		align-items: center;
		justify-content: center;
		height: 30px;
		font-size: 12px;
		border: 1px solid var(--el-border-color-lighter);
		border-radius: 4px;
		color: var(--el-text-color-secondary);
		background: var(--el-fill-color-lighter);

		&.is-on {
			background: var(--el-color-success);
			border-color: var(--el-color-success);
			color: #fff;
			font-weight: 600;
		}
	}

	.analog-grid {
		display: grid;
		grid-template-columns: repeat(auto-fill, minmax(130px, 1fr));
		gap: 8px;
	}

	.analog-item {
		display: flex;
		justify-content: space-between;
		gap: 8px;
		padding: 6px 10px;
		border: 1px solid var(--el-border-color-lighter);
		border-radius: 4px;
	}

	.analog-index {
		color: var(--el-text-color-secondary);
		font-size: 12px;
	}

	.analog-value {
		font-family: var(--el-font-family-monospace, monospace);
		font-size: 13px;
	}

	.raw-json {
		margin: 12px 0 0;
		padding: 12px;
		max-height: 320px;
		overflow: auto;
		font-size: 12px;
		background: var(--el-fill-color-lighter);
		border-radius: 4px;
	}

	.empty-hint {
		padding: 20px 0;
		color: var(--el-text-color-secondary);
	}
}
</style>
