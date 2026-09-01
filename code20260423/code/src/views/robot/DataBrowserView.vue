<template>
	<div class="robot-data-browser-view">
		<h2 class="page-title">机器人数据浏览</h2>

		<el-card class="data-browser-card">
			<template #header>
				<div class="card-header">
					<span>数据浏览</span>
					<el-button type="primary" :loading="connecting" @click="connectDevice">
						<el-icon><Link /></el-icon>
						连接设备
					</el-button>
				</div>
			</template>

			<div class="data-browser-content">
				<!-- 设备选择 -->
				<div class="device-selection">
					<el-form :inline="true" :model="deviceForm">
						<el-form-item label="选择设备">
							<el-select
								v-model="deviceForm.deviceId"
								placeholder="请选择设备"
								style="width: 320px"
							>
								<el-option
									v-for="device in devices"
									:key="device.id"
									:label="`${device.name}（${device.protocol}）`"
									:value="device.id"
								/>
							</el-select>
						</el-form-item>
					</el-form>

					<div v-if="isConnected" class="connection-status">
						<el-tag type="success">已连接到: {{ connectedDeviceName }}</el-tag>
						<el-tag v-if="connectionMode" size="small" style="margin-left: 8px">
							{{ connectionMode === "driver" ? "协议驱动握手" : "TCP 端口探测" }}
						</el-tag>
					</div>
				</div>

				<!-- 数据浏览区域 -->
				<div v-if="isConnected" class="browser-area">
					<el-tabs v-model="activeDataType">
						<!-- 地址空间：真实浏览 + 读值 + Bool 可写点位开关写入 -->
						<el-tab-pane label="地址空间" name="address">
							<div class="tab-toolbar">
								<el-button
									type="primary"
									:loading="loadingAddress"
									@click="loadAddressSpace"
								>
									<el-icon><Refresh /></el-icon>
									刷新地址空间
								</el-button>
								<el-button
									:disabled="!selectedNodes.length"
									:loading="reading"
									@click="readSelected"
								>
									读取选中值（{{ selectedNodes.length }}）
								</el-button>
								<el-checkbox
									v-model="autoRefresh"
									style="margin-left: 12px"
									@change="restartAutoRefresh"
								>
									自动刷新已读点位
								</el-checkbox>
								<el-select
									v-model="refreshInterval"
									style="width: 110px; margin-left: 8px"
									:disabled="!autoRefresh"
									@change="restartAutoRefresh"
								>
									<el-option label="1秒" value="1000" />
									<el-option label="2秒" value="2000" />
									<el-option label="5秒" value="5000" />
									<el-option label="10秒" value="10000" />
								</el-select>
							</div>

							<el-table
								:data="addressRows"
								style="width: 100%"
								border
								row-key="path"
								lazy
								:load="loadChildNodes"
								:tree-props="{ children: 'children', hasChildren: 'hasChildren' }"
								max-height="480"
								@selection-change="handleSelectionChange"
							>
								<el-table-column type="selection" width="44" :selectable="isSelectable" />
								<el-table-column prop="displayName" label="名称" min-width="180" show-overflow-tooltip />
								<el-table-column prop="path" label="路径" min-width="220" show-overflow-tooltip />
								<el-table-column prop="dataType" label="数据类型" width="100" />
								<el-table-column label="读/写" width="90">
									<template #default="scope">
										<template v-if="scope.row.nodeType === 'Variable'">
											<el-tag v-if="scope.row.isReadable !== false" size="small" type="info">读</el-tag>
											<el-tag v-if="scope.row.isWritable" size="small" type="warning" style="margin-left: 4px">写</el-tag>
										</template>
									</template>
								</el-table-column>
								<el-table-column label="当前值" min-width="140">
									<template #default="scope">
										<!-- Bool 可写点位：开关直接下发写入（真实 writeTags） -->
										<el-switch
											v-if="isBoolWritable(scope.row)"
											:model-value="scope.row.value === true"
											:loading="writingPath === scope.row.path"
											@change="(v: string | number | boolean) => writeBool(scope.row, v === true)"
										/>
										<span v-else>{{ formatValue(scope.row.value) }}</span>
									</template>
								</el-table-column>
								<el-table-column label="质量" width="90">
									<template #default="scope">
										<el-tag
											v-if="scope.row.quality"
											:type="scope.row.quality === 'Good' ? 'success' : 'danger'"
											size="small"
										>
											{{ scope.row.quality }}
										</el-tag>
									</template>
								</el-table-column>
								<el-table-column label="操作" width="90">
									<template #default="scope">
										<el-button
											v-if="scope.row.nodeType === 'Variable' && scope.row.isReadable !== false"
											size="small"
											@click="readOne(scope.row)"
										>
											读取
										</el-button>
									</template>
								</el-table-column>
							</el-table>
							<el-empty
								v-if="!addressRows.length && !loadingAddress"
								description="地址空间为空或该协议驱动不支持浏览"
								:image-size="80"
							/>
						</el-tab-pane>

						<!-- 程序文件：真实 program-transfer 目录 + 下载 -->
						<el-tab-pane label="程序文件" name="program">
							<el-alert
								v-if="transferCapability && !transferCapability.supportsBrowse"
								type="info"
								:closable="false"
								show-icon
								:title="transferCapability.limitation || `${transferCapability.protocol} 协议不支持程序目录浏览`"
								style="margin-bottom: 12px"
							/>
							<div class="tab-toolbar">
								<el-button
									type="primary"
									:loading="loadingPrograms"
									@click="loadPrograms"
								>
									<el-icon><Refresh /></el-icon>
									刷新文件列表
								</el-button>
							</div>
							<el-table :data="programRows" style="width: 100%" border max-height="480">
								<el-table-column prop="name" label="文件名" min-width="180" show-overflow-tooltip />
								<el-table-column prop="path" label="路径" min-width="240" show-overflow-tooltip />
								<el-table-column label="类型" width="90">
									<template #default="scope">
										<el-tag :type="scope.row.nodeType === 'folder' ? 'info' : 'success'" size="small">
											{{ scope.row.nodeType === "folder" ? "目录" : "文件" }}
										</el-tag>
									</template>
								</el-table-column>
								<el-table-column label="大小" width="110">
									<template #default="scope">{{ formatFileSize(scope.row.sizeBytes) }}</template>
								</el-table-column>
								<el-table-column label="操作" width="110">
									<template #default="scope">
										<el-button
											v-if="scope.row.nodeType === 'file'"
											type="primary"
											size="small"
											:loading="downloadingPath === scope.row.path"
											@click="downloadProgram(scope.row)"
										>
											下载
										</el-button>
									</template>
								</el-table-column>
							</el-table>
							<el-empty
								v-if="!programRows.length && !loadingPrograms"
								description="暂无程序文件"
								:image-size="80"
							/>
						</el-tab-pane>
					</el-tabs>

					<el-alert
						type="info"
						:closable="false"
						show-icon
						style="margin-top: 16px"
						title="说明"
					>
						<p>· 地址空间 / 点位读写 / 程序文件均对接后端真实接口（/api/addressspace、/api/data、/api/program-transfer）。</p>
						<p>· 关节位置、IO 等数据取决于协议驱动暴露的地址空间节点，读取选中即可查看实时值。</p>
						<p>· 机器人程序的「加载 / 运行」控制后端暂无对应接口，未提供该操作以避免造假展示。</p>
					</el-alert>
				</div>

				<div v-else class="not-connected">
					<el-empty description="请先连接设备" />
				</div>
			</div>
		</el-card>
	</div>
