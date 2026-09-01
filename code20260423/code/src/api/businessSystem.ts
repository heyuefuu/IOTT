import axios from "axios";

const baseURL =
	import.meta.env.VITE_MACHINE_CONNECTION_API ?? "/machine-connection";

const client = axios.create({
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
}

export interface SystemLogQuery {
	type?: string;
	user?: string;
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
	lastLogin: string;
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

/** 登录 token 的 localStorage 键；auth store 写入，这里的拦截器读取并附加请求头 */
export const AUTH_TOKEN_KEY = "mc.auth.token";

const enc = encodeURIComponent;

client.interceptors.request.use((config) => {
	const token = localStorage.getItem(AUTH_TOKEN_KEY);
	if (token) config.headers["X-Auth-Token"] = token;
	return config;
});

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
