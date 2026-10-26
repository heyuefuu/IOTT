<template>
	<el-dialog
		:model-value="modelValue"
		:title="`采集入库：${deviceName || deviceId}`"
		width="920px"
		:close-on-click-modal="false"
		@update:model-value="emit('update:modelValue', $event)"
	>
		<div class="collection-summary">
			<el-tag :type="session?.running ? 'success' : 'info'">
				{{ session?.running ? "采集中" : "未采集" }}
			</el-tag>
			<span v-if="session?.profileNames.length">配置：{{ session.profileNames.join(" / ") }}</span>
			<span v-if="session?.groups.length">
				周期：{{ intervalText }}
			</span>
			<span v-if="session?.lastCollectedAt">最近采集：{{ session.lastCollectedAt }}</span>
		</div>

		<el-alert
			v-if="session?.error"
			:title="session.error"
			type="error"
			:closable="false"
			show-icon
			style="margin-bottom: 12px"
		/>
		<el-alert
			v-if="session?.storageError"
			:title="session.storageError"
			type="warning"
			:closable="false"
			show-icon
			style="margin-bottom: 12px"
		/>
		<el-alert
			v-else-if="session?.lastBatch"
			:title="storageText"
			:type="session.lastBatch.storageEnabled ? 'success' : 'warning'"
			:closable="false"
			show-icon
			style="margin-bottom: 12px"
		/>

		<el-table :data="session?.rows ?? []" row-key="path" border max-height="440" empty-text="暂无点位或尚未开始采集">
			<el-table-column prop="name" label="点位" min-width="150" show-overflow-tooltip />
			<el-table-column prop="address" label="地址" min-width="130" show-overflow-tooltip />
			<el-table-column prop="dataType" label="类型" width="95" />
			<el-table-column prop="value" label="当前值" min-width="120" show-overflow-tooltip />
			<el-table-column prop="quality" label="质量" width="90" />
			<el-table-column prop="time" label="采集时间" width="170" />
			<el-table-column label="状态" width="90">
				<template #default="scope">
					<el-tag :type="scope.row.status === '成功' ? 'success' : scope.row.status === '失败' ? 'danger' : 'info'" size="small">
						{{ scope.row.status }}
					</el-tag>
				</template>
			</el-table-column>
		</el-table>

		<template #footer>
			<div class="collection-footer">
				<span class="footer-hint">关闭窗口不会停止采集；停止采集请点“停止采集”。</span>
				<el-button
					type="success"
					:loading="session?.starting"
					:disabled="session?.running"
					@click="emit('start', deviceId)"
				>
					开始采集
				</el-button>
				<el-button type="danger" :disabled="!session?.running" @click="emit('stop', deviceId)">
					停止采集
				</el-button>
				<el-button @click="emit('update:modelValue', false)">关闭</el-button>
			</div>
		</template>
	</el-dialog>
</template>

<script setup lang="ts">
import { computed } from "vue";
import type { PointCollectionSession } from "@/composables/usePointCollection";

const props = defineProps<{
	modelValue: boolean;
	deviceId: string;
	deviceName: string;
	session: PointCollectionSession | null;
}>();

const emit = defineEmits<{
	"update:modelValue": [value: boolean];
	start: [deviceId: string];
	stop: [deviceId: string];
}>();

const intervalText = computed(() => {
	const intervals = [...new Set((props.session?.groups ?? []).map((group) => group.intervalMs))];
	return intervals.length ? intervals.map((value) => `${value}ms`).join(" / ") : "-";
});

const storageText = computed(() => {
	const batch = props.session?.lastBatch;
	if (!batch) return "";
	if (!batch.storageEnabled) return "Influx 存储未启用";
	return `本批写入 ${batch.written} 条；MQTT ${batch.mqttQueued ? "已入队" : "未入队"}`;
});
</script>

<style scoped>
.collection-summary {
	display: flex;
	flex-wrap: wrap;
	align-items: center;
	gap: 12px;
	margin-bottom: 12px;
	color: var(--el-text-color-secondary);
	font-size: 13px;
}

.collection-footer {
	display: flex;
	align-items: center;
	justify-content: flex-end;
	gap: 10px;
}

.footer-hint {
	margin-right: auto;
	color: var(--el-text-color-secondary);
	font-size: 12px;
}
</style>
