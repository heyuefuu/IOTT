<template>
	<div class="file-browser-view">
		<h2 class="page-title">程序文件浏览器</h2>

		<!-- 设备选择 -->
		<el-card class="device-selector-card">
			<template #header>
				<div class="card-header">
					<span>设备选择（数控设备 · 文件传输协议 FTP/SMB/NFS）</span>
					<el-button type="primary" @click="loadDevices">
						<el-icon><Refresh /></el-icon>
						刷新设备
					</el-button>
				</div>
			</template>
			<el-select
				v-model="selectedDevice"
				placeholder="请选择设备"
				value-key="id"
				style="width: 100%"
				@change="handleDeviceChange"
			>
				<el-option
					v-for="device in devices"
					:key="device.id"
					:label="device.name"
					:value="device"
				>
					<div class="device-option">
						<span>{{ device.name }}（{{ device.protocol }} · {{ device.ip }}:{{ device.port }}）</span>
						<el-tag
							:type="device.status === 'Online' || device.status === '在线' ? 'success' : 'warning'"
							size="small"
							style="margin-left: 10px"
						>
							{{ device.status === "Online" ? "在线" : device.status === "Offline" ? "离线" : device.status }}
						</el-tag>
					</div>
				</el-option>
			</el-select>
		</el-card>

		<el-row :gutter="20">
			<el-col :span="12">
				<!-- 本地待上传文件（浏览器真实选择） -->
				<el-card class="local-files-card">
					<template #header>
						<div class="card-header">
							<span>本地待上传文件</span>
						</div>
					</template>
					<el-form label-width="100px">
						<el-form-item label="目标目录">
							<el-input
								v-model="uploadRemotePath"
								placeholder="设备端目标目录，例如 / 或 /NCPrograms"
							/>
						</el-form-item>
						<el-form-item label="选择文件">
							<input
								type="file"
								multiple
								@change="onLocalFilesPicked"
							/>
						</el-form-item>
					</el-form>
					<ul class="picked-files" v-if="localPickedFiles.length">
						<li v-for="(f, i) in localPickedFiles" :key="i">
							<el-icon><Document /></el-icon>
							{{ f.name }}（{{ formatFileSize(f.size) }}）
						</li>
					</ul>
					<el-empty v-else description="未选择文件" :image-size="60" />
				</el-card>
			</el-col>

			<el-col :span="12">
				<!-- 设备文件系统（后端真实目录） -->
				<el-card class="device-files-card" v-if="selectedDevice">
					<template #header>
						<div class="card-header">
							<span>{{ selectedDevice.name }} 文件系统</span>
							<el-button
								type="primary"
								@click="refreshDeviceFiles"
								:loading="loadingDeviceFiles"
							>
								<el-icon><Refresh /></el-icon>
								刷新
							</el-button>
						</div>
					</template>

					<el-tree
						v-model:expanded-keys="expandedDeviceKeys"
						:data="deviceFiles"
						:props="fileTreeProps"
						show-checkbox
						node-key="path"
						@node-click="handleDeviceNodeClick"
						@check-change="handleDeviceCheckChange"
					>
						<template #default="{ data }">
							<div class="file-tree-node">
								<el-icon v-if="data.type === 'directory'">
									<Folder />
								</el-icon>
								<el-icon v-else>
									<Document />
								</el-icon>
								<span class="file-name">{{ data.name }}</span>
								<span
									class="file-size"
									v-if="data.type === 'file' && data.size"
									>{{ formatFileSize(data.size) }}</span
								>
							</div>
						</template>
					</el-tree>
					<el-empty
						v-if="!deviceFiles.length && !loadingDeviceFiles"
						description="目录为空或不可浏览"
						:image-size="60"
					/>
				</el-card>
				<el-empty v-else description="请先选择设备" />
			</el-col>
		</el-row>

		<!-- 文件传输操作 -->
		<div class="transfer-actions" v-if="selectedDevice">
			<el-button
				type="primary"
				@click="uploadSelectedFiles"
				:loading="uploading"
				:disabled="!localPickedFiles.length"
			>
				<el-icon><Upload /></el-icon>
				上传到设备
			</el-button>
			<el-button
				type="success"
				@click="downloadSelectedFiles"
				:loading="downloading"
				:disabled="!selectedDeviceFiles.length"
			>
				<el-icon><Download /></el-icon>
				下载到本地
			</el-button>
		</div>

		<!-- 文件传输任务列表 -->
		<el-card class="transfer-tasks-card" v-if="transferTasks.length">
			<template #header>
				<div class="card-header">
					<span>传输任务</span>
					<el-button type="primary" @click="clearCompletedTasks">
						清除已完成
					</el-button>
				</div>
			</template>

			<el-table :data="transferTasks" style="width: 100%" border>
				<el-table-column prop="fileName" label="文件名" />
				<el-table-column prop="direction" label="方向" width="100">
					<template #default="scope">
						<el-tag
							:type="
								scope.row.direction === 'upload'
									? 'primary'
									: 'success'
							"
						>
							{{
								scope.row.direction === "upload"
									? "上传"
									: "下载"
							}}
						</el-tag>
					</template>
				</el-table-column>
				<el-table-column prop="status" label="状态" width="100">
					<template #default="scope">
						<el-tag :type="getStatusType(scope.row.status)">
							{{ getStatusText(scope.row.status) }}
						</el-tag>
					</template>
				</el-table-column>
				<el-table-column prop="devicePath" label="设备路径" />
			</el-table>
		</el-card>

		<!-- 传输历史与断点续传 -->
		<el-card class="transfer-history-card" v-if="selectedDevice">
			<template #header>
				<div class="card-header">
					<span>
						传输历史（{{ selectedDevice.name }}）
						<el-tag
							v-if="capabilities"
							:type="capabilities.supportsResumeUpload ? 'success' : 'info'"
							size="small"
							style="margin-left: 8px"
						>
							{{
								capabilities.supportsResumeUpload
									? `支持断点续传（${capabilities.resumeMode}）`
									: "不支持断点续传"
							}}
						</el-tag>
					</span>
					<el-button
						type="primary"
						:loading="loadingHistory"
						@click="loadHistory"
					>
						<el-icon><Refresh /></el-icon>
						刷新历史
					</el-button>
				</div>
			</template>
			<el-alert
				v-if="capabilities && !capabilities.supportsResumeUpload"
				type="info"
				:closable="false"
				show-icon
				:title="capabilities.limitation || `${capabilities.protocol} 驱动不支持断点续传，失败后请重新上传`"
				style="margin-bottom: 12px"
			/>
			<el-table :data="transferHistory" style="width: 100%" border max-height="420">
				<el-table-column prop="fileName" label="文件名" min-width="160" show-overflow-tooltip />
				<el-table-column label="方向" width="80">
					<template #default="scope">
						<el-tag
							:type="scope.row.direction === 'Upload' ? 'primary' : 'success'"
						>
							{{ scope.row.direction === "Upload" ? "上传" : "下载" }}
						</el-tag>
					</template>
				</el-table-column>
				<el-table-column label="状态" width="100">
					<template #default="scope">
						<el-tag :type="getHistoryStatusType(scope.row.status)">
							{{ getHistoryStatusText(scope.row.status) }}
						</el-tag>
					</template>
				</el-table-column>
				<el-table-column label="进度" width="180">
					<template #default="scope">
						<el-progress
							:percentage="transferPercent(scope.row)"
							:status="scope.row.status === 'Failed' ? 'exception' : undefined"
						/>
					</template>
				</el-table-column>
				<el-table-column label="大小" width="110">
					<template #default="scope">
						{{ formatFileSize(scope.row.fileSize) }}
					</template>
				</el-table-column>
				<el-table-column label="开始时间" min-width="160">
					<template #default="scope">
						{{ formatHistoryTime(scope.row.startedAt) }}
					</template>
				</el-table-column>
				<el-table-column prop="errorMessage" label="错误信息" min-width="160" show-overflow-tooltip />
				<el-table-column label="操作" width="130" fixed="right">
					<template #default="scope">
						<el-tooltip
							v-if="canResume(scope.row)"
							:content="
								capabilities && !capabilities.supportsResumeUpload
									? '该设备协议不支持断点续传'
									: `从 ${formatFileSize(scope.row.bytesTransferred)} 处续传`
							"
						>
							<el-button
								type="warning"
								size="small"
								:loading="resumingTransferId === scope.row.transferId"
								:disabled="capabilities != null && !capabilities.supportsResumeUpload"
								@click="resumeTransfer(scope.row)"
							>
								断点续传
							</el-button>
						</el-tooltip>
					</template>
				</el-table-column>
			</el-table>
			<el-empty
				v-if="!transferHistory.length && !loadingHistory"
				description="暂无传输记录"
				:image-size="60"
			/>
		</el-card>

		<!-- 能力边界说明（不假装对接） -->
		<el-alert
			type="info"
			:closable="false"
			show-icon
			style="margin-top: 20px"
			title="说明"
		>
			<p>· 设备列表 / 设备目录 / 上传 / 下载 / 传输历史 / 断点续传均对接后端真实接口（/api/program-transfer）。</p>
			<p>· 上传文件来自浏览器本地选择（真实 File）；下载经设备目录勾选后由后端读取并下发。</p>
			<p>· 断点续传仅对「失败/暂停」的上传记录可用，且需设备协议驱动支持（见传输历史卡片的能力标签）。</p>
			<p>· 删除设备文件、常用文件库、文件识别等后端暂无对应接口，已移除以避免造假展示。</p>
		</el-alert>
	</div>
