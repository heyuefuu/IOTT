<template>
	<div class="plc-device-config-view">
		<h2 class="page-title">PLC设备管理</h2>

		<div class="device-layout">
			<el-card class="tree-panel" shadow="never">
				<template #header>
					<div class="tree-title">设备</div>
				</template>
				<el-input v-model="treeKeyword" placeholder="按设备名称或协议搜索" class="tree-search" />
				<el-tree ref="treeRef" :data="deviceTree" node-key="id" :default-expanded-keys="['all']"
					:expand-on-click-node="false" :filter-node-method="filterTreeNode" @node-click="handleTreeNodeClick" />
			</el-card>

			<div>
				<div class="action-bar">
					<el-button @click="refreshDeviceList">
						<el-icon><Refresh /></el-icon>
						刷新列表
					</el-button>
					<el-button type="primary" @click="openAddDeviceDialog">
						<el-icon><Plus /></el-icon>
						新增设备
					</el-button>
					<el-button @click="openCapabilitiesDialog">
						<el-icon><Grid /></el-icon>
						协议能力矩阵
					</el-button>
					<el-button @click="goTo('/plc/import')">采集导入</el-button>
					<el-input v-model="searchQuery" placeholder="搜索设备名称/编号/IP/协议"
						style="width: 300px; margin-left: auto" prefix-icon="Search" />
				</div>

				<div v-if="filteredDevices.length === 0" class="empty-hint">暂无 PLC 设备。</div>
				<div class="device-cards">
					<el-card v-for="device in filteredDevices" :key="device.id" :body-style="{ padding: '20px' }"
						class="device-card">
						<div class="card-header">
							<div class="device-info">
								<el-tag size="small" type="info">{{ device.status || "离线" }}</el-tag>
								<h3 class="device-name">{{ device.name }}</h3>
								<p class="device-code">设备编号：{{ device.deviceCode || "-" }} | {{ device.model || "-" }}</p>
							</div>
							<el-tag :type="getStatusType(device.status)" effect="dark">
								{{ device.status || "离线" }}
							</el-tag>
						</div>
						<div class="card-body">
							<el-descriptions :column="2" size="small">
								<el-descriptions-item label="协议类型">{{ device.protocol }}</el-descriptions-item>
								<el-descriptions-item label="IP地址">{{ device.ip }}</el-descriptions-item>
								<el-descriptions-item label="端口">{{ device.port }}</el-descriptions-item>
								<el-descriptions-item label="站号">{{ device.station ?? "-" }}</el-descriptions-item>
							</el-descriptions>
						</div>
						<div class="card-footer">
							<div class="card-footer-row">
								<el-button type="success" link size="small" @click="testConnection(device)">连接测试</el-button>
								<el-button type="primary" link size="small" @click="goTo('/plc/address')">地址浏览</el-button>
								<el-button type="warning" link size="small" @click="goTo('/plc/rw')">数据读写</el-button>
								<el-button type="info" link size="small" @click="goTo('/plc/status')">状态监控</el-button>
							</div>
							<div class="card-footer-row">
								<el-button type="primary" link size="small" @click="openEditDeviceDialog(device)">编辑</el-button>
								<el-button type="danger" link size="small" @click="deleteDevice(device.id)">删除</el-button>
							</div>
						</div>
					</el-card>
				</div>

				<div class="pagination-bar">
					<el-pagination v-model:current-page="currentPage" v-model:page-size="pageSize"
						:page-sizes="[10, 20, 50, 100]" layout="total, sizes, prev, pager, next, jumper"
						:total="filteredTotal" @size-change="handleSizeChange" @current-change="handleCurrentChange" />
				</div>
			</div>
		</div>

		<!-- 新增/编辑设备对话框 -->
		<el-dialog
			v-model="deviceDialogVisible"
			:title="isEditing ? '编辑设备' : '新增设备'"
			width="500px"
		>
			<el-form :model="currentDevice" label-width="120px">
				<el-form-item label="设备编号" prop="deviceCode" required>
					<el-input
						v-model="currentDevice.deviceCode"
						placeholder="请输入设备编号"
					/>
				</el-form-item>
				<el-form-item label="设备名称" prop="name" required>
					<el-input
						v-model="currentDevice.name"
						placeholder="请输入设备名称"
					/>
				</el-form-item>
				<el-form-item label="IP地址" prop="ip" required>
					<el-input
						v-model="currentDevice.ip"
						placeholder="请输入IP地址"
					/>
				</el-form-item>
				<el-form-item label="端口" prop="port" required>
					<el-input-number
						v-model="currentDevice.port"
						:min="1"
						:max="65535"
						:step="1"
						style="width: 200px"
					/>
				</el-form-item>
				<el-form-item label="协议类型" prop="protocol" required>
					<el-select
						v-model="currentDevice.protocol"
						placeholder="请选择协议类型"
						@change="handleProtocolChange"
					>
						<el-option label="Modbus TCP" value="ModbusTCP" />
						<el-option label="西门子S7" value="SiemensS7" />
						<el-option label="汇川 以太网(Inovance)" value="Inovance" />
						<el-option label="汇川 串口(InovanceSerial)" value="InovanceSerial" />
						<el-option label="汇川 串口透传TCP(InovanceSerialOverTcp)" value="InovanceSerialOverTcp" />
					</el-select>
				</el-form-item>
				<el-form-item label="设备型号" prop="model" required>
					<el-input
						v-model="currentDevice.model"
						placeholder="请输入设备型号"
					/>
				</el-form-item>
				<el-form-item label="站号" prop="station">
					<el-input-number
						v-model="currentDevice.station"
						:min="1"
						:max="255"
						:step="1"
						style="width: 200px"
					/>
				</el-form-item>
				<!-- 汇川专属：Series 是后端硬性必填（缺失或认不出都会连接失败），故做成固定下拉而非自由输入 -->
				<template v-if="isInovance">
					<el-form-item label="PLC系列" prop="series" required>
						<el-select v-model="currentDevice.series" placeholder="请选择汇川 PLC 系列" style="width: 260px">
							<el-option v-for="s in INOVANCE_SERIES" :key="s.value" :label="s.label" :value="s.value" />
						</el-select>
					</el-form-item>

					<template v-if="isInovanceSerial">
						<el-form-item label="串口号" prop="portName" required>
							<el-input v-model="currentDevice.portName" placeholder="如 COM3" style="width: 200px" />
							<span class="field-hint">串口位于采集服务所在主机，不是浏览器本机</span>
						</el-form-item>
						<el-form-item label="波特率" prop="baudRate">
							<el-select v-model="currentDevice.baudRate" style="width: 200px">
								<el-option v-for="b in BAUD_RATES" :key="b" :label="String(b)" :value="b" />
							</el-select>
						</el-form-item>
						<el-form-item label="数据位/停止位">
							<el-input-number v-model="currentDevice.dataBits" :min="5" :max="8" :step="1"
								style="width: 110px" />
							<el-select v-model="currentDevice.stopBits" style="width: 130px; margin-left: 8px">
								<el-option label="1 位" value="One" />
								<el-option label="1.5 位" value="OnePointFive" />
								<el-option label="2 位" value="Two" />
							</el-select>
						</el-form-item>
						<el-form-item label="校验位" prop="parity">
							<el-select v-model="currentDevice.parity" style="width: 200px">
								<el-option label="无校验 None" value="None" />
								<el-option label="奇校验 Odd" value="Odd" />
								<el-option label="偶校验 Even" value="Even" />
								<el-option label="Mark" value="Mark" />
								<el-option label="Space" value="Space" />
							</el-select>
						</el-form-item>
					</template>

					<el-form-item label="字节序" prop="dataFormat">
						<el-select v-model="currentDevice.dataFormat" style="width: 200px">
							<el-option v-for="f in DATA_FORMATS" :key="f" :label="f" :value="f" />
						</el-select>
						<span class="field-hint">32 位数据的字排列，汇川常用 CDAB</span>
					</el-form-item>
					<el-form-item label="其他选项">
						<el-checkbox v-model="currentDevice.addressStartWithZero">地址从 0 开始</el-checkbox>
						<el-checkbox v-model="currentDevice.isStringReverse">字符串字节反转</el-checkbox>
					</el-form-item>

					<el-form-item label="地址格式参考">
						<el-collapse class="addr-help">
							<el-collapse-item :title="`${currentSeriesLabel} 支持的地址写法`" name="addr">
								<el-table :data="seriesAddressExamples" size="small" border>
									<el-table-column prop="example" label="示例" width="100" />
									<el-table-column prop="name" label="含义" width="110" />
									<el-table-column prop="range" label="范围说明" />
								</el-table>
								<p class="addr-help-note">
									所有地址均可加站号前缀，如 <code>s=2;D100</code>，用于一条链路挂多台从站。
								</p>
							</el-collapse-item>
						</el-collapse>
					</el-form-item>
				</template>
				<el-form-item label="描述" prop="description">
					<el-input
						v-model="currentDevice.description"
						type="textarea"
						placeholder="请输入设备描述"
					/>
				</el-form-item>
			</el-form>
			<template #footer>
				<span class="dialog-footer">
					<el-button @click="deviceDialogVisible = false"
						>取消</el-button
					>
					<el-button type="primary" @click="saveDevice"
						>保存</el-button
					>
				</span>
			</template>
		</el-dialog>

		<!-- 连接测试对话框 -->
		<el-dialog
			v-model="connectionTestVisible"
			title="连接测试"
			width="400px"
			:close-on-click-modal="false"
		>
			<div class="connection-test-content">
				<el-alert
					:title="connectionTestStatus"
					:type="connectionTestResult ? 'success' : 'error'"
					description="{{ connectionTestMessage }}"
					show-icon
					:closable="false"
				/>
				<div class="test-details" v-if="connectionTestDetails">
					<h4>测试详情</h4>
					<el-descriptions :column="1">
						<el-descriptions-item label="设备编号">{{
							connectionTestDetails.deviceCode
						}}</el-descriptions-item>
						<el-descriptions-item label="设备名称">{{
							connectionTestDetails.deviceName
						}}</el-descriptions-item>
						<el-descriptions-item label="IP地址">{{
							connectionTestDetails.ip
						}}</el-descriptions-item>
						<el-descriptions-item label="端口">{{
							connectionTestDetails.port
						}}</el-descriptions-item>
						<el-descriptions-item label="协议">{{
							connectionTestDetails.protocol
						}}</el-descriptions-item>
						<el-descriptions-item label="响应时间"
							>{{
								connectionTestDetails.responseTime
							}}ms</el-descriptions-item
						>
						<el-descriptions-item label="测试结果">{{
							connectionTestDetails.result
						}}</el-descriptions-item>
					</el-descriptions>
				</div>
			</div>
			<template #footer>
				<span class="dialog-footer">
					<el-button @click="connectionTestVisible = false"
						>关闭</el-button
					>
				</span>
			</template>
		</el-dialog>

		<!-- 协议能力矩阵（来自后端 /api/plc/capabilities 静态清单） -->
		<el-dialog v-model="capabilitiesDialogVisible" title="PLC 协议能力矩阵" width="860px">
			<el-table v-loading="loadingCapabilities" :data="capabilities" border>
				<el-table-column prop="brand" label="品牌" width="90" />
				<el-table-column label="支持型号" min-width="150">
					<template #default="scope">{{ scope.row.models.join("、") }}</template>
				</el-table-column>
				<el-table-column label="支持协议" min-width="180">
					<template #default="scope">
						<el-tag
							v-for="p in scope.row.protocols"
							:key="p"
							size="small"
							style="margin: 2px"
						>
							{{ p }}
						</el-tag>
					</template>
				</el-table-column>
				<el-table-column label="必填扩展属性" min-width="220">
					<template #default="scope">
						<div
							v-for="(props, protocol) in scope.row.requiredProperties"
							:key="protocol"
							style="font-size: 12px"
						>
							<b>{{ protocol }}</b>：{{ props.length ? props.join("、") : "无" }}
						</div>
					</template>
				</el-table-column>
				<el-table-column label="地址示例" min-width="160">
					<template #default="scope">{{ scope.row.addressExamples.join("、") }}</template>
				</el-table-column>
			</el-table>
			<template #footer>
				<el-button @click="capabilitiesDialogVisible = false">关闭</el-button>
			</template>
		</el-dialog>
	</div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted, watch } from "vue";
