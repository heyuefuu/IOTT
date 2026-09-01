<template>
	<div class="modbus-register-view">
		<h2 class="page-title">Modbus 寄存器窗口</h2>

		<el-card>
			<template #header>
				<div class="card-header">
					<span>读一段原始寄存器，格式在本地切换</span>
					<el-tag v-if="lastReadAt" type="info" size="small">
						最后读取 {{ lastReadAt }}
					</el-tag>
				</div>
			</template>

			<!-- 读取条件 -->
			<el-form :inline="true" :model="form" class="query-bar">
				<el-form-item label="设备">
					<el-select
						v-model="form.deviceId"
						placeholder="请选择设备"
						style="width: 200px"
						@change="onDeviceChange"
					>
						<el-option
							v-for="d in devices"
							:key="d.id"
							:label="d.name"
							:value="d.id"
						/>
					</el-select>
				</el-form-item>

				<el-form-item label="区域">
					<el-select v-model="form.area" style="width: 210px" @change="onAreaChange">
						<el-option label="保持寄存器 (03)" value="HR" />
						<el-option label="输入寄存器 (04)" value="IR" />
						<el-option label="线圈 (01)" value="C" />
						<el-option label="离散输入 (02)" value="DI" />
					</el-select>
				</el-form-item>

				<el-form-item label="起始">
					<el-input
						v-model="form.start"
						placeholder="40001 或 HR0"
						style="width: 150px"
					/>
				</el-form-item>

				<el-form-item label="数量">
					<el-input-number
						v-model="form.count"
						:min="1"
						:max="maxCount"
						:step="1"
						style="width: 130px"
					/>
				</el-form-item>

				<el-form-item>
					<el-button type="primary" :loading="loading" @click="read">
						<el-icon><Refresh /></el-icon>
						读取
					</el-button>
					<el-button
						:type="autoRefresh ? 'danger' : 'default'"
						@click="toggleAutoRefresh"
					>
						{{ autoRefresh ? "停止刷新" : "自动刷新 1s" }}
					</el-button>
				</el-form-item>
			</el-form>

			<div class="hint">
				起始地址支持 <code>40001</code>（Modicon 引用号，1 基）、<code>HR0</code> /
				<code>4x0</code>（协议地址，0 基）。上限：寄存器 125 个 / 位 2000 个。
			</div>

			<el-alert
				v-if="error"
				:title="error"
				type="error"
				show-icon
				:closable="false"
				style="margin: 12px 0"
			/>

			<!-- 寄存器区显示 -->
			<template v-if="!isBitArea">
				<div class="format-bar">
					<span class="label">字序</span>
					<el-radio-group v-model="wordOrder" size="small">
						<el-radio-button value="ABCD">ABCD（大端）</el-radio-button>
						<el-radio-button value="CDAB">CDAB（字交换）</el-radio-button>
					</el-radio-group>

					<span class="label">显示列</span>
					<el-checkbox-group v-model="columns" size="small">
						<el-checkbox-button value="hex">Hex</el-checkbox-button>
						<el-checkbox-button value="int16">Int16</el-checkbox-button>
						<el-checkbox-button value="uint16">UInt16</el-checkbox-button>
						<el-checkbox-button value="bin">Bin</el-checkbox-button>
						<el-checkbox-button value="int32">Int32</el-checkbox-button>
						<el-checkbox-button value="float">Float</el-checkbox-button>
						<el-checkbox-button value="ascii">ASCII</el-checkbox-button>
					</el-checkbox-group>
				</div>

				<el-table :data="registerRows" border size="small" max-height="520">
					<el-table-column prop="reference" label="引用号" width="90" />
					<el-table-column prop="address" label="地址" width="80" />
					<el-table-column v-if="columns.includes('hex')" label="Hex" width="90">
						<template #default="{ row }">
							<span class="mono">{{ row.hex }}</span>
						</template>
					</el-table-column>
					<el-table-column
						v-if="columns.includes('int16')"
						prop="int16"
						label="Int16"
						width="90"
					/>
					<el-table-column
						v-if="columns.includes('uint16')"
						prop="uint16"
						label="UInt16"
						width="90"
					/>
					<el-table-column v-if="columns.includes('bin')" label="Bin" width="160">
						<template #default="{ row }">
							<span class="mono">{{ row.bin }}</span>
						</template>
					</el-table-column>
					<el-table-column
						v-if="columns.includes('int32')"
						label="Int32 (本行起两字)"
						width="160"
					>
						<template #default="{ row }">
							<span :class="{ dim: row.pairTail }">{{ row.int32 }}</span>
						</template>
					</el-table-column>
					<el-table-column
						v-if="columns.includes('float')"
						label="Float (本行起两字)"
						width="170"
					>
						<template #default="{ row }">
							<span :class="{ dim: row.pairTail }">{{ row.float }}</span>
						</template>
					</el-table-column>
					<el-table-column
						v-if="columns.includes('ascii')"
						prop="ascii"
						label="ASCII"
						width="70"
					/>
					<el-table-column label="写入" width="220" fixed="right">
						<template #default="{ row }">
							<div class="write-cell">
								<el-input
									v-model="writeDrafts[row.address]"
									size="small"
									placeholder="Int16 值"
									:disabled="!areaWritable"
								/>
								<el-button
									size="small"
									type="success"
									:disabled="!areaWritable || !writeDrafts[row.address]"
									@click="writeRegister(row)"
								>
									写
								</el-button>
							</div>
						</template>
					</el-table-column>
				</el-table>

				<div v-if="columns.includes('ascii') && registerRows.length" class="ascii-all">
					整段 ASCII：<span class="mono">{{ asciiAll }}</span>
				</div>
			</template>

			<!-- 位区显示 -->
			<template v-else>
				<el-table :data="bitRows" border size="small" max-height="520">
					<el-table-column prop="reference" label="引用号" width="90" />
					<el-table-column prop="address" label="地址" width="80" />
					<el-table-column label="值" width="90">
						<template #default="{ row }">
							<el-tag :type="row.value ? 'success' : 'info'" size="small">
								{{ row.value ? "ON" : "OFF" }}
							</el-tag>
						</template>
					</el-table-column>
					<el-table-column label="写入" width="180" fixed="right">
						<template #default="{ row }">
							<div class="write-cell">
								<el-switch v-model="bitDrafts[row.address]" :disabled="!areaWritable" />
								<el-button
									size="small"
									type="success"
									:disabled="!areaWritable"
									@click="writeCoil(row)"
								>
									写
								</el-button>
							</div>
						</template>
					</el-table-column>
				</el-table>
			</template>
		</el-card>
	</div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted, onUnmounted } from "vue";
