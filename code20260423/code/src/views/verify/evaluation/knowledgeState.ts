import { ref, toRaw } from 'vue';
import { isAxiosError } from 'axios';
import { ElMessage, ElMessageBox } from 'element-plus';
import { evaluationApi, type KnowledgeRecord } from '@/api/evaluation';
import { downloadBlob } from '@/api/browserDownload';
import { basicFields, createDraft } from './knowledgeModel';

function showError(error: unknown, fallback: string) {
  if (isAxiosError(error) && error.response?.status === 409) {
    ElMessage.error('记录已被其他人更新。当前草稿已保留，请刷新列表并核对最新记录后再保存。');
    return;
  }
  const message = isAxiosError(error) ? error.response?.data?.error ?? error.response?.data?.message ?? error.message : error instanceof Error ? error.message : fallback;
  ElMessage.error(typeof message === 'string' ? message : fallback);
}

export function useKnowledgeRecords() {
  const records = ref<KnowledgeRecord[]>([]);
  const loading = ref(false);
  const saving = ref(false);
  const exporting = ref(false);
  const syncing = ref(false);
  const editorVisible = ref(false);
  const detailVisible = ref(false);
  const taskVisible = ref(false);
  const importVisible = ref(false);
  const draft = ref<KnowledgeRecord | null>(null);
  const detail = ref<KnowledgeRecord | null>(null);
  async function loadRecords() {
    loading.value = true;
    try { records.value = await evaluationApi.listRecords(); }
    catch (error) { showError(error, '加载评价记录失败'); }
    finally { loading.value = false; }
  }
  async function addRecord(syncTask = false) {
    loading.value = true;
    try {
      draft.value = createDraft(await evaluationApi.getConfig('machine'));
      editorVisible.value = true;
      taskVisible.value = syncTask;
    } catch (error) { showError(error, '加载评价指标失败'); }
    finally { loading.value = false; }
  }
  function editRecord(record: KnowledgeRecord) {
    draft.value = structuredClone(toRaw(record));
    editorVisible.value = true;
  }
  function viewRecord(record: KnowledgeRecord) {
    detail.value = structuredClone(toRaw(record));
    detailVisible.value = true;
  }
  async function openTaskDraft(id: string) {
    loading.value = true;
    try {
      draft.value = await evaluationApi.taskDraft(id);
      editorVisible.value = true;
      ElMessage.success('已导入任务实测结果，请补充基础信息并完成评价后保存');
    } catch (error) { showError(error, '导入测试任务结果失败'); }
    finally { loading.value = false; }
  }
  async function saveRecord() {
    if (!draft.value) return;
    saving.value = true;
    try {
      for (const field of basicFields) draft.value[field.key] = draft.value[field.key]?.trim() ?? '';
      const saved = draft.value.id ? await evaluationApi.updateRecord(draft.value) : await evaluationApi.createRecord(draft.value);
      const index = records.value.findIndex(record => record.id === saved.id);
      if (index >= 0) records.value.splice(index, 1, saved); else records.value.unshift(saved);
      editorVisible.value = false;
      ElMessage.success('评价记录已保存');
    } catch (error) { showError(error, '保存评价记录失败'); }
    finally { saving.value = false; }
  }
  async function deleteRecord(record: KnowledgeRecord) {
    try {
      await ElMessageBox.confirm(`确定删除“${record.machineName}”的这条评价记录吗？`, '删除评价记录', { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' });
      await evaluationApi.deleteRecord(record.id);
      records.value = records.value.filter(item => item.id !== record.id);
      ElMessage.success('评价记录已删除');
    } catch (error) { if (error !== 'cancel' && error !== 'close') showError(error, '删除评价记录失败'); }
  }
  async function downloadTemplate() {
    exporting.value = true;
    try { downloadBlob(await evaluationApi.downloadTemplate(), '评价知识库导入模板.xlsx'); }
    catch (error) { showError(error, '下载模板失败'); }
    finally { exporting.value = false; }
  }
  async function exportRecord(record: KnowledgeRecord | null, format: 'xlsx' | 'pdf' = 'xlsx') {
    if (!record) return;
    exporting.value = true;
    try {
      const name = record.machineName.replace(/[\\/:*?"<>|]/g, '_');
      downloadBlob(await evaluationApi.exportRecord(record.id, format), `${name}_评价报告.${format}`);
    } catch (error) { showError(error, '导出评价报告失败'); }
    finally { exporting.value = false; }
  }
  async function syncTask(id: string) {
    if (!draft.value || draft.value.id) return;
    syncing.value = true;
    try {
      const synced = await evaluationApi.taskDraft(id, draft.value.category);
      const previous = draft.value;
      const target = synced;
      for (const field of basicFields) {
        if (previous[field.key]?.trim()) target[field.key] = previous[field.key];
      }
      target.conclusion = previous.conclusion;
      target.suggestion = previous.suggestion;
      let count = 0;
      for (const category of target.snapshot.indicators) {
        for (const subcategory of category.children) {
          for (const item of subcategory.items) {
            if (item.metricId) {
              if (target.testResults[item.id]) count += 1;
            } else if (item.id in previous.scores) {
              target.scores[item.id] = previous.scores[item.id] ?? null;
              target.testResults[item.id] = previous.testResults[item.id] || target.testResults[item.id] || '';
              target.remarks[item.id] = previous.remarks[item.id] || target.remarks[item.id] || '';
            }
          }
        }
      }
      if (!count) return void ElMessage.warning('该任务没有与当前评价指标匹配的通讯实测结果');
      target.syncTaskId = id;
      target.syncRunId = synced.syncRunId;
      target.dataSource = 'sync';
      draft.value = target;
      taskVisible.value = false;
      ElMessage.success(`已同步 ${count} 项通讯实测结果，请核实评分后保存`);
    } catch (error) { showError(error, '同步测试任务失败'); }
    finally { syncing.value = false; }
  }
  return { records, loading, saving, exporting, syncing, editorVisible, detailVisible, taskVisible, importVisible,
    draft, detail, loadRecords, addRecord, editRecord, viewRecord, openTaskDraft, saveRecord, deleteRecord, downloadTemplate, exportRecord, syncTask };
}
