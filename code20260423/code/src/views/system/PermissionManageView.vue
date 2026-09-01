<template>
	<div class="system-permission-manage-view">
		<h2 class="page-title">权限管理</h2>

		<el-card class="permission-manage-card">
			<template #header>
				<div class="card-header">
					<span>权限管理</span>
					<el-button type="primary" @click="openAddUserDialog">
						<el-icon><Plus /></el-icon>
						新增用户
					</el-button>
				</div>
			</template>

			<div class="permission-manage-content">
				<!-- 用户列表 -->
				<div class="user-list">
					<el-table :data="pagedUsers" style="width: 100%" border>
						<el-table-column prop="id" label="用户ID" width="100" />
						<el-table-column
							prop="username"
							label="用户名"
							width="150"
						/>
						<el-table-column prop="name" label="姓名" />
						<el-table-column prop="role" label="角色" width="120">
							<template #default="scope">
								<el-tag :type="getRoleType(scope.row.role)">
									{{ getRoleName(scope.row.role) }}
								</el-tag>
							</template>
						</el-table-column>
						<el-table-column prop="status" label="状态" width="100">
							<template #default="scope">
								<el-tag
									:type="
										scope.row.status === '启用'
											? 'success'
											: 'danger'
									"
								>
									{{ scope.row.status }}
								</el-tag>
							</template>
						</el-table-column>
						<el-table-column
							prop="lastLogin"
							label="最后登录"
							width="180"
						/>
						<el-table-column label="操作" width="250">
							<template #default="scope">
								<el-button
									type="primary"
									size="small"
									@click="openEditUserDialog(scope.row)"
								>
									编辑
								</el-button>
								<el-button
									type="success"
									size="small"
									@click="openPermissionDialog(scope.row)"
									style="margin-left: 5px"
								>
									权限设置
								</el-button>
								<el-button
									type="danger"
									size="small"
									@click="deleteUser(scope.row)"
									style="margin-left: 5px"
								>
									删除
								</el-button>
							</template>
						</el-table-column>
					</el-table>

					<!-- 分页 -->
					<div class="pagination-bar">
						<el-pagination
							v-model:current-page="currentPage"
							v-model:page-size="pageSize"
							:page-sizes="[10, 20, 50, 100]"
							layout="total, sizes, prev, pager, next, jumper"
							:total="users.length"
							@size-change="handleSizeChange"
							@current-change="handleCurrentChange"
						/>
					</div>
				</div>
			</div>
		</el-card>

		<!-- 新增/编辑用户对话框 -->
		<el-dialog
			v-model="userDialogVisible"
			:title="isEditing ? '编辑用户' : '新增用户'"
			width="500px"
		>
			<el-form :model="currentUser" label-width="120px">
				<el-form-item label="用户名" prop="username" required>
					<el-input
						v-model="currentUser.username"
						placeholder="请输入用户名"
					/>
				</el-form-item>
				<el-form-item label="姓名" prop="name" required>
					<el-input
						v-model="currentUser.name"
						placeholder="请输入姓名"
					/>
				</el-form-item>
				<el-form-item
					label="密码"
					prop="password"
					:required="!isEditing"
				>
					<el-input
						v-model="currentUser.password"
						type="password"
						placeholder="请输入密码"
					/>
				</el-form-item>
				<el-form-item label="角色" prop="role" required>
					<el-select
						v-model="currentUser.role"
						placeholder="请选择角色"
					>
						<el-option label="管理员" value="admin" />
						<el-option label="操作员" value="operator" />
						<el-option label="查看者" value="viewer" />
					</el-select>
				</el-form-item>
				<el-form-item label="状态" prop="status">
					<el-switch
						v-model="currentUser.status"
						active-value="启用"
						inactive-value="禁用"
					/>
				</el-form-item>
			</el-form>
			<template #footer>
				<span class="dialog-footer">
					<el-button @click="userDialogVisible = false"
						>取消</el-button
					>
					<el-button type="primary" @click="saveUser">保存</el-button>
				</span>
			</template>
		</el-dialog>

		<!-- 权限设置对话框 -->
		<el-dialog
			v-model="permissionDialogVisible"
			:title="`权限设置: ${selectedUser?.name || ''}`"
			width="600px"
		>
			<div v-if="selectedUser" class="permission-setting">
				<el-checkbox-group
					v-model="userPermissions"
					style="width: 100%"
				>
					<el-checkbox
						v-for="permission in allPermissions"
						:key="permission.key"
						:label="permission.key"
						style="width: 33%; margin-bottom: 10px"
					>
						{{ permission.name }}
					</el-checkbox>
				</el-checkbox-group>
			</div>
			<template #footer>
				<span class="dialog-footer">
					<el-button @click="permissionDialogVisible = false"
						>取消</el-button
					>
					<el-button type="primary" @click="savePermission"
						>保存</el-button
					>
				</span>
			</template>
		</el-dialog>
	</div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted } from "vue";
