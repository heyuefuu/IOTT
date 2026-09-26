<template>
	<div class="plc-collection-import-view">
		<h2 class="page-title">PLC采集配置导入</h2>
		<el-alert title="采样频率与批量导入" description="手动输入可设置单个点位的采集频率；CSV/JSON 批量导入可为不同分组设置不同的 IntervalMs。导入后在「采集任务管理」启动采集。" type="info" :closable="false" />

		<el-card class="import-card">
			<template #header>
				<div class="card-header">
					<span>导入采集配置</span>
					<el-button @click="openCollectionManage">采集任务管理</el-button>
				</div>
			</template>

			<div class="import-content">
				<!-- 导入方式选择 -->
				<div class="import-method">
					<h3>选择导入方式</h3>
					<el-radio-group
						v-model="importMethod"
						style="margin-bottom: 20px"
					>
						<el-radio label="file">文件导入</el-radio>
						<el-radio label="manual">手动输入</el-radio>
					</el-radio-group>
				</div>

				<!-- 文件导入 -->
				<div v-if="importMethod === 'file'" class="file-import-section">
					<el-form :inline="true" style="margin-bottom: 12px">
						<el-form-item label="文件类型">
							<el-radio-group v-model="fileType">
								<el-radio-button label="standard">采集配置</el-radio-button>
								<el-radio-button label="tia">TIA 符号表</el-radio-button>
							</el-radio-group>
						</el-form-item>
					</el-form>

					<el-upload
						ref="uploadRef"
						class="upload-demo"
						action=""
						:auto-upload="false"
						:on-change="handleFileChange"
						:on-remove="handleFileRemove"
						:show-file-list="true"
						:accept="fileType === 'tia' ? '.csv' : '.json,.csv'"
						drag
					>
						<el-icon class="el-icon--upload"><Upload /></el-icon>
						<div class="el-upload__text">
							将文件拖到此处，或<em>点击上传</em>
						</div>
						<template #tip>
							<div class="el-upload__tip">
								{{ fileType === "tia"
									? "TIA 符号表仅支持 .csv，需包含 Name、Data Type、Address"
									: "支持上传 .json 或 .csv 格式的采集配置文件" }}
							</div>
						</template>
					</el-upload>

					<div v-if="uploadedFile" class="file-info">
						<el-alert
							:title="`已选择文件: ${uploadedFile.name}`"
							type="success"
							show-icon
							style="margin: 10px 0"
						/>
					</div>
					<el-form :model="fileConfig" label-width="120px">
						<el-form-item label="目标设备" required>
							<el-select
								v-model="fileConfig.deviceId"
								placeholder="请选择已注册的 PLC 设备"
								filterable :loading="devicesLoading" style="width: 100%"
								@visible-change="(visible: boolean) => visible && loadDevices()"
							>
								<el-option v-for="device in devices" :key="device.id" :value="device.id"
									:label="`${device.name} (${device.protocol} · ${device.host}:${device.port}) [${device.id}]`" />
							</el-select>
						</el-form-item>
						<el-form-item v-if="fileType === 'tia'" label="采集频率" required>
							<el-input-number
								v-model="fileConfig.frequency"
								:min="100"
								:max="60000"
								:step="100"
								style="width: 200px"
							/>
							<span style="margin-left: 10px">ms</span>
						</el-form-item>
						<el-form-item v-if="fileType === 'tia'" label="分组名称">
							<el-input
								v-model="fileConfig.groupName"
								placeholder="默认 TIA Import"
							/>
						</el-form-item>
					</el-form>
				</div>

				<!-- 手动输入 -->
				<div v-else class="manual-import-section">
					<el-form :model="manualConfig" label-width="120px">
						<el-form-item label="配置名称" required>
							<el-input
								v-model="manualConfig.name"
								placeholder="请输入配置名称"
							/>
						</el-form-item>
						<el-form-item label="目标设备" required>
							<el-select
								v-model="manualConfig.deviceId"
								placeholder="请选择已注册的 PLC 设备"
								filterable :loading="devicesLoading" style="width: 100%"
								@visible-change="(visible: boolean) => visible && loadDevices()"
							>
								<el-option v-for="device in devices" :key="device.id" :value="device.id"
									:label="`${device.name} (${device.protocol} · ${device.host}:${device.port}) [${device.id}]`" />
							</el-select>
						</el-form-item>
						<el-form-item label="采集地址" required>
							<el-input
								v-model="manualConfig.address"
								placeholder="请输入采集地址"
							/>
						</el-form-item>
						<el-form-item label="数据类型" required>
							<el-select
								v-model="manualConfig.dataType"
								placeholder="请选择数据类型"
							>
								<el-option label="布尔值（Bool）" value="Bool" />
								<el-option label="8位有符号整数（Int8）" value="Int8" />
								<el-option label="8位无符号整数（UInt8）" value="UInt8" />
								<el-option label="16位有符号整数（Int16）" value="Int16" />
								<el-option label="16位无符号整数（UInt16）" value="UInt16" />
								<el-option label="整数（Int32）" value="Int32" />
								<el-option label="32位无符号整数（UInt32）" value="UInt32" />
								<el-option label="64位有符号整数（Int64）" value="Int64" />
								<el-option label="64位无符号整数（UInt64）" value="UInt64" />
								<el-option label="浮点数（Float）" value="Float" />
								<el-option label="双精度浮点数（Double）" value="Double" />
								<el-option label="字符串（String）" value="String" />
								<el-option label="字节数组（ByteArray）" value="ByteArray" />
							</el-select>
						</el-form-item>
						<el-form-item label="采集频率" required>
							<el-input-number
								v-model="manualConfig.frequency"
								:min="1"
								:max="60000"
								:precision="0"
								:step="1"
								style="width: 200px"
							/>
							<span style="margin-left: 10px">ms</span>
						</el-form-item>
					</el-form>
				</div>

				<!-- 预览和导入按钮 -->
				<div class="preview-import-section">
					<el-button type="primary" @click="previewConfig">
						<el-icon><View /></el-icon>
						预览配置
					</el-button>
					<el-button
						type="success"
						@click="importConfig"
						style="margin-left: 10px"
					>
						<el-icon><Check /></el-icon>
						确认导入
					</el-button>
				</div>
			</div>
		</el-card>

		<!-- 预览对话框 -->
		<el-dialog
			v-model="previewDialogVisible"
			title="配置预览"
			width="600px"
		>
			<div class="config-preview">
				<el-table :data="previewConfigData" style="width: 100%" border>
					<el-table-column prop="name" label="配置名称" />
					<el-table-column prop="deviceId" label="设备ID" />
					<el-table-column prop="address" label="采集地址" />
					<el-table-column prop="dataType" label="数据类型" />
					<el-table-column prop="frequency" label="采集频率(ms)" />
					<el-table-column prop="groupName" label="分组" />
				</el-table>
			</div>
			<template #footer>
				<span class="dialog-footer">
					<el-button @click="previewDialogVisible = false"
						>关闭</el-button
					>
				</span>
			</template>
		</el-dialog>
	</div>
