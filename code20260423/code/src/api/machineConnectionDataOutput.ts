import { createMachineConnectionClient } from "./machineConnectionClient";

const baseURL =
	import.meta.env.VITE_MACHINE_CONNECTION_API ?? "/machine-connection";

export interface DataOutputTarget {
	id: string;
	name: string;
	type: string;
	host: string;
	port?: number | null;
	topic: string;
	format: string;
	url?: string | null;
	username?: string | null;
	password?: string | null;
	deviceIds: string[];
	tags: string[];
	enabled: boolean;
	description: string;
	createdAt: string;
}

export interface DataOutputTestResult {
	success: boolean;
	latencyMs: number;
	message: string;
}

const client = createMachineConnectionClient({
	baseURL,
	headers: { "Content-Type": "application/json" },
});

export const machineConnectionDataOutputApi = {
	async list(type?: string, keyword?: string): Promise<DataOutputTarget[]> {
		const q = new URLSearchParams();
		if (type) q.set("type", type);
		if (keyword) q.set("keyword", keyword);
		const suffix = q.toString() ? `?${q.toString()}` : "";
		const res = await client.get<DataOutputTarget[]>(`/api/data-output${suffix}`);
		return Array.isArray(res.data) ? res.data : [];
	},

	async create(input: Partial<DataOutputTarget>): Promise<DataOutputTarget> {
		const res = await client.post<DataOutputTarget>("/api/data-output", input);
		return res.data;
	},

	async update(
		id: string,
		input: Partial<DataOutputTarget>,
	): Promise<DataOutputTarget> {
		const res = await client.put<DataOutputTarget>(
			`/api/data-output/${encodeURIComponent(id)}`,
			input,
		);
		return res.data;
	},

	async remove(id: string): Promise<void> {
		await client.delete(`/api/data-output/${encodeURIComponent(id)}`);
	},

	async test(id: string): Promise<DataOutputTestResult> {
		const res = await client.post<DataOutputTestResult>(
			`/api/data-output/${encodeURIComponent(id)}/test`,
		);
		return res.data;
	},
};