import { useRouter } from "vue-router";
import { Plus, Refresh, Grid } from "@element-plus/icons-vue";
import { ElMessageBox, ElMessage } from "element-plus";
import {
	machineConnectionDevicesApi,
	type DeviceDto,
} from "@/api/machineConnectionDevices";
import {
	machineConnectionDiagnosticsApi,
	type PlcProtocolCapability,
} from "@/api/machineConnectionDiagnostics";

// ---------- 协议能力矩阵 ----------
const capabilitiesDialogVisible = ref(false);
const loadingCapabilities = ref(false);
const capabilities = ref<PlcProtocolCapability[]>([]);

const openCapabilitiesDialog = async () => {
	capabilitiesDialogVisible.value = true;
	if (capabilities.value.length) return;
	loadingCapabilities.value = true;
	try {
		capabilities.value = await machineConnectionDiagnosticsApi.plcCapabilities();
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载协议能力矩阵失败"));
	} finally {
		loadingCapabilities.value = false;
	}
};

// PLC设备类型定义
interface PLCDevice {
	id: string;
	deviceCode: string;
	name: string;
	ip: string;
	port: number;
	protocol: string;
	model?: string;
	station?: number;
	description?: string;
	status: string;
	/** 以下均为汇川专属，落到 extendedProperties（键名大小写必须与驱动一致） */
	series?: string;
	dataFormat?: string;
	addressStartWithZero?: boolean;
	isStringReverse?: boolean;
	portName?: string;
	baudRate?: number;
	dataBits?: number;
	stopBits?: string;
	parity?: string;
}