import { Refresh } from "@element-plus/icons-vue";
import { ElMessage } from "element-plus";
import { machineConnectionDevicesApi } from "@/api/machineConnectionDevices";
import { machineConnectionPointsApi } from "@/api/machineConnectionPoints";

/**
 * 通用 Modbus 调试面板。
 *
 * 关键设计：**读取与解释解耦**。一次读回一段原始寄存器（`地址;数量` + UInt16），
 * Int16 / Hex / Int32 / Float / ASCII 全在前端换算，切格式不用重读设备 ——
 * 这样就不必在读之前猜数据类型，跟 Modbus Poll 之类的通用工具一致。
 */

const devices = ref<{ id: string; name: string }[]>([]);

const form = reactive({
	deviceId: "",
	area: "HR" as "HR" | "IR" | "C" | "DI",
	start: "40001",
	count: 20,
});

const loading = ref(false);
const error = ref("");
const lastReadAt = ref("");

const wordOrder = ref<"ABCD" | "CDAB">("ABCD");
const columns = ref<string[]>(["hex", "int16", "uint16"]);

/** 已读回的原始寄存器（UInt16），位区时为空 */
const words = ref<number[]>([]);
/** 已读回的位，寄存器区时为空 */
const bits = ref<boolean[]>([]);
/** 本次读取实际生效的 0 基起始协议地址，用于表格里回显地址 */
const readStart = ref(0);
/** 本次读取的区域，与 form.area 分开，避免切下拉后表头和数据对不上 */
const readArea = ref<"HR" | "IR" | "C" | "DI">("HR");

const isBitArea = computed(() => readArea.value === "C" || readArea.value === "DI");
const areaWritable = computed(() => readArea.value === "HR" || readArea.value === "C");
const maxCount = computed(() =>
	form.area === "C" || form.area === "DI" ? 2000 : 125,
);