</template>

<script setup lang="ts">
import { ref, reactive, watch, onMounted } from "vue";
import { useRoute, useRouter } from "vue-router";
import { Upload, View, Check } from "@element-plus/icons-vue";
import {
	ElMessage,
	ElMessageBox,
	type UploadFile,
	type UploadInstance,
} from "element-plus";
import {
	machineConnectionCollectionApi,
	type CollectionDataType,
} from "@/api/machineConnectionCollection";
import { machineConnectionDevicesApi, type DeviceDto } from "@/api/machineConnectionDevices";
import {
	parseTiaSymbolTableCsv,
	readImportFileText,
	toCollectionImportCsvFile,
} from "@/utils/tiaSymbolTable";

const importMethod = ref("file");
const uploadedFile = ref<File | null>(null);
const previewDialogVisible = ref(false);
const previewConfigData = ref<any[]>([]);
const fileType = ref<"standard" | "tia">("standard");
const uploadRef = ref<UploadInstance>();

const clearUploadedFile = () => {
	uploadedFile.value = null;
	uploadRef.value?.clearFiles();
};

watch(fileType, clearUploadedFile);

const fileConfig = reactive({ deviceId: "", frequency: 1000, groupName: "" });

const manualConfig = reactive({
	name: "",
	deviceId: "",
	address: "",
	dataType: "Int32",
	frequency: 1000,
});