/** 后端 InovanceAddressSpace.ParseSeries 认得的规范取值；下拉固定这四项，避免手写型号名被拒 */
const INOVANCE_SERIES = [
	{ value: "H3U", label: "H3U（含 XP）" },
	{ value: "H5U", label: "H5U" },
	{ value: "AM", label: "AM（AM400/AM600/AM800、AC、AP）" },
	{ value: "Easy", label: "Easy" },
] as const;

const BAUD_RATES = [9600, 19200, 38400, 57600, 115200] as const;
const DATA_FORMATS = ["ABCD", "BADC", "CDAB", "DCBA"] as const;

interface AddressExample {
	example: string;
	name: string;
	range: string;
}

/**
 * 地址示例。刻意对齐后端 InovanceAddressSpace 的区域表而非照抄 HslCommunication demo ——
 * demo 把 H3U/H5U 合并展示，但后端对 H5U/Easy 不开放 SM/T/C、改为 B，照抄会让界面推荐被后端拒掉的地址。
 */
const H5U_EXAMPLES: AddressExample[] = [
	{ example: "M0", name: "中间继电器", range: "M0-M7679、M8000-M8511" },
	{ example: "B0", name: "位寄存器", range: "B0-B255" },
	{ example: "S0", name: "步进继电器", range: "S0-S4095" },
	{ example: "X0", name: "输入（八进制）", range: "X0-X377" },
	{ example: "Y0", name: "输出（八进制）", range: "Y0-Y377" },
	{ example: "D0", name: "数据寄存器", range: "D0-D8511" },
	{ example: "R0", name: "文件寄存器", range: "R0-R32767" },
];