// 各区的 Modicon 引用号基准，用于把 0 基地址显示回引用号
const REFERENCE_BASE: Record<string, number> = {
	C: 1,
	DI: 10001,
	IR: 30001,
	HR: 40001,
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

/**
 * 把「起始」输入框的内容规范成后端认的地址，并算出 0 基协议地址用于显示。
 * 用户可以填 40001（引用号）、HR0 / 4x0（协议地址）、或裸数字。
 */
function resolveStart(): { address: string; zeroBased: number } | null {
	const raw = form.start.trim();
	if (!raw) return null;

	const upper = raw.toUpperCase();
	const base = REFERENCE_BASE[form.area];

	// 5/6 位引用号：直接透传给后端，本地按同样规则算 0 基地址
	if (/^\d{5}$|^\d{6}$/.test(upper)) {
		const value = Number(upper);
		const span = upper.length === 6 ? 100000 : 10000;
		const areaBase =
			form.area === "C"
				? 1
				: form.area === "DI"
					? span + 1
					: form.area === "IR"
						? span * 3 + 1
						: span * 4 + 1;
		const offset = value - areaBase;
		if (offset < 0) return null;
		return { address: upper, zeroBased: offset };
	}

	// 带前缀的协议地址：HR0 / 4x0 / C0 / 0x40 ...
	const prefixed = /^(HR|IR|DI|C|4X|3X|1X|0X)(\d+)$/.exec(upper);
	if (prefixed) {
		return { address: upper, zeroBased: Number(prefixed[2]) };
	}

	// 裸数字：按当前所选区域补上前缀，避免后端把它当保持寄存器
	if (/^\d+$/.test(upper)) {
		const addr = Number(upper);
		void base;
		return { address: `${form.area}${addr}`, zeroBased: addr };
	}

	return null;
}

const read = async () => {
	if (!form.deviceId) {
		ElMessage.warning("请先选择设备");
		return;
	}

	const start = resolveStart();
	if (!start) {
		error.value = `起始地址 '${form.start}' 无法识别。可填 40001（引用号）、HR0 / 4x0（协议地址）或裸数字。`;
		return;
	}

	loading.value = true;
	error.value = "";
	try {
		// 位区按位读回 bool[]，寄存器区统一按 UInt16 读回原始字，格式在前端换算
		const dataType = isBitAreaForm() ? "Bool" : "UInt16";
		const res = await machineConnectionPointsApi.readTags(form.deviceId, {
			tags: [{ address: `${start.address};${form.count}`, dataType }],
		});

		const tag = res.tags[0];
		if (!tag || tag.quality !== "Good") {
			error.value = tag?.errorMessage ?? "读取失败";
			words.value = [];
			bits.value = [];
			return;
		}

		readArea.value = form.area;
		readStart.value = start.zeroBased;

		if (isBitAreaForm()) {
			bits.value = Array.isArray(tag.value) ? (tag.value as boolean[]) : [];
			words.value = [];
		} else {
			words.value = Array.isArray(tag.value)
				? (tag.value as number[])
				: [Number(tag.value)];
			bits.value = [];
		}

		lastReadAt.value = new Date().toLocaleTimeString();
	} catch (e: unknown) {
		error.value = getErr(e, "读取失败");
		words.value = [];
		bits.value = [];
	} finally {
		loading.value = false;
	}
};

function isBitAreaForm(): boolean {
	return form.area === "C" || form.area === "DI";
}

// ─────────── 前端换算 ───────────

interface RegisterRow {
	index: number;
	reference: number;
	address: number;
	hex: string;
	bin: string;
	int16: number;
	uint16: number;
	int32: string;
	float: string;
	ascii: string;
	/** true 表示本行是某个 32 位值的低位字，Int32/Float 列灰显提示"跨行" */
	pairTail: boolean;
}

/**
 * 写入草稿单独存，不放进 computed 出来的行对象里 ——
 * 自动刷新每秒重算一次行，放在行里的输入内容会被抹掉。
 */
const writeDrafts = reactive<Record<number, string>>({});
const bitDrafts = reactive<Record<number, boolean>>({});

/** 按当前字序把两个 16 位字拼成 32 位 */
function combine32(hi: number, lo: number): number {
	return wordOrder.value === "ABCD"
		? ((hi << 16) | lo) >>> 0
		: ((lo << 16) | hi) >>> 0;
}

const registerRows = computed<RegisterRow[]>(() => {
	const buf = new DataView(new ArrayBuffer(4));
	return words.value.map((w, i) => {
		const int16 = w > 0x7fff ? w - 0x10000 : w;

		let int32 = "";
		let float = "";
		if (i + 1 < words.value.length) {
			const combined = combine32(w, words.value[i + 1]);
			buf.setUint32(0, combined, false);
			int32 = String(buf.getInt32(0, false));
			float = trimFloat(buf.getFloat32(0, false));
		}

		const hiByte = (w >> 8) & 0xff;
		const loByte = w & 0xff;

		return {
			index: i,
			reference: REFERENCE_BASE[readArea.value] + readStart.value + i,
			address: readStart.value + i,
			hex: "0x" + w.toString(16).toUpperCase().padStart(4, "0"),
			bin: w.toString(2).padStart(16, "0"),
			int16,
			uint16: w,
			int32,
			float,
			ascii: printable(hiByte) + printable(loByte),
			pairTail: i % 2 === 1,
		};
	});
});

const asciiAll = computed(() =>
	words.value
		.flatMap((w) => [printable((w >> 8) & 0xff), printable(w & 0xff)])
		.join(""),
);

function printable(byte: number): string {
	return byte >= 0x20 && byte <= 0x7e ? String.fromCharCode(byte) : ".";
}

function trimFloat(v: number): string {
	if (!Number.isFinite(v)) return String(v);
	if (v === 0) return "0";
	return Math.abs(v) < 1e-6 || Math.abs(v) > 1e12
		? v.toExponential(6)
		: String(Number(v.toPrecision(9)));
}

interface BitRow {
	index: number;
	reference: number;
	address: number;
	value: boolean;
}

const bitRows = computed<BitRow[]>(() =>
	bits.value.map((b, i) => {
		const address = readStart.value + i;
		// 首次出现时用当前值预填开关，之后保留用户改过的草稿
		if (!(address in bitDrafts)) bitDrafts[address] = b;
		return {
			index: i,
			reference: REFERENCE_BASE[readArea.value] + address,
			address,
			value: b,
		};
	}),
);

// ─────────── 写入（单寄存器 / 单线圈）───────────

const writeRegister = async (row: RegisterRow) => {
	const raw = writeDrafts[row.address];
	const value = Number(raw);
	if (raw === undefined || raw === "" || !Number.isInteger(value) || value < -32768 || value > 65535) {
		ElMessage.warning("请输入 -32768 ~ 65535 的整数");
		return;
	}

	await doWrite(`HR${row.address}`, "Int16", value > 32767 ? value - 65536 : value);
	delete writeDrafts[row.address];
};

const writeCoil = async (row: BitRow) => {
	await doWrite(`C${row.address}`, "Bool", bitDrafts[row.address] ?? false);
};

async function doWrite(address: string, dataType: string, value: unknown) {
	try {
		const res = await machineConnectionPointsApi.writeTags(form.deviceId, {
			tags: [{ address, dataType, value }],
		});
		const first = res.results[0];
		if (first?.success) {
			ElMessage.success(`${address} 写入成功`);
			await read();
		} else {
			ElMessage.error(first?.errorMessage ?? "写入失败");
		}
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "写入失败"));
	}
}

