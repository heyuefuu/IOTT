<template>
	<div class="plc-address-browser-view">
		<h2 class="page-title">PLC地址浏览器</h2>

		<!-- 设备选择 -->
		<el-card class="device-selection-card">
			<template #header>
				<div class="card-header">
					<span>设备选择</span>
				</div>
			</template>
			<el-form :model="form" label-width="120px">
				<el-form-item label="选择设备" prop="deviceId" required>
					<el-select
						v-model="form.deviceId"
						placeholder="请选择PLC设备"
						@change="handleDeviceChange"
					>
						<el-option
							v-for="device in devices"
							:key="device.id"
							:label="device.name"
							:value="device.id"
						>
							{{ device.name }} ({{ device.ip }}:{{
								device.port
							}})
						</el-option>
					</el-select>
				</el-form-item>
				<el-form-item label="地址类型" prop="addressType">
					<el-select
						v-model="form.addressType"
						placeholder="请选择地址类型"
					>
						<el-option label="线圈 (Coil)" value="coil" />
						<el-option
							label="离散输入 (Discrete Input)"
							value="input"
						/>
						<el-option
							label="保持寄存器 (Holding Register)"
							value="holding"
						/>
						<el-option
							label="输入寄存器 (Input Register)"
							value="inputRegister"
						/>
					</el-select>
				</el-form-item>
				<el-form-item>
					<el-button
						type="primary"
						@click="loadAddressSpace"
						:loading="loading"
					>
						<el-icon><Refresh /></el-icon>
						加载地址空间
					</el-button>
					<el-button
						type="success"
						@click="scanAddressSpace"
						:loading="scanning"
					>
						<el-icon><Search /></el-icon>
						扫描地址
					</el-button>
				</el-form-item>
			</el-form>
		</el-card>

		<!-- 地址空间树 -->
		<el-card class="address-space-card" v-if="addressSpace.length > 0">
			<template #header>
				<div class="card-header">
					<span>地址空间</span>
					<div class="header-actions">
						<el-button
							type="primary"
							size="small"
							@click="exportAddresses"
						>
							<el-icon><Download /></el-icon>
							导出地址
						</el-button>
						<el-button
							type="success"
							size="small"
							@click="addSelectedToCollection"
						>
							<el-icon><Plus /></el-icon>
							添加到采集
						</el-button>
					</div>
				</div>
			</template>

			<!-- 地址树 -->
			<el-tree
				v-model:expanded-keys="expandedKeys"
				:data="addressSpace"
				:props="addressTreeProps"
				show-checkbox
				node-key="address"
				@node-click="handleNodeClick"
				@check-change="handleCheckChange"
			>
				<template #default="{ data }">
					<div class="address-tree-node">
						<el-icon v-if="data.type === 'folder'">
							<Folder />
						</el-icon>
						<el-icon v-else>
							<DataLine />
						</el-icon>
						<span class="address-name">{{ data.name }}</span>
						<span
							class="address-value"
							v-if="
								data.type === 'point' &&
								data.value !== undefined
							"
						>
							{{ data.value }}
							<el-tag
								size="small"
								:type="
									data.quality === 'good'
										? 'success'
										: 'danger'
								"
							>
								{{ data.quality === "good" ? "有效" : "无效" }}
							</el-tag>
						</span>
					</div>
				</template>
			</el-tree>
		</el-card>

		<!-- 地址详情 -->
		<el-card class="address-details-card" v-if="selectedAddress">
			<template #header>
				<div class="card-header">
					<span>地址详情</span>
					<el-button
						type="primary"
						size="small"
						@click="readAddressValue"
						:loading="readingValue"
					>
						<el-icon><View /></el-icon>
						读取值
					</el-button>
				</div>
			</template>

			<el-descriptions :column="1">
				<el-descriptions-item label="地址">{{
					selectedAddress.address
				}}</el-descriptions-item>
				<el-descriptions-item label="名称">{{
					selectedAddress.name
				}}</el-descriptions-item>
				<el-descriptions-item label="类型">{{
					getAddressTypeName(selectedAddress.type)
				}}</el-descriptions-item>
				<el-descriptions-item label="当前值">{{
					selectedAddress.value !== undefined
						? selectedAddress.value
						: "未读取"
				}}</el-descriptions-item>
				<el-descriptions-item label="质量">{{
					selectedAddress.quality === "good" ? "有效" : "无效"
				}}</el-descriptions-item>
				<el-descriptions-item label="描述">{{
					selectedAddress.description || "无"
				}}</el-descriptions-item>
				<el-descriptions-item label="数据类型">{{
					selectedAddress.dataType || "未指定"
				}}</el-descriptions-item>
				<el-descriptions-item label="更新时间">{{
					selectedAddress.timestamp || "未更新"
				}}</el-descriptions-item>
			</el-descriptions>

			<!-- 写入值 -->
			<div
				class="write-section"
				v-if="
					selectedAddress.type !== 'input' &&
					selectedAddress.type !== 'inputRegister'
				"
			>
				<h4>写入值</h4>
				<el-form :model="writeForm" label-width="120px">
					<el-form-item label="新值" prop="value" required>
						<el-input
							v-model="writeForm.value"
							placeholder="请输入要写入的值"
						/>
					</el-form-item>
					<el-form-item>
						<el-button
							type="primary"
							@click="writeAddressValue"
							:loading="writingValue"
						>
							<el-icon><Edit /></el-icon>
							写入值
						</el-button>
					</el-form-item>
				</el-form>
			</div>
		</el-card>

		<!-- 地址搜索 -->
		<el-card class="address-search-card">
			<template #header>
				<div class="card-header">
					<span>地址搜索</span>
				</div>
			</template>
			<el-form :model="searchForm" label-width="120px">
				<el-form-item label="搜索地址" prop="searchAddress">
					<el-input
						v-model="searchForm.searchAddress"
						placeholder="请输入地址或名称"
						prefix-icon="Search"
					/>
				</el-form-item>
				<el-form-item>
					<el-button type="primary" @click="searchAddress">
						<el-icon><Search /></el-icon>
						搜索
					</el-button>
					<el-button @click="resetSearch"> 重置 </el-button>
				</el-form-item>
			</el-form>

			<!-- 搜索结果 -->
			<div class="search-results" v-if="searchResults.length > 0">
				<h4>搜索结果</h4>
				<el-table :data="searchResults" style="width: 100%" border>
					<el-table-column prop="address" label="地址" width="180" />
					<el-table-column prop="name" label="名称" />
					<el-table-column prop="type" label="类型" width="120">
						<template #default="scope">
							{{ getAddressTypeName(scope.row.type) }}
						</template>
					</el-table-column>
					<el-table-column prop="value" label="当前值" width="120" />
					<el-table-column label="操作" width="150">
						<template #default="scope">
							<el-button
								type="primary"
								size="small"
								@click="selectAddress(scope.row)"
							>
								查看
							</el-button>
						</template>
					</el-table-column>
				</el-table>
			</div>
		</el-card>
	</div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from "vue";
