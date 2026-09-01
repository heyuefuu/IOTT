<template>
	<div class="verify-task-manage-view">
		<h2 class="page-title">验证任务管理</h2>

		<el-alert
			type="info"
			:closable="false"
			show-icon
			style="margin-bottom: 16px"
			title="本页已接入业务后端：任务、执行状态和结果持久化保存，执行时调用自动验证接口。"
		/>

		<!-- 任务列表 -->
		<el-card class="task-list-card">
			<template #header>
				<div class="card-header">
					<span>任务列表</span>
					<div>
						<el-button @click="openAdHocRunDialog">
							即席验证
						</el-button>
						<el-button type="primary" @click="openTaskCreateDialog">
							<el-icon><Plus /></el-icon>
							创建任务
						</el-button>
					</div>
				</div>
			</template>

			<div class="task-list-content">
				<!-- 任务搜索和筛选 -->
				<div class="task-filter">
					<el-form :inline="true" :model="filterForm">
						<el-form-item label="任务状态">
							<el-select
								v-model="filterForm.status"
								placeholder="请选择任务状态"
							>
								<el-option label="全部" value="" />
								<el-option label="待执行" value="pending" />
								<el-option label="执行中" value="running" />
								<el-option label="已完成" value="completed" />
								<el-option label="失败" value="failed" />
							</el-select>
						</el-form-item>
						<el-form-item label="任务类型">
							<el-select
								v-model="filterForm.type"
								placeholder="请选择任务类型"
							>
								<el-option label="全部" value="" />
								<el-option
									label="性能测试"
									value="performance"
								/>
								<el-option label="功能测试" value="function" />
								<el-option
									label="稳定性测试"
									value="stability"
								/>
								<el-option
									label="兼容性测试"
									value="compatibility"
								/>
							</el-select>
						</el-form-item>
						<el-form-item label="关键词">
							<el-input
								v-model="filterForm.keyword"
								placeholder="请输入关键词"
								style="width: 200px"
							/>
						</el-form-item>
						<el-form-item>
							<el-button type="primary" @click="searchTasks">
								<el-icon><Search /></el-icon>
								搜索
							</el-button>
						</el-form-item>
						<el-form-item>
							<el-button @click="resetFilter"> 重置 </el-button>
						</el-form-item>
					</el-form>
				</div>

				<!-- 可扩展任务列表 -->
				<el-table
					v-if="filteredTasks.length > 0"
					:data="filteredTasks"
					style="width: 100%"
					border
					row-key="id"
					:expand-row-keys="expandedRows"
				>
					<el-table-column type="expand">
						<template #default="scope">
							<div class="task-detail">
								<el-descriptions :column="2" border>
									<el-descriptions-item label="任务ID">{{ scope.row.id }}</el-descriptions-item>
									<el-descriptions-item label="测试设备">{{ getDeviceName(scope.row.deviceId) }}</el-descriptions-item>
									<el-descriptions-item label="创建时间">{{ scope.row.createdAt }}</el-descriptions-item>
									<el-descriptions-item label="完成时间">{{ scope.row.completedAt || "-" }}</el-descriptions-item>
									<el-descriptions-item label="测试内容" :span="2">{{ getMetricSummary(scope.row.metricIds) }}</el-descriptions-item>
									<el-descriptions-item label="测试参数" :span="2">{{ scope.row.params || "-" }}</el-descriptions-item>
									<el-descriptions-item label="任务描述" :span="2">{{ scope.row.description || "-" }}</el-descriptions-item>
									<el-descriptions-item label="详细信息" :span="2">{{ scope.row.detail || "-" }}</el-descriptions-item>
								</el-descriptions>
							</div>
						</template>
					</el-table-column>
					<el-table-column prop="id" label="任务ID" width="100" />
					<el-table-column prop="name" label="任务名称" />
					<el-table-column prop="type" label="任务类型" width="120">
						<template #default="scope">
							<el-tag :type="getTaskTypeTag(scope.row.type)">
								{{ getTaskTypeName(scope.row.type) }}
							</el-tag>
						</template>
					</el-table-column>
					<el-table-column label="测试内容" min-width="220">
						<template #default="scope">
							{{ getMetricSummary(scope.row.metricIds) }}
						</template>
					</el-table-column>
					<el-table-column prop="status" label="状态" width="100">
						<template #default="scope">
							<el-tag :type="getTaskStatusTag(scope.row.status)">
								{{ getTaskStatusName(scope.row.status) }}
							</el-tag>
						</template>
					</el-table-column>
					<el-table-column prop="priority" label="优先级" width="100">
						<template #default="scope">
							<el-tag
								:type="
									scope.row.priority === '高'
										? 'danger'
										: scope.row.priority === '中'
											? 'warning'
											: 'info'
								"
							>
								{{ scope.row.priority }}
							</el-tag>
						</template>
					</el-table-column>
					<el-table-column prop="createdAt" label="创建时间" width="180" />
					<el-table-column prop="completedAt" label="完成时间" width="180" />
					<el-table-column label="操作" width="360">
						<template #default="scope">
							<el-button
								type="primary"
								size="small"
								@click="openEditTaskDialog(scope.row)"
							>
								编辑
							</el-button>
							<el-button
								type="success"
								size="small"
								@click="executeTask(scope.row)"
								style="margin-left: 5px"
								:loading="executingTaskId === scope.row.id"
							>
								{{ scope.row.status === "running" ? "重新执行" : "执行" }}
							</el-button>
							<el-button
								type="info"
								size="small"
								@click="viewTaskResult(scope.row)"
								style="margin-left: 5px"
								:disabled="scope.row.status === 'pending'"
							>
								查看结果
							</el-button>
							<el-button
								type="warning"
								size="small"
								:loading="exportingTaskId === scope.row.id"
								:disabled="!scope.row.lastRunJson"
								@click="exportTask(scope.row)"
								style="margin-left: 5px"
							>
								导出Excel
							</el-button>
							<el-button
								type="danger"
								size="small"
								@click="deleteTask(scope.row)"
							>
								删除
							</el-button>
						</template>
					</el-table-column>
				</el-table>

				<!-- 无任务提示 -->
				<div v-else class="no-tasks">
					<el-empty description="暂无任务" />
				</div>
			</div>
		</el-card>

		<!-- 任务创建对话框 -->
		<el-dialog
			v-model="taskCreateDialogVisible"
			title="任务创建向导"
			width="900px"
			max-height="80vh"
			:close-on-click-modal="false"
		>
			<div class="task-wizard-container">
				<!-- 步骤指示器 -->
				<div class="task-wizard-steps">
					<el-steps :active="currentStep" finish-status="success">
						<el-step title="任务创建" />
						<el-step title="测试内容" />
						<el-step title="参数配置" />
						<el-step title="任务执行" />
						<el-step title="结果查看" />
					</el-steps>
				</div>

				<!-- 步骤内容 -->
				<div class="task-wizard-content">
					<!-- 步骤1: 任务创建 -->
					<div v-if="currentStep === 0" class="step-content">
						<h3 class="step-title">步骤1 - 任务创建</h3>
						<el-form :model="currentTask" label-width="120px">
							<el-form-item label="任务名称" prop="name" required>
								<el-input
									v-model="currentTask.name"
									placeholder="请输入任务名称"
								/>
							</el-form-item>
							<el-form-item label="任务类型" prop="type" required>
								<el-select
									v-model="currentTask.type"
									placeholder="请选择任务类型"
								>
									<el-option label="性能测试" value="performance" />
									<el-option label="功能测试" value="function" />
									<el-option label="稳定性测试" value="stability" />
									<el-option label="兼容性测试" value="compatibility" />
								</el-select>
							</el-form-item>
							<el-form-item label="优先级" prop="priority">
								<el-select
									v-model="currentTask.priority"
									placeholder="请选择优先级"
								>
									<el-option label="高" value="高" />
									<el-option label="中" value="中" />
									<el-option label="低" value="低" />
								</el-select>
							</el-form-item>
							<el-form-item label="测试机床" prop="machineId" required>
								<el-select
									v-model="currentTask.machineId"
									placeholder="请选择测试机床"
								>
									<el-option
										v-for="machine in machines"
										:key="machine.id"
										:label="machine.name"
										:value="machine.id"
									>
										<div class="machine-option">
											<span>{{ machine.name }}</span>
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
							</el-form-item>
							<el-form-item label="测试设备" prop="deviceId">
								<el-select
									v-model="currentTask.deviceId"
									placeholder="请选择测试设备"
								>
									<el-option
										v-for="machine in machines"
										:key="machine.id"
										:label="machine.name"
										:value="machine.id"
									/>
								</el-select>
							</el-form-item>
						</el-form>
					</div>

					<!-- 步骤2: 测试内容配置 -->
					<div v-else-if="currentStep === 1" class="step-content">
						<h3 class="step-title">步骤2 - 测试内容配置</h3>
						<el-checkbox-group v-model="currentTask.metricIds" class="metric-check-list">
							<el-checkbox
								v-for="metric in availableMetrics"
								:key="metric.id"
								:label="metric.id"
							>
								{{ metric.code }} {{ metric.name }}
							</el-checkbox>
						</el-checkbox-group>
						<div style="margin-top: 16px">
							<el-button
								type="success"
								:loading="adHocRunning"
								:disabled="currentTask.metricIds.length === 0"
								@click="runAdHocVerification"
							>
								立即验证所选指标
							</el-button>
						</div>
						<el-table :data="availableMetrics" style="width: 100%; margin-top: 16px" border>
							<el-table-column prop="code" label="编号" width="90" />
							<el-table-column prop="name" label="指标" width="150" />
							<el-table-column label="当前状态" width="120">
								<template #default="scope">
									<el-tag :type="scope.row.statusType">{{ scope.row.statusLabel }}</el-tag>
								</template>
							</el-table-column>
							<el-table-column prop="description" label="对齐说明" min-width="360" />
						</el-table>
					</div>

					<!-- 步骤3: 参数配置 -->
					<div v-else-if="currentStep === 2" class="step-content">
						<h3 class="step-title">步骤3 - 参数配置</h3>
						<el-form :model="currentTask" label-width="120px">
							<el-form-item label="测试参数" prop="params">
								<el-input
									v-model="currentTask.params"
									type="textarea"
									placeholder="请输入测试参数"
									rows="4"
								/>
							</el-form-item>
							<el-form-item label="任务描述" prop="description">
								<el-input
									v-model="currentTask.description"
									type="textarea"
									placeholder="请输入任务描述"
									rows="3"
								/>
							</el-form-item>
							<el-form-item label="执行设置">
								<el-checkbox v-model="executionSettings.immediate">立即执行</el-checkbox>
								<el-checkbox v-model="executionSettings.scheduled">定时执行</el-checkbox>
								<el-date-picker
									v-if="executionSettings.scheduled"
									v-model="executionSettings.scheduleTime"
									type="datetime"
									placeholder="选择执行时间"
									style="margin-left: 10px"
								/>
							</el-form-item>
						</el-form>
					</div>

					<!-- 步骤4: 任务执行 -->
					<div v-else-if="currentStep === 3" class="step-content">
						<h3 class="step-title">步骤4 - 任务执行</h3>
						<div class="task-execution-info">
							<el-descriptions :column="1" border>
								<el-descriptions-item label="任务名称">{{ currentTask.name }}</el-descriptions-item>
								<el-descriptions-item label="任务类型">{{ getTaskTypeName(currentTask.type) }}</el-descriptions-item>
								<el-descriptions-item label="测试机床">{{ getMachineName(currentTask.machineId) }}</el-descriptions-item>
								<el-descriptions-item label="测试设备">{{ getDeviceName(currentTask.deviceId) }}</el-descriptions-item>
								<el-descriptions-item label="优先级">{{ currentTask.priority }}</el-descriptions-item>
								<el-descriptions-item label="指标数量">{{ currentTask.metricIds.length }}</el-descriptions-item>
								<el-descriptions-item label="测试内容">{{ getMetricSummary(currentTask.metricIds) }}</el-descriptions-item>
								<el-descriptions-item label="测试参数">{{ currentTask.params || "-" }}</el-descriptions-item>
							</el-descriptions>
						</div>
						<div class="execution-actions" style="margin-top: 20px">
							<el-button type="primary" size="large" :loading="taskExecuting" @click="executeCurrentTask">
								自动执行
							</el-button>
							<el-button size="large" @click="resetWizard">
								重新配置
							</el-button>
						</div>
					</div>

					<!-- 步骤5: 结果查看 -->
					<div v-else-if="currentStep === 4" class="step-content">
						<h3 class="step-title">步骤5 - 结果查看</h3>
						<div v-if="executionResult" class="task-result">
							<el-descriptions :column="1" border>
								<el-descriptions-item label="任务状态">{{ getTaskStatusName(executionResult.status) }}</el-descriptions-item>
								<el-descriptions-item label="执行时间">{{ executionResult.executionTime || "-" }}</el-descriptions-item>
								<el-descriptions-item label="结果">{{ executionResult.result || "-" }}</el-descriptions-item>
								<el-descriptions-item label="详细信息">{{ executionResult.detail || "-" }}</el-descriptions-item>
							</el-descriptions>
							
							<!-- 指标结果 -->
							<div v-if="metricResults.length > 0" style="margin-top: 20px">
								<h4>指标结果</h4>
								<el-table :data="metricResults" style="width: 100%" border>
									<el-table-column prop="metricName" label="指标名称" />
									<el-table-column prop="status" label="状态">
										<template #default="scope">
											<el-tag :type="getTaskStatusTag(scope.row.status)">
												{{ getTaskStatusName(scope.row.status) }}
											</el-tag>
										</template>
									</el-table-column>
									<el-table-column prop="reference" label="参考标准" />
									<el-table-column prop="result" label="结果" />
									<el-table-column prop="detail" label="详细信息" />
								</el-table>
							</div>
						</div>
						<div v-else class="execution-pending">
							<el-empty description="任务尚未执行" />
						</div>
					</div>
				</div>

				<!-- 导航按钮 -->
				<div class="task-wizard-navigation">
					<el-button @click="prevStep" :disabled="currentStep === 0">上一步</el-button>
					<el-button v-if="currentStep < 3" type="primary" @click="nextStep">下一步</el-button>
					<el-button v-else-if="currentStep === 3" type="primary" @click="completeWizard">保存任务</el-button>
					<el-button v-else type="success" @click="completeWizard">完成</el-button>
					<el-button @click="closeTaskCreateDialog" style="margin-left: 10px">取消</el-button>
				</div>
			</div>
		</el-dialog>

		<!-- 任务结果对话框 -->
		<el-dialog
			v-model="resultDialogVisible"
			:title="`任务结果: ${selectedTask?.name || ''}`"
			width="700px"
		>
			<div v-if="selectedTask" class="task-result">
				<el-descriptions :column="1" border>
					<el-descriptions-item label="任务ID">{{
						selectedTask.id
					}}</el-descriptions-item>
					<el-descriptions-item label="任务名称">{{
						selectedTask.name
					}}</el-descriptions-item>
					<el-descriptions-item label="任务类型">{{
						getTaskTypeName(selectedTask.type)
					}}</el-descriptions-item>
					<el-descriptions-item label="状态">{{
						getTaskStatusName(selectedTask.status)
					}}</el-descriptions-item>
					<el-descriptions-item label="测试内容">{{
						getMetricSummary(selectedTask.metricIds)
					}}</el-descriptions-item>
					<el-descriptions-item label="执行时间">{{
						selectedTask.executionTime || "-"
					}}</el-descriptions-item>
					<el-descriptions-item label="结果">{{
						selectedTask.result || "-"
					}}</el-descriptions-item>
					<el-descriptions-item label="详细信息">{{
						selectedTask.detail || "-"
					}}</el-descriptions-item>
				</el-descriptions>
				<el-table :data="getMetricDetails(selectedTask.metricIds)" style="width: 100%; margin-top: 16px" border>
					<el-table-column prop="code" label="编号" width="90" />
					<el-table-column prop="name" label="指标" width="150" />
					<el-table-column label="当前状态" width="120">
						<template #default="scope">
							<el-tag :type="scope.row.statusType">{{ scope.row.statusLabel }}</el-tag>
						</template>
					</el-table-column>
					<el-table-column prop="description" label="对齐说明" min-width="360" />
				</el-table>
			</div>
			<template #footer>
				<span class="dialog-footer">
					<el-button @click="resultDialogVisible = false"
						>关闭</el-button
					>
				</span>
			</template>
		</el-dialog>
	</div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted } from "vue";
