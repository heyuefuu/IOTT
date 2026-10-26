<template>
	<div class="device-oplog-view">
		<h2 class="page-title">设备读写操作历史</h2>

		<el-card class="toolbar-card">
			<div class="toolbar">
				<el-select v-model="typeFilter" placeholder="设备类型" clearable style="width: 140px">
					<el-option label="CNC" value="CNC" />
					<el-option label="PLC" value="PLC" />
					<el-option label="Robot" value="Robot" />
				</el-select>
				<el-input v-model="deviceId" placeholder="设备ID" style="max-width: 200px" clearable />
				<el-input
					v-model="keyword"
					placeholder="地址/设备名/操作人"
					style="max-width: 220px"
					clearable
					@keyup.enter="load"
				/>
				<el-button type="primary" @click="load">查询</el-button>
			</div>
		</el-card>

		<el-card>
			<el-table :data="rows" border v-loading="loading" max-height="600">
				<el-table-column prop="timestamp" label="时间" width="200">
					<template #default="scope">{{ formatTime(scope.row.timestamp) }}</template>
				</el-table-column>
				<el-table-column prop="deviceName" label="设备" min-width="140" />
				<el-table-column prop="deviceType" label="类型" width="80" />
				<el-table-column prop="address" label="地址" min-width="160" show-overflow-tooltip />
				<el-table-column prop="dataType" label="类型" width="90" />
				<el-table-column label="变更 (原值 → 新值)" min-width="200">
					<template #default="scope">
						<span class="old">{{ scope.row.oldValue ?? "-" }}</span>
						→
						<span class="new">{{ scope.row.newValue ?? "-" }}</span>
					</template>
				</el-table-column>
				<el-table-column label="结果" width="90">
					<template #default="scope">
						<el-tag :type="scope.row.success ? 'success' : 'danger'">
							{{ scope.row.success ? "成功" : "失败" }}
						</el-tag>
					</template>
				</el-table-column>
				<el-table-column prop="user" label="操作人" width="120" />
			</el-table>
			<el-empty v-if="!rows.length && !loading" description="暂无操作记录（执行一次点位写入后产生）" />
		</el-card>
	</div>
</template>

<script setup lang="ts">
import { ref, onMounted } from "vue";
import { ElMessage } from "element-plus";
import {
	machineConnectionDeviceOperationLogsApi,
	type DeviceOperationLog,
} from "@/api/machineConnectionDeviceOperationLogs";

function getErr(e: unknown, fallback: string): string {
	const ax = e as {
		response?: { data?: { error?: string; detail?: string } };
		message?: string;
	};
	return ax.response?.data?.error ?? ax.response?.data?.detail ?? ax.message ?? fallback;
}

const rows = ref<DeviceOperationLog[]>([]);
const loading = ref(false);
const typeFilter = ref("");
const deviceId = ref("");
const keyword = ref("");

const formatTime = (value: string) =>
	value ? new Date(value).toLocaleString() : "-";

const load = async () => {
	loading.value = true;
	try {
		rows.value = await machineConnectionDeviceOperationLogsApi.list({
			deviceId: deviceId.value || undefined,
			type: typeFilter.value || undefined,
			keyword: keyword.value || undefined,
			take: 500,
		});
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载操作历史失败"));
	} finally {
		loading.value = false;
	}
};

onMounted(() => {
	void load();
});
</script>

<style lang="scss" scoped>
.device-oplog-view {
	.page-title {
		margin-bottom: 16px;
	}
	.toolbar-card {
		margin-bottom: 16px;
	}
	.toolbar {
		display: flex;
		gap: 10px;
		align-items: center;
		flex-wrap: wrap;
	}
	.old {
		color: #909399;
	}
	.new {
		color: #409eff;
		font-weight: 600;
	}
}
</style>