</template>

<script setup lang="ts">
import { ref, onMounted, onBeforeUnmount } from "vue";
import {
	Refresh,
	Upload,
	Download,
	Folder,
	Document,
} from "@element-plus/icons-vue";
import { ElMessage } from "element-plus";
import { machineConnectionDevicesApi } from "@/api/machineConnectionDevices";
import {
	machineConnectionProgramTransferApi,
	type ProgramTransferResponse,
	type ProgramTransferCapability,
} from "@/api/machineConnectionProgramTransfer";
import { TRANSFER_PROTOCOLS } from "./transferRecordMetrics";

// 文件类型定义
interface FileItem {
	name: string;
	path: string;
	type: "file" | "directory";
	size?: number;
	children?: FileItem[];
}

// 传输任务类型定义
interface TransferTask {
	id: string;
	fileName: string;
	direction: "upload" | "download";
	progress: number;
	status: "pending" | "transferring" | "completed" | "failed";
	localPath?: string;
	devicePath?: string;
	fileType?: string;
}

// 设备类型定义
interface Device {
	id: string;
	name: string;
	ip: string;
	port: number;
	protocol: string;
	status: string;
}

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

// 文件树属性
const fileTreeProps = {
	children: "children",
	label: "name",
};

// 本地待上传文件（来自浏览器文件选择，真实 File 对象）
const localPickedFiles = ref<File[]>([]);
const uploadRemotePath = ref("/");