import { Plus, Search } from "@element-plus/icons-vue";
import { ElMessage, ElMessageBox } from "element-plus";
import { machineConnectionDevicesApi } from "@/api/machineConnectionDevices";
import { businessValidationApi, type MetricDto } from "@/api/businessValidation";
import { machineConnectionVerifyApi } from "@/api/machineConnectionVerify";
import { downloadBlob } from "@/api/browserDownload";

// 任务类型定义
interface Task {
	id: string;
	name: string;
	type: string;
	status: string;
	priority: string;
	deviceId: string;
	machineId: string; // 机床ID
	metricIds: string[];
	params: string;
	description: string;
	createdAt: string;
	completedAt?: string | null;
	executionTime: string;
	result: string;
	detail: string;
	/** none = 手动；daily = 每天 scheduleTime（HH:mm）后台自动执行 */
	scheduleType?: string;
	scheduleTime?: string;
	lastAutoRunAt?: string | null;
	/** 最近一次运行结果留痕（JSON），非空才能导出 Excel */
	lastRunJson?: string;
}

// 机床类型定义
interface Machine {
	id: string;
	name: string;
	status: string;
	type: string;
	ip: string;
}

type MetricOption = MetricDto;

// 执行设置类型
interface ExecutionSettings {
	immediate: boolean;
	scheduled: boolean;
	scheduleTime: Date | null;
}

