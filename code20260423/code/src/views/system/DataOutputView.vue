<template>
	<div class="data-output-view">
		<h2 class="page-title">数据出口配置</h2>

		<el-card class="toolbar-card">
			<div class="toolbar">
				<el-select v-model="typeFilter" placeholder="类型" clearable style="width: 140px">
					<el-option
						v-for="t in TYPES"
						:key="t"
						:label="t"
						:value="t"
					/>
				</el-select>
				<el-input
					v-model="keyword"
					placeholder="按名称/地址/Topic 搜索"
					style="max-width: 260px"
					clearable
					@keyup.enter="load"
				/>
				<el-button type="primary" @click="load">查询</el-button>
				<el-button type="success" @click="openCreate">新增出口</el-button>
			</div>
		</el-card>

		<el-card>
			<el-table :data="targets" border v-loading="loading" max-height="560">
				<el-table-column prop="name" label="名称" min-width="150" />
				<el-table-column prop="type" label="类型" width="100" />
				<el-table-column prop="host" label="地址" min-width="140" />
				<el-table-column prop="port" label="端口" width="80" />
				<el-table-column prop="topic" label="Topic/路径" min-width="140" show-overflow-tooltip />
				<el-table-column prop="format" label="格式" width="80" />
				<el-table-column label="启用" width="80">
					<template #default="scope">
						<el-tag :type="scope.row.enabled ? 'success' : 'info'">
							{{ scope.row.enabled ? "启用" : "停用" }}
						</el-tag>
					</template>
				</el-table-column>
				<el-table-column label="操作" width="220" fixed="right">
					<template #default="scope">
						<el-button size="small" :loading="testingId === scope.row.id" @click="testTarget(scope.row)">
							测试
						</el-button>
						<el-button size="small" @click="openEdit(scope.row)">编辑</el-button>
						<el-button size="small" type="danger" @click="removeTarget(scope.row)">
							删除
						</el-button>
					</template>
				</el-table-column>
			</el-table>
			<el-empty v-if="!targets.length && !loading" description="暂无数据出口配置" />
		</el-card>

		<el-dialog
			v-model="dialogVisible"
			:title="editingId ? '编辑数据出口' : '新增数据出口'"
			width="600px"
		>
			<el-form :model="form" label-width="110px">
				<el-form-item label="名称" required>
					<el-input v-model="form.name" />
				</el-form-item>
				<el-form-item label="目标类型" required>
					<el-select v-model="form.type">
						<el-option v-for="t in TYPES" :key="t" :label="t" :value="t" />
					</el-select>
				</el-form-item>
				<el-form-item label="服务器地址">
					<el-input v-model="form.host" placeholder="如 127.0.0.1" />
				</el-form-item>
				<el-form-item label="端口">
					<el-input-number v-model="form.port" :min="0" :max="65535" />
				</el-form-item>
				<el-form-item label="Topic/路径">
					<el-input v-model="form.topic" />
				</el-form-item>
				<el-form-item label="数据格式">
					<el-select v-model="form.format">
						<el-option label="json" value="json" />
						<el-option label="avro" value="avro" />
						<el-option label="csv" value="csv" />
					</el-select>
				</el-form-item>
				<el-form-item label="URL(Webhook)">
					<el-input v-model="form.url" placeholder="HTTP 出口填写完整 URL" />
				</el-form-item>
				<el-form-item label="用户名">
					<el-input v-model="form.username" />
				</el-form-item>
				<el-form-item label="密码">
					<el-input v-model="form.password" type="password" show-password />
				</el-form-item>
				<el-form-item label="转发设备">
					<el-select v-model="form.deviceIds" multiple filterable style="width: 100%">
						<el-option
							v-for="d in devices"
							:key="d.id"
							:label="d.name"
							:value="d.id"
						/>
					</el-select>
				</el-form-item>
				<el-form-item label="点位(逗号)">
					<el-input v-model="tagsText" placeholder="留空表示全部点位" />
				</el-form-item>
				<el-form-item label="启用">
					<el-switch v-model="form.enabled" />
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
	machineConnectionDataOutputApi,
	type DataOutputTarget,
} from "@/api/machineConnectionDataOutput";
import { machineConnectionDevicesApi } from "@/api/machineConnectionDevices";

