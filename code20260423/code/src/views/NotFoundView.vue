<template>
	<div class="not-found-view">
		<div class="not-found-container">
			<h1>404</h1>
			<h2>页面未找到</h2>
			<p>您访问的页面不存在或已被删除</p>
			<el-button type="primary" @click="goHome">
				<el-icon><House /></el-icon>
				返回首页
			</el-button>
		</div>
	</div>
</template>

<script setup lang="ts">
import { useRouter } from "vue-router";
import { House } from "@element-plus/icons-vue";

const router = useRouter();

const goHome = () => {
	router.push("/");
};
</script>

<style lang="scss" scoped>
.not-found-view {
	display: flex;
	align-items: center;
	justify-content: center;
	height: 100%;
	background-color: #f0f2f5;

	.not-found-container {
		text-align: center;
		background-color: #fff;
		padding: 60px;
		border-radius: 8px;
		box-shadow: 0 2px 12px rgba(0, 0, 0, 0.1);

		h1 {
			font-size: 72px;
			font-weight: 600;
			color: #1890ff;
			margin-bottom: 20px;
		}

		h2 {
			font-size: 24px;
			font-weight: 500;
			margin-bottom: 20px;
			color: #333;
		}

		p {
			font-size: 16px;
			color: #666;
			margin-bottom: 30px;
		}
	}
}
</style>