// 设备文件系统（来自后端 /api/program-transfer/{id}/files）
const deviceFiles = ref<FileItem[]>([]);
const expandedDeviceKeys = ref<string[]>([]);
const selectedDeviceFiles = ref<string[]>([]);
const loadingDeviceFiles = ref(false);

// 传输任务
const transferTasks = ref<TransferTask[]>([]);
const uploading = ref(false);
const downloading = ref(false);

// 设备列表（来自后端 /api/devices?type=CNC，文件传输协议）
const devices = ref<Device[]>([]);
const selectedDevice = ref<Device | null>(null);
let deviceContextVersion = 0;
let deviceFilesLoadSequence = 0;
let historyLoadSequence = 0;
let capabilitiesLoadSequence = 0;

const isCurrentDeviceContext = (deviceId: string, version: number) =>
	version === deviceContextVersion && selectedDevice.value?.id === deviceId;

const loadDevices = async () => {
	try {
		const list = await machineConnectionDevicesApi.list("CNC");
		devices.value = list
			.filter((d) => TRANSFER_PROTOCOLS.includes(d.protocol))
			.map((d) => ({
				id: d.id,
				name: d.name,
				ip: d.host,
				port: d.port,
				protocol: d.protocol,
				status: d.status,
			}));
		if (devices.value.length > 0 && !selectedDevice.value) {
			selectedDevice.value = devices.value[0] as Device;
			handleDeviceChange();
		}
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载设备列表失败"));
	}
};

