<template>
	<div class="machine-property-view">
		<h2 class="page-title">机床设备管理</h2>
		<div class="machine-toolbar">
			<div class="toolbar-actions">
				<el-button :loading="loading" @click="loadMachines">刷新列表</el-button>
				<el-button type="primary" :icon="Plus" @click="openAddMachineDialog">新增机床</el-button>
			</div>
			<el-input v-model="searchText" class="machine-search" clearable
				placeholder="搜索机床名称/设备编号/IP/协议" aria-label="搜索机床" />
		</div>
		<el-alert v-if="loadError" :title="loadError" type="error" show-icon :closable="false" />
		<div v-loading="loading" class="machine-list">
			<div class="machine-grid">
				<el-card v-for="machine in filteredMachines" :key="machine.id" class="machine-card" shadow="always">
					<div class="machine-heading">
						<h3>{{ machine.name }}</h3>
						<el-tag :type="machine.status === '在线' ? 'success' : machine.status === '故障' ? 'danger' : 'warning'"
							effect="dark" size="small">{{ machine.status }}</el-tag>
					</div>
					<p class="machine-code">设备编号：{{ machine.deviceCode }} | {{ machine.type }}</p>
					<dl class="machine-summary">
						<div><dt>机床类型</dt><dd>{{ machine.type }}</dd></div>
						<div><dt>品牌</dt><dd>{{ machine.brand || '-' }}</dd></div>
						<div><dt>协议类型</dt><dd>{{ machine.protocol }}</dd></div>
						<div><dt>IP地址</dt><dd>{{ machine.ip }}</dd></div>
						<div><dt>端口</dt><dd>{{ machine.port }}</dd></div>
						<div><dt>所属单位</dt><dd>{{ machine.organization || '-' }}</dd></div>
						<div class="last-communication"><dt>最后通讯</dt><dd>{{ formatLastSeen(machine.lastSeenAt) }}</dd></div>
					</dl>
					<div class="machine-actions">
						<el-button type="primary" link @click="showProperties(machine)">属性</el-button>
						<el-button type="warning" link @click="openEditMachineDialog(machine)">编辑</el-button>
						<el-button type="danger" link :loading="deletingMachineId === machine.id"
							@click="deleteMachine(machine)">删除</el-button>
					</div>
				</el-card>
			</div>
			<el-empty v-if="!loading && !loadError && !filteredMachines.length"
				:description="searchText.trim() ? '未找到匹配的机床' : '暂无机床，请新增机床'" />
		</div>

		<el-dialog v-model="propertyDialogVisible" width="min(900px, 94vw)" class="machine-property-dialog">
			<template #header>
				<div v-if="selectedMachine" class="card-header">
					<span>{{ selectedMachine.name }} 属性</span>
					<el-button type="primary" @click="openEditMachineDialog(selectedMachine)">
						<el-icon><Edit /></el-icon>
						编辑属性
					</el-button>
				</div>
			</template>

			<div v-if="selectedMachine" class="property-content">
				<el-row :gutter="20">
					<el-col :xs="24" :sm="12">
						<el-descriptions :column="1" border>
							<el-descriptions-item label="设备编号">{{ selectedMachine.deviceCode }}</el-descriptions-item>
							<el-descriptions-item label="机床名称">{{ selectedMachine.name }}</el-descriptions-item>
							<el-descriptions-item label="品牌">{{ selectedMachine.brand || '-' }}</el-descriptions-item>
							<el-descriptions-item label="机床类型">{{ selectedMachine.type }}</el-descriptions-item>
							<el-descriptions-item label="状态">{{ selectedMachine.status }}</el-descriptions-item>
							<el-descriptions-item label="IP地址">{{ selectedMachine.ip }}</el-descriptions-item>
							<el-descriptions-item label="端口">{{ selectedMachine.port }}</el-descriptions-item>
							<el-descriptions-item label="协议">{{ selectedMachine.protocol }}</el-descriptions-item>
						</el-descriptions>
					</el-col>
					<el-col :xs="24" :sm="12">
						<el-descriptions :column="1" border>
							<el-descriptions-item label="所属单位">{{ selectedMachine.organization || '-' }}</el-descriptions-item>
							<el-descriptions-item label="负责人">{{ selectedMachine.manager || '-' }}</el-descriptions-item>
							<el-descriptions-item label="联系电话">{{ selectedMachine.phone || '-' }}</el-descriptions-item>
							<el-descriptions-item label="安装位置">{{ selectedMachine.location || '-' }}</el-descriptions-item>
							<el-descriptions-item label="购买日期">{{ selectedMachine.purchaseDate || '-' }}</el-descriptions-item>
							<el-descriptions-item label="维护周期">{{ selectedMachine.maintenanceCycle || '-' }}</el-descriptions-item>
						</el-descriptions>
					</el-col>
				</el-row>

				<!-- 扩展属性 -->
				<div class="extended-properties" v-if="selectedMachine.extendedProperties && Object.keys(selectedMachine.extendedProperties).length > 0">
					<h3 class="section-title">扩展属性</h3>
					<el-table :data="extendedPropertiesList" style="width: 100%" border>
						<el-table-column prop="key" label="属性名" width="150" />
						<el-table-column prop="value" label="属性值" />
					</el-table>
				</div>
			</div>
		</el-dialog>

		<!-- 新增/编辑机床对话框 -->
		<el-dialog
			v-model="machineDialogVisible"
			:title="isEditing ? '编辑机床' : '新增机床'"
			width="min(600px, 94vw)"
		>
			<el-form :model="currentMachine" label-width="120px">
				<el-form-item label="设备编号" prop="deviceCode" required>
					<el-input
						v-model="currentMachine.deviceCode"
						placeholder="请输入设备编号"
					/>
				</el-form-item>
				<el-form-item label="机床名称" prop="name" required>
					<el-input
						v-model="currentMachine.name"
						placeholder="请输入机床名称"
					/>
				</el-form-item>
				<el-form-item label="机床类型" prop="type" required>
					<el-select
						v-model="currentMachine.type"
						placeholder="请选择机床类型"
						filterable allow-create default-first-option
					>
						<el-option label="车床" value="车床" />
						<el-option label="铣床" value="铣床" />
						<el-option label="加工中心" value="加工中心" />
						<el-option label="磨床" value="磨床" />
						<el-option label="钻床" value="钻床" />
					</el-select>
				</el-form-item>
				<el-form-item label="IP地址" prop="ip" required>
					<el-input v-model="currentMachine.ip" placeholder="请输入IP地址" />
				</el-form-item>
				<el-form-item label="品牌" prop="brand">
					<el-input
						v-model="currentMachine.brand"
						placeholder="请输入品牌"
					/>
				</el-form-item>
				<el-form-item label="端口" prop="port" required>
					<el-input-number
						v-model="currentMachine.port"
						:min="0"
						:max="65535"
						:step="1"
						style="width: 200px"
					/>
				</el-form-item>
				<el-form-item label="协议" prop="protocol" required>
					<el-select
						v-model="currentMachine.protocol"
						placeholder="请选择协议"
						filterable allow-create default-first-option
					>
						<el-option label="Modbus TCP" value="ModbusTCP" />
						<el-option label="西门子S7" value="SiemensS7" />
						<el-option label="OPC UA" value="OPCUA" />
						<el-option label="MQTT" value="MQTT" />
						<el-option label="FOCAS" value="FOCAS" />
						<el-option label="NCLink" value="NCLink" />
						<el-option label="NCLinkApi" value="NCLinkApi" />
						<el-option label="MTConnect" value="MTConnect" />
					</el-select>
				</el-form-item>
				<el-form-item label="所属单位" prop="organization">
					<el-input
						v-model="currentMachine.organization"
						placeholder="请输入所属单位"
					/>
				</el-form-item>
				<el-form-item label="负责人" prop="manager">
					<el-input
						v-model="currentMachine.manager"
						placeholder="请输入负责人"
					/>
				</el-form-item>
				<el-form-item label="联系电话" prop="phone">
					<el-input
						v-model="currentMachine.phone"
						placeholder="请输入联系电话"
					/>
				</el-form-item>
				<el-form-item label="安装位置" prop="location">
					<el-input
						v-model="currentMachine.location"
						placeholder="请输入安装位置"
					/>
				</el-form-item>
				<el-form-item label="购买日期" prop="purchaseDate">
					<el-date-picker
						v-model="currentMachine.purchaseDate"
						type="date"
						value-format="YYYY-MM-DD"
						placeholder="请选择购买日期"
						style="width: 100%"
					/>
				</el-form-item>
				<el-form-item label="维护周期" prop="maintenanceCycle">
					<el-input
						v-model="currentMachine.maintenanceCycle"
						placeholder="请输入维护周期（如：3个月）"
					/>
				</el-form-item>
			</el-form>
			<template #footer>
				<span class="dialog-footer">
					<el-button @click="machineDialogVisible = false">取消</el-button>
					<el-button type="primary" :loading="saving" @click="saveMachine">保存</el-button>
				</span>
			</template>
		</el-dialog>
	</div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted } from "vue";
