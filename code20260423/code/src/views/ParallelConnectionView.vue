<template>
	<div class="parallel-connection-view">
		<h2 class="page-title">并行连接验证</h2>

		<!-- 验证模式 -->
		<el-card class="mode-card">
			<el-radio-group v-model="testMode">
				<el-radio-button value="real">真机批量验证</el-radio-button>
				<el-radio-button value="simulate">模拟压测（C/S）</el-radio-button>
			</el-radio-group>
			<span class="mode-tip">
				{{
					testMode === "real"
						? "对已配置的真实设备（CNC/PLC/机器人）按协议驱动并行建连验证，统计成功率与时延"
						: "对指定 IP 段发起 TCP/MQTT 并发压测，模拟大规模连接"
				}}
			</span>
		</el-card>

		<!-- 真机批量验证 -->
		<template v-if="testMode === 'real'">
			<el-card class="config-card">
				<template #header>
					<div class="card-header">
						<span>真机验证配置</span>
					</div>
				</template>
				<el-form label-width="150px">
					<el-form-item label="设备范围" required>
						<el-radio-group v-model="realConfig.scope">
							<el-radio value="byType">按设备类型</el-radio>
							<el-radio value="byDevices">选择设备</el-radio>
						</el-radio-group>
					</el-form-item>
					<el-form-item
						v-if="realConfig.scope === 'byType'"
						label="设备类型"
						required
					>
						<el-select v-model="realConfig.deviceType" style="width: 240px">
							<el-option label="CNC 数控" value="CNC" />
							<el-option label="PLC" value="PLC" />
							<el-option label="机器人" value="Robot" />
						</el-select>
					</el-form-item>
					<el-form-item v-else label="选择设备" required>
						<el-select
							v-model="realConfig.deviceIds"
							multiple
							filterable
							collapse-tags
							collapse-tags-tooltip
							placeholder="请选择要验证的设备（可多选）"
							style="width: 100%"
						>
							<el-option
								v-for="d in realDevices"
								:key="d.id"
								:label="`${d.name}（${d.type} · ${d.protocol} · ${d.host}:${d.port}）`"
								:value="d.id"
							/>
						</el-select>
					</el-form-item>
					<el-form-item label="并发连接数" required>
						<el-input-number
							v-model="realConfig.concurrency"
							:min="1"
							:max="500"
							:step="10"
							style="width: 200px"
						/>
						<span style="margin-left: 10px">（后端上限 500）</span>
					</el-form-item>
					<el-form-item>
						<div class="btn-group btn-group-right">
							<el-button
								type="primary"
								:loading="realTesting"
								@click="startRealTest"
							>
								开始验证
							</el-button>
						</div>
					</el-form-item>
					<el-form-item label="按测试ID查询">
						<el-input
							v-model="historyTestId"
							placeholder="输入历史测试ID（testId）查看已保存结果"
							style="width: 340px"
						/>
						<el-button
							:loading="queryingHistory"
							style="margin-left: 8px"
							@click="queryHistoryResult"
						>
							查询结果
						</el-button>
					</el-form-item>
				</el-form>
			</el-card>

			<!-- 真机验证结果 -->
			<el-card v-if="realResult" class="monitor-card">
				<template #header>
					<div class="card-header">
						<span>验证结果</span>
						<span class="test-status">测试ID：{{ realResult.testId }}</span>
					</div>
				</template>
				<el-row :gutter="20" class="stats-row">
					<el-col :span="5">
						<div class="stat-card">
							<div class="stat-label">设备总数</div>
							<div class="stat-value">{{ realResult.totalDevices }}</div>
						</div>
					</el-col>
					<el-col :span="5">
						<div class="stat-card">
							<div class="stat-label">成功数</div>
							<div class="stat-value success">
								{{ realResult.successCount }}
							</div>
						</div>
					</el-col>
					<el-col :span="5">
						<div class="stat-card">
							<div class="stat-label">失败数</div>
							<div class="stat-value error">
								{{ realResult.failureCount }}
							</div>
						</div>
					</el-col>
					<el-col :span="5">
						<div class="stat-card">
							<div class="stat-label">成功率</div>
							<div class="stat-value">
								{{ (realResult.successRate * 100).toFixed(2) }}%
							</div>
						</div>
					</el-col>
					<el-col :span="4">
						<div class="stat-card">
							<div class="stat-label">总耗时</div>
							<div class="stat-value">
								{{ Math.round(realResult.durationMs) }}ms
							</div>
						</div>
					</el-col>
				</el-row>

				<el-table :data="realResult.results" style="width: 100%" border max-height="420">
					<el-table-column label="设备" min-width="180">
						<template #default="scope">
							{{ realDeviceName(scope.row.deviceId) }}
						</template>
					</el-table-column>
					<el-table-column prop="deviceId" label="设备ID" min-width="200" show-overflow-tooltip />
					<el-table-column label="结果" width="90">
						<template #default="scope">
							<el-tag :type="scope.row.success ? 'success' : 'danger'">
								{{ scope.row.success ? "成功" : "失败" }}
							</el-tag>
						</template>
					</el-table-column>
					<el-table-column label="时延" width="110">
						<template #default="scope">
							{{ scope.row.latencyMs.toFixed(1) }}ms
						</template>
					</el-table-column>
					<el-table-column prop="errorMessage" label="错误信息" min-width="200" show-overflow-tooltip />
				</el-table>
			</el-card>
		</template>

		<!-- 测试配置 -->
		<el-card v-if="testMode === 'simulate'" class="config-card">
			<template #header>
				<div class="card-header">
					<span>压力测试配置</span>
				</div>
			</template>
			<el-form :model="testConfig" label-width="150px">
				<el-row :gutter="20">
					<el-col :span="12">
						<el-form-item label="模拟设备数量" prop="deviceCount" required>
							<el-input-number
								v-model="testConfig.deviceCount"
								:min="1"
								:max="1000"
								:step="1"
								style="width: 200px"
							/>
						</el-form-item>
					</el-col>
					<el-col :span="12">
						<el-form-item label="并发连接数" prop="concurrentCount" required>
							<el-input-number
								v-model="testConfig.concurrentCount"
								:min="1"
								:max="maxConcurrentCount"
								:step="1"
								style="width: 200px"
							/>
						</el-form-item>
					</el-col>
				</el-row>

				<el-row :gutter="20">
					<el-col :span="12">
						<el-form-item label="协议类型" prop="protocol" required>
							<el-select
								v-model="testConfig.protocol"
								placeholder="请选择协议类型"
							>
								<el-option label="Modbus TCP" value="ModbusTCP" />
								<el-option label="西门子S7" value="SiemensS7" />
								<el-option label="OPC UA" value="OPCUA" />
								<el-option label="MQTT" value="MQTT" />
								<el-option label="TCP" value="TCP" />
								<el-option label="UDP" value="UDP" />
							</el-select>
						</el-form-item>
					</el-col>
					<el-col :span="12">
						<el-form-item label="连接模式" prop="connectionMode" required>
							<el-select
								v-model="testConfig.connectionMode"
								placeholder="请选择连接模式"
							>
								<el-option label="长连接" value="long" />
								<el-option label="短连接" value="short" />
							</el-select>
						</el-form-item>
					</el-col>
				</el-row>

				<el-row :gutter="20">
					<el-col :span="12">
						<el-form-item :label="isMqttProtocol ? 'Broker地址' : '起始IP'" prop="startIp" required>
							<el-input
								v-model="testConfig.startIp"
								:placeholder="isMqttProtocol ? '请输入 MQTT Broker IP 或域名' : '请输入起始IP地址'"
							/>
						</el-form-item>
					</el-col>
					<el-col :span="12">
						<el-form-item label="端口" prop="port" required>
							<el-input-number
								v-model="testConfig.port"
								:min="1"
								:max="65535"
								:step="1"
								style="width: 200px"
							/>
						</el-form-item>
					</el-col>
				</el-row>


				<el-row v-if="isMqttProtocol" :gutter="20">
					<el-col :span="6">
						<el-form-item label="TLS" prop="mqttUseTls">
							<el-switch v-model="testConfig.mqttUseTls" />
						</el-form-item>
					</el-col>
					<el-col :span="6">
						<el-form-item label="ClientId" prop="mqttClientId">
							<el-input v-model="testConfig.mqttClientId" placeholder="留空自动生成" />
						</el-form-item>
					</el-col>
					<el-col :span="6">
						<el-form-item label="用户名" prop="mqttUsername">
							<el-input v-model="testConfig.mqttUsername" placeholder="无认证可留空" />
						</el-form-item>
					</el-col>
					<el-col :span="6">
						<el-form-item label="密码" prop="mqttPassword">
							<el-input v-model="testConfig.mqttPassword" type="password" show-password placeholder="无认证可留空" />
						</el-form-item>
					</el-col>
				</el-row>
				<el-row :gutter="20">
					<el-col :span="12">
						<el-form-item label="测试时长" prop="duration" required>
							<el-input-number
								v-model="testConfig.duration"
								:min="1"
								:max="maxHoldSeconds"
								:step="1"
								style="width: 200px"
							/>
							<span style="margin-left: 10px">秒（长连接保持上限 {{ maxHoldSeconds }} 秒，由后端限制）</span>
						</el-form-item>
					</el-col>
				</el-row>


				<el-form-item>
					<div class="btn-group btn-group-right">
						<el-button type="primary" @click="startTest"
							>开始测试</el-button
						>
						<el-button
							type="danger"
							@click="stopTest"
							:disabled="!isTesting"
							>停止测试</el-button
						>
						<el-button @click="resetConfig">重置配置</el-button>
					</div>
				</el-form-item>
			</el-form>
		</el-card>

		<!-- 测试监控 -->
		<el-card v-if="testMode === 'simulate' && isTesting" class="monitor-card">
			<template #header>
				<div class="card-header">
					<span>实时监控仪表盘</span>
					<span class="test-status">{{ testStatus }}</span>
				</div>
			</template>
			<div class="monitor-content">
				<!-- 统计卡片 -->
				<el-row :gutter="20" class="stats-row">
					<el-col :span="6">
						<div class="stat-card">
							<div class="stat-label">总连接数</div>
							<div class="stat-value">
								{{ stats.totalConnections }}
							</div>
						</div>
					</el-col>
					<el-col :span="6">
						<div class="stat-card">
							<div class="stat-label">成功数</div>
							<div class="stat-value success">
								{{ stats.successCount }}
							</div>
						</div>
					</el-col>
					<el-col :span="6">
						<div class="stat-card">
							<div class="stat-label">失败数</div>
							<div class="stat-value error">
								{{ stats.failureCount }}
							</div>
						</div>
					</el-col>
					<el-col :span="6">
						<div class="stat-card">
							<div class="stat-label">成功率</div>
							<div class="stat-value">
								{{ stats.successRate }}%
							</div>
						</div>
					</el-col>
				</el-row>

				<!-- 图表区域 -->
				<div class="chart-container">
					<el-col :span="12">
						<div class="chart-item">
							<h3>连接数变化</h3>
							<div class="chart">连接数变化图表</div>
						</div>
					</el-col>
					<el-col :span="12">
						<div class="chart-item">
							<h3>响应时间变化</h3>
							<div class="chart">响应时间变化图表</div>
						</div>
					</el-col>
				</div>

				<!-- 资源监控 -->
				<div class="resource-monitor">
					<h3>资源监控</h3>
					<el-row :gutter="20">
						<el-col :span="12">
							<div class="chart-item">
								<h4>CPU使用率</h4>
								<div class="small-chart">CPU使用率图表</div>
							</div>
						</el-col>
						<el-col :span="12">
							<div class="chart-item">
								<h4>内存使用率</h4>
								<div class="small-chart">内存使用率图表</div>
							</div>
						</el-col>
					</el-row>
				</div>

				<!-- 失败详情 -->
				<div class="failure-details">
					<h3>失败详情</h3>
					<el-table :data="failureList" style="width: 100%" border>
						<el-table-column prop="time" label="时间" width="180" />
						<el-table-column
							prop="deviceIp"
							label="设备IP"
							width="150"
						/>
						<el-table-column prop="error" label="错误信息" />
					</el-table>
				</div>
			</div>
		</el-card>

		<!-- 测试报告 -->
		<el-card v-if="testMode === 'simulate' && testReport" class="report-card">
			<template #header>
				<div class="card-header">
					<span>测试报告</span>
				</div>
			</template>
			<div class="report-content">
				<div class="report-summary">
					<h3>测试摘要</h3>
					<el-row :gutter="20">
						<el-col :span="8">
							<div class="summary-item">
								<span class="summary-label">测试时间：</span>
								<span class="summary-value">{{
									testReport.testTime
								}}</span>
							</div>
						</el-col>
						<el-col :span="8">
							<div class="summary-item">
								<span class="summary-label">设备数量：</span>
								<span class="summary-value">{{
									testReport.deviceCount
								}}</span>
							</div>
						</el-col>
						<el-col :span="8">
							<div class="summary-item">
								<span class="summary-label">测试时长：</span>
								<span class="summary-value"
									>{{ testReport.duration }}秒</span
								>
							</div>
						</el-col>
						<el-col :span="8">
							<div class="summary-item">
								<span class="summary-label">成功率：</span>
								<span class="summary-value"
									>{{ testReport.successRate }}%</span
								>
							</div>
						</el-col>
						<el-col :span="8">
							<div class="summary-item">
								<span class="summary-label"
									>平均响应时间：</span
								>
								<span class="summary-value"
									>{{ testReport.avgResponseTime }}ms</span
								>
							</div>
						</el-col>
						<el-col :span="8">
							<div class="summary-item">
								<span class="summary-label"
									>最大响应时间：</span
								>
								<span class="summary-value"
									>{{ testReport.maxResponseTime }}ms</span
								>
							</div>
						</el-col>
					</el-row>
				</div>

				<div class="report-actions">
					<el-button type="primary" @click="downloadReport('pdf')">
						<el-icon><Download /></el-icon>
						下载PDF报告
					</el-button>
					<el-button type="success" @click="downloadReport('html')">
						<el-icon><Download /></el-icon>
						下载HTML报告
					</el-button>
				</div>
			</div>
		</el-card>
	</div>
