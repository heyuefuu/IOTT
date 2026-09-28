<template>
  <div class="evaluation-knowledge-view">
    <h2 class="page-title">评价知识库</h2>
    <div class="stats-grid">
      <el-card v-for="stat in stats" :key="stat.label" shadow="never" class="stat-card">
        <span>{{ stat.label }}</span><strong>{{ stat.value }}</strong>
      </el-card>
    </div>
    <el-card class="filter-card" shadow="never">
      <template #header><span>查询筛选</span></template>
      <el-form label-position="top" :model="filters" @submit.prevent="search">
        <div class="filter-grid">
          <el-form-item v-for="field in searchFields" :key="field.key" :label="field.label">
            <el-input v-model="filters[field.key]" clearable :placeholder="`请输入${field.label}`" @keyup.enter="search" />
          </el-form-item>
        </div>
        <div class="filter-actions"><el-button @click="resetSearch">重置</el-button><el-button type="primary" :icon="Search" @click="search">查询</el-button></div>
      </el-form>
    </el-card>
    <el-card shadow="never" class="records-card">
      <template #header>
        <div class="card-header">
          <span>评价记录列表</span>
          <div class="record-actions">
            <el-button :icon="Refresh" :loading="loading" @click="loadRecords">刷新</el-button>
            <el-button :icon="Download" :loading="exporting" @click="downloadTemplate">下载模板</el-button>
            <el-button :icon="Upload" @click="importVisible = true">Excel导入</el-button>
            <el-button type="success" plain @click="addRecord(true)">同步测试任务结果</el-button>
            <el-button type="primary" :icon="Plus" @click="addRecord()">新增评价记录</el-button>
          </div>
        </div>
      </template>
      <el-table v-loading="loading" :data="pageRecords" border row-key="id" empty-text="暂无评价记录，可新增记录或同步测试任务结果">
        <el-table-column prop="machineName" label="机床名称" min-width="140" show-overflow-tooltip />
        <el-table-column prop="machineNo" label="机床编号" min-width="160" show-overflow-tooltip />
        <el-table-column prop="machineModel" label="机床型号" min-width="120" show-overflow-tooltip />
        <el-table-column prop="controlSystem" label="数控系统" min-width="150" show-overflow-tooltip />
        <el-table-column prop="partName" label="零件名称" min-width="120" show-overflow-tooltip />
        <el-table-column prop="testDate" label="测试日期" width="115" />
        <el-table-column label="综合得分" width="100"><template #default="scope"><span :class="scope.row.totalScore == null ? 'text-info' : 'text-primary'">{{ formatScore(scope.row.totalScore) }}</span></template></el-table-column>
        <el-table-column label="数据来源" width="105"><template #default="scope"><el-tag size="small" :type="scope.row.dataSource === 'sync' ? 'success' : scope.row.dataSource === 'import' ? 'primary' : 'info'">{{ sourceLabels[scope.row.dataSource as keyof typeof sourceLabels] }}</el-tag></template></el-table-column>
        <el-table-column label="操作" fixed="right" width="220">
          <template #default="scope">
            <el-button type="primary" link @click="viewRecord(scope.row)">查看</el-button>
            <el-button type="primary" link @click="editRecord(scope.row)">编辑</el-button>
            <el-button type="success" link :disabled="exporting" @click="exportRecord(scope.row)">导出</el-button>
            <el-button type="danger" link @click="deleteRecord(scope.row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
      <div class="pagination-bar"><el-pagination v-model:current-page="currentPage" v-model:page-size="pageSize" :page-sizes="[10, 20, 50, 100]" layout="total, sizes, prev, pager, next" :total="filteredRecords.length" /></div>
    </el-card>
    <knowledge-editor v-model:visible="editorVisible" :draft="draft" :saving="saving" @save="saveRecord" @sync="taskVisible = true" />
    <knowledge-details v-model:visible="detailVisible" :record="detail" :exporting="exporting" @export="format => exportRecord(detail, format)" />
    <knowledge-transfer v-model:task-visible="taskVisible" v-model:import-visible="importVisible" :syncing="syncing" :category="draft?.category" @sync="syncTask" @imported="loadRecords" />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import { Download, Plus, Refresh, Search, Upload } from '@element-plus/icons-vue';
