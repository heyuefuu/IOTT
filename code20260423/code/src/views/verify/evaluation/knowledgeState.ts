import { ref, toRaw } from 'vue';
import { isAxiosError } from 'axios';
import { ElMessage, ElMessageBox } from 'element-plus';
import { evaluationApi, type EvaluationCategory, type KnowledgeRecord } from '@/api/evaluation';
import { downloadBlob } from '@/api/browserDownload';
import { basicFields, categoryLabels, createDraft, mergeTaskDraft } from './knowledgeModel';

function showError(error: unknown, fallback: string) {
  const message = isAxiosError(error) ? error.response?.data?.error ?? error.response?.data?.message ?? error.message : error instanceof Error ? error.message : fallback;
  if (isAxiosError(error) && error.response?.status === 409) {
    ElMessage.error(`${typeof message === 'string' ? message : '记录版本或任务运行来源冲突'} 当前草稿已保留，请核对最新记录或重新选择运行结果；不会覆盖历史记录。`);
    return;
  }
  ElMessage.error(typeof message === 'string' ? message : fallback);
}

export function useKnowledgeRecords() {
  const records = ref<KnowledgeRecord[]>([]);
  const loading = ref(false);
  const saving = ref(false);
  const exporting = ref(false);
  const syncing = ref(false);
  const categoryLoading = ref(false);
  const currentCategory = ref<EvaluationCategory>('machine');
  const editorVisible = ref(false);
  const detailVisible = ref(false);
  const taskVisible = ref(false);
  const importVisible = ref(false);
  const draft = ref<KnowledgeRecord | null>(null);
  const detail = ref<KnowledgeRecord | null>(null);
  // A category switch keeps the unsaved draft, including custom scores and weights.
  const categoryDrafts = new Map<EvaluationCategory, KnowledgeRecord>();
  function rememberDraft() {
    if (draft.value && !draft.value.id) categoryDrafts.set(draft.value.category, draft.value);
  }
  async function loadRecords() {
    loading.value = true;
    try { records.value = await evaluationApi.listRecords(); }
    catch (error) { showError(error, '加载评价记录失败'); }
    finally { loading.value = false; }
  }
  async function changeCategory(category: EvaluationCategory) {
    if (category === currentCategory.value || categoryLoading.value || syncing.value || saving.value) return;
    if (!editorVisible.value) { currentCategory.value = category; return; }
    if (draft.value?.id) return void ElMessage.warning('历史记录的评价类别和快照不可更改；可从新任务另存新评价。');
    categoryLoading.value = true;
    try {
      const next = categoryDrafts.get(category) ?? createDraft(await evaluationApi.getConfig(category));
      rememberDraft();
      draft.value = next;
      currentCategory.value = category;
      ElMessage.info('已切换评价类别，原类别草稿已保留，切回后可继续编辑。');
    } catch (error) { showError(error, '加载评价类别失败，原草稿已保留'); }
    finally { categoryLoading.value = false; }
  }
  async function addRecord(sync = false) {
    if (categoryLoading.value || syncing.value || saving.value) return;
    categoryLoading.value = true;
    try {
      rememberDraft();
      const cached = categoryDrafts.get(currentCategory.value);
      draft.value = cached ?? createDraft(await evaluationApi.getConfig(currentCategory.value));
      editorVisible.value = true;
      taskVisible.value = sync;
      if (cached) ElMessage.info('已恢复此类别的未保存草稿。');
    } catch (error) { showError(error, '加载评价指标失败'); }
    finally { categoryLoading.value = false; }
  }
  function editRecord(record: KnowledgeRecord) {
    rememberDraft();
    draft.value = structuredClone(toRaw(record));
    currentCategory.value = record.category;
    editorVisible.value = true;
  }
  function viewRecord(record: KnowledgeRecord) {
    detail.value = structuredClone(toRaw(record));
    detailVisible.value = true;
  }
  async function openTaskDraft(id: string) {
    // Deep links use the run's own category, not the default/current indicator configuration.
    await importTaskDraft(id);
  }
  async function saveRecord() {
    if (!draft.value || saving.value || syncing.value || categoryLoading.value) return;
    saving.value = true;
    try {
      for (const field of basicFields) draft.value[field.key] = draft.value[field.key]?.trim() ?? '';
      const saved = draft.value.id ? await evaluationApi.updateRecord(draft.value) : await evaluationApi.createRecord(draft.value);
      const index = records.value.findIndex(record => record.id === saved.id);
      if (index >= 0) records.value.splice(index, 1, saved); else records.value.unshift(saved);
      if (!draft.value.id) categoryDrafts.delete(saved.category);
      draft.value = null;
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
  async function downloadTemplate(category: EvaluationCategory = currentCategory.value) {
    exporting.value = true;
    try { downloadBlob(await evaluationApi.downloadTemplate(category), `${categoryLabels[category]}知识库导入模板.xlsx`); }
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
  async function syncTask(id: string, category: EvaluationCategory = draft.value?.category ?? currentCategory.value) {
    await importTaskDraft(id, category);
  }
  async function importTaskDraft(id: string, category?: EvaluationCategory) {
    if (syncing.value || saving.value || categoryLoading.value) return;
    syncing.value = true;
    const previous = draft.value;
    try {
      const synced = await evaluationApi.taskDraft(id, category);
      if (synced.dataSource !== 'sync' || synced.syncTaskId !== id || !synced.syncRunId) {
        throw new Error('任务草稿缺少有效运行来源，未修改当前评价。');
      }
      if (draft.value !== previous) return;
      const merged = previous ? mergeTaskDraft(previous, synced) : null;
      if (previous) {
        const lost = merged!.lostItems;
        const message = [
          previous.id ? '将另存为一条新评价，原记录及其历史快照、来源保持不变。' : '将以所选任务已结束运行的真实快照和来源更新当前未保存草稿。',
          '保留基础信息、结论建议，并按稳定项目 ID 保留非自动项目的人工分、测试结果和备注；自动项目使用本轮分数（含 0 和未评分），权重采用新快照。',
          lost.length ? `以下人工项目无法映射，不会带入新评价：${lost.join('、')}。` : '',
          previous.category !== synced.category ? `评价类别将切换为${categoryLabels[synced.category]}。` : '',
        ].filter(Boolean).join('\n');
        await ElMessageBox.confirm(message, previous.id ? '另存新评价，保留原记录' : '同步已结束运行', {
          type: 'warning', confirmButtonText: previous.id ? '生成新评价草稿' : '同步到草稿', cancelButtonText: '保留当前草稿',
        });
      }
      if (draft.value !== previous) return;
      if (previous?.category !== synced.category) rememberDraft();
      draft.value = merged?.target ?? synced;
      currentCategory.value = synced.category;
      editorVisible.value = true;
      taskVisible.value = false;
      const count = merged?.syncedCount ?? Object.keys(synced.testResults).length;
      ElMessage.success(`已同步 ${count} 项任务结果${previous?.id ? '到新评价草稿，原记录未变' : ''}，请补充基础信息并完成评价后保存`);
    } catch (error) { if (error !== 'cancel' && error !== 'close') showError(error, '同步测试任务失败'); }
    finally { syncing.value = false; }
  }
  return { records, loading, saving, exporting, syncing, categoryLoading, currentCategory, editorVisible, detailVisible, taskVisible, importVisible,
    draft, detail, loadRecords, changeCategory, addRecord, editRecord, viewRecord, openTaskDraft, saveRecord, deleteRecord, downloadTemplate, exportRecord, syncTask };
}