</template>

<script setup lang="ts">
import { computed, ref, reactive, onMounted, onUnmounted, watch } from "vue";
import { Download } from "@element-plus/icons-vue";
import { ElMessage } from "element-plus";
import { csApi, type CsParallelTestRequest, type CsParallelTestResult } from "@/api/cs";
import { downloadBlob } from "@/api/browserDownload";
import {
	machineConnectionVerificationApi,
	type BatchTestRequest,
	type BatchTestResult,
} from "@/api/machineConnectionVerification";
import {
	machineConnectionDevicesApi,
	type DeviceDto,
	type DeviceTypeApi,
} from "@/api/machineConnectionDevices";

// 验证模式：真机批量验证（/api/connection-verification） / 模拟压测（/api/cs/parallel-test）
const testMode = ref<"real" | "simulate">("real");

// ---------- 真机批量验证 ----------
const realDevices = ref<DeviceDto[]>([]);
const realTesting = ref(false);
const realResult = ref<BatchTestResult | null>(null);
const realConfig = reactive({
	scope: "byType" as "byType" | "byDevices",
	deviceType: "CNC" as DeviceTypeApi,
	deviceIds: [] as string[],
	concurrency: 50,
});

const realDeviceName = (id: string) =>
	realDevices.value.find((d) => d.id === id)?.name ?? id;

