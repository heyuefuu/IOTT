import axios from 'axios'

/** 与 MachineConnectionApi / Industrial IoT 文档中设备 DTO 一致（枚举为 JSON 字符串） */
export type DeviceTypeApi = 'CNC' | 'PLC' | 'Robot'

export interface DeviceDto {
	id: string
	name: string
	type: DeviceTypeApi
	brand: string
	model: string
	protocol: string
	status: string
	host: string
	port: number
	username?: string | null
	connectTimeoutMs: number
	readTimeoutMs: number
	extendedProperties: Record<string, string>
	transfer?: TransferDeviceRequest | null
	createdAt: string
	lastSeenAt?: string | null
	/** 最近一次向上游 Industrial IoT 注册表同步是否成功（null/undefined = 尚未同步过） */
	upstreamSynced?: boolean | null
	upstreamError?: string | null
}

export interface TransferDeviceRequest {
	protocol: string
	host: string
	port: number
	username?: string | null
	password?: string | null
	connectTimeoutMs?: number | null
	readTimeoutMs?: number | null
	extendedProperties?: Record<string, string> | null
}

export interface CreateDeviceRequest {
	name: string
	type: DeviceTypeApi
	brand: string
	model: string
	protocol: string
	host: string
	port: number
	username?: string | null
	password?: string | null
	connectTimeoutMs?: number | null
	readTimeoutMs?: number | null
	extendedProperties?: Record<string, string> | null
	transfer?: TransferDeviceRequest | null
}

export interface UpdateDeviceRequest {
	name?: string | null
	type?: DeviceTypeApi | null
	brand?: string | null
	model?: string | null
	protocol?: string | null
	host?: string | null
	port?: number | null
	username?: string | null
	password?: string | null
	connectTimeoutMs?: number | null
	readTimeoutMs?: number | null
	extendedProperties?: Record<string, string> | null
	transfer?: TransferDeviceRequest | null
}

export interface ConnectionTestResult {
	success: boolean
	errorMessage?: string | null
	latency?: string | null
	/** driver = 上游协议驱动握手；tcp = 网关本地端口探测（上游不可用时的回退） */
	mode?: 'driver' | 'tcp'
}

export interface UpstreamSyncError {
	deviceId: string
	name: string
	error: string
}

export interface UpstreamSyncReport {
	total: number
	created: number
	updated: number
	failed: number
	errors: UpstreamSyncError[]
}

export interface DeviceImportResult {
	total: number
	success: number
	failed: number
	errors: string[]
}

const baseURL =
	import.meta.env.VITE_MACHINE_CONNECTION_API ?? '/machine-connection'

const client = axios.create({
	baseURL,
	timeout: 120_000,
	headers: { 'Content-Type': 'application/json' },
})

export const machineConnectionDevicesApi = {
	async list(type?: string): Promise<DeviceDto[]> {
		const res = await client.get<DeviceDto[]>('/api/devices', {
			params: type ? { type } : undefined,
		})
		return res.data
	},

	async getById(id: string): Promise<DeviceDto> {
		const res = await client.get<DeviceDto>(
			`/api/devices/${encodeURIComponent(id)}`,
		)
		return res.data
	},

	async create(body: CreateDeviceRequest): Promise<DeviceDto> {
		const res = await client.post<DeviceDto>('/api/devices', body)
		return res.data
	},

	async update(id: string, body: UpdateDeviceRequest): Promise<DeviceDto> {
		const res = await client.put<DeviceDto>(
			`/api/devices/${encodeURIComponent(id)}`,
			body,
		)
		return res.data
	},

	async remove(id: string): Promise<void> {
		await client.delete(`/api/devices/${encodeURIComponent(id)}`)
	},

	/** 将网关本地设备注册表全量对账同步到上游 Industrial IoT（新建缺失、更新已有） */
	async syncUpstream(): Promise<UpstreamSyncReport> {
		const res = await client.post<UpstreamSyncReport>('/api/devices/sync-upstream')
		return res.data
	},

	async testConnection(id: string): Promise<ConnectionTestResult> {
		const res = await client.post<ConnectionTestResult>(
			`/api/devices/${encodeURIComponent(id)}/test-connection`,
		)
		return res.data
	},

	async downloadTemplate(): Promise<Blob> {
		const res = await client.get('/api/devices/template', {
			responseType: 'blob',
		})
		return res.data
	},

	async importDevices(file: File): Promise<DeviceImportResult> {
		const form = new FormData()
		form.append('file', file)
		const res = await client.post<DeviceImportResult>('/api/devices/import', form, {
			headers: { 'Content-Type': 'multipart/form-data' },
		})
		return res.data
	},
}