import { Plus, Edit } from "@element-plus/icons-vue";
import { ElMessage, ElMessageBox } from "element-plus";
import {
	machineConnectionDevicesApi,
	type DeviceDto,
	type CreateDeviceRequest,
	type UpdateDeviceRequest,
} from "@/api/machineConnectionDevices";

interface Machine {
	id: string;
	deviceCode: string;
	name: string;
	brand: string;
	type: string;
	status: string;
	ip: string;
	port: number;
	protocol: string;
	lastSeenAt?: string | null;
	organization?: string;
	manager?: string;
	phone?: string;
	location?: string;
	purchaseDate?: string;
	maintenanceCycle?: string;
	extendedProperties?: Record<string, string>;
}

const machines = ref<Machine[]>([]);
const searchText = ref("");
const loading = ref(false);
const loadError = ref("");
const saving = ref(false);
const deletingMachineId = ref("");
const propertyDialogVisible = ref(false);
const selectedMachine = ref<Machine | null>(null);
const machineDialogVisible = ref(false);
const isEditing = ref(false);
const currentMachine = reactive<Machine>({
	id: "",
	deviceCode: "",
	name: "",
	brand: "CNC",
	type: "加工中心",
	status: "离线",
	ip: "",
	port: 502,
	protocol: "ModbusTCP",
	organization: "",
	manager: "",
	phone: "",
	location: "",
	purchaseDate: "",
	maintenanceCycle: "",
	extendedProperties: {},
});

