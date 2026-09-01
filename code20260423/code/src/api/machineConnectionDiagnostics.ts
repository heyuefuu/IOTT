import axios from "axios";

/** PLC 品牌/协议能力矩阵（来自 Industrial IoT /api/plc/capabilities 静态清单） */
export interface PlcProtocolCapability {
	brand: string;
	models: string[];
	protocols: string[];
	/** 协议 → 必填扩展属性说明 */
	requiredProperties: Record<string, string[]>;
	addressExamples: string[];
}

/** NC-Link Probe 自报数据项（选点排查用） */
export interface NCLinkDataItem {
	id: string;
	name: string;
	type: string;
	settable: boolean;
	unit?: string | null;
	componentPath?: string | null;
}

export interface NCLinkProbeModel {
	id: string;
	guid?: string | null;
	version?: string | null;
	dataItemCount: number;
	sampleChannelCount: number;
	dataItems: NCLinkDataItem[];
	sampleChannels: unknown[];
}

const baseURL =
	import.meta.env.VITE_MACHINE_CONNECTION_API ?? "/machine-connection";

const client = axios.create({
	baseURL,
	timeout: 60_000,
	headers: { "Content-Type": "application/json" },
});

const enc = encodeURIComponent;

export const machineConnectionDiagnosticsApi = {
	async plcCapabilities(): Promise<PlcProtocolCapability[]> {
		const res = await client.get<PlcProtocolCapability[]>("/api/plc/capabilities");
		return res.data ?? [];
	},

	async nclinkProbe(deviceId: string): Promise<NCLinkProbeModel> {
		const res = await client.get<NCLinkProbeModel>(`/api/nclink/${enc(deviceId)}/probe`);
		return res.data;
	},

	async nclinkDataItems(deviceId: string): Promise<NCLinkDataItem[]> {
		const res = await client.get<NCLinkDataItem[]>(`/api/nclink/${enc(deviceId)}/dataitems`);
		return res.data ?? [];
	},

	async nclinkSampleChannels(deviceId: string): Promise<unknown[]> {
		const res = await client.get<unknown[]>(`/api/nclink/${enc(deviceId)}/sample-channels`);
		return res.data ?? [];
	},
};