// 执行结果类型
interface ExecutionResult {
	status: string;
	executionTime: string;
	result: string;
	detail: string;
}

// 指标结果类型
interface MetricResult {
	metricName: string;
	status: string;
	reference: string;
	result: string;
	detail: string;
}

const machines = ref<Machine[]>([]);
const availableMetrics = ref<MetricOption[]>([]);
const tasks = ref<Task[]>([]);

// 筛选表单
const filterForm = reactive({
	status: "",
	type: "",
	keyword: "",
});

// 选中的机床
const selectedMachine = ref("");

// 激活的机床分组
const activeMachineGroups = ref<string[]>([]);

// 任务对话框
const resultDialogVisible = ref(false);
const selectedTask = ref<Task | null>(null);

// 步骤管理
const currentStep = ref(0);
const currentTask = reactive<Task>({
	id: "",
	name: "",
	type: "performance",
	status: "pending",
	priority: "中",
	deviceId: "",
	machineId: "",
	metricIds: [],
	params: "",
	description: "",
	createdAt: new Date().toISOString(),
	completedAt: null,
	executionTime: "",
	result: "",
	detail: "",
});

// 任务创建对话框
const taskCreateDialogVisible = ref(false);

// 展开的行
const expandedRows = ref<string[]>([]);

// 指标结果
const metricResults = ref<MetricResult[]>([]);
const taskExecuting = ref(false);
const executingTaskId = ref("");
const adHocRunning = ref(false);

