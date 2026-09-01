<template>
	<router-view v-if="isLoginRoute" />
	<el-container v-else class="app-container">
		<!-- 顶部导航栏 -->
		<el-header class="app-header">
			<div class="app-title">
				<img src="@/assets/logo.svg" alt="logo" class="app-logo" />
				<span>机床成组连线通讯验证系统</span>
			</div>
			<div class="app-header-actions">
				<!-- 主题切换 -->
				<el-switch
					v-model="isDark"
					inline-prompt
					active-text="暗黑"
					inactive-text="浅色"
					@change="toggleTheme"
				/>
				<span class="current-time">{{ currentTime }}</span>
				<el-dropdown @command="handleUserCommand">
					<span class="el-dropdown-link">
						{{ userDisplayName }}
						<el-icon class="el-icon--right">
							<ArrowDown />
						</el-icon>
					</span>
					<template #dropdown>
						<el-dropdown-menu>
							<el-dropdown-item command="profile">个人中心</el-dropdown-item>
							<el-dropdown-item command="logout" divided>退出登录</el-dropdown-item>
						</el-dropdown-menu>
					</template>
				</el-dropdown>
			</div>
		</el-header>

		<!-- 主内容区 -->
		<el-container class="main-container">
			<!-- 左侧菜单 -->
			<el-aside class="app-sidebar">
				<el-menu
					:default-active="currentRoutePath"
					class="app-menu"
					@select="handleMenuSelect"
					router
				>
					<template v-for="item in menuItems" :key="item.path">
						<el-menu-item v-if="!item.subItems" :index="item.path">
							<el-icon>
								<component :is="item.icon" />
							</el-icon>
							<span>{{ item.label }}</span>
						</el-menu-item>
						<el-sub-menu v-else :index="item.path">
							<template #title>
								<el-icon>
									<component :is="item.icon" />
								</el-icon>
								<span>{{ item.label }}</span>
							</template>
							<el-menu-item
								v-for="subItem in item.subItems"
								:key="subItem.path"
								:index="subItem.path"
							>
								{{ subItem.label }}
							</el-menu-item>
						</el-sub-menu>
					</template>
				</el-menu>
			</el-aside>

			<!-- 右侧内容区 -->
			<el-main class="app-main">
				<el-scrollbar class="main-scrollbar">
					<router-view v-slot="{ Component }">
						<transition name="fade" mode="out-in">
							<component :is="Component" />
						</transition>
					</router-view>
				</el-scrollbar>
			</el-main>
		</el-container>
	</el-container>
	<el-dialog v-model="profileDialogVisible" title="个人中心" width="420px">
		<el-descriptions v-if="auth.user" :column="1" border>
			<el-descriptions-item label="姓名">{{ auth.user.name }}</el-descriptions-item>
			<el-descriptions-item label="账号">{{ auth.user.username }}</el-descriptions-item>
			<el-descriptions-item label="角色">{{ auth.user.role }}</el-descriptions-item>
			<el-descriptions-item label="权限">
				{{ auth.user.permissions.join("、") || "无" }}
			</el-descriptions-item>
		</el-descriptions>
		<template #footer>
			<el-button type="primary" @click="profileDialogVisible = false">关闭</el-button>
		</template>
	</el-dialog>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from "vue";
import { useRoute, useRouter } from "vue-router";
import { ElMessageBox } from "element-plus";
import { useAuthStore } from "@/stores/auth";
import { requiredPermission } from "@/auth/permissions";
import {
	Grid,
	Monitor,
	Document,
	Cpu,
	Operation,
	Files,
	FolderOpened,
	Lock,
	ArrowDown,
	DataLine,
} from "@element-plus/icons-vue";

const route = useRoute();
const router = useRouter();
const auth = useAuthStore();
const currentTime = ref("");
const isDark = ref(false);
const profileDialogVisible = ref(false);

const isLoginRoute = computed(() => route.path === "/login");
const userDisplayName = computed(
	() => auth.user?.name || auth.user?.username || "未登录",
);

const rawMenuItems = [
	{ path: "/", label: "首页", icon: Grid },
	{ path: "/industrial/device", label: "CNC监控", icon: Monitor },
	{
		path: "/plc/device",
		label: "PLC监控",
		icon: Cpu,
	},
	{
		path: "/robot/device",
		label: "机器人监控",
		icon: Operation,
	},
	{ path: "/collection/manage", label: "采集任务管理", icon: DataLine },
	{
		path: "/transfer",
		label: "程序传输",
		icon: FolderOpened,
		subItems: [
			{ path: "/transfer/device", label: "传输设备" },
			{ path: "/transfer/browser", label: "文件浏览器" },
			{ path: "/transfer/records", label: "传输验证记录" },
		],
	},
	{
		path: "/cs",
		label: "Client/Server 模式",
		icon: Files,
		subItems: [
			{ path: "/cs/gateway", label: "网关管理" },
			{ path: "/cs/client", label: "客户端数据源" },
			{ path: "/cs/server", label: "服务器服务" },
		],
	},
	{
		path: "/verify",
		label: "验证管理",
		icon: Document,
		subItems: [
			{ path: "/industrial/property", label: "机床属性" },
			{ path: "/parallel", label: "并行连接验证" },
			{ path: "/verify/task", label: "任务管理" },
			{ path: "/verify/metric", label: "指标管理" },
			{ path: "/verify/report", label: "报表模板" },
			{ path: "/verify/visualization", label: "数据可视化" },
		],
	},
	{
		path: "/system",
		label: "系统管理",
		icon: Lock,
		subItems: [
			{ path: "/system/log", label: "日志管理" },
			{ path: "/system/permission", label: "权限管理" },
		],
	},
];