// 初始化
onMounted(() => {
	void loadDevices();
});

// 将后端文件项构建为 el-tree 数据
function buildFileTree(
	items: { name: string; path: string; nodeType: "folder" | "file"; sizeBytes?: number }[],
): FileItem[] {
	return items.map((it) => ({
		name: it.name,
		path: it.path,
		type: it.nodeType === "folder" ? "directory" : "file",
		size: it.sizeBytes,
	}));
}

// 加载设备文件 = 后端真实目录浏览
const loadDeviceFiles = async () => {
	if (!selectedDevice.value) return;
	const deviceId = selectedDevice.value.id;
	const contextVersion = deviceContextVersion;
	const requestSequence = ++deviceFilesLoadSequence;
	loadingDeviceFiles.value = true;
	selectedDeviceFiles.value = [];
	try {
		const items = await machineConnectionProgramTransferApi.files(
			deviceId,
			undefined,
			true,
		);
		if (
			!isCurrentDeviceContext(deviceId, contextVersion) ||
			requestSequence !== deviceFilesLoadSequence
		) return;
		deviceFiles.value = buildFileTree(items);
		expandedDeviceKeys.value = deviceFiles.value
			.filter((n) => n.type === "directory")
			.map((n) => n.path);
	} catch (e: unknown) {
		if (
			!isCurrentDeviceContext(deviceId, contextVersion) ||
			requestSequence !== deviceFilesLoadSequence
		) return;
		deviceFiles.value = [];
		ElMessage.error(getErr(e, "加载设备文件失败"));
	} finally {
		if (
			isCurrentDeviceContext(deviceId, contextVersion) &&
			requestSequence === deviceFilesLoadSequence
		) {
			loadingDeviceFiles.value = false;
		}
	}
};

const refreshDeviceFiles = () => {
	void loadDeviceFiles();
};

function handleDeviceChange() {
	deviceContextVersion += 1;
	stopAllTransferPolling();
	transferHistory.value = [];
	capabilities.value = null;
	resumingTransferId.value = "";
	deviceFiles.value = [];
	expandedDeviceKeys.value = [];
	selectedDeviceFiles.value = [];
	if (selectedDevice.value) {
		void loadDeviceFiles();
		void loadHistory();
		void loadCapabilities();
	}
}

// ---------- 传输历史与断点续传 ----------
const transferHistory = ref<ProgramTransferResponse[]>([]);
const loadingHistory = ref(false);
const capabilities = ref<ProgramTransferCapability | null>(null);
const resumingTransferId = ref("");
type TransferPoller = {
	timer: number | null;
	deviceId: string;
	contextVersion: number;
};
const transferPollers = new Map<string, TransferPoller>();

