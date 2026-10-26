import { createMachineConnectionClient } from "./machineConnectionClient";
export { AUTH_TOKEN_KEY } from "./machineConnectionClient";

const baseURL =
	import.meta.env.VITE_MACHINE_CONNECTION_API ?? "/machine-connection";

const client = createMachineConnectionClient({
	baseURL,
	timeout: 120_000,
	headers: { "Content-Type": "application/json" },
});

export interface SystemLogDto {
	id: string;
	type: string;
	user: string;
	action: string;
	ip: string;
	timestamp: string;
	detail: string;
	device?: string;
	result?: string;
}

export interface SystemLogQuery {
	type?: string;
	user?: string;
	device?: string;
	result?: string;
	keyword?: string;
}

export interface AppUserDto {
	id: string;
	username: string;
	name: string;
	password: string;
	role: string;
	status: string;
	permissions: string[];
	roles?: string[];
	lastLogin: string;
}

export interface RoleDto {
	id: string;
	key: string;
	name: string;
	description: string;
	permissions: string[];
	dataScopes?: string[];
	builtIn: boolean;
	createdAt: string;
}

export interface PermissionDto {
	key: string;
	name: string;
}

export interface SystemStatusDto {
	uptime: string;
	startedAt: string;
	currentTime: string;
}

export interface LoginUserInfo {
	id: string;
	username: string;
	name: string;
	role: string;
	permissions: string[];
}

export interface LoginResponse {
	token: string;
	user: LoginUserInfo;
}

const enc = encodeURIComponent;

export const businessSystemApi = {
	async login(username: string, password: string): Promise<LoginResponse> {
		const res = await client.post<LoginResponse>("/api/system/auth/login", {
			username,
			password,
		});
		return res.data;
	},

	async logout(): Promise<void> {
		await client.post("/api/system/auth/logout");
	},

	async me(): Promise<LoginUserInfo> {
		const res = await client.get<LoginUserInfo>("/api/system/auth/me");
		return res.data;
	},

	async getStatus(): Promise<SystemStatusDto> {
		const res = await client.get<SystemStatusDto>("/api/system/status");
		return res.data;
	},

	async listLogs(params: SystemLogQuery = {}): Promise<SystemLogDto[]> {
		const res = await client.get<SystemLogDto[]>("/api/system/logs", {
			params,
		});
		return res.data ?? [];
	},

	async exportLogs(params: SystemLogQuery = {}): Promise<Blob> {
		const res = await client.get("/api/system/logs/export", {
			params,
			responseType: "blob",
		});
		return res.data;
	},

	async listUsers(): Promise<AppUserDto[]> {
		const res = await client.get<AppUserDto[]>("/api/system/users");
		return res.data ?? [];
	},

	async listPermissions(): Promise<PermissionDto[]> {
		const res = await client.get<PermissionDto[]>(
			"/api/system/users/permissions",
		);
		return res.data ?? [];
	},

	async listRoles(): Promise<RoleDto[]> {
		const res = await client.get<RoleDto[]>("/api/system/roles");
		return res.data ?? [];
	},

	async createRole(role: Partial<RoleDto>): Promise<RoleDto> {
		const res = await client.post<RoleDto>("/api/system/roles", role);
		return res.data;
	},

	async updateRole(id: string, role: Partial<RoleDto>): Promise<RoleDto> {
		const res = await client.put<RoleDto>(
			`/api/system/roles/${enc(id)}`,
			role,
		);
		return res.data;
	},

	async deleteRole(id: string): Promise<void> {
		await client.delete(`/api/system/roles/${enc(id)}`);
	},

	async createUser(user: AppUserDto): Promise<AppUserDto> {
		const res = await client.post<AppUserDto>("/api/system/users", user);
		return res.data;
	},

	async updateUser(id: string, user: AppUserDto): Promise<AppUserDto> {
		const res = await client.put<AppUserDto>(
			`/api/system/users/${enc(id)}`,
			user,
		);
		return res.data;
	},

	async updateUserPermissions(
		id: string,
		permissions: string[],
	): Promise<AppUserDto> {
		const res = await client.put<AppUserDto>(
			`/api/system/users/${enc(id)}/permissions`,
			permissions,
		);
		return res.data;
	},

	async deleteUser(id: string): Promise<void> {
		await client.delete(`/api/system/users/${enc(id)}`);
	},
};