const SERIES_ADDRESS_EXAMPLES: Record<string, AddressExample[]> = {
	H3U: [
		{ example: "M0", name: "中间继电器", range: "M0-M7679、M8000-M8511" },
		{ example: "SM0", name: "特殊继电器", range: "SM0-SM1023（只读）" },
		{ example: "S0", name: "步进继电器", range: "S0-S4095" },
		{ example: "T0", name: "定时器", range: "T0-T511，读位=线圈，读字=当前值" },
		{ example: "C0", name: "计数器", range: "C0-C255，读位=线圈，读字=当前值" },
		{ example: "X0", name: "输入（八进制）", range: "X0-X377" },
		{ example: "Y0", name: "输出（八进制）", range: "Y0-Y377" },
		{ example: "D0", name: "数据寄存器", range: "D0-D8511" },
		{ example: "SD0", name: "特殊寄存器", range: "SD0-SD1023（只读）" },
		{ example: "R0", name: "文件寄存器", range: "R0-R32767" },
	],
	H5U: H5U_EXAMPLES,
	Easy: H5U_EXAMPLES,
	AM: [
		{ example: "Q0.0", name: "输出", range: "Q0.0-Q8191.7，写 Q0 等价 Q0.0" },
		{ example: "IX0.0", name: "输入", range: "IX0.0-IX8191.7（只读），别名 I" },
		{ example: "MX0.0", name: "M 位", range: "MX0.0-MX1000.7" },
		{ example: "MW0", name: "M 字", range: "MW0-MW65535" },
		{ example: "MD0", name: "M 双字", range: "MD0-MD32767" },
		{ example: "MB0", name: "M 字节", range: "MB0-MB65534，须偶数地址" },
		{ example: "SM0", name: "系统位", range: "SM0-SM65535（只读）" },
		{ example: "SD0", name: "系统字", range: "SD0-SD65535" },
	],
};