const devices = ref<DeviceDto[]>([]);
const devicesLoading = ref(false);
const route = useRoute();
const router = useRouter();
const openCollectionManage = () => {
	const deviceId = importMethod.value === "file" ? fileConfig.deviceId : manualConfig.deviceId;
	void router.push({ path: "/collection/manage", query: deviceId ? { deviceId } : {} });
};
const getImportError = (error: unknown, fallback: string): string => {
	const requestError = error as {
		response?: { data?: string | { error?: string; detail?: string } };
		message?: string;
	} | null;
	const data = requestError?.response?.data;
	if (typeof data === "string" && data.trim()) return data;
	return data && typeof data === "object"
		? data.error || data.detail || requestError?.message || fallback
		: requestError?.message || fallback;
};

const loadDevices = async () => {
	if (devicesLoading.value) return;
	devicesLoading.value = true;
	try {
		devices.value = await machineConnectionDevicesApi.list("PLC");
	} catch (error) {
		ElMessage.error(getImportError(error, "加载 PLC 设备列表失败，请重新展开下拉框重试"));
	} finally {
		devicesLoading.value = false;
	}
};

onMounted(async () => {
	await loadDevices();
	const deviceId = route.query.deviceId;
	if (typeof deviceId === "string" && devices.value.some((device) => device.id === deviceId)) {
		fileConfig.deviceId = deviceId;
		manualConfig.deviceId = deviceId;
	}
});

const normalizeDataType = (value: string): CollectionDataType => {
	switch (value.toLowerCase()) {
		case "bool": return "Bool";
		case "int": return "Int32";
		case "float": return "Float";
		case "double": return "Double";
		case "string": return "String";
		default: return value as CollectionDataType;
	}
};

const handleFileChange = (file: UploadFile) => {
	const rawFile = file.raw;
	if (!rawFile) return;

	const extension = /\.[^.]+$/.exec(rawFile.name)?.[0]?.toLowerCase() ?? "";
	const supportedExtensions = fileType.value === "tia" ? [".csv"] : [".json", ".csv"];
	if (!supportedExtensions.includes(extension)) {
		ElMessage.error(fileType.value === "tia" ? "TIA 符号表仅支持 .csv 文件" : "仅支持 .json 或 .csv 文件");
		clearUploadedFile();
		return;
	}

	uploadedFile.value = rawFile;
};

const handleFileRemove = (_file: UploadFile, remainingFiles: UploadFile[]) => {
	uploadedFile.value = remainingFiles[remainingFiles.length - 1]?.raw ?? null;
};

const parseTiaFile = async (file: File) => {
	if (!Number.isInteger(fileConfig.frequency) || fileConfig.frequency < 100 || fileConfig.frequency > 60000) {
		throw new Error("采集频率应为 100–60000 ms 的整数");
	}
	const rows = parseTiaSymbolTableCsv(await readImportFileText(file), {
		defaultIntervalMs: fileConfig.frequency,
		defaultGroupName: fileConfig.groupName.trim() || "TIA Import",
	});
	if (rows.length === 0) {
		throw new Error("TIA 符号表没有可导入的变量");
	}
	return rows;
};

const previewFile = async (file: File) => {
	if (fileType.value === "tia") {
		const rows = (await parseTiaFile(file)).slice(0, 20);
		previewConfigData.value = rows.map((row) => ({
			name: row.displayName,
			deviceId: fileConfig.deviceId,
			address: row.address,
			dataType: row.dataType,
			frequency: row.intervalMs,
			groupName: row.groupName,
		}));
		return;
	}

	const text = await readImportFileText(file);
	const rows = text.split(/\r?\n/).filter(Boolean).slice(0, 20);
	previewConfigData.value = rows.slice(1).map((line, index) => {
		const [address, dataType, groupName, intervalMs, displayName] = line.split(",");
		return {
			name: displayName || groupName || `第 ${index + 1} 行`,
			deviceId: fileConfig.deviceId || "请先选择目标设备",
			address: address || "",
			dataType: dataType || "",
			frequency: Number(intervalMs || 0),
			groupName: groupName || "",
		};
	});
};