</template>

<script setup lang="ts">
import { ref, reactive, onUnmounted, onMounted } from "vue";
import { Link, Refresh } from "@element-plus/icons-vue";
import { ElMessage } from "element-plus";
import { machineConnectionDevicesApi } from "@/api/machineConnectionDevices";
import {
	machineConnectionPointsApi,
	type AddressNode,
} from "@/api/machineConnectionPoints";
import {
	machineConnectionProgramTransferApi,
	type ProgramTransferCapability,
	type ProgramTransferFileItem,
} from "@/api/machineConnectionProgramTransfer";

interface AddressRow {
	path: string;
	displayName: string;
	nodeType: "Folder" | "Variable";
	dataType?: string | null;
	isReadable?: boolean;
	isWritable?: boolean;
	sourceId?: string | null;
	hasChildren?: boolean;
	value?: unknown;
	quality?: string;
	timestamp?: string;
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

// ---------- 设备与连接 ----------
const devices = ref<{ id: string; name: string; protocol: string }[]>([]);
const deviceForm = reactive({ deviceId: "" });
const isConnected = ref(false);
const connecting = ref(false);
const connectedDeviceName = ref("");
const connectionMode = ref<"driver" | "tcp" | "">("");

const loadDevices = async () => {
	try {
		const list = await machineConnectionDevicesApi.list("Robot");
		devices.value = list.map((d) => ({ id: d.id, name: d.name, protocol: d.protocol }));
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载设备列表失败"));
	}
};

const connectDevice = async () => {
	if (!deviceForm.deviceId) {
		ElMessage.warning("请选择设备");
		return;
	}
	const device = devices.value.find((d) => d.id === deviceForm.deviceId);
	if (!device) return;
	connecting.value = true;
	try {
		const r = await machineConnectionDevicesApi.testConnection(device.id);
		if (r.success) {
			isConnected.value = true;
			connectedDeviceName.value = device.name;
			connectionMode.value = r.mode ?? "";
			ElMessage.success(`成功连接到设备: ${device.name}`);
			await loadAddressSpace();
			void loadCapability();
			void loadPrograms();
		} else {
			isConnected.value = false;
			ElMessage.error(r.errorMessage ?? "连接失败");
		}
	} catch (e: unknown) {
		isConnected.value = false;
		ElMessage.error(getErr(e, "连接失败"));
	} finally {
		connecting.value = false;
	}
};

// ---------- 地址空间（真实浏览 / 读值 / Bool 写入） ----------
const activeDataType = ref("address");
const addressRows = ref<AddressRow[]>([]);
const loadingAddress = ref(false);
const selectedNodes = ref<AddressRow[]>([]);
const reading = ref(false);
const writingPath = ref("");
// tree-table 中所有已加载的行（含懒加载子节点），读值时按 path 回填
const rowIndex = new Map<string, AddressRow>();

const toRow = (node: AddressNode): AddressRow => {
	const row: AddressRow = {
		path: node.path,
		displayName: node.displayName || node.path,
		nodeType: node.nodeType,
		dataType: node.dataType,
		isReadable: node.isReadable,
		isWritable: node.isWritable,
		sourceId: node.sourceId,
		hasChildren: node.nodeType === "Folder",
	};
	rowIndex.set(row.path, row);
	return row;
};

const loadAddressSpace = async () => {
	loadingAddress.value = true;
	rowIndex.clear();
	try {
		const nodes = await machineConnectionPointsApi.browseAddressSpace(deviceForm.deviceId);
		addressRows.value = nodes.map(toRow);
	} catch (e: unknown) {
		addressRows.value = [];
		ElMessage.error(getErr(e, "加载地址空间失败"));
	} finally {
		loadingAddress.value = false;
	}
};

const loadChildNodes = async (
	row: AddressRow,
	_treeNode: unknown,
	resolve: (children: AddressRow[]) => void,
) => {
	try {
		const nodes = await machineConnectionPointsApi.browseAddressSpace(
			deviceForm.deviceId,
			row.path,
		);
		resolve(nodes.map(toRow));
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载子节点失败"));
		resolve([]);
	}
};

const isSelectable = (row: AddressRow) =>
	row.nodeType === "Variable" && row.isReadable !== false;

const handleSelectionChange = (rows: AddressRow[]) => {
	selectedNodes.value = rows.filter(isSelectable);
};

const applyReadResults = (tags: { address: string; value: unknown; quality: string; timestamp: string; errorMessage?: string | null }[]) => {
	for (const tag of tags) {
		const row = rowIndex.get(tag.address);
		if (!row) continue;
		row.value = tag.value;
		row.quality = tag.errorMessage ? "Bad" : tag.quality;
		row.timestamp = tag.timestamp;
	}
};

// 最近一次读取的目标点位，供自动刷新复用
let readTargets: AddressRow[] = [];

const readRows = async (rows: AddressRow[], silent = false) => {
	if (!rows.length) return;
	reading.value = true;
	try {
		const res = await machineConnectionPointsApi.readTags(deviceForm.deviceId, {
			tags: rows.map((r) => ({
				address: r.path,
				dataType: r.dataType || "String",
				sourceId: r.sourceId ?? undefined,
			})),
		});
		applyReadResults(res.tags);
		readTargets = rows;
		if (!silent) ElMessage.success(`已读取 ${res.tags.length} 个点位`);
	} catch (e: unknown) {
		if (!silent) ElMessage.error(getErr(e, "读取失败"));
	} finally {
		reading.value = false;
	}
};

const readSelected = () => readRows(selectedNodes.value);
const readOne = (row: AddressRow) => readRows([row]);

const isBoolWritable = (row: AddressRow) =>
	row.nodeType === "Variable" &&
	row.isWritable === true &&
	(row.dataType ?? "").toLowerCase() === "bool" &&
	row.value !== undefined;

// Bool 点位开关 = 真实下发写入
const writeBool = async (row: AddressRow, value: boolean) => {
	writingPath.value = row.path;
	try {
		const res = await machineConnectionPointsApi.writeTags(deviceForm.deviceId, {
			tags: [{ address: row.path, dataType: "Bool", value }],
		});
		const result = res.results[0];
		if (result?.success) {
			row.value = value;
			ElMessage.success(`已写入 ${row.displayName} = ${value}`);
		} else {
			ElMessage.error(result?.errorMessage ?? "写入失败");
		}
		await readRows([row], true);
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "写入失败"));
	} finally {
		writingPath.value = "";
	}
};