const loadHistory = async () => {
	if (!selectedDevice.value) return;
	const deviceId = selectedDevice.value.id;
	const contextVersion = deviceContextVersion;
	const requestSequence = ++historyLoadSequence;
	loadingHistory.value = true;
	try {
		const history = await machineConnectionProgramTransferApi.history(deviceId);
		if (
			!isCurrentDeviceContext(deviceId, contextVersion) ||
			requestSequence !== historyLoadSequence
		) return;
		stopAllTransferPolling();
		transferHistory.value = history;
		transferHistory.value
			.filter(isActiveTransfer)
			.forEach((row) => startTransferPolling(row.transferId));
	} catch (e: unknown) {
		if (
			!isCurrentDeviceContext(deviceId, contextVersion) ||
			requestSequence !== historyLoadSequence
		) return;
		stopAllTransferPolling();
		transferHistory.value = [];
		ElMessage.error(getErr(e, "加载传输历史失败"));
	} finally {
		if (
			isCurrentDeviceContext(deviceId, contextVersion) &&
			requestSequence === historyLoadSequence
		) {
			loadingHistory.value = false;
		}
	}
};

const loadCapabilities = async () => {
	if (!selectedDevice.value) return;
	const deviceId = selectedDevice.value.id;
	const contextVersion = deviceContextVersion;
	const requestSequence = ++capabilitiesLoadSequence;
	try {
		const result = await machineConnectionProgramTransferApi.capabilities(deviceId);
		if (
			isCurrentDeviceContext(deviceId, contextVersion) &&
			requestSequence === capabilitiesLoadSequence
		) {
			capabilities.value = result;
		}
	} catch {
		if (
			!isCurrentDeviceContext(deviceId, contextVersion) ||
			requestSequence !== capabilitiesLoadSequence
		) return;
		// 能力查询失败不阻塞页面；续传按钮回退为可点击，由后端最终判定
		capabilities.value = null;
	}
};

const isActiveTransfer = (row: ProgramTransferResponse) =>
	row.status === "InProgress" || row.status === "Pending";

const upsertTransferHistory = (row: ProgramTransferResponse) => {
	const index = transferHistory.value.findIndex((x) => x.transferId === row.transferId);
	if (index >= 0) transferHistory.value[index] = row;
	else transferHistory.value.unshift(row);
};

const stopTransferPolling = (transferId: string) => {
	const poller = transferPollers.get(transferId);
	if (poller?.timer != null) window.clearTimeout(poller.timer);
	transferPollers.delete(transferId);
};

const stopAllTransferPolling = () => {
	for (const transferId of [...transferPollers.keys()]) {
		stopTransferPolling(transferId);
	}
};

const startTransferPolling = (transferId: string) => {
	if (!transferId || transferPollers.has(transferId)) return;
	const deviceId = selectedDevice.value?.id;
	const contextVersion = deviceContextVersion;
	if (!deviceId) return;
	const poller: TransferPoller = { timer: null, deviceId, contextVersion };
	transferPollers.set(transferId, poller);

	const scheduleNext = () => {
		if (transferPollers.get(transferId) !== poller) return;
		poller.timer = window.setTimeout(() => void poll(), 1000);
	};
	const poll = async () => {
		poller.timer = null;
		if (transferPollers.get(transferId) !== poller) return;
		if (!isCurrentDeviceContext(poller.deviceId, poller.contextVersion)) {
			stopTransferPolling(transferId);
			return;
		}
		try {
			const latest = await machineConnectionProgramTransferApi.transferStatus(transferId);
			if (
				transferPollers.get(transferId) !== poller ||
				!isCurrentDeviceContext(poller.deviceId, poller.contextVersion)
			) return;
			upsertTransferHistory(latest);
			if (isActiveTransfer(latest)) scheduleNext();
			else stopTransferPolling(transferId);
		} catch {
			if (transferPollers.get(transferId) === poller) {
				stopTransferPolling(transferId);
			}
		}
	};
	scheduleNext();
};

onBeforeUnmount(() => {
	deviceContextVersion += 1;
	stopAllTransferPolling();
});

const canResume = (row: ProgramTransferResponse) =>
	row.direction === "Upload" &&
	(row.status === "Failed" || row.status === "Paused");

const transferPercent = (row: ProgramTransferResponse) => {
	if (row.status === "Completed") return 100;
	if (!row.fileSize) return 0;
	return Math.min(
		100,
		Math.round((row.bytesTransferred / row.fileSize) * 100),
	);
};