const previewConfig = async () => {
	if (importMethod.value === "file") {
		if (!uploadedFile.value) {
			ElMessage.warning("请先选择文件");
			return;
		}
		try {
			await previewFile(uploadedFile.value);
		} catch (error) {
			ElMessage.error(error instanceof Error ? error.message : "文件解析失败");
			return;
		}
	} else {
		previewConfigData.value = [{ ...manualConfig }];
	}
	previewDialogVisible.value = true;
};

const importManualConfig = async () => {
	await machineConnectionCollectionApi.createProfile(manualConfig.deviceId, {
		name: manualConfig.name,
		groups: [
			{
				groupName: manualConfig.name,
				intervalMs: manualConfig.frequency,
				tags: [
					{
						address: manualConfig.address,
						dataType: normalizeDataType(manualConfig.dataType),
						displayName: manualConfig.name,
					},
				],
			},
		],
	});
};

const getImportFile = async (file: File) => {
	if (fileType.value === "standard") {
		return new File([await readImportFileText(file)], file.name, {
			type: file.name.toLowerCase().endsWith(".json") ? "application/json" : "text/csv",
		});
	}

	const rows = await parseTiaFile(file);
	const baseName = file.name.replace(/\.[^.]+$/, "");
	return toCollectionImportCsvFile(rows, `${baseName}-collection.csv`);
};

const importConfig = () => {
	if (importMethod.value === "manual" && (!Number.isInteger(manualConfig.frequency) || manualConfig.frequency < 1 || manualConfig.frequency > 60000)) {
		ElMessage.warning("采样周期应为 1–60000 ms 的整数");
		return;
	}
	if (importMethod.value === "file" && !uploadedFile.value) {
		ElMessage.warning("请先选择文件");
		return;
	}
	if (importMethod.value === "file" && fileType.value === "tia" &&
		(!Number.isInteger(fileConfig.frequency) || fileConfig.frequency < 100 || fileConfig.frequency > 60000)) {
		ElMessage.warning("请填写有效的采集频率");
		return;
	}
	if (importMethod.value === "manual" && (!manualConfig.name || !manualConfig.deviceId || !manualConfig.address)) {
		ElMessage.warning("请填写完整的配置信息");
		return;
	}

	const deviceId = importMethod.value === "file" ? fileConfig.deviceId : manualConfig.deviceId;
	if (!devices.value.some((device) => device.id === deviceId)) {
		ElMessage.warning("请从列表选择已注册的 PLC 设备，不要填写设备名称或编号");
		return;
	}

	return ElMessageBox.confirm("确定要导入配置吗？", "确认", {
		confirmButtonText: "确定",
		cancelButtonText: "取消",
		type: "warning",
	}).then(async () => {
		try {
			if (importMethod.value === "file" && uploadedFile.value) {
				const importFile = await getImportFile(uploadedFile.value);
				const result = await machineConnectionCollectionApi.importTags(
					fileConfig.deviceId,
					importFile,
				);
				ElMessage.success(`导入完成：成功 ${result.successCount} 条，失败 ${result.errorCount} 条`);
				clearUploadedFile();
				Object.assign(fileConfig, { frequency: 1000, groupName: "" });
			} else {
				await importManualConfig();
				ElMessage.success("配置导入成功");
				Object.assign(manualConfig, {
					name: "",
					address: "",
					dataType: "Int32",
					frequency: 1000,
				});
			}
		} catch (error) {
			ElMessage.error(getImportError(error, "配置导入失败"));
		}
	}).catch(() => {});
};
</script>

<style lang="scss" scoped>
.plc-collection-import-view {
	.import-card {
		margin-bottom: 20px;
	}

	.card-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
	}

	.import-content {
		padding: 20px;
	}

	.import-method {
		margin-bottom: 30px;
	}

	.file-import-section,
	.manual-import-section {
		margin-bottom: 30px;
	}

	.preview-import-section {
		margin-top: 20px;
	}

	.file-info {
		margin-top: 10px;
	}
}
</style>
