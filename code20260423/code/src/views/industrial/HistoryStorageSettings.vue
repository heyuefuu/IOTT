<template>
    <el-dialog v-model="visible" title="历史库设置" width="560px" append-to-body>
        <el-form v-loading="pending === 'load'" :model="form" label-width="100px"
            :disabled="!!pending || !loaded">
            <el-form-item label="保存历史数据">
                <el-switch v-model="form.enabled" />
            </el-form-item>
            <el-form-item label="部署位置">
                <el-radio-group :model-value="location" @change="changeLocation">
                    <el-radio value="local">本地</el-radio>
                    <el-radio value="remote">其他服务器</el-radio>
                </el-radio-group>
            </el-form-item>
            <p class="storage-hint">本地指运行后端服务的电脑。切换历史库后，历史记录从所选库查询。</p>
            <el-form-item label="地址" required>
                <el-input v-model="form.url" :placeholder="location === 'local' ? localUrl : 'http://服务器地址:8181'" />
            </el-form-item>
            <el-form-item label="数据库名称" required>
                <el-input v-model="form.bucket" placeholder="machine_collection" />
            </el-form-item>
            <el-form-item label="组织" required>
                <el-input v-model="form.org" />
            </el-form-item>
            <el-form-item label="数据表名称" required>
                <el-input v-model="form.measurement" placeholder="datapoint" />
            </el-form-item>
            <el-form-item label="Token">
                <el-input v-model="form.token" type="password" show-password autocomplete="new-password"
                    :placeholder="hasToken ? '已配置，留空保留现有 Token' : '首次使用，请填写数据库 Token'" />
            </el-form-item>
            <p v-if="!hasToken" class="storage-hint">尚未配置 Token，启用历史数据保存前需要填写。</p>
        </el-form>
        <el-alert v-if="feedback" :title="feedback.message" :type="feedback.success ? 'success' : 'error'"
            :closable="false" show-icon />
        <template #footer>
            <el-button @click="visible = false">关闭</el-button>
            <el-button :loading="pending === 'test'" :disabled="!!pending || !loaded" @click="submitSettings('test')">
                测试连接
            </el-button>
            <el-button type="primary" :loading="pending === 'save'" :disabled="!!pending || !loaded"
                @click="submitSettings('save')">保存</el-button>
        </template>
    </el-dialog>
</template>

<script setup lang="ts">
import { ref, watch } from "vue";
import { isAxiosError } from "axios";
import {
    telemetryInfluxApi,
    type InfluxStorageSettings,
    type InfluxStorageSettingsInput,
} from "@/api/telemetryInflux";

const visible = defineModel<boolean>({ required: true });
const localUrl = "http://127.0.0.1:8181";
const location = ref<"local" | "remote">("local");
const locationUrls = { local: localUrl, remote: "" };
const form = ref<InfluxStorageSettingsInput & { token: string }>({
    enabled: true, url: localUrl, org: "", bucket: "", measurement: "", token: "",
});
const hasToken = ref(false);
const loaded = ref(false);
const pending = ref<"" | "load" | "test" | "save">("");
const feedback = ref<{ success: boolean; message: string } | null>(null);

function applySettings(settings: InfluxStorageSettings) {
    const { hasToken: tokenConfigured, ...fields } = settings;
    form.value = { ...fields, token: "" };
    hasToken.value = tokenConfigured;
    try {
        const hostname = new URL(settings.url).hostname;
        location.value = ["127.0.0.1", "localhost", "[::1]"].includes(hostname) ? "local" : "remote";
    } catch {
        location.value = "remote";
    }
    locationUrls[location.value] = settings.url;
}

function changeLocation(value: unknown) {
    locationUrls[location.value] = form.value.url;
    location.value = value === "remote" ? "remote" : "local";
    form.value.url = locationUrls[location.value];
}

function errorMessage(error: unknown, fallback: string): string {
    if (isAxiosError<{ error?: string; message?: string }>(error)) {
        return error.response?.data?.error || error.response?.data?.message || error.message || fallback;
    }
    return error instanceof Error ? error.message : fallback;
}

async function loadSettings() {
    pending.value = "load";
    loaded.value = false;
    feedback.value = null;
    try {
        applySettings(await telemetryInfluxApi.getSettings());
        loaded.value = true;
    } catch (error: unknown) {
        feedback.value = { success: false, message: errorMessage(error, "读取历史库配置失败") };
    } finally {
        pending.value = "";
    }
}

function buildSettingsRequest(action: "test" | "save"): InfluxStorageSettingsInput {
    let address: URL;
    try {
        address = new URL(form.value.url.trim());
    } catch {
        throw new Error("请输入完整的 HTTP 或 HTTPS 地址");
    }
    if (!["http:", "https:"].includes(address.protocol) || address.username || address.password
        || address.search || address.hash) {
        throw new Error("地址仅支持 HTTP 或 HTTPS，不包含账号密码、查询参数或片段");
    }
    const org = form.value.org.trim();
    const bucket = form.value.bucket.trim();
    const measurement = form.value.measurement.trim();
    if (!org || !bucket) throw new Error("请填写组织和数据库名称");
    if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(measurement)) {
        throw new Error("数据表名称仅使用字母、数字和下划线，且以字母或下划线开头");
    }
    const token = form.value.token.trim();
    if ((form.value.enabled || action === "test") && !hasToken.value && !token) {
        throw new Error("首次使用，请填写数据库 Token");
    }
    return {
        enabled: form.value.enabled, url: form.value.url.trim().replace(/\/+$/, ""),
        org, bucket, measurement, ...(token ? { token } : {}),
    };
}

async function submitSettings(action: "test" | "save") {
    if (pending.value || !loaded.value) return;
    feedback.value = null;
    try {
        const request = buildSettingsRequest(action);
        pending.value = action;
        if (action === "test") {
            feedback.value = await telemetryInfluxApi.testSettings(request);
        } else {
            applySettings(await telemetryInfluxApi.saveSettings(request));
            feedback.value = {
                success: true,
                message: request.enabled
                    ? "历史库设置已保存，后续采集使用此配置。"
                    : "历史数据保存已关闭，实时采集可继续运行。",
            };
        }
    } catch (error: unknown) {
        feedback.value = { success: false, message: errorMessage(error, "历史库操作失败") };
    } finally {
        pending.value = "";
        if (!visible.value) form.value.token = "";
    }
}

watch(form, () => { feedback.value = null; }, { deep: true, flush: "sync" });
watch(visible, (isOpen) => {
    if (isOpen) void loadSettings();
    else form.value.token = "";
}, { immediate: true });
</script>

<style scoped>
.storage-hint {
    margin: 0 0 16px 100px;
    color: var(--el-text-color-secondary);
    font-size: 13px;
    line-height: 1.5;
}
</style>
