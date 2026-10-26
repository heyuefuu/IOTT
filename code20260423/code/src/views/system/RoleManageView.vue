<template>
	<div class="role-manage-view">
		<h2 class="page-title">角色管理</h2>

		<el-card class="toolbar-card">
			<div class="toolbar">
				<el-button type="success" @click="openCreate">新增角色</el-button>
				<span class="hint">角色用于给用户赋予一组权限；用户可被赋予多个角色（生效权限为并集）。</span>
			</div>
		</el-card>

		<el-card>
			<el-table :data="roles" border v-loading="loading">
				<el-table-column prop="name" label="角色名称" width="160" />
				<el-table-column prop="key" label="标识" width="140" />
				<el-table-column prop="description" label="说明" min-width="160" />
				<el-table-column label="权限" min-width="260">
					<template #default="scope">
						<el-tag
							v-for="p in scope.row.permissions"
							:key="p"
							size="small"
							class="perm-tag"
						>
							{{ permissionName(p) }}
						</el-tag>
					</template>
				</el-table-column>
				<el-table-column label="内置" width="80">
					<template #default="scope">
						<el-tag :type="scope.row.builtIn ? 'info' : 'success'">
							{{ scope.row.builtIn ? "内置" : "自定义" }}
						</el-tag>
					</template>
				</el-table-column>
				<el-table-column label="操作" width="160" fixed="right">
					<template #default="scope">
						<el-button size="small" @click="openEdit(scope.row)">编辑</el-button>
						<el-button
							size="small"
							type="danger"
							:disabled="scope.row.builtIn"
							@click="removeRole(scope.row)"
						>
							删除
						</el-button>
					</template>
				</el-table-column>
			</el-table>
		</el-card>

		<el-dialog
			v-model="dialogVisible"
			:title="editingId ? '编辑角色' : '新增角色'"
			width="560px"
		>
			<el-form :model="form" label-width="90px">
				<el-form-item label="角色标识" required>
					<el-input v-model="form.key" :disabled="editingBuiltIn" placeholder="如 数据录入员" />
				</el-form-item>
				<el-form-item label="角色名称" required>
					<el-input v-model="form.name" />
				</el-form-item>
				<el-form-item label="说明">
					<el-input v-model="form.description" type="textarea" :rows="2" />
				</el-form-item>
				<el-form-item label="权限">
					<el-checkbox-group v-model="form.permissions">
						<el-checkbox
							v-for="p in permissions"
							:key="p.key"
							:label="p.key"
						>
							{{ p.name }}
						</el-checkbox>
					</el-checkbox-group>
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
	businessSystemApi,
	type RoleDto,
	type PermissionDto,
} from "@/api/businessSystem";

function getErr(e: unknown, fallback: string): string {
	const ax = e as {
		response?: { data?: { error?: string; detail?: string } };
		message?: string;
	};
	return ax.response?.data?.error ?? ax.response?.data?.detail ?? ax.message ?? fallback;
}

const roles = ref<RoleDto[]>([]);
const permissions = ref<PermissionDto[]>([]);
const loading = ref(false);

const permissionName = (key: string) =>
	permissions.value.find((p) => p.key === key)?.name ?? key;

const load = async () => {
	loading.value = true;
	try {
		roles.value = await businessSystemApi.listRoles();
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "加载角色失败"));
	} finally {
		loading.value = false;
	}
};

const emptyForm = () => ({
	key: "",
	name: "",
	description: "",
	permissions: [] as string[],
});

const dialogVisible = ref(false);
const editingId = ref("");
const editingBuiltIn = ref(false);
const saving = ref(false);
const form = reactive(emptyForm());

const openCreate = () => {
	editingId.value = "";
	editingBuiltIn.value = false;
	Object.assign(form, emptyForm());
	dialogVisible.value = true;
};

const openEdit = (row: RoleDto) => {
	editingId.value = row.id;
	editingBuiltIn.value = row.builtIn;
	Object.assign(form, {
		key: row.key,
		name: row.name,
		description: row.description,
		permissions: [...row.permissions],
	});
	dialogVisible.value = true;
};

const save = async () => {
	if (!form.key.trim() || !form.name.trim()) {
		ElMessage.warning("请填写角色标识与名称");
		return;
	}
	saving.value = true;
	try {
		const payload = {
			key: form.key.trim(),
			name: form.name.trim(),
			description: form.description,
			permissions: form.permissions,
		};
		if (editingId.value) {
			await businessSystemApi.updateRole(editingId.value, payload);
		} else {
			await businessSystemApi.createRole(payload);
		}
		ElMessage.success("已保存");
		dialogVisible.value = false;
		await load();
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "保存角色失败"));
	} finally {
		saving.value = false;
	}
};

const removeRole = async (row: RoleDto) => {
	try {
		await ElMessageBox.confirm(`确定删除角色「${row.name}」？`, "删除确认", {
			type: "warning",
		});
	} catch {
		return;
	}
	try {
		await businessSystemApi.deleteRole(row.id);
		ElMessage.success("已删除");
		await load();
	} catch (e: unknown) {
		ElMessage.error(getErr(e, "删除角色失败"));
	}
};

onMounted(async () => {
	try {
		permissions.value = await businessSystemApi.listPermissions();
	} catch {
		/* 权限清单可选 */
	}
	void load();
});
</script>

<style lang="scss" scoped>
.role-manage-view {
	.page-title {
		margin-bottom: 16px;
	}
	.toolbar-card {
		margin-bottom: 16px;
	}
	.toolbar {
		display: flex;
		gap: 12px;
		align-items: center;
		flex-wrap: wrap;
	}
	.hint {
		color: #909399;
		font-size: 13px;
	}
	.perm-tag {
		margin: 2px 4px 2px 0;
	}
}
</style>