import { Plus } from "@element-plus/icons-vue";
import { ElMessage, ElMessageBox } from "element-plus";
import {
	businessSystemApi,
	type AppUserDto,
	type PermissionDto,
} from "@/api/businessSystem";

type User = AppUserDto;

const users = ref<User[]>([]);

// 分页
const currentPage = ref(1);
const pageSize = ref(10);

const pagedUsers = computed(() => {
	const start = (currentPage.value - 1) * pageSize.value;
	return users.value.slice(start, start + pageSize.value);
});

// 所有权限
const allPermissions = ref<PermissionDto[]>([]);

// 用户对话框
const userDialogVisible = ref(false);
const isEditing = ref(false);
const currentUser = reactive<User>({
	id: "",
	username: "",
	name: "",
	password: "",
	role: "viewer",
	status: "启用",
	permissions: ["data_read"],
	lastLogin: "",
});

// 权限对话框
const permissionDialogVisible = ref(false);
const selectedUser = ref<User | null>(null);
const userPermissions = ref<string[]>([]);

const createEmptyUser = (): User => ({
	id: "",
	username: "",
	name: "",
	password: "",
	role: "viewer",
	status: "启用",
	permissions: ["data_read"],
	lastLogin: "",
});

const loadUsers = async () => {
	try {
		users.value = await businessSystemApi.listUsers();
	} catch (error) {
		console.error(error);
		ElMessage.error("用户列表加载失败，请检查业务后端服务");
	}
};

const loadPermissions = async () => {
	allPermissions.value = await businessSystemApi.listPermissions();
};

// 获取角色类型
const getRoleType = (role: string) => {
	switch (role) {
		case "admin":
			return "danger";
		case "operator":
			return "warning";
		case "viewer":
			return "info";
		default:
			return "info";
	}
};

// 获取角色名称
const getRoleName = (role: string) => {
	switch (role) {
		case "admin":
			return "管理员";
		case "operator":
			return "操作员";
		case "viewer":
			return "查看者";
		default:
			return "未知角色";
	}
};

// 打开新增用户对话框
const openAddUserDialog = () => {
	isEditing.value = false;
	Object.assign(currentUser, createEmptyUser());
	userDialogVisible.value = true;
};

// 打开编辑用户对话框
const openEditUserDialog = (user: User) => {
	isEditing.value = true;
	Object.assign(currentUser, { ...user });
	userDialogVisible.value = true;
};

// 保存用户
const saveUser = async () => {
	try {
		if (isEditing.value) {
			await businessSystemApi.updateUser(currentUser.id, { ...currentUser });
		} else {
			await businessSystemApi.createUser({ ...currentUser });
		}
		userDialogVisible.value = false;
		await loadUsers();
		ElMessage.success("用户已保存");
	} catch (error) {
		console.error(error);
		ElMessage.error("用户保存失败，请检查用户名是否重复或后端服务状态");
	}
};

// 打开权限设置对话框
const openPermissionDialog = (user: User) => {
	selectedUser.value = user;
	userPermissions.value = [...user.permissions];
	permissionDialogVisible.value = true;
};

// 保存权限
const savePermission = async () => {
	if (!selectedUser.value) return;
	try {
		await businessSystemApi.updateUserPermissions(
			selectedUser.value.id,
			userPermissions.value,
		);
		permissionDialogVisible.value = false;
		await loadUsers();
		ElMessage.success("权限已保存");
	} catch (error) {
		console.error(error);
		ElMessage.error("权限保存失败，请检查业务后端服务");
	}
};

// 删除用户
const deleteUser = async (user: User) => {
	try {
		await ElMessageBox.confirm(
			`确定删除用户 ${user.username}？`,
			"删除确认",
			{ type: "warning" },
		);
		await businessSystemApi.deleteUser(user.id);
		await loadUsers();
		ElMessage.success("用户已删除");
	} catch (error) {
		if (error !== "cancel") {
			console.error(error);
			ElMessage.error("用户删除失败，请检查业务后端服务");
		}
	}
};

// 分页处理
const handleSizeChange = (size: number) => {
	pageSize.value = size;
	currentPage.value = 1;
};

const handleCurrentChange = (current: number) => {
	currentPage.value = current;
};

onMounted(() => {
	void loadUsers();
	void loadPermissions();
});
</script>

<style lang="scss" scoped>
.system-permission-manage-view {
	.permission-manage-card {
		margin-bottom: 20px;
	}

	.card-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
	}

	.permission-manage-content {
		padding: 20px;
	}

	.user-list {
		margin-top: 20px;
	}

	.pagination-bar {
		margin-top: 20px;
		display: flex;
		justify-content: flex-end;
	}

	.permission-setting {
		padding: 20px;
	}
}
</style>