// 执行设置
const executionSettings = reactive<ExecutionSettings>({
	immediate: true,
	scheduled: false,
	scheduleTime: null,
});

// 执行结果
const executionResult = ref<ExecutionResult | null>(null);

const defaultMetricIds = () => availableMetrics.value.map((metric) => metric.id);

const loadPageData = async () => {
	try {
		const [deviceList, metricList, taskList] = await Promise.all([
			machineConnectionDevicesApi.list(),
			businessValidationApi.listMetrics(),
			machineConnectionVerifyApi.listTasks(),
		]);
		machines.value = deviceList.map((device) => ({
			id: device.id,
			name: device.name,
			status: device.status === "Online" ? "在线" : "离线",
			type: device.type,
			ip: device.host,
		}));
		availableMetrics.value = metricList;
		tasks.value = taskList;
	} catch (error) {
		console.error(error);
		ElMessage.error("验证任务数据加载失败，请检查业务后端服务");
	}
};

// 获取任务类型标签
const getTaskTypeTag = (type: string) => {
	switch (type) {
		case "performance":
			return "primary";
		case "function":
			return "success";
		case "stability":
			return "warning";
		case "compatibility":
			return "info";
		default:
			return "info";
	}
};

// 获取任务类型名称
const getTaskTypeName = (type: string) => {
	switch (type) {
		case "performance":
			return "性能测试";
		case "function":
			return "功能测试";
		case "stability":
			return "稳定性测试";
		case "compatibility":
			return "兼容性测试";
		default:
			return "未知类型";
	}
};