const mapStatus = (status: string) =>
	status === "Online" ? "在线" : status === "Error" ? "故障" : "离线";

const toMachine = (device: DeviceDto): Machine => ({
	id: device.id,
	deviceCode: device.extendedProperties?.deviceCode || device.extendedProperties?.DeviceCode
		|| device.extendedProperties?.Code || device.extendedProperties?.code || device.id,
	name: device.name,
	brand: device.brand,
	type: device.model || "CNC",
	status: mapStatus(device.status),
	ip: device.host,
	port: device.port,
	protocol: device.protocol,
	lastSeenAt: device.lastSeenAt,
	organization: device.extendedProperties?.organization || "",
	manager: device.extendedProperties?.manager || "",
	phone: device.extendedProperties?.phone || "",
	location: device.extendedProperties?.location || "",
	purchaseDate: device.extendedProperties?.purchaseDate || "",
	maintenanceCycle: device.extendedProperties?.maintenanceCycle || "",
	extendedProperties: device.extendedProperties || {},
});

const buildExtendedProperties = () => ({
	...(currentMachine.extendedProperties || {}),
	deviceCode: currentMachine.deviceCode,
	DeviceCode: currentMachine.deviceCode,
	organization: currentMachine.organization || "",
	manager: currentMachine.manager || "",
	phone: currentMachine.phone || "",
	location: currentMachine.location || "",
	purchaseDate: currentMachine.purchaseDate || "",
	maintenanceCycle: currentMachine.maintenanceCycle || "",
});

const extendedPropertiesList = computed(() => {
	if (!selectedMachine.value?.extendedProperties) return [];
	return Object.entries(selectedMachine.value.extendedProperties).map(([key, value]) => ({
		key,
		value,
	}));
});

const filteredMachines = computed(() => {
	const keyword = searchText.value.trim().toLocaleLowerCase();
	return machines.value.filter((machine) =>
		[machine.name, machine.deviceCode, machine.ip, machine.protocol, machine.brand, machine.type]
			.some((value) => value.toLocaleLowerCase().includes(keyword)),
	);
});

const formatLastSeen = (value?: string | null) => {
	if (!value) return "-";
	const date = new Date(value);
	return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
};

const loadMachines = async () => {
	if (loading.value) return;
	loading.value = true;
	loadError.value = "";
	try {
		const list = await machineConnectionDevicesApi.list("CNC");
		machines.value = list.map(toMachine);
		if (selectedMachine.value) {
			selectedMachine.value = machines.value.find((machine) => machine.id === selectedMachine.value?.id) || null;
			if (!selectedMachine.value) propertyDialogVisible.value = false;
		}
	} catch (error) {
		loadError.value = error instanceof Error ? error.message : "加载机床失败，请重试";
	} finally {
		loading.value = false;
	}
};

const showProperties = (machine: Machine) => {
	selectedMachine.value = machine;
	propertyDialogVisible.value = true;
};

const openAddMachineDialog = () => {
	isEditing.value = false;
	Object.assign(currentMachine, {
		id: "",
		deviceCode: "",
		name: "",
		brand: "CNC",
		type: "加工中心",
		status: "离线",
		ip: "",
		port: 502,
		protocol: "ModbusTCP",
		organization: "",
		manager: "",
		phone: "",
		location: "",
		purchaseDate: "",
		maintenanceCycle: "",
		extendedProperties: {},
	});
	machineDialogVisible.value = true;
};