const loadRealDevices = async () => {
	try {
		realDevices.value = await machineConnectionDevicesApi.list();
	} catch (e: unknown) {
		const ax = e as { message?: string };
		ElMessage.error(ax.message ?? "加载设备列表失败");
	}
};

const startRealTest = async () => {
	const request: BatchTestRequest = { concurrency: realConfig.concurrency };
	if (realConfig.scope === "byDevices") {
		if (!realConfig.deviceIds.length) {
			ElMessage.warning("请至少选择一台设备");
			return;
		}
		request.deviceIds = [...realConfig.deviceIds];
	} else {
		request.deviceType = realConfig.deviceType;
	}

	realTesting.value = true;
	realResult.value = null;
	try {
		const res = await machineConnectionVerificationApi.start(request);
		realResult.value = res;
		historyTestId.value = res.testId;
		const rate = (res.successRate * 100).toFixed(2);
		if (res.failureCount === 0) {
			ElMessage.success(
				`验证完成：${res.successCount}/${res.totalDevices} 全部成功`,
			);
		} else {
			ElMessage.warning(
				`验证完成：成功率 ${rate}%（${res.failureCount} 台失败）`,
			);
		}
	} catch (e: unknown) {
		const ax = e as {
			response?: { data?: { error?: string; detail?: string } | string };
			message?: string;
		};
		const data = ax.response?.data;
		const msg =
			typeof data === "string"
				? data
				: data?.error ?? data?.detail ?? ax.message ?? "真机批量验证失败";
		ElMessage.error(msg);
	} finally {
		realTesting.value = false;
	}
};

