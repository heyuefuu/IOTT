<template>
	<div class="device-template-view">
		<h2 class="page-title">设备配置模板</h2>

		<el-card class="toolbar-card">
			<div class="toolbar">
				<el-input
					v-model="keyword"
					placeholder="按名称/品牌/型号/协议搜索"
					style="max-width: 280px"
					clearable
					@keyup.enter="load"
				/>
				<el-button type="primary" @click="load">查询</el-button>
				<el-button type="success" @click="openCreate">新增模板</el-button>
			</div>
		</el-card>

		<el-card class="recommend-card">
			<template #header>
				<span>智能推荐（协议 / 端口）</span>
			</template>
			<div class="recommend-row">
				<el-input v-model="recForm.brand" placeholder="品牌，如 西门子" style="width: 160px" />
				<el-input v-model="recForm.model" placeholder="型号，如 S7-1200" style="width: 160px" />
				<el-input v-model="recForm.ip" placeholder="设备 IP（可选）" style="width: 180px" />
				<el-button type="primary" :loading="recommending" @click="doRecommend">推荐</el-button>
				<el-tag v-if="recommendation" type="success">
					推荐：{{ recommendation.protocol }} / {{ recommendation.port }}
					（来源：{{ sourceText(recommendation.source) }}）
				</el-tag>
				<el-button
					v-if="recommendation"
					size="small"
					@click="applyRecommendation"
				>
					填入表单
				</el-button>
			</div>
		</el-card>

		<el-card>
			<el-table :data="templates" border v-loading="loading" max-height="560">
				<el-table-column prop="name" label="模板名称" min-width="160" />
				<el-table-column prop="type" label="类型" width="90" />
				<el-table-column prop="brand" label="品牌" width="120" />
				<el-table-column prop="model" label="型号" width="120" />
				<el-table-column prop="protocol" label="协议" width="110" />
				<el-table-column prop="port" label="端口" width="90" />
				<el-table-column prop="description" label="说明" min-width="160" show-overflow-tooltip />
				<el-table-column label="操作" width="260" fixed="right">
					<template #default="scope">
						<el-button
							size="small"
							type="primary"
							@click="createDevice(scope.row)"
						>
							创建设备
						</el-button>
						<el-button size="small" @click="openEdit(scope.row)">编辑</el-button>
						<el-button
							size="small"
							type="danger"
							@click="removeTemplate(scope.row)"
						>
							删除
						</el-button>
					</template>
				</el-table-column>
			</el-table>
			<el-empty v-if="!templates.length && !loading" description="暂无模板" />
		</el-card>

		<el-dialog
			v-model="dialogVisible"
			:title="editingId ? '编辑模板' : '新增模板'"
			width="560px"
		>
			<el-form :model="form" label-width="100px">
				<el-form-item label="模板名称" required>
					<el-input v-model="form.name" placeholder="如 西门子S7-1200标准模板" />
				</el-form-item>
				<el-form-item label="类型">
					<el-select v-model="form.type">
						<el-option label="CNC" value="CNC" />
						<el-option label="PLC" value="PLC" />
						<el-option label="Robot" value="Robot" />
					</el-select>
				</el-form-item>
				<el-form-item label="品牌">
					<el-input v-model="form.brand" />
				</el-form-item>
				<el-form-item label="型号">
					<el-input v-model="form.model" />
				</el-form-item>
				<el-form-item label="协议">
					<el-input v-model="form.protocol" placeholder="如 S7 / Modbus / FINS" />
				</el-form-item>
				<el-form-item label="默认IP">
					<el-input v-model="form.host" placeholder="可留空，应用模板时再填" />
				</el-form-item>
				<el-form-item label="端口">
					<el-input-number v-model="form.port" :min="0" :max="65535" />
				</el-form-item>
				<el-form-item label="用户名">
					<el-input v-model="form.username" />
				</el-form-item>
				<el-form-item label="说明">
					<el-input v-model="form.description" type="textarea" :rows="2" />
				</el-form-item>
			</el-form>
			<template #footer>
				<el-button @click="dialogVisible = false">取消</el-button>
				<el-button type="primary" :loading="saving" @click="save">保存</el-button>
			</template>
		</el-dialog>
	</div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from "vue";
import { ElMessage, ElMessageBox } from "element-plus";
import {
	machineConnectionDeviceTemplatesApi,
	type DeviceTemplate,
	type DeviceRecommendation,
} from "@/api/machineConnectionDeviceTemplates";
import { machineConnectionDevicesApi } from "@/api/machineConnectionDevices";

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

const templates = ref<DeviceTemplate[]>([]);
const loading = ref(false);
const keyword = ref("");

const load = async () => {
	loading.value = true;
	try {
		templates.value = await machineConnectionDeviceTemplatesApi.list(
			keyword.value,
		);
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载模板失败"));
	} finally {
		loading.value = false;
	}
};