const resumeTransfer = async (row: ProgramTransferResponse) => {
	if (!selectedDevice.value) return;
	const deviceId = selectedDevice.value.id;
	const contextVersion = deviceContextVersion;
	resumingTransferId.value = row.transferId;
	startTransferPolling(row.transferId);
	try {
		// offset 传 0：后端默认从已传字节数处续传
		const result = await machineConnectionProgramTransferApi.resume(
			deviceId,
			row.transferId,
		);
		if (!isCurrentDeviceContext(deviceId, contextVersion)) return;
		upsertTransferHistory(result);
		if (result.status === "Completed") {
			stopTransferPolling(row.transferId);
			ElMessage.success(`断点续传完成：${result.fileName}`);
			await loadDeviceFiles();
		} else if (isActiveTransfer(result)) {
			ElMessage.info(`断点续传已恢复：${result.fileName}`);
		} else {
			stopTransferPolling(row.transferId);
			ElMessage.warning(result.errorMessage ?? `续传结束，状态：${result.status}`);
		}
		void loadHistory();
	} catch (e: unknown) {
		if (!isCurrentDeviceContext(deviceId, contextVersion)) return;
		stopTransferPolling(row.transferId);
		ElMessage.error(getErr(e, "断点续传失败"));
	} finally {
		if (isCurrentDeviceContext(deviceId, contextVersion)) {
			resumingTransferId.value = "";
		}
	}
};

const formatHistoryTime = (value?: string | null) =>
	value ? new Date(value).toLocaleString("zh-CN") : "-";

const getHistoryStatusType = (status: string): string => {
	switch (status) {
		case "Completed":
			return "success";
		case "Failed":
			return "danger";
		case "InProgress":
			return "warning";
		case "Paused":
			return "warning";
		default:
			return "info";
	}
};

const getHistoryStatusText = (status: string): string => {
	switch (status) {
		case "Pending":
			return "等待中";
		case "InProgress":
			return "传输中";
		case "Paused":
			return "已暂停";
		case "Completed":
			return "已完成";
		case "Failed":
			return "失败";
		default:
			return status;
	}
};

const handleDeviceNodeClick = () => {
	/* 选择交给复选框；点击不触发副作用 */
};

const handleDeviceCheckChange = (data: FileItem, checked: boolean) => {
	if (data.type !== "file") return; // 仅文件可下载
	if (checked) {
		if (!selectedDeviceFiles.value.includes(data.path))
			selectedDeviceFiles.value.push(data.path);
	} else {
		selectedDeviceFiles.value = selectedDeviceFiles.value.filter(
			(p) => p !== data.path,
		);
	}
};

// 本地文件选择（浏览器真实 File）
const onLocalFilesPicked = (e: Event) => {
	const input = e.target as HTMLInputElement;
	localPickedFiles.value = input.files ? Array.from(input.files) : [];
};

// 上传到设备 = 真实批量上传并轮询完成
const uploadSelectedFiles = async () => {
	if (!selectedDevice.value || localPickedFiles.value.length === 0) return;
	const remotePath = uploadRemotePath.value.trim() || "/";
	const files = localPickedFiles.value;
	uploading.value = true;
	const task: TransferTask = {
		id: Date.now().toString(),
		fileName: files.map((f) => f.name).join(", "),
		direction: "upload",
		progress: 0,
		status: "transferring",
		devicePath: remotePath,
	};
	transferTasks.value.unshift(task);
	try {
		const result = await machineConnectionProgramTransferApi.uploadBatchWait(
			selectedDevice.value.id,
			remotePath,
			files,
		);
		task.status = result.status === "Completed" ? "completed" : "failed";
		task.progress = 100;
		ElMessage.success(
			`上传完成：${result.completedFiles ?? 0}/${result.totalFiles ?? files.length} 成功`,
		);
		localPickedFiles.value = [];
		await loadDeviceFiles();
		void loadHistory();
	} catch (e: unknown) {
		task.status = "failed";
		ElMessage.error(getErr(e, "上传失败"));
	} finally {
		uploading.value = false;
	}
};

