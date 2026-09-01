import axios from "axios";
import type { DeviceTypeApi } from "./machineConnectionDevices";

/** 与 IndustrialIoT.Host ConnectionVerificationController 的 DTO 一致（经网关代理） */
export interface BatchTestRequest {
	deviceIds?: string[];
	deviceType?: DeviceTypeApi;
	/** 并发数 1–500，后端默认 50 */
	concurrency?: number;
}

export interface DeviceTestResult {
	deviceId: string;
	success: boolean;
	latencyMs: number;
	errorMessage?: string | null;
}

export interface BatchTestResult {
	testId: string;
	totalDevices: number;
	successCount: number;
	failureCount: number;
	/** 0–1，后端保留 4 位小数 */
	successRate: number;
	results: DeviceTestResult[];
	durationMs: number;
}

const baseURL =
	import.meta.env.VITE_MACHINE_CONNECTION_API ?? "/machine-connection";

const client = axios.create({
	baseURL,
	timeout: 300_000,
	headers: { "Content-Type": "application/json" },
});

export const machineConnectionVerificationApi = {
	/** 真机批量并行连接验证（按设备 ID 列表或设备类型） */
	async start(body: BatchTestRequest): Promise<BatchTestResult> {
		const res = await client.post<BatchTestResult>(
			"/api/connection-verification/start",
			body,
		);
		return res.data;
	},

	/** 查询某次批量验证结果 */
	async getResult(testId: string): Promise<BatchTestResult> {
		const res = await client.get<BatchTestResult>(
			`/api/connection-verification/${encodeURIComponent(testId)}`,
		);
		return res.data;
	},
};
