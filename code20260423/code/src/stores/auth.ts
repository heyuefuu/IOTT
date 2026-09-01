import { defineStore } from "pinia";
import {
	businessSystemApi,
	AUTH_TOKEN_KEY,
	type LoginUserInfo,
} from "@/api/businessSystem";

const AUTH_USER_KEY = "mc.auth.user";

function restoreUser(): LoginUserInfo | null {
	try {
		const raw = localStorage.getItem(AUTH_USER_KEY);
		return raw ? (JSON.parse(raw) as LoginUserInfo) : null;
	} catch {
		return null;
	}
}

export const useAuthStore = defineStore("auth", {
	state: () => ({
		token: localStorage.getItem(AUTH_TOKEN_KEY) ?? "",
		user: restoreUser(),
	}),
	getters: {
		isLoggedIn: (state) => Boolean(state.token && state.user),
		hasPermission() {
			return (key: string): boolean => {
				const user = this.user;
				if (!user) return false;
				return (
					user.role === "admin" ||
					user.permissions.includes("all") ||
					user.permissions.includes(key)
				);
			};
		},
	},
	actions: {
		async login(username: string, password: string) {
			const res = await businessSystemApi.login(username, password);
			this.token = res.token;
			this.user = res.user;
			localStorage.setItem(AUTH_TOKEN_KEY, res.token);
			localStorage.setItem(AUTH_USER_KEY, JSON.stringify(res.user));
		},
		async logout() {
			try {
				await businessSystemApi.logout();
			} catch {
				// 后端不可达也要完成本地登出
			}
			this.token = "";
			this.user = null;
			localStorage.removeItem(AUTH_TOKEN_KEY);
			localStorage.removeItem(AUTH_USER_KEY);
		},
		/** 用 token 换取最新用户信息；token 失效则清理本地登录态 */
		async refreshSession(): Promise<boolean> {
			if (!this.token) return false;
			try {
				this.user = await businessSystemApi.me();
				localStorage.setItem(AUTH_USER_KEY, JSON.stringify(this.user));
				return true;
			} catch {
				this.token = "";
				this.user = null;
				localStorage.removeItem(AUTH_TOKEN_KEY);
				localStorage.removeItem(AUTH_USER_KEY);
				return false;
			}
		},
	},
});
