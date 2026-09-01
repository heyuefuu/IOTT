<template>
	<div class="machine-property-view">
		<h2 class="page-title">机床属性管理</h2>

		<!-- 机床选择 -->
		<el-card class="machine-selector-card">
			<template #header>
				<div class="card-header">
					<span>机床选择</span>
					<el-button type="primary" @click="openAddMachineDialog">
						<el-icon><Plus /></el-icon>
						新增机床
					</el-button>
				</div>
			</template>
			<el-select
				v-model="selectedMachine"
				placeholder="请选择机床"
				style="width: 100%"
				@change="handleMachineChange"
			>
				<el-option
					v-for="machine in machines"
					:key="machine.id"
					:label="machine.name"
					:value="machine"
				>
					<div class="machine-option">
						<span>{{ machine.deviceCode }} - {{ machine.name }}</span>
						<el-tag
							:type="machine.status === '在线' ? 'success' : 'warning'"
							size="small"
							style="margin-left: 10px"
						>
							{{ machine.status }}
						</el-tag>
					</div>
				</el-option>
			</el-select>
		</el-card>

		<!-- 机床属性编辑 -->
		<el-card class="machine-property-card" v-if="selectedMachine">
			<template #header>
				<div class="card-header">
					<span>{{ selectedMachine.name }} 属性管理</span>
					<el-button type="primary" @click="openEditMachineDialog">
						<el-icon><Edit /></el-icon>
						编辑属性
					</el-button>
				</div>
			</template>

			<div class="property-content">
				<el-row :gutter="20">
					<el-col :span="12">
						<el-descriptions :column="1" border>
							<el-descriptions-item label="设备编号">{{ selectedMachine.deviceCode }}</el-descriptions-item>
							<el-descriptions-item label="机床名称">{{ selectedMachine.name }}</el-descriptions-item>
							<el-descriptions-item label="机床类型">{{ selectedMachine.type }}</el-descriptions-item>
							<el-descriptions-item label="状态">{{ selectedMachine.status }}</el-descriptions-item>
							<el-descriptions-item label="IP地址">{{ selectedMachine.ip }}</el-descriptions-item>
							<el-descriptions-item label="端口">{{ selectedMachine.port }}</el-descriptions-item>
							<el-descriptions-item label="协议">{{ selectedMachine.protocol }}</el-descriptions-item>
						</el-descriptions>
					</el-col>
					<el-col :span="12">
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
		</el-card>

		<!-- 新增/编辑机床对话框 -->
		<el-dialog
			v-model="machineDialogVisible"
			:title="isEditing ? '编辑机床' : '新增机床'"
			width="600px"
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
					>
						<el-option label="车床" value="车床" />
						<el-option label="铣床" value="铣床" />
						<el-option label="加工中心" value="加工中心" />
						<el-option label="磨床" value="磨床" />
						<el-option label="钻床" value="钻床" />
					</el-select>
				</el-form-item>
				<el-form-item label="IP地址" prop="ip" required>
					<el-input
						v-model="currentMachine.ip"
						placeholder="请输入IP地址"
					/>
				</el-form-item>
				<el-form-item label="端口" prop="port" required>
					<el-input-number
						v-model="currentMachine.port"
						:min="1"
						:max="65535"
						:step="1"
						style="width: 200px"
					/>
				</el-form-item>
				<el-form-item label="协议" prop="protocol" required>
					<el-select
						v-model="currentMachine.protocol"
						placeholder="请选择协议"
					>
						<el-option label="Modbus TCP" value="ModbusTCP" />
						<el-option label="西门子S7" value="SiemensS7" />
						<el-option label="OPC UA" value="OPCUA" />
						<el-option label="MQTT" value="MQTT" />
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
					<el-button type="primary" @click="saveMachine">保存</el-button>
				</span>
			</template>
		</el-dialog>
	</div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted } from "vue";
import { Plus, Edit } from "@element-plus/icons-vue";
import { ElMessage } from "element-plus";
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
	type: string;
	status: string;
	ip: string;
	port: number;
	protocol: string;
	organization?: string;
	manager?: string;
	phone?: string;
	location?: string;
	purchaseDate?: string;
	maintenanceCycle?: string;
	extendedProperties?: Record<string, string>;
}

const machines = ref<Machine[]>([]);
const selectedMachine = ref<Machine | null>(null);
const machineDialogVisible = ref(false);
const isEditing = ref(false);
const currentMachine = reactive<Machine>({
	id: "",
	deviceCode: "",
	name: "",
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
	deviceCode: device.extendedProperties?.deviceCode || device.id,
	name: device.name,
	type: device.model || "CNC",
	status: mapStatus(device.status),
	ip: device.host,
	port: device.port,
	protocol: device.protocol,
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

const loadMachines = async () => {
	try {
		const list = await machineConnectionDevicesApi.list("CNC");
		machines.value = list.map(toMachine);
		if (selectedMachine.value) {
			selectedMachine.value = machines.value.find((m) => m.id === selectedMachine.value?.id) || null;
		}
	} catch (error) {
		ElMessage.error(error instanceof Error ? error.message : "加载机床失败");
	}
};

const handleMachineChange = (machine: Machine) => {
	selectedMachine.value = machine;
};

const openAddMachineDialog = () => {
	isEditing.value = false;
	Object.assign(currentMachine, {
		id: "",
		deviceCode: "",
		name: "",
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

const openEditMachineDialog = () => {
	if (!selectedMachine.value) return;
	isEditing.value = true;
	Object.assign(currentMachine, { ...selectedMachine.value });
	machineDialogVisible.value = true;
};

const saveMachine = async () => {
	if (!currentMachine.deviceCode || !currentMachine.name || !currentMachine.ip || !currentMachine.port || !currentMachine.protocol) {
		ElMessage.warning("请填写必填字段");
		return;
	}

	const body: CreateDeviceRequest | UpdateDeviceRequest = {
		name: currentMachine.name,
		type: "CNC",
		brand: currentMachine.extendedProperties?.brand || "CNC",
		model: currentMachine.type,
		protocol: currentMachine.protocol,
		host: currentMachine.ip,
		port: currentMachine.port,
		extendedProperties: buildExtendedProperties(),
	};

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
	}
};

onMounted(loadMachines);
</script>

<style lang="scss" scoped>
.machine-property-view {
	.machine-selector-card,
	.machine-property-card {
		margin-bottom: 20px;
	}

	.card-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
	}

	.property-content {
		padding: 20px 0;
	}

	.section-title {
		font-size: 16px;
		font-weight: 600;
		margin-top: 30px;
		margin-bottom: 15px;
		color: #333;
	}

	.extended-properties {
		margin-top: 30px;
	}

	.machine-option {
		display: flex;
		align-items: center;
		justify-content: space-between;
		width: 100%;
	}
}
</style>
