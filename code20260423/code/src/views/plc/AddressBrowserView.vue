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
				<el-form-item>
					<el-button
						type="primary"
						@click="loadAddressSpace"
						:loading="loading"
					>
						<el-icon><Refresh /></el-icon>
						浏览设备地址
					</el-button>
					<el-button
						type="success"
						@click="$router.push('/plc/rw')"
					>
						<el-icon><View /></el-icon>
						按地址读写
					</el-button>
				</el-form-item>
			</el-form>
		</el-card>

		<el-alert
			v-if="browseNotice"
			class="address-space-card"
			:title="browseNotice"
			:type="browseNoticeType"
			:closable="false"
			show-icon
		/>

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
				:key="addressSpaceVersion"
				v-model:expanded-keys="expandedKeys"
				:data="addressSpace"
				:props="addressTreeProps"
				show-checkbox
				node-key="address"
				lazy
				:load="loadAddressChildren"
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
					<span>地址查询</span>
				</div>
			</template>
			<el-form :model="searchForm" label-width="120px">
				<p>目录搜索只筛选已加载节点；按地址读取会请求设备，不代表发现了点位名称或类型。</p>
				<el-form-item label="地址/名称" prop="searchAddress">
					<el-input
						v-model="searchForm.searchAddress"
						placeholder="目录搜索可用名称，直接读取请输入完整地址"
						prefix-icon="Search"
					/>
				</el-form-item>
				<el-form-item label="读取数据类型" prop="dataType">
					<el-select v-model="searchForm.dataType" placeholder="直接读取时必须指定类型" clearable>
						<el-option v-for="dataType in directReadDataTypes" :key="dataType" :label="dataType" :value="dataType" />
					</el-select>
				</el-form-item>
				<el-form-item>
					<el-button
						type="primary"
						@click="searchAddress"
						:disabled="addressSpace.length === 0 || loading"
					>
						<el-icon><Search /></el-icon>
						搜索已加载目录
					</el-button>
					<el-button type="success" @click="readExactAddress" :loading="directRead.loading">
						<el-icon><View /></el-icon>
						按地址读取
					</el-button>
					<el-button @click="resetSearch"> 重置 </el-button>
				</el-form-item>
			</el-form>

			<el-alert
				v-if="directRead.error"
				:title="directRead.error"
				type="error"
				:closable="false"
				show-icon
			/>
			<el-descriptions
				v-if="directRead.result"
				title="按地址读取结果（非目录发现）"
				:column="2"
				border
				class="search-results"
			>
				<el-descriptions-item label="地址">{{ directRead.result.address }}</el-descriptions-item>
				<el-descriptions-item label="读取类型">{{ directRead.result.dataType }}</el-descriptions-item>
				<el-descriptions-item label="值">{{ directRead.error ? "读取未成功" : (directRead.result.value ?? "无返回值") }}</el-descriptions-item>
				<el-descriptions-item label="质量">{{ directRead.result.quality }}</el-descriptions-item>
				<el-descriptions-item label="读取时间">{{ directRead.result.timestamp }}</el-descriptions-item>
			</el-descriptions>

			<!-- 搜索结果 -->
			<div class="search-results" v-if="searchResults.length > 0">
				<h4>已加载目录中的匹配节点</h4>
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
import { ref, reactive, onMounted, watch } from "vue";
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
import { ElMessage, type LoadFunction } from "element-plus";
import { machineConnectionDevicesApi } from "@/api/machineConnectionDevices";
import {
	machineConnectionPointsApi,
	type AddressNode,
	type DataTypeApi,
	type ReadTagResult,
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
	isLeaf?: boolean;
}

// 表单数据
const form = reactive({
	deviceId: "",
});

// 写入表单
const writeForm = reactive({
	value: "",
});

// 搜索表单
const searchForm = reactive({
	searchAddress: "",
	dataType: "" as DataTypeApi | "",
});
const directReadDataTypes: DataTypeApi[] = [
	"Bool", "Int8", "UInt8", "Int16", "UInt16", "Int32", "UInt32",
	"Int64", "UInt64", "Float", "Double", "String", "ByteArray",
];

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
		children: n.children ? n.children.map(mapNode) : [],
		isLeaf: n.nodeType !== "Folder",
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
const browseNotice = ref("");
const browseNoticeType = ref<"info" | "warning" | "error">("info");
const addressSpaceVersion = ref(0);
const expandedKeys = ref<string[]>([]);
const selectedAddress = ref<AddressItem | null>(null);
const selectedAddresses = ref<string[]>([]);

// 加载状态
const loading = ref(false);
const readingValue = ref(false);
const writingValue = ref(false);

// 搜索结果
const searchResults = ref<AddressItem[]>([]);
const directRead = reactive({
	loading: false,
	version: 0,
	error: "",
	result: null as ReadTagResult | null,
});
const clearExactAddressRead = () => {
	directRead.version += 1;
	directRead.loading = false;
	directRead.error = "";
	directRead.result = null;
	searchResults.value = [];
};
watch(
	[() => form.deviceId, () => searchForm.searchAddress, () => searchForm.dataType],
	clearExactAddressRead,
	{ flush: "sync" },
);

