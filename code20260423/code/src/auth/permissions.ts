/**
 * 路由路径 → 所需权限（键与后端 /api/system/users/permissions 权限清单一致）。
 * 未命中的路径登录即可访问；admin 角色或含 "all" 权限的用户不受限制。
 * App.vue 侧边栏过滤与 router 守卫共用这一份映射。
 */
const RULES: Array<[prefix: string, permission: string]> = [
	["/system/log", "log_manage"],
	["/system/permission", "permission_manage"],
	["/industrial", "device_manage"],
	["/plc", "device_manage"],
	["/robot", "device_manage"],
	["/collection", "device_manage"],
	["/transfer", "device_manage"],
	["/cs", "config_manage"],
	["/parallel", "report_manage"],
	["/verify", "report_manage"],
];

export function requiredPermission(path: string): string | null {
	const matches = RULES.filter(
		([prefix]) => path === prefix || path.startsWith(`${prefix}/`),
	);
	if (!matches.length) return null;
	// 更长前缀优先（如 /system/permission 优先于 /system）
	matches.sort((a, b) => b[0].length - a[0].length);
	return matches[0]?.[1] ?? null;
}
