<template>
	<div class="plc-read-write-view">
		<h2 class="page-title">PLC数据读写</h2>

		<el-card class="rw-card">
			<template #header>
				<div class="card-header">
					<span>数据读写操作</span>
				</div>
			</template>

			<div class="rw-content">
				<!-- 设备选择 -->
				<div class="device-selection">
					<el-form :inline="true" :model="deviceForm">
						<el-form-item label="选择设备">
							<el-select
								v-model="deviceForm.deviceId"
								placeholder="请选择设备"
							>
								<el-option
									v-for="device in devices"
									:key="device.id"
									:label="device.name"
									:value="device.id"
								/>
							</el-select>
						</el-form-item>
						<el-form-item>
							<el-button type="primary" @click="connectDevice">
								<el-icon><Link /></el-icon>
								连接设备
							</el-button>
						</el-form-item>
						<el-form-item>
							<el-button @click="disconnectDevice">
								<el-icon><Link /></el-icon>
								断开连接
							</el-button>
						</el-form-item>
					</el-form>

					<div v-if="isConnected" class="connection-status">
						<el-tag type="success"
							>已连接到: {{ connectedDeviceName }}</el-tag
						>
					</div>
				</div>

				<!-- 读写操作区域 -->
				<div class="rw-operation">
					<el-tabs v-model="activeTab">
						<el-tab-pane label="单次读写" name="single">
							<div class="single-rw">
								<el-form :model="rwForm" label-width="120px">
									<el-form-item label="地址" required>
										<el-input
											v-model="rwForm.address"
											placeholder="请输入地址"
										/>
									</el-form-item>
									<el-form-item label="数据类型" required>
										<el-select
											v-model="rwForm.dataType"
											placeholder="请选择数据类型"
										>
											<el-option
												label="布尔值（位）"
												value="bool"
											/>
											<el-option
												label="16位整数（单字）"
												value="int16"
											/>
											<el-option
												label="16位无符号（单字）"
												value="uint16"
											/>
											<el-option
												label="32位整数（双字）"
												value="int"
											/>
											<el-option
												label="浮点数"
												value="float"
											/>
											<el-option
												label="字符串"
												value="string"
											/>
										</el-select>
									</el-form-item>
									<el-form-item label="值" required>
										<el-input
											v-model="rwForm.value"
											placeholder="请输入值"
										/>
									</el-form-item>
									<el-form-item>
										<el-button
											type="primary"
											@click="readData"
										>
											<el-icon><Reading /></el-icon>
											读取
										</el-button>
										<el-button
											type="success"
											@click="writeData"
											style="margin-left: 10px"
										>
											<el-icon><Edit /></el-icon>
											写入
										</el-button>
									</el-form-item>
								</el-form>

								<!-- 读写结果 -->
								<div v-if="rwResult" class="rw-result">
									<el-alert
										:title="`操作结果: ${rwResult.success ? '成功' : '失败'}`"
										:type="
											rwResult.success
												? 'success'
												: 'error'
										"
										:description="rwResult.message"
										show-icon
										style="margin: 10px 0"
									/>
								</div>
							</div>
						</el-tab-pane>

						<el-tab-pane label="批量读写" name="batch">
							<div class="batch-rw">
								<el-button
									type="primary"
									@click="addBatchItem"
									style="margin-bottom: 10px"
								>
									<el-icon><Plus /></el-icon>
									添加项目
								</el-button>

								<el-table
									:data="batchItems"
									style="width: 100%"
									border
								>
									<el-table-column
										prop="address"
										label="地址"
										width="150"
									>
										<template #default="scope">
											<el-input
												v-model="scope.row.address"
												placeholder="请输入地址"
											/>
										</template>
									</el-table-column>
									<el-table-column
										prop="dataType"
										label="数据类型"
										width="120"
									>
										<template #default="scope">
											<el-select
												v-model="scope.row.dataType"
												placeholder="请选择"
											>
												<el-option
													label="布尔值（位）"
													value="bool"
												/>
												<el-option
													label="16位整数（单字）"
													value="int16"
												/>
												<el-option
													label="16位无符号（单字）"
													value="uint16"
												/>
												<el-option
													label="32位整数（双字）"
													value="int"
												/>
												<el-option
													label="浮点数"
													value="float"
												/>
												<el-option
													label="字符串"
													value="string"
												/>
											</el-select>
										</template>
									</el-table-column>
									<el-table-column prop="value" label="值">
										<template #default="scope">
											<el-input
												v-model="scope.row.value"
												placeholder="请输入值"
											/>
										</template>
									</el-table-column>
									<el-table-column label="操作" width="100">
										<template #default="scope">
											<el-button
												type="danger"
												size="small"
												@click="
													removeBatchItem(
														scope.$index,
													)
												"
											>
												删除
											</el-button>
										</template>
									</el-table-column>
								</el-table>

								<div class="batch-buttons">
									<el-button
										type="primary"
										@click="batchRead"
									>
										<el-icon><Reading /></el-icon>
										批量读取
									</el-button>
									<el-button
										type="success"
										@click="batchWrite"
										style="margin-left: 10px"
									>
										<el-icon><Edit /></el-icon>
										批量写入
									</el-button>
								</div>
							</div>
						</el-tab-pane>
					</el-tabs>
				</div>
			</div>
		</el-card>
	</div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from "vue";