import {
	Refresh,
	Search,
	Download,
	Plus,
	View,
	Edit,
	Folder,
	DataLine,
} from "@element-plus/icons-vue";
import { ElMessage } from "element-plus";
import { machineConnectionDevicesApi } from "@/api/machineConnectionDevices";
import {
	machineConnectionPointsApi,
	type AddressNode,
} from "@/api/machineConnectionPoints";

// PLC设备类型定义
interface PLCDevice {
	id: string;
	name: string;
	ip: string;
	port: number;
	protocol: string;
	status: string;
}

// 地址类型定义
interface AddressItem {
	address: string;
	name: string;
	type: "folder" | "coil" | "input" | "holding" | "inputRegister" | "point";
	value?: any;
	quality?: "good" | "bad";
	description?: string;
	dataType?: string;
	timestamp?: string;
	children?: AddressItem[];
}

// 表单数据
const form = reactive({
	deviceId: "",
	addressType: "coil",
});

// 写入表单
const writeForm = reactive({
	value: "",
});

// 搜索表单
const searchForm = reactive({
	searchAddress: "",
});

// 设备列表（来自后端 /api/devices?type=PLC）
const devices = ref<PLCDevice[]>([]);

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

// 后端 AddressNode → 页面 AddressItem（递归）
function mapNode(n: AddressNode): AddressItem {
	return {
		address: n.path,
		name: n.displayName,
		type: n.nodeType === "Folder" ? "folder" : "point",
		dataType: n.dataType ?? undefined,
		children: n.children ? n.children.map(mapNode) : undefined,
	};
}