const canAccessPath = (path: string) => {
	const permission = requiredPermission(path);
	return !permission || auth.hasPermission(permission);
};

const menuItems = computed(() =>
	rawMenuItems
		.map((item) => {
			if (!item.subItems) return canAccessPath(item.path) ? item : null;
			const subItems = item.subItems.filter((subItem) => canAccessPath(subItem.path));
			return subItems.length > 0 ? { ...item, subItems } : null;
		})
		.filter((item): item is NonNullable<typeof item> => item != null),
);

const currentRoutePath = computed(() => route.path);

const handleMenuSelect = (key: string) => {
	router.push(key);
};

const handleUserCommand = async (command: string) => {
	if (command === "profile") {
		profileDialogVisible.value = true;
		return;
	}
	if (command !== "logout") return;
	try {
		await ElMessageBox.confirm("确定退出当前账号？", "退出登录", {
			confirmButtonText: "退出",
			cancelButtonText: "取消",
			type: "warning",
		});
		await auth.logout();
		profileDialogVisible.value = false;
		await router.push("/login");
	} catch {
		// 取消退出
	}
};

const updateCurrentTime = () => {
	const now = new Date();
	currentTime.value = now.toLocaleString("zh-CN", {
		year: "numeric",
		month: "2-digit",
		day: "2-digit",
		hour: "2-digit",
		minute: "2-digit",
		second: "2-digit",
	});
};

// 时钟定时器句柄，卸载时清理
let clockTimer: number | undefined;

// 初始化
onMounted(() => {
	updateCurrentTime();
	clockTimer = window.setInterval(updateCurrentTime, 1000);

	// 加载保存的主题
	const savedTheme = localStorage.getItem("theme");
	if (
		savedTheme === "dark" ||
		(!savedTheme &&
			window.matchMedia("(prefers-color-scheme: dark)").matches)
	) {
		enableDarkMode();
	}
});

// 切换主题
const toggleTheme = () => {
	if (isDark.value) {
		enableDarkMode();
	} else {
		disableDarkMode();
	}
};

// 启用暗黑模式
const enableDarkMode = () => {
	document.documentElement.classList.add("dark");
	localStorage.setItem("theme", "dark");
	isDark.value = true;
};

// 禁用暗黑模式
const disableDarkMode = () => {
	document.documentElement.classList.remove("dark");
	localStorage.setItem("theme", "light");
	isDark.value = false;
};

onUnmounted(() => {
	if (clockTimer !== undefined) {
		clearInterval(clockTimer);
		clockTimer = undefined;
	}
});
</script>

<style scoped>
.app-container {
	height: 100vh;
	display: flex;
	flex-direction: column;
}

.main-container {
	flex: 1;
	display: flex;
	overflow: hidden;
}

.app-header {
	height: 60px;
	background-color: var(--el-bg-color-overlay);
	color: var(--el-text-color-primary);
	display: flex;
	align-items: center;
	justify-content: space-between;
	padding: 0 20px;
	border-bottom: 1px solid var(--el-border-color);
}

.app-title {
	font-size: 18px;
	font-weight: bold;
	color: var(--el-color-primary);
	display: flex;
	align-items: center;
	gap: 10px;
}

.app-logo {
	height: 32px;
	width: 32px;
	object-fit: contain;
}

.app-header-actions {
	display: flex;
	align-items: center;
	gap: 20px;
}

.current-time {
	color: var(--el-text-color-primary);
}

.el-dropdown-link {
	color: var(--el-text-color-primary);
	cursor: pointer;
}

.app-sidebar {
	width: 200px;
	background-color: var(--el-bg-color);
	border-right: 1px solid var(--el-border-color);
	display: flex;
	flex-direction: column;
}

.app-menu {
	border-right: none;
	background-color: transparent;
	flex: 1;
}

.app-main {
	padding: 16px;
	overflow: hidden;
	background-color: var(--el-bg-color-page);
	flex: 1;
	display: flex;
	flex-direction: column;
}

.main-scrollbar {
	height: 100%;
	width: 100%;
}

.main-scrollbar .el-scrollbar__wrap {
	overflow-x: hidden;
}

.fade-enter-active,
.fade-leave-active {
	transition: opacity 0.3s ease;
}

.fade-enter-from,
.fade-leave-to {
	opacity: 0;
}
/* 暗黑模式适配 */
:global(.dark) .app-menu {
	--el-menu-bg-color: var(--el-bg-color);
	--el-menu-text-color: var(--el-text-color-primary);
	--el-menu-active-color: var(--el-color-primary);
	--el-menu-hover-bg-color: var(--el-bg-color-overlay);
}

/* 暗黑模式适配 */
:global(.dark) .app-header {
	background-color: var(--el-bg-color-overlay);
	border-bottom-color: var(--el-border-color);
}

:global(.dark) .app-sidebar {
	background-color: var(--el-bg-color);
	border-right-color: var(--el-border-color);
}

:global(.dark) .app-main {
	background-color: var(--el-bg-color-page);
}
</style>