// ---------- 自动刷新（重读已读点位） ----------
const autoRefresh = ref(false);
const refreshInterval = ref("2000");
let refreshTimer: number | null = null;

const stopAutoRefresh = () => {
	if (refreshTimer !== null) {
		clearInterval(refreshTimer);
		refreshTimer = null;
	}
};

const restartAutoRefresh = () => {
	stopAutoRefresh();
	if (autoRefresh.value && isConnected.value) {
		refreshTimer = window.setInterval(() => {
			const targets = readTargets.length ? readTargets : selectedNodes.value;
			if (targets.length) void readRows(targets, true);
		}, parseInt(refreshInterval.value));
	}
};

// ---------- 程序文件（真实 program-transfer） ----------
const programRows = ref<ProgramTransferFileItem[]>([]);
const loadingPrograms = ref(false);
const transferCapability = ref<ProgramTransferCapability | null>(null);
const downloadingPath = ref("");

const loadCapability = async () => {
	try {
		transferCapability.value = await machineConnectionProgramTransferApi.capabilities(
			deviceForm.deviceId,
		);
	} catch {
		transferCapability.value = null;
	}
};

const loadPrograms = async () => {
	loadingPrograms.value = true;
	try {
		programRows.value = await machineConnectionProgramTransferApi.files(
			deviceForm.deviceId,
			undefined,
			true,
		);
	} catch (e: unknown) {
		programRows.value = [];
		// 部分机器人协议不支持文件浏览，能力标签/Alert 已说明，这里静默为空即可
		if (transferCapability.value?.supportsBrowse !== false)
			ElMessage.warning(getErr(e, "加载程序文件失败"));
	} finally {
		loadingPrograms.value = false;
	}
};