// 按测试ID查询历史验证结果（后端 /api/connection-verification/{testId}，服务重启后清空）
const historyTestId = ref("");
const queryingHistory = ref(false);
const queryHistoryResult = async () => {
	const id = historyTestId.value.trim();
	if (!id) {
		ElMessage.warning("请输入测试ID");
		return;
	}
	queryingHistory.value = true;
	try {
		realResult.value = await machineConnectionVerificationApi.getResult(id);
		ElMessage.success("已加载历史验证结果");
	} catch (e: unknown) {
		const ax = e as { response?: { status?: number } };
		ElMessage.error(
			ax.response?.status === 404
				? "未找到该测试ID的结果（结果保存在采集服务内存中，服务重启后会清空）"
				: "查询历史结果失败",
		);
	} finally {
		queryingHistory.value = false;
	}
};

// ---------- 模拟压测（C/S） ----------
// 测试配置
const testConfig = reactive({
	deviceCount: 100,
	concurrentCount: 100,
	protocol: "ModbusTCP",
	connectionMode: "long",
	startIp: "192.168.1.1",
	port: 502,
	duration: 60,
	mqttUseTls: false,
	mqttClientId: "",
	mqttUsername: "",
	mqttPassword: "",
});

// 测试状态
const isTesting = ref(false);
const testStatus = ref("准备中");