// 获取任务状态标签
const getTaskStatusTag = (status: string) => {
	switch (status) {
		case "pending":
			return "info";
		case "running":
			return "warning";
		case "completed":
		case "passed":
			return "success";
		case "failed":
			return "danger";
		default:
			return "info";
	}
};

// 获取任务状态名称
const getTaskStatusName = (status: string) => {
	switch (status) {
		case "pending":
			return "待执行";
		case "running":
			return "执行中";
		case "completed":
			return "已完成";
		case "passed":
			return "通过";
		case "failed":
			return "失败";
		default:
			return "未知状态";
	}
};

// 获取机床名称
const getMachineName = (machineId: string) => {
	const machine = machines.value.find((m) => m.id === machineId);
	return machine ? machine.name : "未知机床";
};

// 获取设备名称
const getDeviceName = (deviceId: string) => {
	const device = machines.value.find((item) => item.id === deviceId);
	return device ? device.name : "未知设备";
};

const getMetricName = (metricId: string) => {
	return availableMetrics.value.find((metric) => metric.id === metricId)?.name || metricId;
};

const getMetricSummary = (metricIds: string[]) => {
	if (!metricIds?.length) return "-";
	const names = metricIds.map(getMetricName);
	return names.length > 3
		? `${names.slice(0, 3).join("、")} 等${names.length}项`
		: names.join("、");
};