// ─────────── 自动刷新 ───────────

const autoRefresh = ref(false);
let timer: number | undefined;

function toggleAutoRefresh() {
	autoRefresh.value = !autoRefresh.value;
	if (autoRefresh.value) {
		timer = window.setInterval(() => {
			if (!loading.value) void read();
		}, 1000);
	} else {
		stopAutoRefresh();
	}
}

function stopAutoRefresh() {
	if (timer !== undefined) {
		window.clearInterval(timer);
		timer = undefined;
	}
	autoRefresh.value = false;
}

function onDeviceChange() {
	resetData();
}

function onAreaChange() {
	// 区域一换，引用号基准也变，把起始地址带到新区的第一个，免得读到无意义的位置
	form.start = String(REFERENCE_BASE[form.area]).padStart(5, "0");
	if (form.count > maxCount.value) form.count = maxCount.value;
	resetData();
}

function resetData() {
	words.value = [];
	bits.value = [];
	error.value = "";
	for (const k of Object.keys(writeDrafts)) delete writeDrafts[Number(k)];
	for (const k of Object.keys(bitDrafts)) delete bitDrafts[Number(k)];
	stopAutoRefresh();
}

onMounted(async () => {
	try {
		const list = await machineConnectionDevicesApi.list("Robot");
		devices.value = list.map((d) => ({ id: d.id, name: d.name }));
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载设备列表失败"));
	}
});

onUnmounted(stopAutoRefresh);
</script>

<style scoped>
.modbus-register-view {
	padding: 20px;
}

.page-title {
	margin-bottom: 16px;
}

.card-header {
	display: flex;
	align-items: center;
	justify-content: space-between;
}

.query-bar {
	margin-bottom: 4px;
}

.hint {
	margin-bottom: 8px;
	font-size: 12px;
	color: #909399;
}

.hint code {
	padding: 1px 4px;
	background: #f4f4f5;
	border-radius: 3px;
}

.format-bar {
	display: flex;
	flex-wrap: wrap;
	gap: 12px;
	align-items: center;
	margin-bottom: 10px;
}

.format-bar .label {
	font-size: 13px;
	color: #606266;
}

.mono {
	font-family: Consolas, Monaco, monospace;
}

.dim {
	color: #c0c4cc;
}

.write-cell {
	display: flex;
	gap: 6px;
	align-items: center;
}

.ascii-all {
	margin-top: 10px;
	font-size: 13px;
	color: #606266;
	word-break: break-all;
}
</style>
