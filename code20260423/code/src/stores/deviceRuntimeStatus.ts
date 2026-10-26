import { reactive } from "vue";

/**
 * 会话内设备运行状态。
 *
 * 设备在后端数据库中可能残留上一次运行时的 Online/Error，但页面刚启动时
 * 并没有真正发起过连接，因此前端一律先显示“未知”。只有主动连接测试
 * （或诊断等依赖连接的行为）执行后，才把结果写入这里并驱动卡片颜色。
 */
export type DeviceRuntimeStatus = "unknown" | "online" | "error";

const states = reactive<Record<string, DeviceRuntimeStatus>>({});

export function getDeviceRuntimeStatus(id: string): DeviceRuntimeStatus {
	return states[id] ?? "unknown";
}

export function markDeviceOnline(id: string): void {
	states[id] = "online";
}

export function markDeviceError(id: string): void {
	states[id] = "error";
}

export function markDeviceUnknown(id: string): void {
	delete states[id];
}
