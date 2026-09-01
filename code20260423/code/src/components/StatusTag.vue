<template>
	<el-button
		:type="statusType"
		size="small"
		:icon="statusIcon"
		class="status-button"
	>
		{{ text || statusText }}
	</el-button>
</template>

<script setup lang="ts">
import { computed } from "vue";
import {
	Check,
	CircleCheck,
	Warning,
	Close,
	InfoFilled,
} from "@element-plus/icons-vue";

const props = defineProps<{
	status?: string;
	text?: string;
}>();

// 状态文本映射
const statusText = computed(() => {
	const statusMap: Record<string, string> = {
		// 英文状态码映射为中文
		running: "运行中",
		standby: "待机",
		alarm: "报警中",
		offline: "离线",
		success: "成功",
		warning: "警告",
		danger: "危险",
		info: "信息",
		// 中文状态文本保持不变
		"成功": "成功",
		"失败": "失败",
		"测试中": "测试中",
		"已连接": "已连接",
		"未连接": "未连接",
		"连接中": "连接中",
	};
	return statusMap[props.status || ""] || props.status || "";
});

// 状态类型映射（对应Element Plus的button类型）
const statusType = computed(() => {
	const typeMap: Record<string, string> = {
		// 英文状态码
		running: "success",
		standby: "primary",
		alarm: "warning",
		offline: "danger",
		success: "success",
		warning: "warning",
		danger: "danger",
		info: "info",
		// 中文状态文本
		"成功": "success",
		"失败": "danger",
		"测试中": "warning",
		"已连接": "success",
		"未连接": "danger",
		"连接中": "warning",
	};
	return typeMap[props.status || ""] || "info";
});

// 状态图标映射
const statusIcon = computed(() => {
	const iconMap: Record<string, any> = {
		// 英文状态码
		running: Check,
		standby: CircleCheck,
		alarm: Warning,
		offline: Close,
		success: Check,
		warning: Warning,
		danger: Close,
		info: InfoFilled,
		// 中文状态文本
		"成功": Check,
		"失败": Close,
		"测试中": Warning,
		"已连接": Check,
		"未连接": Close,
		"连接中": Warning,
	};
	return iconMap[props.status || ""] || InfoFilled;
});
</script>

<style scoped lang="scss">
.status-button {
	gap: 4px;
}
</style>
