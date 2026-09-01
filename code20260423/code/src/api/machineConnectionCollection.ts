import axios from "axios";

export type CollectionDataType =
	| "Bool"
	| "Int16"
	| "Int32"
	| "Int64"
	| "UInt16"
	| "UInt32"
	| "Float"
	| "Double"
	| "String"
	| "ByteArray";

export interface CollectionTagConfig {
	address: string;
	dataType: CollectionDataType;
	displayName?: string | null;
	unit?: string | null;
}

export interface CollectionGroupConfig {
	groupName: string;
	intervalMs: number;
	tags: CollectionTagConfig[];
}

export interface CollectionProfileDto {
	id: string;
	deviceId: string;
	name: string;
	isEnabled: boolean;
	groups: CollectionGroupConfig[];
}

export interface CollectionTaskStatus {
	taskId: string;
	deviceId: string;
	isRunning: boolean;
	lastCollectedAt?: string | null;
	totalCollections: number;
	totalErrors: number;
}

export interface BatchImportResult {
	totalRows: number;
	successCount: number;
	errorCount: number;
	errors: string[];
}

const baseURL =
	import.meta.env.VITE_MACHINE_CONNECTION_API ?? "/machine-connection";

const client = axios.create({
	baseURL,
	timeout: 120_000,
	headers: { "Content-Type": "application/json" },
});

export const machineConnectionCollectionApi = {
	async listProfiles(deviceId: string): Promise<CollectionProfileDto[]> {
		const res = await client.get<CollectionProfileDto[]>(
			`/api/collection-config/${encodeURIComponent(deviceId)}`,
		);
		return res.data;
	},

	async createProfile(
		deviceId: string,
		body: { name: string; groups: CollectionGroupConfig[] },
	): Promise<CollectionProfileDto> {
		const res = await client.post<CollectionProfileDto>(
			`/api/collection-config/${encodeURIComponent(deviceId)}`,
			body,
		);
		return res.data;
	},

	async updateProfile(
		profileId: string,
		body: { name?: string; isEnabled?: boolean; groups?: CollectionGroupConfig[] },
	): Promise<CollectionProfileDto> {
		const res = await client.put<CollectionProfileDto>(
			`/api/collection-config/profile/${encodeURIComponent(profileId)}`,
			body,
		);
		return res.data;
	},

	async deleteProfile(profileId: string): Promise<void> {
		await client.delete(
			`/api/collection-config/profile/${encodeURIComponent(profileId)}`,
		);
	},

	async startCollection(deviceId: string, groups: CollectionGroupConfig[]) {
		const res = await client.post<{ taskId: string }>("/api/collection/start", {
			deviceId,
			groups,
		});
		return res.data;
	},

	async stopCollection(taskId: string): Promise<void> {
		await client.post(`/api/collection/stop/${encodeURIComponent(taskId)}`);
	},

	async status(): Promise<Record<string, CollectionTaskStatus>> {
		const res =
			await client.get<Record<string, CollectionTaskStatus>>(
				"/api/collection/status",
			);
		return res.data;
	},

	async importTags(deviceId: string, file: File): Promise<BatchImportResult> {
		const form = new FormData();
		form.append("file", file);
		const res = await client.post<BatchImportResult>(
			`/api/batch-import/tags/${encodeURIComponent(deviceId)}`,
			form,
			{ headers: { "Content-Type": "multipart/form-data" } },
		);
		return res.data;
	},
};