const getMetricDetails = (metricIds: string[]) =>
	metricIds
		.map((metricId) => availableMetrics.value.find((metric) => metric.id === metricId))
		.filter((metric): metric is MetricOption => Boolean(metric));

const getApiErrorMessage = (e: unknown, fallback: string): string => {
	const ax = e as { response?: { data?: { error?: string; detail?: string } }; message?: string };
	return ax.response?.data?.error || ax.response?.data?.detail || ax.message || fallback;
};

// 过滤后的任务
const filteredTasks = computed(() => {
	let result = [...tasks.value];

	// 机床过滤
	if (selectedMachine.value) {
		result = result.filter((task) => task.machineId === selectedMachine.value);
	}

	// 状态过滤
	if (filterForm.status) {
		result = result.filter((task) => task.status === filterForm.status);
	}

	// 类型过滤
	if (filterForm.type) {
		result = result.filter((task) => task.type === filterForm.type);
	}

	// 关键词过滤
	if (filterForm.keyword) {
		const keyword = filterForm.keyword.toLowerCase();
		result = result.filter(
			(task) =>
				task.name.toLowerCase().includes(keyword) ||
				task.description.toLowerCase().includes(keyword),
		);
	}

	return result;
});



// 筛选是响应式实时生效的（filteredTasks computed）；此按钮只负责收起分组便于查看结果
const searchTasks = () => {
	activeMachineGroups.value = [];
};

// 重置筛选
const resetFilter = () => {
	Object.assign(filterForm, {
		status: "",
		type: "",
		keyword: "",
	});
	selectedMachine.value = "";
	activeMachineGroups.value = [];
};

// 下一步
const nextStep = () => {
	if (currentStep.value < 3) {
		// 验证当前步骤
		if (currentStep.value === 0) {
			if (!currentTask.name || !currentTask.type || !currentTask.machineId) {
				ElMessage.warning("请填写任务基本信息");
				return;
			}
		}
		if (currentStep.value === 1 && currentTask.metricIds.length === 0) {
			ElMessage.warning("请至少选择一个测试指标");
			return;
		}
		currentStep.value++;
	}
};

// 上一步
const prevStep = () => {
	if (currentStep.value > 0) {
		currentStep.value--;
	}
};

// 定时字段序列化：Date → "HH:mm"（后端每天该时刻自动执行）
const formatScheduleTime = (value: Date | null): string =>
	value
		? `${String(value.getHours()).padStart(2, "0")}:${String(value.getMinutes()).padStart(2, "0")}`
		: "";

const parseScheduleTime = (time?: string): Date | null => {
	if (!time) return null;
	const [h, m] = time.split(":").map(Number);
	if (!Number.isFinite(h) || !Number.isFinite(m)) return null;
	const d = new Date();
	d.setHours(h as number, m as number, 0, 0);
	return d;
};

