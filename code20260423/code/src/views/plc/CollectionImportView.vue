<template>
	<div class="plc-collection-import-view">
		<h2 class="page-title">PLC采集配置导入</h2>

		<el-card class="import-card">
			<template #header>
				<div class="card-header">
					<span>导入采集配置</span>
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
					<el-upload
						class="upload-demo"
						action=""
						:auto-upload="false"
						:on-change="handleFileChange"
						:show-file-list="true"
						accept=".json,.csv"
						drag
					>
						<el-icon class="el-icon--upload"><Upload /></el-icon>
						<div class="el-upload__text">
							将文件拖到此处，或<em>点击上传</em>
						</div>
						<template #tip>
							<div class="el-upload__tip">
								支持上传 .json 或 .csv 格式的采集配置文件
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
					<el-form :model="manualConfig" label-width="120px">
						<el-form-item label="目标设备ID" required>
							<el-input
								v-model="manualConfig.deviceId"
								placeholder="请输入后端设备ID"
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
						<el-form-item label="设备ID" required>
							<el-input
								v-model="manualConfig.deviceId"
								placeholder="请输入设备ID"
							/>
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
								<el-option label="布尔值" value="bool" />
								<el-option label="整数" value="int" />
								<el-option label="浮点数" value="float" />
								<el-option label="字符串" value="string" />
							</el-select>
						</el-form-item>
						<el-form-item label="采集频率" required>
							<el-input-number
								v-model="manualConfig.frequency"
								:min="1"
								:max="1000"
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
import { ref, reactive } from "vue";
import { Upload, View, Check } from "@element-plus/icons-vue";
import { ElMessage, ElMessageBox } from "element-plus";
import {
	machineConnectionCollectionApi,
	type CollectionDataType,
} from "@/api/machineConnectionCollection";

const importMethod = ref("file");
const uploadedFile = ref<File | null>(null);
const previewDialogVisible = ref(false);
const previewConfigData = ref<any[]>([]);

const manualConfig = reactive({
	name: "",
	deviceId: "",
	address: "",
	dataType: "Int32",
	frequency: 1000,
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

const handleFileChange = (file: any) => {
	uploadedFile.value = file.raw;
};

const previewFile = async (file: File) => {
	const text = await file.text();
	const rows = text.split(/\r?\n/).filter(Boolean).slice(0, 20);
	previewConfigData.value = rows.slice(1).map((line, index) => {
		const [address, dataType, groupName, intervalMs, displayName] = line.split(",");
		return {
			name: displayName || groupName || `第 ${index + 1} 行`,
			deviceId: manualConfig.deviceId || "请在设备ID中填写目标设备",
			address: address || "",
			dataType: dataType || "",
			frequency: Number(intervalMs || 0),
		};
	});
};

const previewConfig = async () => {
	if (importMethod.value === "file") {
		if (!uploadedFile.value) {
			ElMessage.warning("请先选择文件");
			return;
		}
		await previewFile(uploadedFile.value);
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

const importConfig = () => {
	if (importMethod.value === "file" && !uploadedFile.value) {
		ElMessage.warning("请先选择文件");
		return;
	}
	if (importMethod.value === "file" && !manualConfig.deviceId) {
		ElMessage.warning("文件导入前请在手动输入区域填写目标设备ID");
		return;
	}
	if (importMethod.value === "manual" && (!manualConfig.name || !manualConfig.deviceId || !manualConfig.address)) {
		ElMessage.warning("请填写完整的配置信息");
		return;
	}

	ElMessageBox.confirm("确定要导入配置吗？", "确认", {
		confirmButtonText: "确定",
		cancelButtonText: "取消",
		type: "warning",
	}).then(async () => {
		try {
			if (importMethod.value === "file" && uploadedFile.value) {
				const result = await machineConnectionCollectionApi.importTags(
					manualConfig.deviceId,
					uploadedFile.value,
				);
				ElMessage.success(`导入完成：成功 ${result.successCount} 条，失败 ${result.errorCount} 条`);
				uploadedFile.value = null;
			} else {
				await importManualConfig();
				ElMessage.success("配置导入成功");
			}
			Object.assign(manualConfig, {
				name: "",
				deviceId: "",
				address: "",
				dataType: "Int32",
				frequency: 1000,
			});
		} catch (error) {
			ElMessage.error(error instanceof Error ? error.message : "配置导入失败");
		}
	});
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