// 地址树属性
const addressTreeProps = {
	children: "children",
	label: "name",
	isLeaf: "isLeaf",
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
	clearExactAddressRead();
	addressSpaceVersion.value += 1;
	loading.value = false;
	browseNotice.value = "";
	browseNoticeType.value = "info";
	addressSpace.value = [];
	expandedKeys.value = [];
	selectedAddress.value = null;
	selectedAddresses.value = [];
	searchResults.value = [];
};

// 加载地址空间 = 真实浏览后端地址空间
const loadAddressSpace = async () => {
	const deviceId = form.deviceId;
	if (!deviceId) return false;

	handleDeviceChange();
	const requestVersion = addressSpaceVersion.value;
	loading.value = true;
	try {
		const nodes = await machineConnectionPointsApi.browseAddressSpace(
			deviceId,
			undefined,
			devices.value.find((device) => device.id === deviceId)?.protocol,
		);
		if (requestVersion !== addressSpaceVersion.value || deviceId !== form.deviceId) {
			return false;
		}
		addressSpace.value = nodes.map(mapNode);
		if (nodes.length === 0) {
			browseNotice.value = "设备未返回可浏览的地址目录，未生成任何预设点位。";
		}
		expandedKeys.value = addressSpace.value
			.filter((n) => n.type === "folder")
			.map((n) => n.address);
		return true;
	} catch (e: unknown) {
		if (requestVersion === addressSpaceVersion.value) {
			const response = (e as { response?: { data?: { code?: string } } })?.response;
			browseNotice.value = getErr(e, "加载地址空间失败");
			browseNoticeType.value = response?.data?.code === "ADDRESS_SPACE_BROWSING_NOT_SUPPORTED"
				? "warning" : "error";
			if (browseNoticeType.value === "error") ElMessage.error(browseNotice.value);
		}
		return false;
	} finally {
		if (requestVersion === addressSpaceVersion.value) loading.value = false;
	}
};

// 展开节点 = 按目录懒加载子地址
const loadAddressChildren: LoadFunction = async (node, resolve, reject) => {
	if (node.level === 0) {
		resolve(addressSpace.value);
		return;
	}
	const data = node.data as AddressItem | undefined;
	if (!data || data.type !== "folder") {
		resolve([]);
		return;
	}
	const deviceId = form.deviceId;
	const requestVersion = addressSpaceVersion.value;
	try {
		const nodes = await machineConnectionPointsApi.browseAddressSpace(
			deviceId,
			data.address,
			devices.value.find((device) => device.id === deviceId)?.protocol,
		);
		if (requestVersion !== addressSpaceVersion.value || deviceId !== form.deviceId) {
			reject();
			return;
		}
		data.children = nodes.map(mapNode);
		resolve(data.children);
	} catch (e: unknown) {
		if (requestVersion === addressSpaceVersion.value) {
			ElMessage.error(getErr(e, "加载子地址失败"));
		}
		reject();
	}
};

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

const readExactAddress = async () => {
	if (directRead.loading) return;
	const deviceId = form.deviceId;
	const address = searchForm.searchAddress.trim();
	const dataType = searchForm.dataType;
	if (!deviceId || !address || !dataType) {
		ElMessage.warning("请先选择设备，并输入完整地址及读取数据类型。");
		return;
	}
	clearExactAddressRead();
	const requestVersion = directRead.version;
	directRead.loading = true;
	try {
		const response = await machineConnectionPointsApi.readTags(deviceId, {
			tags: [{ address, dataType }],
		});
		if (requestVersion !== directRead.version) return;
		const tag = response.tags[0];
		if (!tag) throw new Error("设备未返回该地址的读取结果。");
		directRead.result = tag;
		if (tag.quality !== "Good" || tag.errorMessage) {
			directRead.error = tag.errorMessage || `读取质量为 ${tag.quality}，未确认有效值。`;
		}
	} catch (error: unknown) {
		if (requestVersion === directRead.version) {
			directRead.error = getErr(error, "读取地址失败");
		}
	} finally {
		if (requestVersion === directRead.version) directRead.loading = false;
	}
};

// 搜索地址 = 在已加载地址树内前端过滤
const searchAddress = () => {
	clearExactAddressRead();
	if (addressSpace.value.length === 0) {
		ElMessage.info("没有已加载的地址目录，请指定数据类型后使用按地址读取。");
		return;
	}
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
	if (searchResults.value.length === 0) ElMessage.info("已加载目录中没有匹配节点；这不代表该地址不可读取。");
};

// 重置搜索
const resetSearch = () => {
	searchForm.searchAddress = "";
	searchForm.dataType = "";
	clearExactAddressRead();
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
