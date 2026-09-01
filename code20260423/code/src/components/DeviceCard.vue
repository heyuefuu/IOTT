<template>
	<el-card
		:class="{ offline: device.status === 'offline' }"
		shadow="hover"
		:body-style="{ padding: '16px' }"
		class="device-card"
	>
		<template #header>
			<div class="card-header">
				<div class="device-name-container">
					<div class="device-name">{{ device.name }}</div>
					<div class="device-type-tag">
						<el-tag size="small" effect="plain">{{
							getDeviceTypeText(device.type)
						}}</el-tag>
					</div>
				</div>
				<div class="device-status">
					<StatusTag :status="device.status" />
				</div>
			</div>
		</template>

		<div class="card-content">
			<!-- 基本信息 - 左右布局 -->
			<div class="info-item">
				<span class="info-label">IP地址：</span>
				<span class="info-value"
					>{{ device.ip }}:{{ device.port }}</span
				>
			</div>
			<div class="info-item">
				<span class="info-label">协议：</span>
				<span class="info-value">{{ device.protocol }}</span>
			</div>

			<!-- 核心参数展示 - 和IP地址一样的布局 -->
			<div
				v-if="
					device.parameters &&
					Object.keys(device.parameters).length > 0
				"
			>
				<div
					class="info-item"
					v-for="(param, index) in Object.entries(
						device.parameters,
					).slice(0, 2)"
					:key="index"
				>
					<span class="info-label">{{ param[0] }}</span>
					<span class="param-value">{{ param[1] }}</span>
				</div>
			</div>

			<!-- Hover时显示的额外信息 -->
			<div class="hover-info">
				<el-divider content-position="left" size="small">
					<span class="divider-text">详细信息</span>
				</el-divider>
				<div class="info-item" v-if="device.collectionFrequency">
					<span class="info-label">采集频率：</span>
					<span class="info-value"
						>{{ device.collectionFrequency }}Hz</span
					>
				</div>
				<div class="info-item" v-if="device.lastCommunication">
					<span class="info-label">最后通讯：</span>
					<span class="info-value">{{
						device.lastCommunication
					}}</span>
				</div>
				<div class="info-item" v-if="device.connectionCount">
					<span class="info-label">连接次数：</span>
					<span class="info-value">{{ device.connectionCount }}</span>
				</div>
			</div>
		</div>

		<el-divider style="margin: 12px 0"></el-divider>

		<div class="card-footer">
			<el-button
				size="small"
				type="primary"
				@click="handleConfig"
				:icon="Setting"
			>
				配置
			</el-button>
			<el-button size="small" @click="handleReport" :icon="Document">
				报告
			</el-button>
		</div>
	</el-card>
</template>

<script setup lang="ts">
import {
	Setting,
	Document,
} from "@element-plus/icons-vue";
import StatusTag from "./StatusTag.vue";

interface Device {
	id: string;
	name: string;
	type: "nc" | "plc" | "robot";
	ip: string;
	port: number;
	protocol: string;
	status: "running" | "standby" | "alarm" | "offline";
	parameters: Record<string, any>;
	lastOnline?: string;
	collectionFrequency?: number;
	lastCommunication?: string;
	connectionCount?: number;
}

const props = defineProps<{
	device: Device;
}>();

const emit = defineEmits(["config", "report"]);

// 获取设备类型文本
const getDeviceTypeText = (type: string): string => {
	const typeMap: Record<string, string> = {
		nc: "数控系统",
		plc: "PLC",
		robot: "机器人",
	};
	return typeMap[type] || type;
};

// 处理配置按钮点击
const handleConfig = () => {
	emit("config", props.device);
};

// 处理报告按钮点击
const handleReport = () => {
	emit("report", props.device);
};
</script>

<style scoped lang="scss">
.device-card {
	transition: all 0.3s ease;
	border-radius: var(--el-border-radius-base);
	height: 100%;
	/* 确保所有卡片大小一致，内容完整显示 */
	min-height: 280px;
	display: flex;
	flex-direction: column;

	&:hover {
		box-shadow:
			0 10px 15px -3px rgba(0, 0, 0, 0.1),
			0 4px 6px -2px rgba(0, 0, 0, 0.05);
		transform: translateY(-2px);
	}

	&:hover .hover-info {
		opacity: 1;
		max-height: 200px;
		margin-top: 12px;
		transition: all 0.3s ease;
	}

	&.offline {
		opacity: 0.7;
		filter: grayscale(30%);
	}

	.card-header {
		display: flex;
		justify-content: space-between;
		align-items: flex-start;
		width: 100%;
	}

	.device-name-container {
		display: flex;
		flex-direction: column;
		gap: 4px;
	}

	.device-name {
		font-size: var(--el-font-size-medium);
		font-weight: 600;
		color: var(--el-text-color-primary);
		line-height: 1.2;
	}

	.device-type-tag {
		margin-top: 2px;
	}

	.card-content {
		flex: 1;
	}

	.info-item {
		display: flex;
		align-items: center;
		justify-content: space-between;
		margin-bottom: 6px;
	}

	.info-label {
		font-size: var(--el-font-size-small);
		color: var(--el-text-color-secondary);
	}

	.info-value {
		font-size: var(--el-font-size-small);
		color: var(--el-text-color-primary);
	}

	.param-label {
		font-size: var(--el-font-size-extra-small);
		color: var(--el-text-color-secondary);
		margin-bottom: 4px;
	}

	.param-value {
		font-size: var(--el-font-size-medium);
		font-weight: 600;
		color: var(--el-color-primary);
	}

	/* 带图标的状态标签 */
	.status-tag-with-icon {
		display: inline-flex;
		align-items: center;
		gap: 4px;
		vertical-align: middle;
		line-height: 1;
		padding: 4px 8px;
	}

	.status-tag-with-icon .el-icon {
		font-size: var(--el-font-size-small);
		vertical-align: middle;
		line-height: 1;
		margin-top: -1px;
	}

	.offline-info {
		margin-top: 12px;
	}

	.divider-text {
		font-size: var(--el-font-size-small);
		color: var(--el-text-color-secondary);
	}

	.hover-info {
		opacity: 0;
		max-height: 0;
		overflow: hidden;
		transition: all 0.3s ease;
	}

	.card-footer {
		display: flex;
		gap: 8px;
		justify-content: flex-end;
		margin-top: 8px;
	}
}

// 暗黑模式适配
:global(.dark) .device-card {
	--el-card-bg-color: var(--el-bg-color);
	--el-card-border-color: var(--el-border-color);
}

:global(.dark) .param-item {
	background-color: var(--el-bg-color-overlay);
}

:global(.dark) .param-value {
	color: var(--el-color-primary-light-3);
}
</style>