// 压测取消句柄（停止/卸载时中断进行中的请求）
let abortController: AbortController | undefined;

// 统计数据
const stats = reactive({
	totalConnections: 0,
	successCount: 0,
	failureCount: 0,
	successRate: 0,
});

// 失败列表
const failureList = ref<{ time: string; deviceIp: string; error: string }[]>(
	[],
);

// 测试报告
const testReport = ref<any>(null);
const lastParallelRequest = ref<CsParallelTestRequest | null>(null);
const lastParallelResult = ref<CsParallelTestResult | null>(null);

const parseIpv4 = (ip: string): number | null => {
	const parts = ip.split(".").map((part) => Number(part));
	if (parts.length !== 4 || parts.some((part) => !Number.isInteger(part) || part < 0 || part > 255)) {
		return null;
	}
	return parts.reduce((acc, part) => (acc << 8) + part, 0) >>> 0;
};

const formatIpv4 = (value: number) =>
	`${(value >>> 24) & 255}.${(value >>> 16) & 255}.${(value >>> 8) & 255}.${value & 255}`;

// 与后端 CsConnectivityService 的钳制值保持一致（MaxConcurrent=100、MaxHoldMs=60_000），
// 否则用户填的参数会被后端静默降级，报告展示与实际执行不符。
const maxConcurrentCount = 100;
const maxHoldSeconds = 60;