const openEditMachineDialog = (machine: Machine) => {
	isEditing.value = true;
	Object.assign(currentMachine, { ...machine });
	propertyDialogVisible.value = false;
	machineDialogVisible.value = true;
};

const saveMachine = async () => {
	if (saving.value) return;
	if (!currentMachine.deviceCode.trim() || !currentMachine.name.trim() || !currentMachine.ip.trim()
		|| currentMachine.port == null || !currentMachine.protocol) {
		ElMessage.warning("请填写必填字段");
		return;
	}

	const body: CreateDeviceRequest | UpdateDeviceRequest = {
		name: currentMachine.name,
		type: "CNC",
		brand: currentMachine.brand,
		model: currentMachine.type,
		protocol: currentMachine.protocol,
		host: currentMachine.ip,
		port: currentMachine.port,
		extendedProperties: buildExtendedProperties(),
	};

	saving.value = true;
	try {
		if (isEditing.value) {
			await machineConnectionDevicesApi.update(currentMachine.id, body as UpdateDeviceRequest);
			ElMessage.success("机床属性已保存");
		} else {
			await machineConnectionDevicesApi.create(body as CreateDeviceRequest);
			ElMessage.success("机床已创建");
		}
		machineDialogVisible.value = false;
		await loadMachines();
	} catch (error) {
		ElMessage.error(error instanceof Error ? error.message : "保存机床失败");
	} finally {
		saving.value = false;
	}
};

const deleteMachine = async (machine: Machine) => {
	if (deletingMachineId.value) return;
	try {
		await ElMessageBox.confirm(`确定删除机床“${machine.name}”？`, "删除机床", {
			type: "warning", confirmButtonText: "删除", cancelButtonText: "取消",
		});
		deletingMachineId.value = machine.id;
		await machineConnectionDevicesApi.remove(machine.id);
		ElMessage.success("机床已删除");
		await loadMachines();
	} catch (error) {
		if (error === "cancel" || error === "close") return;
		ElMessage.error(error instanceof Error ? error.message : "删除机床失败");
	} finally {
		deletingMachineId.value = "";
	}
};

onMounted(loadMachines);
</script>

<style lang="scss" scoped>
.machine-property-view {
	.page-title { font-size: 22px; color: var(--el-text-color-primary); }
	.machine-toolbar {
		display: flex;
		justify-content: space-between;
		align-items: center;
		gap: 16px;
		flex-wrap: wrap;
		margin-bottom: 20px;
	}
	.machine-search { width: 320px; max-width: 100%; }
	.machine-list { min-height: 180px; }
	.machine-grid {
		display: grid;
		grid-template-columns: repeat(4, minmax(0, 1fr));
		gap: 20px;
	}

	.machine-card {
		border-radius: 8px;
		:deep(.el-card__body) { display: flex; flex-direction: column; height: 100%; padding: 20px; }
	}
	.machine-heading {
		display: flex;
		align-items: flex-start;
		justify-content: space-between;
		gap: 12px;
		h3 { font-size: 20px; line-height: 28px; overflow-wrap: anywhere; }
		.el-tag { flex-shrink: 0; }
	}
	.machine-code {
		margin-top: 6px;
		min-height: 44px;
		color: var(--el-text-color-secondary);
		overflow-wrap: anywhere;
	}
	.machine-summary {
		display: grid;
		grid-template-columns: repeat(2, minmax(0, 1fr));
		gap: 14px 16px;
		margin-top: 12px;
		padding: 18px 0 24px;
		border-top: 1px solid var(--el-border-color);
		font-size: 12px;
		div { display: flex; align-items: baseline; gap: 8px; min-width: 0; }
		dt { flex-shrink: 0; color: var(--el-text-color-primary); }
		dd { margin: 0; overflow-wrap: anywhere; color: var(--el-text-color-regular); }
		.last-communication { grid-column: 1 / -1; }
	}
	.machine-actions {
		margin-top: auto;
		padding-top: 14px;
		border-top: 1px solid var(--el-border-color);
		.el-button { font-size: 12px; }
		.el-button + .el-button { margin-left: 20px; }
	}
	@media (max-width: 1599px) {
		.machine-grid { grid-template-columns: repeat(3, minmax(0, 1fr)); }
	}
	@media (max-width: 1199px) {
		.machine-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
	}
	@media (max-width: 800px) {
		.machine-grid { grid-template-columns: minmax(0, 1fr); }
		.machine-search { width: 100%; }
	}
}
.machine-property-dialog {
	.card-header { display: flex; justify-content: space-between; align-items: center; padding-right: 24px; gap: 12px; }
	.property-content { overflow-wrap: anywhere; }
	.section-title { font-size: 16px; margin-bottom: 15px; }
	.extended-properties { margin-top: 24px; }
}
</style>