import knowledgeEditor from './evaluation/knowledgeEditor.vue';
import knowledgeDetails from './evaluation/knowledgeDetails.vue';
import knowledgeTransfer from './evaluation/knowledgeTransfer.vue';
import { basicFields, formatScore, sourceLabels } from './evaluation/knowledgeModel';
import { useKnowledgeRecords } from './evaluation/knowledgeState';
const { records, loading, saving, exporting, syncing, editorVisible, detailVisible, taskVisible, importVisible,
  draft, detail, loadRecords, addRecord, editRecord, viewRecord, openTaskDraft, saveRecord, deleteRecord, downloadTemplate, exportRecord, syncTask } = useKnowledgeRecords();
const route = useRoute();
watch(() => route.query.taskId, taskId => { if (typeof taskId === 'string' && taskId) void openTaskDraft(taskId); }, { immediate: true });
const searchFields = basicFields.slice(0, 6);
const filters = reactive<Record<string, string>>(Object.fromEntries(searchFields.map(field => [field.key, ''])));
const appliedFilters = ref<Record<string, string>>({});
const currentPage = ref(1);
const pageSize = ref(10);
const filteredRecords = computed(() => records.value.filter(record => searchFields.every(field => {
  const query = appliedFilters.value[field.key]?.trim().toLowerCase() ?? '';
  return !query || (record[field.key] ?? '').toLowerCase().includes(query);
})));
const pageRecords = computed(() => filteredRecords.value.slice((currentPage.value - 1) * pageSize.value, currentPage.value * pageSize.value));
const stats = computed(() => {
  const scored = records.value.filter(record => record.totalScore != null);
  const average = scored.length ? scored.reduce((sum, record) => sum + (record.totalScore ?? 0), 0) / scored.length : null;
  const now = new Date();
  const monthly = records.value.filter(record => {
    const date = new Date(record.createdAt);
    return date.getFullYear() === now.getFullYear() && date.getMonth() === now.getMonth();
  }).length;
  return [
    { label: '评价记录总数', value: records.value.length },
    { label: '平均得分', value: average == null ? '—' : average.toFixed(1) },
    { label: '覆盖机床型号', value: new Set(records.value.map(record => record.machineModel).filter(Boolean)).size },
    { label: '本月新增', value: monthly },
  ];
});
function search() { appliedFilters.value = { ...filters }; currentPage.value = 1; }
function resetSearch() { Object.keys(filters).forEach(key => filters[key] = ''); search(); }
watch([() => filteredRecords.value.length, pageSize], () => {
  currentPage.value = Math.min(currentPage.value, Math.max(1, Math.ceil(filteredRecords.value.length / pageSize.value)));
});
onMounted(loadRecords);
</script>

<style scoped>
.evaluation-knowledge-view { padding-bottom: 20px; }
.stats-grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 16px; margin-bottom: 20px; }
.stat-card span { color: var(--el-text-color-secondary); font-size: 13px; }
.stat-card strong { display: block; font-size: 28px; color: var(--el-color-primary); margin-top: 10px; font-weight: 600; }
.filter-card { margin-bottom: 20px; }
.filter-grid { display: grid; grid-template-columns: repeat(6, minmax(0, 1fr)); gap: 16px; }
.filter-actions, .pagination-bar { display: flex; justify-content: flex-end; }
.card-header { display: flex; align-items: center; justify-content: space-between; gap: 16px; flex-wrap: wrap; }
.record-actions { display: flex; gap: 10px; flex-wrap: wrap; }
.record-actions .el-button + .el-button { margin-left: 0; }
.pagination-bar { margin-top: 20px; overflow-x: auto; }
@media (max-width: 1400px) { .filter-grid { grid-template-columns: repeat(3, minmax(0, 1fr)); } }
@media (max-width: 760px) { .stats-grid, .filter-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
</style>