// 保存/执行共用的提交体：把向导里的定时设置写进任务
const taskPayload = () => ({
	...currentTask,
	scheduleType:
		executionSettings.scheduled && executionSettings.scheduleTime ? "daily" : "none",
	scheduleTime: executionSettings.scheduled
		? formatScheduleTime(executionSettings.scheduleTime)
		: "",
});

const executeCurrentTask = async () => {
	if (taskExecuting.value) return;
	taskExecuting.value = true;
	try {
		const saved = currentTask.id
			? await machineConnectionVerifyApi.updateTask(currentTask.id, taskPayload())
			: await machineConnectionVerifyApi.createTask(taskPayload());
		currentTask.id = saved.id;
		const response = await machineConnectionVerifyApi.runTask(saved.id);
		executionResult.value = {
			status: response.status,
			executionTime: response.executionTime,
			result: response.result,
			detail: response.detail,
		};
		metricResults.value = [];
		await loadPageData();
		const message = `任务 ${currentTask.name} 自动验证结束：${response.result}`;
		response.status === "completed" ? ElMessage.success(message) : ElMessage.warning(message);
		currentStep.value = 4;
	} catch (e) {
		ElMessage.error(getApiErrorMessage(e, "自动验证执行失败"));
	} finally {
		taskExecuting.value = false;
	}
};

// 重置向导
const resetWizard = () => {
	currentStep.value = 0;
	Object.assign(currentTask, {
		id: "",
		name: "",
		type: "performance",
		status: "pending",
		priority: "中",
		deviceId: "",
		machineId: "",
		metricIds: defaultMetricIds(),
		params: "",
		description: "",
		createdAt: new Date().toISOString(),
		completedAt: null,
		executionTime: "",
		result: "",
		detail: "",
	});
	Object.assign(executionSettings, {
		immediate: true,
		scheduled: false,
		scheduleTime: null,
	});
	metricResults.value = [];
	executionResult.value = null;
};

// 打开任务创建对话框
const openTaskCreateDialog = () => {
	resetWizard();
	taskCreateDialogVisible.value = true;
};

const openAdHocRunDialog = () => {
	resetWizard();
	currentTask.name = "即席验证";
	currentTask.metricIds = defaultMetricIds();
	currentStep.value = 1;
	taskCreateDialogVisible.value = true;
};

const runAdHocVerification = async () => {
	if (adHocRunning.value) return;
	if (currentTask.metricIds.length === 0) {
		ElMessage.warning("请至少选择一个验证指标");
		return;
	}
	adHocRunning.value = true;
	try {
		const response = await machineConnectionVerifyApi.run({
			taskName: currentTask.name || "即席验证",
			metricIds: [...currentTask.metricIds],
		});
		executionResult.value = {
			status: response.status,
			executionTime: response.completedAt || response.startedAt,
			result: response.result,
			detail: response.detail,
		};
		metricResults.value = response.metrics.map((metric) => ({
			metricName: `${metric.code} ${metric.name}`,
			status: metric.status,
			reference: metric.reference,
			result: metric.result || metric.value,
			detail: metric.detail,
		}));
		currentStep.value = 4;
		ElMessage.success(`即席验证完成：${response.result}`);
	} catch (e) {
		ElMessage.error(getApiErrorMessage(e, "即席验证执行失败"));
	} finally {
		adHocRunning.value = false;
	}
};

const openEditTaskDialog = (task: Task) => {
	resetWizard();
	Object.assign(currentTask, { ...task, metricIds: [...task.metricIds] });
	// 回填定时设置（后端 scheduleType=daily / scheduleTime="HH:mm"）
	Object.assign(executionSettings, {
		immediate: task.scheduleType !== "daily",
		scheduled: task.scheduleType === "daily",
		scheduleTime: parseScheduleTime(task.scheduleTime),
	});
	currentStep.value = 0;
	taskCreateDialogVisible.value = true;
};

// 关闭任务创建对话框
const closeTaskCreateDialog = () => {
	taskCreateDialogVisible.value = false;
	resetWizard();
};

