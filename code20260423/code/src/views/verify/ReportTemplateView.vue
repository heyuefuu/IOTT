<template>
	<div class="report-template-view">
		<h2 class="page-title">报告模板管理</h2>

		<!-- 模板列表 -->
		<el-card class="template-list-card">
			<template #header>
				<div class="card-header">
					<span>模板列表</span>
					<el-button type="primary" @click="openAddTemplateDialog">
						<el-icon><Plus /></el-icon>
						新增模板
					</el-button>
				</div>
			</template>

			<!-- 搜索和筛选 -->
			<div class="search-filter-bar">
				<el-input
					v-model="searchQuery"
					placeholder="搜索模板名称或描述"
					prefix-icon="Search"
					style="width: 300px; margin-right: 10px"
				/>
				<el-select
					v-model="filterType"
					placeholder="筛选类型"
					style="width: 150px; margin-right: 10px"
				>
					<el-option label="全部" value="" />
					<el-option label="性能报告" value="performance" />
					<el-option label="功能报告" value="function" />
					<el-option label="综合报告" value="comprehensive" />
				</el-select>
				<el-button type="primary" @click="refreshTemplateList">
					<el-icon><Refresh /></el-icon>
					刷新
				</el-button>
			</div>

			<!-- 模板表格 -->
			<el-table :data="filteredTemplates" style="width: 100%" border>
				<el-table-column prop="id" label="模板ID" width="80" />
				<el-table-column prop="name" label="模板名称" />
				<el-table-column prop="type" label="类型" width="120">
					<template #default="scope">
						<el-tag :type="getTypeType(scope.row.type)">
							{{ getTypeLabel(scope.row.type) }}
						</el-tag>
					</template>
				</el-table-column>
				<el-table-column prop="description" label="描述" />
				<el-table-column
					prop="createdAt"
					label="创建时间"
					width="180"
				/>
				<el-table-column label="操作" width="220">
					<template #default="scope">
						<el-button
							type="primary"
							size="small"
							@click="openEditTemplateDialog(scope.row)"
						>
							编辑
						</el-button>
						<el-button
							type="success"
							size="small"
							@click="previewTemplate(scope.row.id)"
						>
							预览
						</el-button>
						<el-button
							type="danger"
							size="small"
							@click="deleteTemplate(scope.row.id)"
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
					:total="templates.length"
					@size-change="handleSizeChange"
					@current-change="handleCurrentChange"
				/>
			</div>
		</el-card>

		<!-- 新增/编辑模板对话框 -->
		<el-dialog
			v-model="templateDialogVisible"
			:title="isEditing ? '编辑模板' : '新增模板'"
			width="600px"
		>
			<el-form :model="currentTemplate" label-width="120px">
				<el-form-item label="模板名称" prop="name" required>
					<el-input
						v-model="currentTemplate.name"
						placeholder="请输入模板名称"
					/>
				</el-form-item>
				<el-form-item label="模板类型" prop="type" required>
					<el-select
						v-model="currentTemplate.type"
						placeholder="请选择模板类型"
					>
						<el-option label="性能报告" value="performance" />
						<el-option label="功能报告" value="function" />
						<el-option label="稳定性报告" value="stability" />
						<el-option label="综合报告" value="comprehensive" />
					</el-select>
				</el-form-item>
				<el-form-item label="描述" prop="description">
					<el-input
						v-model="currentTemplate.description"
						type="textarea"
						:rows="3"
						placeholder="请输入模板描述"
					/>
				</el-form-item>
				<el-form-item label="模板内容" prop="content">
					<el-input
						v-model="currentTemplate.content"
						type="textarea"
						:rows="5"
						placeholder="请输入模板内容"
					/>
				</el-form-item>
			</el-form>
			<template #footer>
				<span class="dialog-footer">
					<el-button @click="templateDialogVisible = false"
						>取消</el-button
					>
					<el-button type="primary" @click="saveTemplate"
						>保存</el-button
					>
				</span>
			</template>
		</el-dialog>

		<!-- 预览对话框 -->
		<el-dialog
			v-model="previewDialogVisible"
			title="模板预览"
			width="800px"
		>
			<div class="preview-content" v-html="previewContent"></div>
			<template #footer>
				<span class="dialog-footer">
					<el-button @click="previewDialogVisible = false"
						>关闭</el-button
					>
				</span>
			</template>
		</el-dialog>
	</div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted } from "vue";
import { Plus, Refresh } from "@element-plus/icons-vue";
import { ElMessage, ElMessageBox } from "element-plus";
import {
	businessValidationApi,
	type ReportTemplateDto,
} from "@/api/businessValidation";

type Template = ReportTemplateDto;

const templates = ref<Template[]>([]);

// 搜索和筛选
const searchQuery = ref("");
const filterType = ref("");

// 分页
const currentPage = ref(1);
const pageSize = ref(10);