// 连接测试详情类型
interface ConnectionTestDetails {
	deviceCode: string;
	deviceName: string;
	ip: string;
	port: number;
	protocol: string;
	responseTime: number;
	result: string;
}

// 设备列表（数据来自 MachineConnectionApi /api/devices?type=PLC）
const devices = ref<PLCDevice[]>([]);
const router = useRouter();

const goTo = (path: string) => {
	void router.push(path);
};

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

function parseLatency(latency?: string | null): number {
	if (!latency) return 0;
	const n = Number(latency);
	return Number.isFinite(n) ? Math.round(n) : 0;
}

/**
 * 读取扩展属性并忽略键名大小写 —— 早期版本本页存的是小写 station，
 * 而驱动读的是 Station，历史设备两种写法都要能回显。
 */
function extProp(d: DeviceDto, key: string): string | undefined {
	const props = d.extendedProperties;
	if (!props) return undefined;
	const hit = Object.keys(props).find((k) => k.toLowerCase() === key.toLowerCase());
	return hit === undefined ? undefined : props[hit];
}

// 后端 DeviceDto → 页面 PLCDevice（站号/编号/描述存于 extendedProperties）
function mapToPlc(d: DeviceDto): PLCDevice {
	const station = extProp(d, "station");
	return {
		id: d.id,
		deviceCode: extProp(d, "deviceCode") ?? d.model ?? "",
		name: d.name,
		ip: d.host,
		port: d.port,
		protocol: d.protocol,
		model: d.model,
		station: station ? Number(station) : undefined,
		description: extProp(d, "description") ?? "",
		status: d.status,
		series: extProp(d, "Series") ?? "",
		dataFormat: extProp(d, "DataFormat") ?? "CDAB",
		addressStartWithZero: extProp(d, "AddressStartWithZero") === "true",
		isStringReverse: extProp(d, "IsStringReverse") === "true",
		portName: extProp(d, "PortName") ?? "",
		baudRate: Number(extProp(d, "BaudRate") ?? 9600) || 9600,
		dataBits: Number(extProp(d, "DataBits") ?? 8) || 8,
		stopBits: extProp(d, "StopBits") ?? "One",
		parity: extProp(d, "Parity") ?? "None",
	};
}

const loadDevices = async () => {
	try {
		const list = await machineConnectionDevicesApi.list("PLC");
		devices.value = list.map(mapToPlc);
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载设备列表失败"));
	}
};

// 搜索和筛选
const searchQuery = ref("");
const filterProtocol = ref("");
const treeKeyword = ref("");
const selectedTreeNodeId = ref("all");
const treeRef = ref();

const deviceTree = [
	{
		id: "all",
		label: "全部设备",
		children: [
			{ id: "protocol-ModbusTCP", label: "Modbus TCP" },
			{ id: "protocol-SiemensS7", label: "西门子S7" },
			{ id: "protocol-Inovance", label: "汇川 以太网(Inovance)" },
			{ id: "protocol-InovanceSerial", label: "汇川 串口(InovanceSerial)" },
			{ id: "protocol-InovanceSerialOverTcp", label: "汇川 串口透传TCP" },
		],
	},
];

const handleTreeNodeClick = (data: { id: string }) => {
	selectedTreeNodeId.value = data.id;
	currentPage.value = 1;
};

