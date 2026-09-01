<template>
	<div class="login-view">
		<el-card class="login-card">
			<div class="login-header">
				<img src="@/assets/logo.svg" alt="logo" class="login-logo" />
				<h2>机床成组连线通讯验证系统</h2>
				<p>请登录后继续</p>
			</div>
			<el-form :model="form" label-position="top" @keyup.enter="submit">
				<el-form-item label="用户名">
					<el-input
						v-model="form.username"
						placeholder="请输入用户名"
						autocomplete="username"
					/>
				</el-form-item>
				<el-form-item label="密码">
					<el-input
						v-model="form.password"
						type="password"
						show-password
						placeholder="请输入密码"
						autocomplete="current-password"
					/>
				</el-form-item>
				<el-button
					type="primary"
					class="login-button"
					:loading="loading"
					@click="submit"
				>
					登录
				</el-button>
			</el-form>
			<el-alert
				type="info"
				:closable="false"
				show-icon
				title="首次启动会自动创建默认管理员 admin（初始密码 admin@123），登录后请在「系统管理 → 权限管理」中修改密码。"
				style="margin-top: 16px"
			/>
		</el-card>
	</div>
</template>

<script setup lang="ts">
import { reactive, ref } from "vue";
import { useRoute, useRouter } from "vue-router";
import { ElMessage } from "element-plus";
import { useAuthStore } from "@/stores/auth";

const auth = useAuthStore();
const router = useRouter();
const route = useRoute();

const form = reactive({ username: "", password: "" });
const loading = ref(false);

const submit = async () => {
	if (!form.username.trim() || !form.password) {
		ElMessage.warning("请输入用户名和密码");
		return;
	}
	loading.value = true;
	try {
		await auth.login(form.username.trim(), form.password);
		ElMessage.success(`欢迎，${auth.user?.name ?? auth.user?.username ?? ""}`);
		const redirect =
			typeof route.query.redirect === "string" ? route.query.redirect : "/";
		void router.push(redirect);
	} catch (e: unknown) {
		const ax = e as { response?: { data?: { error?: string } } };
		ElMessage.error(ax.response?.data?.error ?? "登录失败，请检查网关服务是否已启动");
	} finally {
		loading.value = false;
	}
};
</script>

<style lang="scss" scoped>
.login-view {
	height: 100vh;
	display: flex;
	align-items: center;
	justify-content: center;
	background: linear-gradient(135deg, #1f2d3d 0%, #2b4a6f 100%);

	.login-card {
		width: 420px;
		padding: 8px 12px 20px;
	}

	.login-header {
		text-align: center;
		margin-bottom: 12px;

		.login-logo {
			height: 48px;
			width: 48px;
		}

		h2 {
			margin: 8px 0 4px;
			font-size: 20px;
		}

		p {
			margin: 0;
			color: var(--el-text-color-secondary);
			font-size: 13px;
		}
	}

	.login-button {
		width: 100%;
		margin-top: 4px;
	}
}
</style>
