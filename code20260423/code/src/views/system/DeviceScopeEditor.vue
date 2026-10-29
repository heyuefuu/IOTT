<template>
	<div>
		<el-select :model-value="mode" @update:model-value="setMode" style="width: 100%">
			<el-option v-if="allowInherit" label="继承角色数据范围" value="inherit" />
			<el-option label="仅指定设备（未选择即无权访问）" value="selected" />
			<el-option label="全部设备" value="all" />
		</el-select>
		<el-select v-if="mode === 'selected'" :model-value="modelValue?.deviceIds ?? []"
			multiple filterable @update:model-value="setDevices" placeholder="选择允许访问的设备"
			style="width: 100%; margin-top: 8px" :loading="loading">
			<el-option v-for="device in choices" :key="device.id" :value="device.id" :label="device.name" />
		</el-select>
		<el-alert v-if="error" :title="error" type="error" :closable="false" />
	</div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { businessSystemApi, type DeviceDataScope } from "@/api/businessSystem";
const props = withDefaults(defineProps<{ modelValue: DeviceDataScope | null; allowInherit?: boolean }>(), { allowInherit: true });
const emit = defineEmits<{ "update:modelValue": [DeviceDataScope | null] }>();
const devices = ref<{ id: string; name: string }[]>([]);
const loading = ref(false);
const error = ref("");
const mode = computed(() => props.modelValue?.mode ?? (props.allowInherit ? "inherit" : "selected"));
const choices = computed(() => {
	const all = [...devices.value];
	for (const id of props.modelValue?.deviceIds ?? [])
		if (!all.some(device => device.id === id)) all.push({ id, name: `未加载设备 ${id}` });
	return all;
});
function setMode(value: string) {
	if (value === "inherit") emit("update:modelValue", null);
	else emit("update:modelValue", { mode: value as DeviceDataScope["mode"], deviceIds: [...(props.modelValue?.deviceIds ?? [])] });
}
function setDevices(deviceIds: string[]) { emit("update:modelValue", { mode: "selected", deviceIds: [...deviceIds] }); }
onMounted(async () => {
	loading.value = true;
	try { devices.value = await businessSystemApi.scopeDevices(); }
	catch { error.value = "设备范围列表加载失败，当前已选设备仍保留，请关闭后重试。"; }
	finally { loading.value = false; }
});
</script>