const downloadProgram = async (row: ProgramTransferFileItem) => {
	downloadingPath.value = row.path;
	try {
		await machineConnectionProgramTransferApi.download(deviceForm.deviceId, row.path);
		ElMessage.success(`已下载：${row.name}`);
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "下载失败"));
	} finally {
		downloadingPath.value = "";
	}
};

// ---------- 工具 ----------
const formatValue = (value: unknown) => {
	if (value === null || value === undefined) return "-";
	if (typeof value === "object") return JSON.stringify(value);
	return String(value);
};

const formatFileSize = (size?: number): string => {
	if (!size || size < 0) return "-";
	if (size < 1024) return size + " B";
	if (size < 1024 * 1024) return (size / 1024).toFixed(2) + " KB";
	return (size / (1024 * 1024)).toFixed(2) + " MB";
};

onUnmounted(() => {
	stopAutoRefresh();
});

onMounted(() => {
	void loadDevices();
});
</script>

<style lang="scss" scoped>
.robot-data-browser-view {
	.data-browser-card {
		margin-bottom: 20px;
	}

	.card-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
	}

	.data-browser-content {
		padding: 8px 4px;
	}

	.device-selection {
		margin-bottom: 20px;
	}

	.connection-status {
		margin-top: 10px;
	}

	.browser-area {
		margin-top: 8px;
	}

	.tab-toolbar {
		display: flex;
		align-items: center;
		margin-bottom: 12px;
	}

	.not-connected {
		padding: 100px 0;
	}
}
</style>