watch(treeKeyword, (value) => {
	treeRef.value?.filter(value);
});

const filterTreeNode = (value: string, data: { label: string }) => {
	if (!value) return true;
	return data.label.includes(value);
};

// 分页
const currentPage = ref(1);
const pageSize = ref(10);

// 设备对话框
const deviceDialogVisible = ref(false);
const isEditing = ref(false);

// 字段变多后，新增/重置两处若各写一份字面量容易漏字段，故收敛成工厂函数
function blankDevice(): PLCDevice {
	return {
		id: "",
		deviceCode: "",
		name: "",
		ip: "",
		port: 502,
		protocol: "ModbusTCP",
		model: "",
		station: 1,
		description: "",
		status: "离线",
		series: "",
		dataFormat: "CDAB",
		addressStartWithZero: false,
		isStringReverse: false,
		portName: "",
		baudRate: 9600,
		dataBits: 8,
		stopBits: "One",
		parity: "None",
	};
}

const currentDevice = reactive<PLCDevice>(blankDevice());

const isInovance = computed(() => currentDevice.protocol.startsWith("Inovance"));
const isInovanceSerial = computed(() => currentDevice.protocol === "InovanceSerial");
const currentSeriesLabel = computed(
	() => INOVANCE_SERIES.find((s) => s.value === currentDevice.series)?.label ?? "汇川",
);
const seriesAddressExamples = computed(
	() => SERIES_ADDRESS_EXAMPLES[currentDevice.series ?? ""] ?? [],
);

/** 串口变体不使用 IP，但后端 Host 恒为必填校验项，补个占位免得卡在无关报错上 */
const handleProtocolChange = (protocol: string) => {
	if (protocol === "InovanceSerial" && !currentDevice.ip) {
		currentDevice.ip = "127.0.0.1";
	}
};

// 连接测试
const connectionTestVisible = ref(false);
const connectionTestStatus = ref("测试中");
const connectionTestResult = ref(false);
const connectionTestMessage = ref("正在测试连接...");
const connectionTestDetails = ref<ConnectionTestDetails | null>(null);

// 过滤后的设备列表
const filteredDevices = computed(() => {
	let result = [...devices.value];

	if (selectedTreeNodeId.value.startsWith("protocol-")) {
		const protocol = selectedTreeNodeId.value.replace("protocol-", "");
		result = result.filter((device) => device.protocol === protocol);
	}

	// 搜索
	if (searchQuery.value) {
		const query = searchQuery.value.toLowerCase();
		result = result.filter(
			(device) =>
				device.name.toLowerCase().includes(query) ||
				device.deviceCode.toLowerCase().includes(query) ||
				device.ip.includes(query) ||
				device.protocol.toLowerCase().includes(query),
		);
	}

	// 协议筛选
	if (filterProtocol.value) {
		result = result.filter(
			(device) => device.protocol === filterProtocol.value,
		);
	}

	// 分页
	const startIndex = (currentPage.value - 1) * pageSize.value;
	const endIndex = startIndex + pageSize.value;
	return result.slice(startIndex, endIndex);
});

const filteredTotal = computed(() => {
	let result = [...devices.value];
	if (selectedTreeNodeId.value.startsWith("protocol-")) {
		const protocol = selectedTreeNodeId.value.replace("protocol-", "");
		result = result.filter((device) => device.protocol === protocol);
	}
	if (searchQuery.value) {
		const query = searchQuery.value.toLowerCase();
		result = result.filter(
			(device) =>
				device.name.toLowerCase().includes(query) ||
				device.deviceCode.toLowerCase().includes(query) ||
				device.ip.includes(query) ||
				device.protocol.toLowerCase().includes(query),
		);
	}
	if (filterProtocol.value) {
		result = result.filter((device) => device.protocol === filterProtocol.value);
	}
	return result.length;
});

// 打开新增设备对话框
const openAddDeviceDialog = () => {
	isEditing.value = false;
	Object.assign(currentDevice, blankDevice());
	deviceDialogVisible.value = true;
};

// 打开编辑设备对话框
const openEditDeviceDialog = (device: PLCDevice) => {
	isEditing.value = true;
	Object.assign(currentDevice, { ...device });
	deviceDialogVisible.value = true;
};

