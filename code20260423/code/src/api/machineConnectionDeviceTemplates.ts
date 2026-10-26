import { createMachineConnectionClient } from "./machineConnectionClient";

const baseURL =
	import.meta.env.VITE_MACHINE_CONNECTION_API ?? "/machine-connection";

export interface DeviceTemplate {
	id: string;
	name: string;
	type: string;
	brand: string;
	model: string;
	protocol: string;
	host?: string | null;
	port?: number | null;
	username?: string | null;
	password?: string | null;
	connectTimeoutMs?: number | null;
	readTimeoutMs?: number | null;
	extendedProperties?: Record<string, string> | null;
	description: string;
	createdAt: string;
}

export interface DeviceRecommendation {
	protocol: string;
	port: number;
	source: "template" | "existing-device" | "same-subnet" | "builtin" | string;
}

const client = createMachineConnectionClient({
	baseURL,
	headers: { "Content-Type": "application/json" },
});

export const machineConnectionDeviceTemplatesApi = {
	async list(keyword?: string): Promise<DeviceTemplate[]> {
		const query =
			keyword && keyword.trim()
				? `?keyword=${encodeURIComponent(keyword.trim())}`
				: "";
		const res = await client.get<DeviceTemplate[]>(
			`/api/device-templates${query}`,
		);
		return Array.isArray(res.data) ? res.data : [];
	},

	async create(input: Partial<DeviceTemplate>): Promise<DeviceTemplate> {
		const res = await client.post<DeviceTemplate>(
			"/api/device-templates",
			input,
		);
		return res.data;
	},

	async update(
		id: string,
		input: Partial<DeviceTemplate>,
	): Promise<DeviceTemplate> {
		const res = await client.put<DeviceTemplate>(
			`/api/device-templates/${encodeURIComponent(id)}`,
			input,
		);
		return res.data;
	},

	async remove(id: string): Promise<void> {
		await client.delete(`/api/device-templates/${encodeURIComponent(id)}`);
	},

	async recommend(params: {
		brand?: string;
		model?: string;
		ip?: string;
		type?: string;
	}): Promise<DeviceRecommendation> {
		const q = new URLSearchParams();
		if (params.brand) q.set("brand", params.brand);
		if (params.model) q.set("model", params.model);
		if (params.ip) q.set("ip", params.ip);
		if (params.type) q.set("type", params.type);
		const res = await client.get<DeviceRecommendation>(
			`/api/device-templates/recommend?${q.toString()}`,
		);
		return res.data;
	},
};
