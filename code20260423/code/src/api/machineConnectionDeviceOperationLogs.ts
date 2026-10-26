import { createMachineConnectionClient } from "./machineConnectionClient";

const baseURL =
	import.meta.env.VITE_MACHINE_CONNECTION_API ?? "/machine-connection";

export interface DeviceOperationLog {
	id: string;
	deviceId: string;
	deviceName: string;
	deviceType: string;
	address: string;
	dataType: string;
	oldValue?: string | null;
	newValue?: string | null;
	success: boolean;
	error?: string | null;
	user: string;
	timestamp: string;
}

const client = createMachineConnectionClient({
	baseURL,
	headers: { "Content-Type": "application/json" },
});

export const machineConnectionDeviceOperationLogsApi = {
	async list(params: {
		deviceId?: string;
		type?: string;
		keyword?: string;
		take?: number;
	}): Promise<DeviceOperationLog[]> {
		const q = new URLSearchParams();
		if (params.deviceId) q.set("deviceId", params.deviceId);
		if (params.type) q.set("type", params.type);
		if (params.keyword) q.set("keyword", params.keyword);
		q.set("take", String(params.take ?? 200));
		const res = await client.get<DeviceOperationLog[]>(
			`/api/device-operation-logs?${q.toString()}`,
		);
		return Array.isArray(res.data) ? res.data : [];
	},
};
