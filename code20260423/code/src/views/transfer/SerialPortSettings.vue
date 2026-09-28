<template>
	<el-form-item label="串口名称" required>
		<el-input v-model="settings.PortName" placeholder="采集服务所在主机的串口，如 COM3" />
	</el-form-item>
	<el-form-item label="波特率" required>
		<el-input-number :model-value="Number(settings.BaudRate ?? 9600)" :min="1" :max="4000000"
			@update:model-value="settings.BaudRate = String($event ?? 9600)" />
	</el-form-item>
	<el-form-item label="数据位">
		<el-select :model-value="settings.DataBits ?? '8'" @update:model-value="settings.DataBits = $event">
			<el-option v-for="bits in ['5', '6', '7', '8']" :key="bits" :label="bits" :value="bits" />
		</el-select>
	</el-form-item>
	<el-form-item label="校验位">
		<el-select :model-value="settings.Parity ?? 'None'" @update:model-value="settings.Parity = $event">
			<el-option v-for="parity in ['None', 'Odd', 'Even', 'Mark', 'Space']" :key="parity" :label="parity" :value="parity" />
		</el-select>
	</el-form-item>
	<el-form-item label="停止位">
		<el-select :model-value="settings.StopBits ?? 'One'" @update:model-value="settings.StopBits = $event">
			<el-option label="1" value="One" />
			<el-option label="1.5" value="OnePointFive" />
			<el-option label="2" value="Two" />
		</el-select>
	</el-form-item>
	<el-alert type="info" :closable="false" show-icon
		title="此通道使用 STX/CRC 分块协议及 XON/XOFF 流控，需连接支持该协议的对端。" />
</template>

<script setup lang="ts">
const settings = defineModel<Record<string, string | undefined>>({ required: true });
</script>