// 模板对话框
const templateDialogVisible = ref(false);
const isEditing = ref(false);
const currentTemplate = reactive<Template>({
	id: "",
	name: "",
	type: "performance",
	description: "",
	content: "",
	createdAt: new Date().toISOString(),
});

const createEmptyTemplate = (): Template => ({
	id: "",
	name: "",
	type: "performance",
	description: "",
	content: "",
	createdAt: new Date().toISOString(),
});

// 预览对话框
const previewDialogVisible = ref(false);
const previewContent = ref("");

const loadTemplates = async () => {
	try {
		templates.value = await businessValidationApi.listTemplates();
	} catch (error) {
		console.error(error);
		ElMessage.error("模板列表加载失败，请检查业务后端服务");
	}
};
// 过滤后的模板列表
const filteredTemplates = computed(() => {
	let result = [...templates.value];

	// 搜索
	if (searchQuery.value) {
		const query = searchQuery.value.toLowerCase();
		result = result.filter(
			(template) =>
				template.name.toLowerCase().includes(query) ||
				template.description.toLowerCase().includes(query),
		);
	}

	// 类型筛选
	if (filterType.value) {
		result = result.filter(
			(template) => template.type === filterType.value,
		);
	}

	// 分页
	const startIndex = (currentPage.value - 1) * pageSize.value;
	const endIndex = startIndex + pageSize.value;
	return result.slice(startIndex, endIndex);
});

// 打开新增模板对话框
const openAddTemplateDialog = () => {
	isEditing.value = false;
	Object.assign(currentTemplate, createEmptyTemplate());
	templateDialogVisible.value = true;
};

// 打开编辑模板对话框
const openEditTemplateDialog = (template: Template) => {
	isEditing.value = true;
	Object.assign(currentTemplate, { ...template });
	templateDialogVisible.value = true;
};

// 保存模板
const saveTemplate = async () => {
	const payload = {
		...currentTemplate,
		createdAt: currentTemplate.createdAt || new Date().toISOString(),
	};
	try {
		if (isEditing.value) {
			await businessValidationApi.updateTemplate(currentTemplate.id, payload);
		} else {
			await businessValidationApi.createTemplate(payload);
		}
		templateDialogVisible.value = false;
		await loadTemplates();
		ElMessage.success("模板已保存");
	} catch (error) {
		console.error(error);
		ElMessage.error("模板保存失败，请检查业务后端服务");
	}
};

// 删除模板
const deleteTemplate = async (id: string) => {
	try {
		await ElMessageBox.confirm("确定删除该模板？", "删除确认", {
			type: "warning",
		});
		await businessValidationApi.deleteTemplate(id);
		await loadTemplates();
		ElMessage.success("模板已删除");
	} catch (error) {
		if (error !== "cancel") {
			console.error(error);
			ElMessage.error("模板删除失败，请检查业务后端服务");
		}
	}
};

// 预览模板
const previewTemplate = async (id: string) => {
	try {
		const res = await businessValidationApi.previewTemplate(id);
		previewContent.value = res.html;
		previewDialogVisible.value = true;
	} catch (error) {
		console.error(error);
		ElMessage.error("模板预览失败，请检查业务后端服务");
	}
};

// 刷新模板列表
const refreshTemplateList = async () => {
	await loadTemplates();
};

// 获取类型类型
const getTypeType = (type: string) => {
	switch (type) {
		case "performance":
			return "primary";
		case "function":
			return "success";
		case "stability":
			return "warning";
		case "comprehensive":
			return "info";
		default:
			return "default";
	}
};

// 获取类型标签
const getTypeLabel = (type: string) => {
	switch (type) {
		case "performance":
			return "性能报告";
		case "function":
			return "功能报告";
		case "stability":
			return "稳定性报告";
		case "comprehensive":
			return "综合报告";
		default:
			return type;
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
	void loadTemplates();
});
</script>

<style lang="scss" scoped>
.report-template-view {
	.template-list-card {
		margin-bottom: 20px;
	}

	.card-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
	}

	.search-filter-bar {
		display: flex;
		align-items: center;
		margin-bottom: 20px;
	}

	.pagination-bar {
		margin-top: 20px;
		display: flex;
		justify-content: flex-end;
	}

	.preview-content {
		padding: 20px;
		border: 1px solid #e4e7ed;
		border-radius: 4px;
		background-color: #f9fafc;
		max-height: 500px;
		overflow-y: auto;

		:deep(h1) {
			font-size: 24px;
			margin-bottom: 20px;
		}

		:deep(h2) {
			font-size: 20px;
			margin: 16px 0;
		}

		:deep(h3) {
			font-size: 16px;
			margin: 12px 0;
		}

		:deep(ul) {
			margin: 10px 0;
			padding-left: 20px;
		}

		:deep(li) {
			margin: 5px 0;
		}
	}
}
</style>