import { Link, Edit, Plus, Reading } from "@element-plus/icons-vue";
import { ElMessage, ElMessageBox } from "element-plus";
import { machineConnectionDevicesApi } from "@/api/machineConnectionDevices";
import { machineConnectionPointsApi } from "@/api/machineConnectionPoints";

// 设备列表（来自后端 /api/devices?type=PLC）
const devices = ref<{ id: string; name: string }[]>([]);

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

// 页面数据类型 → 后端 DataType 枚举名（不区分大小写）
function toApiDataType(t: string): string {
	switch (t) {
		case "bool":
			return "Bool";
		// 汇川 D/R/T/C 等寄存器都是 16 位单字，用 Int32 读会跨占两个寄存器、读出的是拼接值
		case "int16":
			return "Int16";
		case "uint16":
			return "UInt16";
		case "int":
			return "Int32";
		case "float":
			return "Float";
		default:
			return "String";
	}
}

// 写入值按类型转换
function coerceValue(t: string, v: string): unknown {
	if (t === "bool") return v === "true" || v === "1";
	if (t === "int" || t === "int16" || t === "uint16") return parseInt(v, 10);
	if (t === "float") return parseFloat(v);
	return v;
}

const loadDevices = async () => {
	try {
		const list = await machineConnectionDevicesApi.list("PLC");
		devices.value = list.map((d) => ({ id: d.id, name: d.name }));
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载设备列表失败"));
	}
};

// 设备表单
const deviceForm = reactive({ deviceId: "" });
const isConnected = ref(false);
const connectedDeviceName = ref("");

// 标签页
const activeTab = ref("single");

// 单次读写表单
const rwForm = reactive({
	address: "",
	dataType: "int",
	value: "",
});

// 读写结果
const rwResult = ref<any>(null);

// 批量读写
const batchItems = ref([{ address: "", dataType: "int", value: "" }]);

// 连接设备 = 真实连通性测试
const connectDevice = async () => {
	if (!deviceForm.deviceId) {
		ElMessage.warning("请选择设备");
		return;
	}
	const device = devices.value.find((d) => d.id === deviceForm.deviceId);
	if (!device) return;
	try {
		const r = await machineConnectionDevicesApi.testConnection(device.id);
		if (r.success) {
			isConnected.value = true;
			connectedDeviceName.value = device.name;
			ElMessage.success(`成功连接到设备: ${device.name}`);
		} else {
			isConnected.value = false;
			ElMessage.error(r.errorMessage ?? "连接失败");
		}
	} catch (e: unknown) {
		isConnected.value = false;
		ElMessage.error(getErr(e, "连接失败"));
	}
};

// 断开连接
const disconnectDevice = () => {
	if (!isConnected.value) {
		ElMessage.warning("当前未连接设备");
		return;
	}

	ElMessageBox.confirm("确定要断开连接吗？", "确认", {
		confirmButtonText: "确定",
		cancelButtonText: "取消",
		type: "warning",
	})
		.then(() => {
			isConnected.value = false;
			connectedDeviceName.value = "";
			ElMessage.success("已断开连接");
		})
		.catch(() => {
			// 取消断开
		});
};