const emptyForm = () => ({
	name: "",
	type: "CNC",
	brand: "",
	model: "",
	protocol: "",
	host: "",
	port: undefined as number | undefined,
	username: "",
	description: "",
});

const dialogVisible = ref(false);
const editingId = ref("");
const saving = ref(false);
const form = reactive(emptyForm());

const openCreate = () => {
	editingId.value = "";
	Object.assign(form, emptyForm());
	dialogVisible.value = true;
};

const openEdit = (row: DeviceTemplate) => {
	editingId.value = row.id;
	Object.assign(form, {
		name: row.name,
		type: row.type,
		brand: row.brand,
		model: row.model,
		protocol: row.protocol,
		host: row.host ?? "",
		port: row.port ?? undefined,
		username: row.username ?? "",
		description: row.description,
	});
	dialogVisible.value = true;
};

const save = async () => {
	if (!form.name.trim()) {
		ElMessage.warning("请填写模板名称");
		return;
	}
	saving.value = true;
	try {
		const payload: Partial<DeviceTemplate> = {
			name: form.name.trim(),
			type: form.type,
			brand: form.brand.trim(),
			model: form.model.trim(),
			protocol: form.protocol.trim(),
			host: form.host?.trim() || null,
			port: form.port ?? null,
			username: form.username?.trim() || null,
			description: form.description,
		};
		if (editingId.value) {
			await machineConnectionDeviceTemplatesApi.update(
				editingId.value,
				payload,
			);
		} else {
			await machineConnectionDeviceTemplatesApi.create(payload);
		}
		ElMessage.success("已保存");
		dialogVisible.value = false;
		await load();
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "保存失败"));
	} finally {
		saving.value = false;
	}
};

const createDevice = async (row: DeviceTemplate) => {
	try {
		const result = (await ElMessageBox.prompt(
			"请输入设备 IP（Host）",
			`新建：${row.name}`,
			{
				inputPlaceholder: row.host || "如 192.168.1.10",
				inputValue: row.host ?? "",
				confirmButtonText: "创建",
				cancelButtonText: "取消",
			},
		)) as { value: string };
		const value = result.value;
		if (!value?.trim()) {
			ElMessage.warning("Host 不能为空");
			return;
		}
		const device = await machineConnectionDevicesApi.createFromTemplate(
			row.id,
			"",
			value.trim(),
		);
		ElMessage.success(`已创建设备：${device.name ?? row.name}`);
	} catch (e: unknown) {
		if (e === "cancel" || e === "close") return;
		ElMessage.error(getErr(e, "创建设备失败"));
	}
};

const removeTemplate = async (row: DeviceTemplate) => {
	try {
		await ElMessageBox.confirm(`确定删除模板「${row.name}」？`, "删除确认", {
			type: "warning",
		});
	} catch {
		return;
	}
	try {
		await machineConnectionDeviceTemplatesApi.remove(row.id);
		ElMessage.success("已删除");
		await load();
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "删除失败"));
	}
};

const recForm = reactive({ brand: "", model: "", ip: "" });
const recommending = ref(false);
const recommendation = ref<DeviceRecommendation | null>(null);

const sourceText = (source: string): string =>
	({
		template: "设备模板",
		"existing-device": "现网同型号设备",
		"same-subnet": "同网段设备",
		builtin: "内置品牌映射",
	}[source] ?? source);

const doRecommend = async () => {
	if (!recForm.brand && !recForm.model && !recForm.ip) {
		ElMessage.warning("请至少填写品牌、型号或 IP");
		return;
	}
	recommending.value = true;
	try {
		recommendation.value = await machineConnectionDeviceTemplatesApi.recommend(
			{
				brand: recForm.brand,
				model: recForm.model,
				ip: recForm.ip,
			},
		);
	} catch (e: unknown) {
		recommendation.value = null;
		ElMessage.warning(getErr(e, "无法推荐"));
	} finally {
		recommending.value = false;
	}
};

const applyRecommendation = () => {
	if (!recommendation.value) return;
	if (!dialogVisible.value) openCreate();
	form.protocol = recommendation.value.protocol;
	form.port = recommendation.value.port;
	form.brand = form.brand || recForm.brand;
	form.model = form.model || recForm.model;
	form.host = form.host || recForm.ip;
};

onMounted(() => {
	void load();
});
</script>

<style lang="scss" scoped>
.device-template-view {
	.page-title {
		margin-bottom: 16px;
	}
	.toolbar-card {
		margin-bottom: 16px;
	}
	.toolbar {
		display: flex;
		gap: 10px;
		align-items: center;
	}
	.recommend-card {
		margin-bottom: 16px;
	}
	.recommend-row {
		display: flex;
		gap: 10px;
		align-items: center;
		flex-wrap: wrap;
	}
}
</style>
