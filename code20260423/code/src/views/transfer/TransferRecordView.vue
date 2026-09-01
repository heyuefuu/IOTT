<template>
	<div class="transfer-record-view">
		<h2 class="page-title">传输验证记录</h2>
		<el-alert
			title="请连接真实机床执行文件传输；本页仅记录后端返回的实时实验数据，不生成模拟结果。"
			type="info"
			:closable="false"
			show-icon
		/>
		<el-card class="toolbar-card">
			<div class="toolbar">
				<el-select v-model="selectedDeviceId" placeholder="选择 CNC 传输设备" style="width: 320px">
					<el-option
						v-for="device in devices"
						:key="device.id"
						:label="`${device.name}（${transferProtocol(device)} · ${device.host}:${device.port}）`"
						:value="device.id"
					/>
				</el-select>
				<el-switch v-model="autoRefresh" active-text="自动刷新" />
				<el-tag type="success" effect="dark">实时记录中</el-tag>
				<el-button type="primary" :loading="loading" @click="loadRecords(false)">刷新记录</el-button>
			</div>
		</el-card>

		<el-row :gutter="16" class="summary-row">
			<el-col :xs="12" :sm="6"><el-card shadow="hover"><el-statistic title="历史记录" :value="records.length" /></el-card></el-col>
			<el-col :xs="12" :sm="6"><el-card shadow="hover"><el-statistic title="文件完整性" :value="summary.integrity"><template #suffix>条一致</template></el-statistic></el-card></el-col>
			<el-col :xs="12" :sm="6"><el-card shadow="hover"><el-statistic title="平均传输速度" :value="summary.averageSpeed" :precision="2"><template #suffix>MB/s</template></el-statistic></el-card></el-col>
			<el-col :xs="12" :sm="6"><el-card shadow="hover"><el-statistic title="最大文件大小" :value="summary.maximumSize" :precision="2"><template #suffix>MB</template></el-statistic></el-card></el-col>
		</el-row>

		<el-card>
			<template #header><span>实验数据实时记录</span></template>
			<TransferRecordTable :records="records" :loading="loading" :now-ms="nowMs" />
			<el-empty v-if="!records.length && !loading" description="暂无历史记录，请连接机床并执行文件传输" />
		</el-card>
	</div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { ElMessage } from "element-plus";
import { machineConnectionDevicesApi, type DeviceDto } from "@/api/machineConnectionDevices";
import {
	machineConnectionProgramTransferApi,
	type ProgramTransferResponse,
} from "@/api/machineConnectionProgramTransfer";
import TransferRecordTable from "./TransferRecordTable.vue";
import { calculateTransferSpeed, getIntegrityState, TRANSFER_PROTOCOLS } from "./transferRecordMetrics";

const transferProtocols = new Set(TRANSFER_PROTOCOLS);
const devices = ref<DeviceDto[]>([]);
const selectedDeviceId = ref("");
const records = ref<ProgramTransferResponse[]>([]);
const loading = ref(false);
const autoRefresh = ref(true);
const nowMs = ref(Date.now());
let refreshTimer: number | undefined;

const transferProtocol = (device: DeviceDto) => device.transfer?.protocol ?? device.protocol;
const summary = computed(() => {
	const completed = records.value.filter((row) => row.status === "Completed");
	const integrity = completed.filter((row) => ["verified", "size-matched"].includes(getIntegrityState(row))).length;
	const speeds = completed.map((row) => calculateTransferSpeed(row, nowMs.value)).filter((value) => value > 0);
	return {
		integrity,
		averageSpeed: speeds.length ? speeds.reduce((sum, value) => sum + value, 0) / speeds.length : 0,
		maximumSize: completed.length ? Math.max(...completed.map((row) => row.fileSize)) / 1024 / 1024 : 0,
	};
});

async function loadDevices() {
	try {
		devices.value = (await machineConnectionDevicesApi.list("CNC"))
			.filter((device) => transferProtocols.has(transferProtocol(device).toUpperCase()));
		if (!selectedDeviceId.value && devices.value[0]) selectedDeviceId.value = devices.value[0].id;
	} catch (error) {
		ElMessage.error(error instanceof Error ? error.message : "加载 CNC 设备失败");
	}
}

async function loadRecords(silent = true) {
	if (!selectedDeviceId.value || (silent && loading.value)) return;
	loading.value = true;
	try {
		records.value = await machineConnectionProgramTransferApi.history(selectedDeviceId.value);
		nowMs.value = Date.now();
	} catch (error) {
		if (!silent) ElMessage.error(error instanceof Error ? error.message : "加载传输记录失败");
	} finally {
		loading.value = false;
	}
}

watch(selectedDeviceId, () => void loadRecords(false));
onMounted(async () => {
	await loadDevices();
	refreshTimer = window.setInterval(() => {
		nowMs.value = Date.now();
		if (autoRefresh.value) void loadRecords(true);
	}, 2000);
});
onBeforeUnmount(() => refreshTimer != null && window.clearInterval(refreshTimer));
</script>

<style scoped>
.transfer-record-view { display: grid; gap: 16px; }
.toolbar { display: flex; align-items: center; flex-wrap: wrap; gap: 16px; }
.summary-row :deep(.el-card__body) { text-align: center; }
</style>
