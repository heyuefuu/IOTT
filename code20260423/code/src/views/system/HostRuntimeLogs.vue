<template>
  <el-collapse @change="openChanged">
    <el-collapse-item title="Host 连接、采集与传输运行日志" name="host">
      <el-alert title="日志来自 Host 实际滚动文件；按文件日期倒序、文件内时间正序显示。保留范围由 Host 配置决定。" type="info" :closable="false" />
      <el-form inline @submit.prevent="search">
        <el-form-item label="关键词"><el-input v-model="keyword" clearable /></el-form-item>
        <el-form-item label="级别"><el-select v-model="level" clearable style="width:120px"><el-option v-for="item in levels" :key="item" :label="item" :value="item" /></el-select></el-form-item>
        <el-button :loading="loading" @click="search">查询运行日志</el-button>
      </el-form>
      <el-alert v-if="error" :title="error" type="error" :closable="false" />
      <el-table v-loading="loading" :data="rows" border>
        <el-table-column prop="timestamp" label="时间" width="230" />
        <el-table-column prop="level" label="级别" width="75" />
        <el-table-column prop="message" label="详细记录"><template #default="scope"><pre style="white-space:pre-wrap;margin:0">{{ scope.row.message }}</pre></template></el-table-column>
      </el-table>
      <el-pagination v-model:current-page="page" :page-size="50" :total="total" layout="total, prev, pager, next" @current-change="load" />
    </el-collapse-item>
  </el-collapse>
</template>

<script setup lang="ts">
import { ref } from "vue";
import { createMachineConnectionClient } from "@/api/machineConnectionClient";
const client = createMachineConnectionClient({ baseURL: import.meta.env.VITE_MACHINE_CONNECTION_API ?? "/machine-connection", timeout: 30000 });
const rows = ref<{ id: string; timestamp: string; level: string; message: string }[]>([]);
const levels = ["INF", "WRN", "ERR", "FTL", "DBG", "VRB"];
const keyword = ref("");
const level = ref("");
const page = ref(1);
const total = ref(0);
const loading = ref(false);
const error = ref("");
let version = 0;
async function load() {
  const current = ++version;
  loading.value = true;
  error.value = "";
  try {
    const response = await client.get("/api/system/logs/host", { params: { keyword: keyword.value, level: level.value, page: page.value, pageSize: 50 } });
    if (current !== version) return;
    rows.value = response.data.items;
    total.value = response.data.total;
  } catch (reason: unknown) {
    if (current !== version) return;
    const detail = reason as { response?: { data?: { error?: string } }; message?: string };
    error.value = detail.response?.data?.error || detail.message || "Host 运行日志读取失败";
    rows.value = [];
    total.value = 0;
  } finally { if (current === version) loading.value = false; }
}
function search() { page.value = 1; void load(); }
function openChanged(names: string | number | (string | number)[]) {
  if (Array.isArray(names) && names.includes("host") && !rows.value.length && !loading.value) void load();
}
</script>