const TYPES = ["MQTT", "Kafka", "Database", "Webhook", "HttpApi"];

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

const targets = ref<DataOutputTarget[]>([]);
const devices = ref<{ id: string; name: string }[]>([]);
const loading = ref(false);
const typeFilter = ref("");
const keyword = ref("");

const load = async () => {
	loading.value = true;
	try {
		targets.value = await machineConnectionDataOutputApi.list(
			typeFilter.value || undefined,
			keyword.value || undefined,
		);
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载失败"));
	} finally {
		loading.value = false;
	}
};

const emptyForm = () => ({
	name: "",
	type: "MQTT",
	host: "",
	port: undefined as number | undefined,
	topic: "",
	format: "json",
	url: "",
	username: "",
	password: "",
	deviceIds: [] as string[],
	enabled: true,
	description: "",
});

const dialogVisible = ref(false);
const editingId = ref("");
const saving = ref(false);
const tagsText = ref("");
const form = reactive(emptyForm());

const openCreate = () => {
	editingId.value = "";
	Object.assign(form, emptyForm());
	tagsText.value = "";
	dialogVisible.value = true;
};

const openEdit = (row: DataOutputTarget) => {
	editingId.value = row.id;
	Object.assign(form, {
		name: row.name,
		type: row.type,
		host: row.host,
		port: row.port ?? undefined,
		topic: row.topic,
		format: row.format,
		url: row.url ?? "",
		username: row.username ?? "",
		password: row.password ?? "",
		deviceIds: [...(row.deviceIds ?? [])],
		enabled: row.enabled,
		description: row.description,
	});
	tagsText.value = (row.tags ?? []).join(",");
	dialogVisible.value = true;
};

const save = async () => {
	if (!form.name.trim()) {
		ElMessage.warning("请填写名称");
		return;
	}
	saving.value = true;
	try {
		const payload: Partial<DataOutputTarget> = {
			name: form.name.trim(),
			type: form.type,
			host: form.host.trim(),
			port: form.port ?? null,
			topic: form.topic.trim(),
			format: form.format,
			url: form.url?.trim() || null,
			username: form.username?.trim() || null,
			password: form.password || null,
			deviceIds: form.deviceIds,
			tags: tagsText.value
				.split(",")
				.map((s) => s.trim())
				.filter(Boolean),
			enabled: form.enabled,
			description: form.description,
		};
		if (editingId.value) {
			await machineConnectionDataOutputApi.update(editingId.value, payload);
		} else {
			await machineConnectionDataOutputApi.create(payload);
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

const removeTarget = async (row: DataOutputTarget) => {
	try {
		await ElMessageBox.confirm(`确定删除出口「${row.name}」？`, "删除确认", {
			type: "warning",
		});
	} catch {
		return;
	}
	try {
		await machineConnectionDataOutputApi.remove(row.id);
		ElMessage.success("已删除");
		await load();
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "删除失败"));
	}
};

const testingId = ref("");
const testTarget = async (row: DataOutputTarget) => {
	testingId.value = row.id;
	try {
		const r = await machineConnectionDataOutputApi.test(row.id);
		const msg = `${r.message}（${r.latencyMs} ms）`;
		if (r.success) ElMessage.success(msg);
		else ElMessage.error(msg);
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "测试失败"));
	} finally {
		testingId.value = "";
	}
};

onMounted(async () => {
	void load();
	try {
		const list = await machineConnectionDevicesApi.list();
		devices.value = list.map((d) => ({ id: d.id, name: d.name }));
	} catch {
		/* 设备列表可选 */
	}
});
</script>

<style lang="scss" scoped>
.data-output-view {
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
		flex-wrap: wrap;
	}
}
</style>