// 下载到本地 = 真实下载（单文件直下；多文件打包 ZIP）
const downloadSelectedFiles = async () => {
	if (!selectedDevice.value || selectedDeviceFiles.value.length === 0) return;
	const paths = [...selectedDeviceFiles.value];
	downloading.value = true;
	const task: TransferTask = {
		id: Date.now().toString(),
		fileName: paths.map((p) => p.split(/[\\/]/).pop()).join(", "),
		direction: "download",
		progress: 0,
		status: "transferring",
		devicePath: paths.join(", "),
	};
	transferTasks.value.unshift(task);
	try {
		if (paths.length === 1) {
			await machineConnectionProgramTransferApi.download(
				selectedDevice.value.id,
				paths[0] as string,
			);
		} else {
			await machineConnectionProgramTransferApi.downloadBatchZip(
				selectedDevice.value.id,
				paths,
			);
		}
		task.status = "completed";
		task.progress = 100;
		ElMessage.success("下载完成");
		selectedDeviceFiles.value = [];
	} catch (e: unknown) {
		task.status = "failed";
		ElMessage.error(getErr(e, "下载失败"));
	} finally {
		downloading.value = false;
	}
};

// 清除已完成任务
const clearCompletedTasks = () => {
	transferTasks.value = transferTasks.value.filter(
		(t) => t.status !== "completed" && t.status !== "failed",
	);
};

// 格式化文件大小
const formatFileSize = (size?: number): string => {
	if (!size || size < 0) return "-";
	if (size < 1024) return size + " B";
	if (size < 1024 * 1024) return (size / 1024).toFixed(2) + " KB";
	return (size / (1024 * 1024)).toFixed(2) + " MB";
};

// 传输任务状态 → 颜色/文本
const getStatusType = (status: string): string => {
	switch (status) {
		case "completed":
			return "success";
		case "failed":
			return "danger";
		case "transferring":
			return "warning";
		default:
			return "info";
	}
};

const getStatusText = (status: string): string => {
	switch (status) {
		case "pending":
			return "等待中";
		case "transferring":
			return "传输中";
		case "completed":
			return "已完成";
		case "failed":
			return "失败";
		default:
			return status;
	}
};
</script>

<style lang="scss" scoped>
.file-browser-view {
	.device-selector-card,
	.file-library-card,
	.local-files-card,
	.device-files-card,
	.transfer-tasks-card,
	.transfer-history-card,
	.file-identification-card {
		margin-bottom: 20px;
	}

	.card-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
	}

	.device-option {
		display: flex;
		align-items: center;
		justify-content: space-between;
		width: 100%;
	}

	.library-file-card {
		cursor: pointer;
		transition: all 0.3s ease;

		&:hover {
			transform: translateY(-2px);
			box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
		}

		.file-icon {
			font-size: 32px;
			color: #1890ff;
			margin-bottom: 10px;
		}

		.file-info {
			margin-bottom: 15px;

			.file-name {
				font-size: 14px;
				font-weight: 600;
				margin-bottom: 5px;
				color: #333;
			}

			.file-description {
				font-size: 12px;
				color: #666;
				margin-bottom: 5px;
				line-height: 1.4;
			}

			.file-size {
				font-size: 12px;
				color: #999;
			}
		}
	}

	.file-tree-node {
		display: flex;
		align-items: center;
		width: 100%;

		.file-name {
			margin-left: 8px;
			flex: 1;
		}

		.file-size {
			font-size: 12px;
			color: #999;
			margin-left: 10px;
		}
	}

	.transfer-actions {
		margin-bottom: 20px;
		display: flex;
		gap: 10px;
	}

	.file-identification-card {
		.el-table {
			margin-top: 10px;
		}
	}
}
</style>