const loadDevices = async () => {
	try {
		const list = await machineConnectionDevicesApi.list("PLC");
		devices.value = list.map((d) => ({
			id: d.id,
			name: d.name,
			ip: d.host,
			port: d.port,
			protocol: d.protocol,
			status: d.status,
		}));
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载设备列表失败"));
	}
};

// 地址空间
const addressSpace = ref<AddressItem[]>([]);
const expandedKeys = ref<string[]>([]);
const selectedAddress = ref<AddressItem | null>(null);
const selectedAddresses = ref<string[]>([]);

// 加载状态
const loading = ref(false);
const scanning = ref(false);
const readingValue = ref(false);
const writingValue = ref(false);

// 搜索结果
const searchResults = ref<AddressItem[]>([]);

// 地址树属性
const addressTreeProps = {
	children: "children",
	label: "name",
};

// 初始化
onMounted(async () => {
	await loadDevices();
	// 默认选择第一个设备
	if (devices.value.length > 0) {
		form.deviceId = devices.value[0]?.id || "";
		await loadAddressSpace();
	}
});

// 处理设备变更
const handleDeviceChange = () => {
	addressSpace.value = [];
	expandedKeys.value = [];
	selectedAddress.value = null;
	selectedAddresses.value = [];
	searchResults.value = [];
};

// 加载地址空间 = 真实浏览后端地址空间
const loadAddressSpace = async () => {
	if (!form.deviceId) return;

	loading.value = true;
	try {
		const nodes = await machineConnectionPointsApi.browseAddressSpace(
			form.deviceId,
		);
		addressSpace.value = nodes.map(mapNode);
		expandedKeys.value = addressSpace.value
			.filter((n) => n.type === "folder")
			.map((n) => n.address);
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载地址空间失败"));
	} finally {
		loading.value = false;
	}
};

// 扫描地址空间 = 重新浏览
const scanAddressSpace = async () => {
	if (!form.deviceId) return;

	scanning.value = true;
	try {
		await loadAddressSpace();
		ElMessage.success("地址扫描完成");
	} finally {
		scanning.value = false;
	}
};

// 处理节点点击
const handleNodeClick = (data: AddressItem) => {
	if (data.type !== "folder") {
		selectAddress(data);
	}
};

// 处理地址选择
const handleCheckChange = (data: AddressItem, checked: boolean) => {
	if (data.type !== "folder") {
		if (checked) {
			selectedAddresses.value.push(data.address);
		} else {
			selectedAddresses.value = selectedAddresses.value.filter(
				(addr) => addr !== data.address,
			);
		}
	}
};

// 选择地址
const selectAddress = (address: AddressItem) => {
	selectedAddress.value = address;
	writeForm.value = address.value?.toString() || "";
};

