import type { EvaluationConfig, KnowledgeRecord } from '@/api/evaluation';

export const basicFields = [
  { key: 'machineName', label: '机床名称', required: true },
  { key: 'machineNo', label: '机床编号', required: true },
  { key: 'machineModel', label: '机床型号', required: true },
  { key: 'controlSystem', label: '数控系统', required: true },
  { key: 'partName', label: '零件名称', required: true },
  { key: 'partFeature', label: '零件特征', required: false },
  { key: 'testLocation', label: '测试地点', required: false },
  { key: 'testDate', label: '测试日期', required: true },
  { key: 'tester', label: '测试人员', required: false },
] as const;

export const sourceLabels = { manual: '手工录入', sync: '任务同步', import: 'Excel导入' };
export const categoryLabels = { machine: '机床评价', machining: '加工评价' };
export const scoreOptions = [
  { value: 100, label: '100 · 完全满足' },
  { value: 80, label: '80 · 基本满足' },
  { value: 60, label: '60 · 部分满足' },
  { value: 0, label: '0 · 不满足' },
];

/** Build a new record from the new run; never carry a historical snapshot or provenance forward. */
export function mergeTaskDraft(previous: KnowledgeRecord, synced: KnowledgeRecord) {
  const target = structuredClone(synced);
  target.id = '';
  target.version = 0;
  target.createdAt = '';
  target.updatedAt = '';
  for (const field of basicFields) {
    if (previous[field.key]?.trim()) target[field.key] = previous[field.key];
  }
  target.conclusion = previous.conclusion;
  target.suggestion = previous.suggestion;
  const oldManualItems = previous.snapshot.indicators.flatMap(section => section.children)
    .flatMap(child => child.items).filter(item => !item.metricId);
  const newItems = target.snapshot.indicators.flatMap(section => section.children).flatMap(child => child.items);
  const manualIds = new Set(newItems.filter(item => !item.metricId).map(item => item.id));
  const lostItems: string[] = [];
  for (const item of oldManualItems) {
    if (manualIds.has(item.id)) {
      target.scores[item.id] = previous.scores[item.id] ?? null;
      target.testResults[item.id] = previous.testResults[item.id] ?? '';
      target.remarks[item.id] = previous.remarks[item.id] ?? '';
    } else if (previous.scores[item.id] != null || previous.testResults[item.id] || previous.remarks[item.id]) {
      lostItems.push(item.name);
    }
  }
  const syncedCount = newItems.filter(item => item.metricId && Object.prototype.hasOwnProperty.call(target.testResults, item.id)).length;
  return { target, syncedCount, lostItems };
}

export function createDraft(config: EvaluationConfig): KnowledgeRecord {
  const now = new Date();
  const localDate = new Date(now.getTime() - now.getTimezoneOffset() * 60_000).toISOString().slice(0, 10);
  const draft: KnowledgeRecord = {
    id: '', version: 0, category: config.category, snapshot: structuredClone(config),
    machineName: '', machineNo: '', machineModel: '', controlSystem: '', partName: '',
    partFeature: '', testLocation: '', testDate: localDate, tester: '', dataSource: 'manual',
    categoryWeights: {}, subCategoryWeights: {}, scores: {}, weights: {}, testResults: {}, remarks: {},
    conclusion: '', suggestion: '', syncTaskId: null, totalScore: null, createdAt: '', updatedAt: '',
  };
  for (const category of config.indicators) {
    draft.categoryWeights[category.id] = category.weight;
    for (const subcategory of category.children) {
      draft.subCategoryWeights[subcategory.id] = subcategory.weight;
      for (const item of subcategory.items) {
        draft.scores[item.id] = null;
        draft.weights[item.id] = item.weight;
        draft.testResults[item.id] = '';
        draft.remarks[item.id] = '';
      }
    }
  }
  return draft;
}

export function scoreSummary(record: KnowledgeRecord) {
  const subScores: Record<string, number | null> = {};
  const categoryScores: Record<string, number | null> = {};
  const categoryContributions: Record<string, number | null> = {};
  let total = 0;
  let pending = false;
  for (const category of record.snapshot.indicators) {
    let categoryScore = 0;
    let categoryPending = false;
    for (const subcategory of category.children) {
      let subScore = 0;
      let weightSum = 0;
      let subPending = false;
      for (const item of subcategory.items) {
        const weight = record.weights[item.id] ?? item.weight;
        const score = record.scores[item.id];
        if (weight > 0 && score == null) subPending = true;
        subScore += (score ?? 0) * weight;
        weightSum += weight;
      }
      subScores[subcategory.id] = subPending ? null : weightSum > 0 ? subScore / weightSum : 0;
      const weight = record.subCategoryWeights[subcategory.id] ?? subcategory.weight;
      if (weight > 0 && subPending) categoryPending = true;
      categoryScore += (subScores[subcategory.id] ?? 0) * weight / 100;
    }
    categoryScores[category.id] = categoryPending ? null : categoryScore;
    const weight = record.categoryWeights[category.id] ?? category.weight;
    if (weight > 0 && categoryPending) pending = true;
    categoryContributions[category.id] = weight === 0 ? 0 : categoryPending ? null : categoryScore * weight / 100;
    total += categoryScore * weight / 100;
  }
  return { total: pending ? null : Math.round(total * 10) / 10, subScores, categoryScores, categoryContributions };
}

export function weightErrors(record: KnowledgeRecord): string[] {
  const errors: string[] = [];
  const check = (label: string, values: number[]) => {
    if (values.some(value => !Number.isFinite(value) || value < 0 || value > 100)) {
      errors.push(`${label}需为0–100之间的数字`);
    } else if (Math.abs(values.reduce((sum, value) => sum + value, 0) - 100) > 0.1) {
      errors.push(`${label}之和需为100%`);
    }
  };
  check('一级权重', record.snapshot.indicators.map(category => record.categoryWeights[category.id] ?? category.weight));
  for (const category of record.snapshot.indicators) {
    check(`${category.name}二级权重`, category.children.map(sub => record.subCategoryWeights[sub.id] ?? sub.weight));
    for (const subcategory of category.children) {
      check(`${subcategory.name}项目权重`, subcategory.items.map(item => record.weights[item.id] ?? item.weight));
    }
  }
  return errors;
}

export function formatScore(score: number | null | undefined, digits = 1): string {
  return score == null || !Number.isFinite(score) ? '待评分' : score.toFixed(digits);
}

export function gradeLabel(score: number | null | undefined): string {
  if (score == null) return '待评分';
  return score >= 90 ? '优秀' : score >= 80 ? '良好' : score >= 60 ? '合格' : '不合格';
}
