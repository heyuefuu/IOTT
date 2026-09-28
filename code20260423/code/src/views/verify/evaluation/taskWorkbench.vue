<template>
  <div class="task-workbench">
    <h2 class="page-title">验证任务管理</h2>
    <el-card shadow="never">
      <template #header><div class="card-header"><span>任务列表</span><div><el-button :icon="Refresh" :loading="loading" @click="loadPage">刷新</el-button><el-button type="primary" :icon="Plus" @click="addTask">创建任务</el-button></div></div></template>
      <el-form :inline="true" :model="filters" class="task-filters" @submit.prevent="search">
        <el-form-item label="任务状态"><el-select v-model="filters.status" placeholder="全部" clearable><el-option label="全部" value="" /><el-option v-for="status in ['pending', 'running', 'completed', 'failed']" :key="status" :value="status" :label="taskStatusLabels[status]" /></el-select></el-form-item>
        <el-form-item label="关键词"><el-input v-model="filters.keyword" clearable placeholder="任务名称、编号或机床" @keyup.enter="search" /></el-form-item>
        <el-form-item><el-button type="primary" :icon="Search" @click="search">搜索</el-button><el-button @click="resetSearch">重置</el-button></el-form-item>
      </el-form>
      <el-table v-loading="loading" :data="filteredTasks" row-key="id" :expand-row-keys="expandedRows" border empty-text="暂无验证任务" @expand-change="onExpand">
        <el-table-column type="expand"><template #default="scope"><task-results :task="scope.row" :machine="machineFor(scope.row)" /></template></el-table-column>
        <el-table-column prop="id" label="任务ID" width="135" show-overflow-tooltip />
        <el-table-column prop="name" label="任务名称" min-width="180" show-overflow-tooltip />
        <el-table-column label="测试内容" min-width="260" show-overflow-tooltip><template #default="scope">{{ taskMetricSummary(scope.row, metricsFor(scope.row)) }}</template></el-table-column>
        <el-table-column label="状态" width="100"><template #default="scope"><el-tag :type="taskStatusType(scope.row.status)">{{ taskStatusLabels[scope.row.status] || scope.row.status }}</el-tag></template></el-table-column>
        <el-table-column label="创建时间" width="175"><template #default="scope">{{ taskTime(scope.row.createdAt) }}</template></el-table-column>
        <el-table-column label="完成时间" width="175"><template #default="scope">{{ taskTime(scope.row.completedAt) }}</template></el-table-column>
        <el-table-column label="操作" fixed="right" width="405">
          <template #default="scope">
            <div class="row-actions">
              <el-button type="primary" link :disabled="scope.row.status === 'running'" @click="editTask(scope.row)">编辑</el-button>
              <el-button type="success" link :loading="executingId === scope.row.id" :disabled="Boolean(executingId) || scope.row.status === 'running'" @click="executeTask(scope.row)">{{ scope.row.lastRunJson ? '重新执行' : '执行' }}</el-button>
              <el-button type="primary" link :disabled="!scope.row.lastRunJson" @click="viewResult(scope.row)">查看结果</el-button>
              <el-button type="warning" link :loading="exportingId === scope.row.id" :disabled="!scope.row.lastRunJson || scope.row.status === 'running'" @click="exportTask(scope.row)">导出Excel</el-button>
              <el-tooltip :disabled="canImport(scope.row)" content="需先执行任务，旧版结果需重新执行以保存机床和指标快照"><span><el-button type="primary" link :disabled="!canImport(scope.row) || scope.row.status === 'running'" @click="importKnowledge(scope.row)">导入知识库</el-button></span></el-tooltip>
              <el-button type="danger" link :disabled="scope.row.status === 'running'" @click="deleteTask(scope.row)">删除</el-button>
            </div>
          </template>
        </el-table-column>
      </el-table>
    </el-card>
    <task-editor v-model:visible="editorVisible" :task="draft" :machines="machines" :metrics="metrics" :saving="saving" @save="saveTask" @category-change="selectCategory" />
    <el-dialog v-model="resultVisible" title="机床条例验证明细" width="min(1220px, 96vw)" top="5vh">
      <div v-if="selectedTask" class="result-dialog"><task-results :task="selectedTask" :machine="machineFor(selectedTask)" /></div>
      <template #footer><el-button @click="resultVisible = false">关闭</el-button><template v-if="selectedTask"><el-button :disabled="!canImport(selectedTask)" @click="importKnowledge(selectedTask)">导入评价知识库</el-button><el-button :loading="exportingId === selectedTask.id" @click="exportTask(selectedTask)">导出 Excel</el-button><el-button type="primary" :loading="exportingId === selectedTask.id" @click="exportTask(selectedTask, 'pdf')">导出 PDF</el-button></template></template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import { Plus, Refresh, Search } from '@element-plus/icons-vue';
import type { VerifyTaskDto } from '@/api/machineConnectionVerify';
import taskEditor from './taskEditor.vue';
import taskResults from './taskResults.vue';
import { taskMetricSummary, taskStatusLabels, taskStatusType, taskTime } from './taskModel';
import { useTaskWorkbench } from './taskState';
const { tasks, machines, metrics, loading, saving, executingId, exportingId, editorVisible, resultVisible,
  draft, selectedTask, expandedRows, machineFor, metricsFor, selectCategory, loadPage, addTask, editTask, saveTask, executeTask, deleteTask, viewResult, exportTask, canImport, importKnowledge } = useTaskWorkbench();
const filters = reactive({ status: '', keyword: '' });
const applied = ref({ status: '', keyword: '' });
const filteredTasks = computed(() => tasks.value.filter(task => {
  const statusMatches = !applied.value.status || task.status === applied.value.status;
  const keyword = applied.value.keyword.trim().toLowerCase();
  const text = `${task.id} ${task.name} ${machineFor(task)?.name ?? ''} ${taskMetricSummary(task, metricsFor(task))}`.toLowerCase();
  return statusMatches && (!keyword || text.includes(keyword));
}));
function search() { applied.value = { ...filters }; expandedRows.value = []; }
function resetSearch() { Object.assign(filters, { status: '', keyword: '' }); search(); }
function onExpand(_row: VerifyTaskDto, expanded: VerifyTaskDto[]) { expandedRows.value = expanded.map(task => task.id); }
onMounted(loadPage);
</script>

<style scoped>
.task-workbench { padding-bottom: 20px; }
.card-header { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.task-filters .el-select { width: 150px; }
.task-filters .el-input { width: 240px; }
.row-actions { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
.row-actions .el-button + .el-button { margin-left: 0; }
.result-dialog { max-height: 72vh; overflow-y: auto; }
</style>