// 读取地址值 = 真实点位读取
const readAddressValue = async () => {
	if (!selectedAddress.value) return;

	readingValue.value = true;
	try {
		const res = await machineConnectionPointsApi.readTags(form.deviceId, {
			tags: [
				{
					address: selectedAddress.value.address,
					dataType: selectedAddress.value.dataType ?? "String",
				},
			],
		});
		const tag = res.tags[0];
		if (tag) {
			selectedAddress.value.value = tag.value;
			selectedAddress.value.quality = tag.quality === "Good" ? "good" : "bad";
			selectedAddress.value.timestamp = tag.timestamp;
			if (tag.errorMessage) ElMessage.error(tag.errorMessage);
		}
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "读取失败"));
	} finally {
		readingValue.value = false;
	}
};

// 写入地址值 = 真实点位写入
const writeAddressValue = async () => {
	if (!selectedAddress.value) return;

	writingValue.value = true;
	try {
		const res = await machineConnectionPointsApi.writeTags(form.deviceId, {
			tags: [
				{
					address: selectedAddress.value.address,
					dataType: selectedAddress.value.dataType ?? "String",
					value: writeForm.value,
				},
			],
		});
		const r = res.results[0];
		if (r?.success) {
			selectedAddress.value.value = writeForm.value;
			selectedAddress.value.quality = "good";
			selectedAddress.value.timestamp = r.timestamp;
			ElMessage.success("写入成功");
		} else {
			ElMessage.error(r?.errorMessage ?? "写入失败");
		}
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "写入失败"));
	} finally {
		writingValue.value = false;
	}
};

// 搜索地址 = 在已加载地址树内前端过滤
const searchAddress = () => {
	const q = searchForm.searchAddress.trim().toLowerCase();
	if (!q) return;

	const flat: AddressItem[] = [];
	const walk = (items: AddressItem[]) => {
		for (const it of items) {
			if (it.type !== "folder") flat.push(it);
			if (it.children) walk(it.children);
		}
	};
	walk(addressSpace.value);

	searchResults.value = flat.filter(
		(it) =>
			it.address.toLowerCase().includes(q) ||
			it.name.toLowerCase().includes(q),
	);
	if (searchResults.value.length === 0) ElMessage.info("未找到匹配地址");
};

// 重置搜索
const resetSearch = () => {
	searchForm.searchAddress = "";
	searchResults.value = [];
};

// 导出地址 = 导出后端地址空间 CSV
const exportAddresses = async () => {
	if (!form.deviceId) {
		ElMessage.warning("请先选择设备");
		return;
	}
	try {
		const { blob, fileName } =
			await machineConnectionPointsApi.exportAddressSpace(
				form.deviceId,
				"CSV",
			);
		const url = URL.createObjectURL(blob);
		const a = document.createElement("a");
		a.href = url;
		a.download = fileName;
		a.click();
		URL.revokeObjectURL(url);
		ElMessage.success("地址空间已导出");
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "导出失败"));
	}
};

// 添加到采集
const addSelectedToCollection = () => {
	if (selectedAddresses.value.length === 0) {
		ElMessage.warning("请先选择点位");
		return;
	}
	ElMessage.info("请通过 PLC采集配置导入 页面写入后端采集配置");
};

// 获取地址类型名称
const getAddressTypeName = (type: string) => {
	const typeNames = {
		coil: "线圈",
		input: "离散输入",
		holding: "保持寄存器",
		inputRegister: "输入寄存器",
		point: "点位",
	};
	return typeNames[type as keyof typeof typeNames] || type;
};
</script>

<style lang="scss" scoped>
.plc-address-browser-view {
	.device-selection-card,
	.address-space-card,
	.address-details-card,
	.address-search-card {
		margin-bottom: 20px;
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

	.address-tree-node {
		display: flex;
		align-items: center;
		width: 100%;

		.address-name {
			margin-left: 8px;
			flex: 1;
		}

		.address-value {
			margin-left: 10px;
			display: flex;
			align-items: center;
			gap: 5px;
		}
	}

	.write-section {
		margin-top: 20px;
		padding-top: 20px;
		border-top: 1px solid #eaeaea;
	}

	.search-results {
		margin-top: 20px;
	}
}
</style>
