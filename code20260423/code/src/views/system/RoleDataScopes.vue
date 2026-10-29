<template>
	<el-collapse style="margin-bottom: 16px">
		<el-collapse-item title="角色默认数据范围" name="scope">
			<el-alert title="管理员可访问全部设备；其他角色默认无设备权限。用户可继承角色范围或单独指定范围，保存后相关会话需重新登录。"
				type="info" :closable="false" />
			<el-alert v-if="error" :title="error" type="error" :closable="false" />
			<el-form v-loading="loading" label-width="100px" style="margin-top: 12px">
				<el-form-item v-for="role in roles" :key="role.id" :label="role.name">
					<DeviceScopeEditor :model-value="scopes[role.id] ?? null" @update:model-value="scopes[role.id] = $event"
						:allow-inherit="false" style="width: 450px" />
					<el-button :disabled="loading || saving || !!error" :loading="saving" @click="save(role.id)" style="margin-left: 12px">保存</el-button>
				</el-form-item>
			</el-form>
		</el-collapse-item>
	</el-collapse>
</template>
<script setup lang="ts">
import { onMounted, reactive, ref } from "vue";
import { ElMessage } from "element-plus";
import { businessSystemApi, type DeviceDataScope } from "@/api/businessSystem";
import DeviceScopeEditor from "./DeviceScopeEditor.vue";
const roles = [{ id: "operator", name: "操作员" }, { id: "viewer", name: "查看者" }];
const scopes = reactive<Record<string, DeviceDataScope | null>>({ operator: { mode: "selected", deviceIds: [] }, viewer: { mode: "selected", deviceIds: [] } });
const loading = ref(true);
const saving = ref(false);
const error = ref("");
onMounted(async () => {
	try { for (const row of await businessSystemApi.roleScopes()) if (row.role in scopes) scopes[row.role] = row.scope; }
	catch { error.value = "角色范围加载失败，请刷新后重试。"; }
	finally { loading.value = false; }
});
async function save(role: string) {
	if (saving.value || error.value) return;
	saving.value = true;
	try { await businessSystemApi.saveRoleScope(role, scopes[role] ?? { mode: "selected", deviceIds: [] }); ElMessage.success("角色数据范围已保存"); }
	catch { ElMessage.error("角色数据范围保存失败，请重试"); }
	finally { saving.value = false; }
}
</script>
