<template>
  <div class="report-template-select">
    <span>Word/PDF 模板</span>
    <el-select :model-value="modelValue" :disabled="disabled" :loading="loading" clearable placeholder="默认报告" @update:model-value="$emit('update:modelValue', $event || '')">
      <el-option v-for="item in templates" :key="item.id" :value="item.id" :label="item.name" />
      <el-option v-if="modelValue && !templates.some(item => item.id === modelValue)" :value="modelValue" label="当前模板不可用，请重新选择" disabled />
    </el-select>
    <el-button :disabled="disabled" :loading="loading" text @click="loadTemplates">刷新模板</el-button>
    <span v-if="error" class="template-error" role="alert">{{ error }}</span>
    <span v-else-if="!loading && !templates.length" class="template-hint">暂无自定义模板，可在报告模板管理中新增。</span>
  </div>
</template>

<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import { businessValidationApi, type ReportTemplateDto } from '@/api/businessValidation';
defineProps<{ modelValue: string; disabled?: boolean }>();
defineEmits<{ 'update:modelValue': [value: string] }>();
const templates = ref<ReportTemplateDto[]>([]);
const loading = ref(false);
const error = ref('');
let requestVersion = 0;
async function loadTemplates() {
  const version = ++requestVersion;
  loading.value = true;
  error.value = '';
  try {
    const result = await businessValidationApi.listTemplates();
    if (version === requestVersion) templates.value = result;
  } catch {
    if (version === requestVersion) error.value = '模板列表加载失败，可刷新重试；未选模板时使用默认报告。';
  } finally { if (version === requestVersion) loading.value = false; }
}
onMounted(loadTemplates);
onBeforeUnmount(() => { requestVersion++; });
</script>

<style scoped>
.report-template-select { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin: 12px 0; }
.el-select { width: 240px; }
.template-error { color: var(--el-color-danger); }
.template-hint { color: var(--el-text-color-secondary); }
</style>