// 保存设备 = 真实创建/更新（站号/编号/描述存 extendedProperties）
const saveDevice = async () => {
	if (!currentDevice.deviceCode || !currentDevice.name || !currentDevice.ip || !currentDevice.port || !currentDevice.protocol) {
		ElMessage.warning("请填写必填字段");
		return;
	}
	// 后端 Model 是 NotEmpty 硬校验，留空会导致设备存进网关但同步上游失败，界面看着有、实际不可用
	if (!currentDevice.model) {
		ElMessage.warning("请填写设备型号");
		return;
	}
	// 后端 CreateDeviceRequestValidator 对汇川强制要求非空 Series，串口变体还强制 PortName；
	// 这里先挡一层，避免用户拿到一串英文 400 校验信息
	if (isInovance.value && !currentDevice.series) {
		ElMessage.warning("汇川设备必须选择 PLC 系列");
		return;
	}
	if (isInovanceSerial.value && !currentDevice.portName?.trim()) {
		ElMessage.warning("汇川串口设备必须填写串口号（如 COM3）");
		return;
	}

	// 键名大小写必须与驱动内 Get(config, "...") 完全一致，否则会被静默忽略
	const ext: Record<string, string> = {};
	if (currentDevice.station != null) ext.Station = String(currentDevice.station);
	if (currentDevice.deviceCode) ext.deviceCode = currentDevice.deviceCode;
	if (currentDevice.description) ext.description = currentDevice.description;
	if (isInovance.value) {
		ext.Series = currentDevice.series ?? "";
		ext.DataFormat = currentDevice.dataFormat ?? "CDAB";
		ext.AddressStartWithZero = String(currentDevice.addressStartWithZero === true);
		ext.IsStringReverse = String(currentDevice.isStringReverse === true);
	}
	if (isInovanceSerial.value) {
		ext.PortName = (currentDevice.portName ?? "").trim();
		ext.BaudRate = String(currentDevice.baudRate ?? 9600);
		ext.DataBits = String(currentDevice.dataBits ?? 8);
		ext.StopBits = currentDevice.stopBits ?? "One";
		ext.Parity = currentDevice.parity ?? "None";
	}

	// InovanceSerial / InovanceSerialOverTcp 注册时没有 "*" 通配品牌（只有以太网版有），
	// brand 必须命中 Inovance/汇川，否则后端 DriverRegistry.Resolve 找不到驱动
	const brand = isInovance.value
		? "Inovance"
		: currentDevice.model || currentDevice.protocol;

	try {
		if (isEditing.value) {
			const saved = await machineConnectionDevicesApi.update(currentDevice.id, {
				name: currentDevice.name,
				brand,
				model: currentDevice.model ?? "",
				protocol: currentDevice.protocol,
				host: currentDevice.ip,
				port: currentDevice.port,
				extendedProperties: ext,
			});
			if (saved.upstreamSynced === false)
				ElMessage.warning(`已保存，但同步采集服务失败：${saved.upstreamError ?? "上游不可用"}`);
			else ElMessage.success("设备编辑成功");
		} else {
			const saved = await machineConnectionDevicesApi.create({
				name: currentDevice.name,
				type: "PLC",
				brand,
				model: currentDevice.model ?? "",
				protocol: currentDevice.protocol,
				host: currentDevice.ip,
				port: currentDevice.port,
				extendedProperties: ext,
			});
			if (saved.upstreamSynced === false)
				ElMessage.warning(`设备已添加，但同步采集服务失败：${saved.upstreamError ?? "上游不可用"}`);
			else ElMessage.success("设备添加成功");
		}
		deviceDialogVisible.value = false;
		await loadDevices();
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "保存设备失败"));
	}
};

// 删除设备 = 真实删除
const deleteDevice = (id: string) => {
	ElMessageBox.confirm("确定要删除该设备吗？", "警告", {
		confirmButtonText: "确定",
		cancelButtonText: "取消",
		type: "warning",
	})
		.then(async () => {
			try {
				await machineConnectionDevicesApi.remove(id);
				ElMessage.success("设备删除成功");
				await loadDevices();
			} catch (e: unknown) {
				ElMessage.error(getErr(e, "删除失败"));
			}
		})
		.catch(() => {
			// 取消删除
		});
};