// 完成向导
const completeWizard = async () => {
	if (currentStep.value === 4) {
		resetWizard();
		taskCreateDialogVisible.value = false;
		return;
	}
	try {
		if (currentTask.id) {
			await machineConnectionVerifyApi.updateTask(currentTask.id, taskPayload());
		} else {
			await machineConnectionVerifyApi.createTask(taskPayload());
		}
		await loadPageData();
		resetWizard();
		taskCreateDialogVisible.value = false;
		ElMessage.success("任务已保存");
	} catch (e) {
		ElMessage.error(getApiErrorMessage(e, "任务保存失败"));
	}
};

const executeTask = async (task: Task) => {
	if (taskExecuting.value) return;
	taskExecuting.value = true;
	executingTaskId.value = task.id;
	try {
		const response = await machineConnectionVerifyApi.runTask(task.id);
		await loadPageData();
		selectedTask.value = response;
		metricResults.value = [];
		const message = `任务 ${task.name} 自动验证结束：${response.result}`;
		response.status === "completed" ? ElMessage.success(message) : ElMessage.warning(message);
	} catch (e) {
		ElMessage.error(getApiErrorMessage(e, "自动验证执行失败"));
	} finally {
		taskExecuting.value = false;
		executingTaskId.value = "";
	}
};

// 导出最近一次运行结果为 Excel（含实测/参考值对比与空白人工评分列）
const exportingTaskId = ref("");
const exportTask = async (task: Task) => {
	exportingTaskId.value = task.id;
	try {
		const { blob, fileName } = await machineConnectionVerifyApi.exportTaskResult(task.id);
		downloadBlob(blob, fileName);
		ElMessage.success("验证报表已导出");
	} catch (e) {
		ElMessage.error(getApiErrorMessage(e, "导出失败：任务需先成功运行一次"));
	} finally {
		exportingTaskId.value = "";
	}
};

const deleteTask = async (task: Task) => {
	try {
		await ElMessageBox.confirm(`确定删除任务 ${task.name}？`, "删除确认", {
			type: "warning",
		});
		await machineConnectionVerifyApi.deleteTask(task.id);
		await loadPageData();
		ElMessage.success("任务已删除");
	} catch (e) {
		if (e !== "cancel") {
			ElMessage.error(getApiErrorMessage(e, "任务删除失败"));
		}
	}
};

// 查看任务结果
const viewTaskResult = (task: Task) => {
	selectedTask.value = task;
	resultDialogVisible.value = true;
};

onMounted(() => {
	void loadPageData();
});

</script>

<style lang="scss" scoped>
.verify-task-manage-view {
	.task-list-card {
		margin-bottom: 20px;
	}

	.card-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
	}

	/* 任务向导样式 */
	.task-wizard-container {
		max-height: 60vh;
		overflow-y: auto;
	}

	.task-wizard-steps {
		margin: 20px 0;
	}

	.task-wizard-content {
		padding: 20px;
		background-color: var(--el-bg-color-overlay);
		border-radius: 4px;
		margin-bottom: 20px;
	}

	.step-content {
		padding: 20px;
		background-color: var(--el-bg-color);
		border-radius: 4px;
		border: 1px solid var(--el-border-color);
	}

	.step-title {
		margin-top: 0;
		margin-bottom: 20px;
		font-size: 16px;
		font-weight: 600;
		color: var(--el-text-color-primary);
	}

	.task-wizard-navigation {
		display: flex;
		justify-content: flex-end;
		gap: 10px;
		margin-top: 20px;
		padding-top: 20px;
		border-top: 1px solid var(--el-border-color);
	}

	.task-execution-info {
		margin-bottom: 20px;
	}

	.execution-actions {
		display: flex;
		gap: 10px;
	}

	.execution-pending {
		padding: 40px 0;
		display: flex;
		justify-content: center;
	}

	/* 任务列表样式 */
	.task-list-content {
		padding: 20px;
	}

	.task-filter {
		margin-bottom: 20px;
	}

	.machine-option {
		display: flex;
		align-items: center;
		justify-content: space-between;
		width: 100%;
	}

	.task-detail {
		padding: 20px;
		background-color: var(--el-bg-color-overlay);
		border-radius: 4px;
		margin-top: 10px;
	}

	.no-tasks {
		padding: 40px 0;
		display: flex;
		justify-content: center;
	}

	.task-result {
		padding: 20px;
	}

	.metric-check-list {
		display: grid;
		grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
		gap: 12px;
		margin-top: 20px;
	}
}
</style>