// 读取数据 = 真实点位读取
const readData = async () => {
	if (!isConnected.value) {
		ElMessage.warning("请先连接设备");
		return;
	}

	if (!rwForm.address) {
		ElMessage.warning("请输入地址");
		return;
	}

	try {
		const res = await machineConnectionPointsApi.readTags(
			deviceForm.deviceId,
			{
				tags: [
					{
						address: rwForm.address,
						dataType: toApiDataType(rwForm.dataType),
					},
				],
			},
		);
		const tag = res.tags[0];
		if (tag && (tag.quality === "Good" || !tag.errorMessage)) {
			rwForm.value = String(tag.value ?? "");
			rwResult.value = {
				success: true,
				message: `读取成功，值为: ${rwForm.value}`,
			};
		} else {
			rwResult.value = {
				success: false,
				message: tag?.errorMessage ?? "读取失败",
			};
		}
	} catch (e: unknown) {
		rwResult.value = { success: false, message: getErr(e, "读取失败") };
	}
};

// 写入数据 = 真实点位写入
const writeData = async () => {
	if (!isConnected.value) {
		ElMessage.warning("请先连接设备");
		return;
	}

	if (!rwForm.address || !rwForm.value) {
		ElMessage.warning("请填写地址和值");
		return;
	}

	try {
		const res = await machineConnectionPointsApi.writeTags(
			deviceForm.deviceId,
			{
				tags: [
					{
						address: rwForm.address,
						dataType: toApiDataType(rwForm.dataType),
						value: coerceValue(rwForm.dataType, rwForm.value),
					},
				],
			},
		);
		const r = res.results[0];
		rwResult.value = {
			success: r?.success ?? false,
			message: r?.success
				? `写入成功，地址: ${rwForm.address}，值: ${rwForm.value}`
				: (r?.errorMessage ?? "写入失败"),
		};
	} catch (e: unknown) {
		rwResult.value = { success: false, message: getErr(e, "写入失败") };
	}
};

// 添加批量项目
const addBatchItem = () => {
	batchItems.value.push({ address: "", dataType: "int", value: "" });
};

// 删除批量项目
const removeBatchItem = (index: number) => {
	batchItems.value.splice(index, 1);
};

// 批量读取 = 真实批量读取
const batchRead = async () => {
	if (!isConnected.value) {
		ElMessage.warning("请先连接设备");
		return;
	}

	const valid = batchItems.value.filter((i) => i.address);
	if (valid.length === 0) {
		ElMessage.warning("请添加读取项目");
		return;
	}

	try {
		const res = await machineConnectionPointsApi.readTags(
			deviceForm.deviceId,
			{
				tags: valid.map((i) => ({
					address: i.address,
					dataType: toApiDataType(i.dataType),
				})),
			},
		);
		res.tags.forEach((tag) => {
			const item = batchItems.value.find((i) => i.address === tag.address);
			if (item) item.value = String(tag.value ?? "");
		});
		ElMessage.success("批量读取完成");
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "批量读取失败"));
	}
};

// 批量写入 = 真实批量写入
const batchWrite = async () => {
	if (!isConnected.value) {
		ElMessage.warning("请先连接设备");
		return;
	}

	const valid = batchItems.value.filter((i) => i.address);
	if (valid.length === 0) {
		ElMessage.warning("请添加写入项目");
		return;
	}

	try {
		const res = await machineConnectionPointsApi.writeTags(
			deviceForm.deviceId,
			{
				tags: valid.map((i) => ({
					address: i.address,
					dataType: toApiDataType(i.dataType),
					value: coerceValue(i.dataType, i.value),
				})),
			},
		);
		ElMessage.success(
			`批量写入完成：${res.successCount}/${res.totalCount} 成功`,
		);
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "批量写入失败"));
	}
};

onMounted(() => {
	void loadDevices();
});
</script>

<style lang="scss" scoped>
.plc-read-write-view {
	.rw-card {
		margin-bottom: 20px;
	}

	.card-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
	}

	.device-selection {
		margin-bottom: 20px;
	}

	.connection-status {
		margin-top: 10px;
	}

	.rw-operation {
		margin-top: 20px;
	}

	.single-rw {
		padding: 20px;
	}

	.batch-rw {
		padding: 20px;
	}

	.batch-buttons {
		margin-top: 20px;
	}

	.rw-result {
		margin-top: 20px;
	}
}
</style>