watch(
	() => testConfig.deviceCount,
	(deviceCount, oldDeviceCount) => {
		const fullConcurrentCount = Math.min(deviceCount, maxConcurrentCount);
		if (testConfig.concurrentCount === oldDeviceCount || testConfig.concurrentCount > fullConcurrentCount) {
			testConfig.concurrentCount = fullConcurrentCount;
		}
	},
);
const isMqttProtocol = computed(() => testConfig.protocol === "MQTT");
const reportConnectionMode = computed(() => testConfig.connectionMode === "long" ? "长连接" : "短连接");

const updateSuccessRate = () => {
	stats.successRate = stats.totalConnections === 0
		? 0
		: Math.round((stats.successCount * 100) / stats.totalConnections);
};

watch(
	() => testConfig.protocol,
	(protocol) => {
		if (protocol === "MQTT" && testConfig.port === 502) {
			testConfig.port = 1883;
		}
		if (protocol !== "MQTT" && testConfig.port === 1883) {
			testConfig.port = 502;
		}
	},
);

// 开始测试 = 一次调用网关并发压测接口，由后端按 concurrentCount 控制在途连接数
const startTest = async () => {
	const host = testConfig.startIp.trim();
	const startValue = isMqttProtocol.value ? null : parseIpv4(host);
	if (!host) {
		ElMessage.error(isMqttProtocol.value ? "Broker 地址不能为空" : "起始 IP 不能为空");
		return;
	}
	if (!isMqttProtocol.value && startValue === null) {
		ElMessage.error("起始 IP 格式不正确");
		return;
	}

	isTesting.value = true;
	testStatus.value = "测试中 0%";

	stats.totalConnections = 0;
	stats.successCount = 0;
	stats.failureCount = 0;
	stats.successRate = 0;
	failureList.value = [];
	testReport.value = null;
	lastParallelRequest.value = null;
	lastParallelResult.value = null;

	abortController = new AbortController();

	try {
		const total = testConfig.deviceCount;
		const currentConcurrent = Math.min(testConfig.concurrentCount, total);
		stats.totalConnections = currentConcurrent;
		testStatus.value = isMqttProtocol.value
			? `MQTT连接中：当前并发 ${currentConcurrent} / 目标 ${total}`
			: testConfig.connectionMode === "long"
				? `连接保持中：当前并发 ${currentConcurrent} / 目标 ${total}`
				: `测试中：目标 ${total}`;
		const request: CsParallelTestRequest = {
			startIp: isMqttProtocol.value ? host : formatIpv4(startValue!),
			port: testConfig.port,
			deviceCount: total,
			concurrentCount: testConfig.concurrentCount,
			timeoutMs: 3000,
			holdMs: !isMqttProtocol.value && testConfig.connectionMode === "long" ? testConfig.duration * 1000 : 0,
			protocol: testConfig.protocol,
			mqttUseTls: testConfig.mqttUseTls,
			mqttClientId: testConfig.mqttClientId.trim() || undefined,
			mqttUsername: testConfig.mqttUsername.trim() || undefined,
			mqttPassword: testConfig.mqttPassword || undefined,
		};
		const res = await csApi.runParallelTest(request, abortController.signal);
		lastParallelRequest.value = request;
		lastParallelResult.value = res;

		stats.totalConnections = res.total;
		stats.successCount = res.success;
		stats.failureCount = res.failure;
		updateSuccessRate();
		failureList.value = res.failures
			.map((f) => ({ time: f.time, deviceIp: f.deviceIp, error: f.error }))
			.slice(-100);
		testReport.value = {
			testTime: res.finishedAt || new Date().toLocaleString(),
			deviceCount: stats.totalConnections,
			duration: testConfig.duration,
			successRate: stats.successRate,
			avgResponseTime: res.avgRttMs,
			maxResponseTime: res.maxRttMs,
		};
		testStatus.value = abortController.signal.aborted ? "已停止" : "测试完成";
		if (!abortController.signal.aborted) {
			ElMessage.success(
				`压测完成：${stats.successCount}/${stats.totalConnections} 成功，成功率 ${stats.successRate}%`,
			);
		}
	} catch (e: unknown) {
		const ax = e as { code?: string; message?: string };
		if (ax.code === "ERR_CANCELED") {
			testStatus.value = "已停止";
		} else {
			testStatus.value = "测试失败";
			ElMessage.error(ax.message ?? "并发压测失败");
		}
	} finally {
		isTesting.value = false;
		abortController = undefined;
	}
};