// 刷新设备列表 = 重新拉取后端真实设备
const refreshDeviceList = async () => {
	await loadDevices();
	ElMessage.success("设备列表已刷新");
};

// 测试连接 = 调后端真实连通性测试
const testConnection = async (device: PLCDevice) => {
	connectionTestVisible.value = true;
	connectionTestStatus.value = "测试中";
	connectionTestResult.value = false;
	connectionTestMessage.value = "正在测试连接...";
	connectionTestDetails.value = null;

	try {
		const r = await machineConnectionDevicesApi.testConnection(device.id);
		connectionTestResult.value = r.success;
		connectionTestStatus.value = r.success ? "测试成功" : "测试失败";
		connectionTestMessage.value = r.success
			? "设备连接正常"
			: (r.errorMessage ?? "无法连接到设备，请检查网络和设备状态");
		connectionTestDetails.value = {
			deviceCode: device.deviceCode,
			deviceName: device.name,
			ip: device.ip,
			port: device.port,
			protocol: device.protocol,
			responseTime: parseLatency(r.latency),
			result: r.success ? "连接成功" : "连接失败",
		};
	} catch (e: unknown) {
		connectionTestResult.value = false;
		connectionTestStatus.value = "测试失败";
		connectionTestMessage.value = getErr(e, "连接测试失败");
		connectionTestDetails.value = null;
	}
};

// 获取状态类型
const getStatusType = (status: string) => {
	switch (status) {
		case "在线":
			return "success";
		case "离线":
			return "danger";
		default:
			return "info";
	}
};

// 分页处理
const handleSizeChange = (size: number) => {
	pageSize.value = size;
	currentPage.value = 1;
};

const handleCurrentChange = (current: number) => {
	currentPage.value = current;
};

onMounted(() => {
	void loadDevices();
});
</script>

<style lang="scss" scoped>
.plc-device-config-view {
	.device-layout {
		display: grid;
		grid-template-columns: 260px 1fr;
		gap: 16px;
	}

	.tree-panel {
		.tree-title {
			font-weight: 600;
		}

		.tree-search {
			margin-bottom: 10px;
		}
	}

	.action-bar {
		display: flex;
		align-items: center;
		margin-bottom: 20px;
		gap: 10px;
	}

	.device-cards {
		display: grid;
		grid-template-columns: repeat(auto-fill, minmax(400px, 1fr));
		gap: 20px;
		margin-bottom: 20px;
	}

	.device-card {
		border-radius: 8px;
		box-shadow: 0 2px 12px 0 rgba(0, 0, 0, 0.1);
	}

	.card-header {
		display: flex;
		justify-content: space-between;
		align-items: flex-start;
		margin-bottom: 15px;
		padding-bottom: 10px;
		border-bottom: 1px solid var(--el-border-color);
	}

	.device-name {
		margin: 4px 0 5px;
		font-size: 18px;
		font-weight: 600;
	}

	.device-code {
		margin: 0;
		font-size: 14px;
		color: var(--el-text-color-secondary);
	}

	.card-body {
		margin-bottom: 15px;
	}

	.card-footer {
		display: flex;
		flex-direction: column;
		align-items: flex-start;
		gap: 4px;
		padding-top: 15px;
		border-top: 1px solid var(--el-border-color);
	}

	.card-footer-row {
		display: flex;
		flex-wrap: wrap;
		gap: 10px;
	}

	.empty-hint {
		margin-bottom: 16px;
		color: var(--el-text-color-secondary);
	}

	.field-hint {
		margin-left: 10px;
		font-size: 12px;
		color: var(--el-text-color-secondary);
	}

	.addr-help {
		width: 100%;

		:deep(.el-collapse-item__content) {
			padding-bottom: 10px;
		}
	}

	.addr-help-note {
		margin: 10px 0 0;
		font-size: 12px;
		line-height: 1.6;
		color: var(--el-text-color-secondary);
	}
}

@media (max-width: 1200px) {
	.plc-device-config-view .device-layout {
		grid-template-columns: 1fr;
	}
}
</style>
