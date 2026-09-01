<template>
	<el-table :data="records" border max-height="560" v-loading="loading">
		<el-table-column prop="fileName" label="文件名" min-width="160" show-overflow-tooltip />
		<el-table-column label="方向" width="80">
			<template #default="scope">
				<el-tag :type="scope.row.direction === 'Upload' ? 'primary' : 'success'">
					{{ scope.row.direction === "Upload" ? "上传" : "下载" }}
				</el-tag>
			</template>
		</el-table-column>
		<el-table-column label="状态" width="100">
			<template #default="scope">
				<el-tag :type="statusType(scope.row.status)">{{ statusText(scope.row.status) }}</el-tag>
			</template>
		</el-table-column>
		<el-table-column label="进度" width="160">
			<template #default="scope"><el-progress :percentage="getTransferPercent(scope.row)" /></template>
		</el-table-column>
		<el-table-column label="文件大小" width="120">
			<template #default="scope">{{ formatFileSize(scope.row.fileSize) }}</template>
		</el-table-column>
		<el-table-column label="已传输" width="120">
			<template #default="scope">{{ formatFileSize(scope.row.bytesTransferred) }}</template>
		</el-table-column>
		<el-table-column label="文件完整性" width="130">
			<template #default="scope">
				<el-tag :type="integrityType(scope.row)">{{ integrityText(scope.row) }}</el-tag>
			</template>
		</el-table-column>
		<el-table-column label="传输速度" width="120">
			<template #default="scope">{{ calculateTransferSpeed(scope.row, nowMs) }} MB/s</template>
		</el-table-column>
		<el-table-column label="开始时间" min-width="170">
			<template #default="scope">{{ formatTime(scope.row.startedAt) }}</template>
		</el-table-column>
		<el-table-column label="完成时间" min-width="170">
			<template #default="scope">{{ formatTime(scope.row.completedAt) }}</template>
		</el-table-column>
		<el-table-column prop="checksum" label="校验值" min-width="180" show-overflow-tooltip />
		<el-table-column prop="errorMessage" label="错误信息" min-width="180" show-overflow-tooltip />
	</el-table>
</template>

<script setup lang="ts">
import type { ProgramTransferResponse } from "@/api/machineConnectionProgramTransfer";
import {
	calculateTransferSpeed,
	formatFileSize,
	getIntegrityState,
	getTransferPercent,
} from "./transferRecordMetrics";

defineProps<{ records: ProgramTransferResponse[]; loading: boolean; nowMs: number }>();

const statusType = (status: string) => ({
	Completed: "success", Failed: "danger", InProgress: "warning", Paused: "warning",
}[status] ?? "info");
const statusText = (status: string) => ({
	Pending: "等待中", InProgress: "传输中", Paused: "已暂停", Completed: "已完成", Failed: "失败",
}[status] ?? status);
const integrityText = (row: ProgramTransferResponse) => ({
	pending: "校验中", verified: "校验值已记录", "size-matched": "大小一致",
	failed: "不一致", unknown: "无法判定",
}[getIntegrityState(row)]);
const integrityType = (row: ProgramTransferResponse) => ({
	pending: "warning", verified: "success", "size-matched": "success", failed: "danger", unknown: "info",
}[getIntegrityState(row)]);
const formatTime = (value?: string | null) => value ? new Date(value).toLocaleString("zh-CN") : "-";
</script>