// 停止测试 = 中断进行中的压测请求
const stopTest = () => {
	abortController?.abort();
	isTesting.value = false;
	testStatus.value = "已停止";
};

// 重置配置
const resetConfig = () => {
	Object.assign(testConfig, {
		deviceCount: 100,
		concurrentCount: 100,
		protocol: "ModbusTCP",
		connectionMode: "long",
		startIp: "192.168.1.1",
		port: 502,
		duration: 60,
		mqttUseTls: false,
		mqttClientId: "",
		mqttUsername: "",
		mqttPassword: "",
	});
};

// 下载报告
const downloadReport = async (format: "pdf" | "html") => {
	if (!lastParallelRequest.value || !lastParallelResult.value) {
		ElMessage.warning("请先完成一次并行连接验证");
		return;
	}

	try {
		const blob = await csApi.downloadParallelReport(format, {
			request: lastParallelRequest.value,
			result: lastParallelResult.value,
			connectionMode: reportConnectionMode.value,
			durationSeconds: testConfig.duration,
			generatedAt: new Date().toLocaleString(),
		});
		downloadBlob(blob, `parallel-connection-report-${Date.now()}.${format}`);
	} catch (e: unknown) {
		const ax = e as { message?: string };
		ElMessage.error(ax.message ?? "报告下载失败");
	}
};

onMounted(() => {
	void loadRealDevices();
});

onUnmounted(() => {
	// 清理资源：中断仍在进行的压测请求，避免组件卸载后回调泄漏
	abortController?.abort();
	abortController = undefined;
});
</script>

<style lang="scss" scoped>
.parallel-connection-view {
	.mode-card {
		margin-bottom: 20px;

		.mode-tip {
			margin-left: 16px;
			font-size: 13px;
			color: var(--el-text-color-secondary);
		}
	}

	.config-card {
		margin-bottom: 20px;
	}

	.monitor-card {
		margin-bottom: 20px;

		.card-header {
			display: flex;
			justify-content: space-between;
			align-items: center;

			.test-status {
				font-size: 14px;
				color: #1890ff;
				font-weight: 500;
			}
		}

		.stats-row {
			margin-bottom: 30px;
		}

		.stat-card {
			background-color: #fafafa;
			border-radius: 8px;
			padding: 20px;
			text-align: center;

			.stat-label {
				font-size: 14px;
				color: #666;
				margin-bottom: 10px;
			}

			.stat-value {
				font-size: 24px;
				font-weight: 600;

				&.success {
					color: #52c41a;
				}

				&.error {
					color: #ff4d4f;
				}
			}
		}

		.chart-container {
			margin-bottom: 30px;
		}

		.chart-item {
			margin-bottom: 20px;

			h3 {
				font-size: 14px;
				font-weight: 600;
				margin-bottom: 10px;
				color: #333;
			}

			.chart {
				height: 300px;
				background-color: #fafafa;
				border-radius: 4px;
				display: flex;
				align-items: center;
				justify-content: center;
				color: #999;
			}

			.small-chart {
				height: 200px;
			}
		}

		.resource-monitor {
			margin-bottom: 30px;

			h3 {
				font-size: 14px;
				font-weight: 600;
				margin-bottom: 15px;
				color: #333;
			}
		}

		.failure-details {
			h3 {
				font-size: 14px;
				font-weight: 600;
				margin-bottom: 15px;
				color: #333;
			}
		}
	}

	.report-card {
		.report-summary {
			margin-bottom: 30px;

			h3 {
				font-size: 16px;
				font-weight: 600;
				margin-bottom: 15px;
				color: #333;
			}

			.summary-item {
				margin-bottom: 10px;

				.summary-label {
					font-weight: 500;
					color: #666;
				}

				.summary-value {
					color: #333;
				}
			}
		}

		.report-actions {
			display: flex;
			gap: 10px;
			justify-content: flex-end;
		}
	}
}
</style>
