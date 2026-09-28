<template>
  <div class="knowledge-scores">
    <el-alert v-if="!readonly && errors.length" type="warning" :closable="false" show-icon :title="errors.join('；')" />
    <p class="score-help">任务均分是该次所选测试项的简单平均，任一项未评分则为空；通讯类分按本评价快照的项目、二级权重计算，并非任务均分。分类加权贡献 = 分类得分 × 一级权重，所有分类贡献之和为综合分；正权重项未评分时不计算综合分，零权重不影响综合分。</p>
    <section v-for="category in record.snapshot.indicators" :key="category.id" class="score-category">
      <div class="category-header">
        <strong>{{ category.name }}</strong>
        <div class="weight-control">
          <span>一级权重</span>
          <span v-if="readonly">{{ record.categoryWeights[category.id] ?? category.weight }}%</span>
          <el-input-number v-else v-model="record.categoryWeights[category.id]" :min="0" :max="100" :precision="2" size="small" controls-position="right" />
          <span v-if="!readonly">%</span>
        </div>
        <span class="subtotal">分类得分 {{ formatScore(summary.categoryScores[category.id]) }} · 加权贡献 {{ formatScore(summary.categoryContributions[category.id]) }}</span>
      </div>
      <div v-for="subcategory in category.children" :key="subcategory.id" class="subcategory">
        <div class="subcategory-header">
          <strong>{{ subcategory.name }}</strong>
          <div class="weight-control">
            <span>二级权重</span>
            <span v-if="readonly">{{ record.subCategoryWeights[subcategory.id] ?? subcategory.weight }}%</span>
            <el-input-number v-else v-model="record.subCategoryWeights[subcategory.id]" :min="0" :max="100" :precision="2" size="small" controls-position="right" />
            <span v-if="!readonly">%</span>
          </div>
          <el-button v-if="!readonly && canSync !== false && subcategory.items.some(item => Boolean(item.metricId))" type="success" link @click="$emit('sync')">从测试任务同步通讯数据</el-button>
          <span class="subtotal">小计 {{ formatScore(summary.subScores[subcategory.id]) }}</span>
        </div>
        <el-table :data="subcategory.items" border size="small" class="score-table">
          <el-table-column type="index" label="序号" width="60" />
          <el-table-column prop="name" label="评价项目" min-width="230">
            <template #default="scope">
              <el-tooltip v-if="scope.row.method || scope.row.desc" :content="scope.row.method || scope.row.desc" placement="top" :show-after="300">
                <span class="item-name">{{ scope.row.name }}</span>
              </el-tooltip>
              <span v-else>{{ scope.row.name }}</span>
              <div class="item-threshold">达标阈值：{{ thresholdSummary(scope.row) }}</div>
            </template>
          </el-table-column>
          <el-table-column label="得分 (0–100)" width="210">
            <template #default="scope">
              <span v-if="readonly">{{ formatScore(record.scores[scope.row.id]) }}</span>
              <div v-else class="score-inputs">
                <el-select :model-value="record.scores[scope.row.id] ?? undefined" clearable placeholder="快捷评分 / 待评分" aria-label="文字评分档位" @update:model-value="setScore(scope.row.id, $event)">
                  <el-option v-for="option in scoreOptions" :key="option.value" :value="option.value" :label="option.label" />
                  <el-option v-if="isCustomScore(record.scores[scope.row.id])" :value="record.scores[scope.row.id]!" :label="`${record.scores[scope.row.id]} · 自定义`" />
                </el-select>
                <el-input-number :model-value="record.scores[scope.row.id] ?? undefined" :min="0" :max="100" controls-position="right" placeholder="自定义小数分 / 留空" aria-label="自定义得分" @update:model-value="setScore(scope.row.id, $event)" />
              </div>
            </template>
          </el-table-column>
          <el-table-column label="权重 (%)" width="140">
            <template #default="scope">
              <span v-if="readonly">{{ record.weights[scope.row.id] ?? scope.row.weight }}%</span>
              <el-input-number v-else v-model="record.weights[scope.row.id]" :min="0" :max="100" :precision="2" controls-position="right" />
            </template>
          </el-table-column>
          <el-table-column label="测试结果（数据）" min-width="220">
            <template #default="scope">
              <span v-if="readonly" class="multiline">{{ record.testResults[scope.row.id] || '—' }}</span>
              <el-input v-else v-model="record.testResults[scope.row.id]" type="textarea" :autosize="{ minRows: 1, maxRows: 4 }" placeholder="填写实际测试数据" />
            </template>
          </el-table-column>
          <el-table-column label="备注（说明）" min-width="180">
            <template #default="scope">
              <span v-if="readonly" class="multiline">{{ record.remarks[scope.row.id] || '—' }}</span>
              <el-input v-else v-model="record.remarks[scope.row.id]" placeholder="填写备注说明" />
            </template>
          </el-table-column>
        </el-table>
      </div>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import type { KnowledgeRecord } from '@/api/evaluation';
import { formatScore, scoreOptions, scoreSummary, weightErrors } from './knowledgeModel';
import { thresholdSummary } from './automationRules';
const props = defineProps<{ record: KnowledgeRecord; readonly?: boolean; canSync?: boolean }>();
defineEmits<{ sync: [] }>();
const summary = computed(() => scoreSummary(props.record));
const errors = computed(() => weightErrors(props.record));
function setScore(id: string, value: unknown) { props.record.scores[id] = typeof value === 'number' ? value : null; }
function isCustomScore(value: number | null | undefined) { return value != null && !scoreOptions.some(option => option.value === value); }
</script>

<style scoped>
.score-help { color: var(--el-text-color-secondary); font-size: 13px; line-height: 1.6; }
.score-category { border: 1px solid var(--el-border-color); border-radius: 6px; overflow: hidden; margin-top: 16px; }
.category-header, .subcategory-header { display: flex; align-items: center; gap: 16px; flex-wrap: wrap; padding: 12px 16px; }
.category-header { background: var(--el-color-primary-light-9); }
.subcategory-header { background: var(--el-fill-color-light); font-size: 13px; }
.weight-control { display: flex; align-items: center; gap: 8px; color: var(--el-text-color-regular); }
.weight-control .el-input-number { width: 105px; }
.subtotal { margin-left: auto; color: var(--el-text-color-secondary); font-size: 13px; }
.score-table { width: 100%; }
.score-table :deep(.el-input-number) { width: 100%; }
.score-inputs { display: grid; gap: 6px; }
.item-name { text-decoration: underline dotted var(--el-border-color); text-underline-offset: 4px; }
.item-threshold { margin-top: 4px; color: var(--el-text-color-secondary); font-size: 12px; overflow-wrap: anywhere; }
.multiline { white-space: pre-wrap; overflow-wrap: anywhere; }
</style>
