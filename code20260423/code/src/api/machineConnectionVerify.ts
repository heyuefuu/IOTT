import { createMachineConnectionClient } from "./machineConnectionClient";
import type { EvaluationCategory, EvaluationConfig } from "./evaluation";

const baseURL =
    import.meta.env.VITE_MACHINE_CONNECTION_API ?? "/machine-connection";

const client = createMachineConnectionClient({
    baseURL,
    timeout: 300_000,
    headers: { "Content-Type": "application/json" },
});

export interface VerifyRunRequest {
    taskId?: string;
    taskName?: string;
    deviceId?: string;
    evaluationCategory?: EvaluationCategory;
    metricIds: string[];
    options?: VerifyRunOptions;
}

export interface VerifyRunOptions {
    communicationRounds?: number;
    probeTimeoutMs?: number;
    maxParallelTargets?: number;
    requiredMinConcurrentSuccess?: number;
    concurrentDeviceIds?: string[] | null;
    /** Explicit permission for isolated file-test writes; never authorizes NC execution. */
    allowFileWrites?: boolean;
}

export interface VerifyMetricResult {
    metricId: string;
    code: string;
    name: string;
    status: "passed" | "failed" | "unrated" | "error" | "pending" | "running";
    result: string;
    value: string;
    reference: string;
    detail: string;
    evidence: string[];
    measurement?: number | null;
    unit?: string;
    score?: number | null;
    scoreReason?: string;
    measurementSource?: string;
    executionStatus?: "pending" | "running" | "completed" | "error" | "skipped";
    progressPercent?: number;
}

export interface VerifyRunResponse {
    runId: string;
    taskId?: string;
    taskName: string;
    status: "completed" | "failed" | "pending" | "running";
    result: string;
    detail: string;
    startedAt: string;
    completedAt: string;
    metrics: VerifyMetricResult[];
    totalScore?: number | null;
    scoreSummary?: string;
    progressPercent?: number;
    currentMetricId?: string | null;
    completedMetricCount?: number;
    totalMetricCount?: number;
    optionsSnapshot?: VerifyRunOptions | null;
    evaluationSnapshot?: EvaluationConfig;
    deviceId?: string;
    machineSnapshot?: {
        id: string; name: string; deviceCode: string; model: string; controlSystem: string;
        host: string; port: number; protocol: string; connectTimeoutMs: number; readTimeoutMs: number;
    };
}

export interface VerifyTaskDto {
    id: string;
    name: string;
    type: string;
    status: string;
    priority: string;
    deviceId: string;
    machineId: string;
    evaluationCategory?: EvaluationCategory;
    metricIds: string[];
    options?: VerifyRunOptions | null;
    /** 本轮运行快照；不得用 lastRunJson 的上一轮数据冒充实时进度。 */
    currentRunJson?: string;
    activeRunId?: string | null;
    params: string;
    description: string;
    createdAt: string;
    completedAt?: string | null;
    executionTime: string;
    result: string;
    detail: string;
    /** none = 手动执行；daily = 每天 scheduleTime（HH:mm）由网关后台自动执行 */
    scheduleType?: string;
    scheduleTime?: string;
    lastAutoRunAt?: string | null;
    /** 最近一次运行完整结果（VerifyRunResponse JSON），为空表示从未运行 */
    lastRunJson?: string;
}

const enc = encodeURIComponent;

export interface VerifyTaskRunResult {
    taskId: string;
    task: VerifyTaskDto | null;
    error: string | null;
}

export const machineConnectionVerifyApi = {
    async run(body: VerifyRunRequest): Promise<VerifyRunResponse> {
        const res = await client.post<VerifyRunResponse>("/api/verify/run", body, { timeout: 2 * 60 * 60_000 });
        return res.data;
    },

    async listTasks(signal?: AbortSignal): Promise<VerifyTaskDto[]> {
        const res = await client.get<VerifyTaskDto[]>("/api/verify/tasks", { signal, timeout: 30_000 });
        return res.data ?? [];
    },

    async createTask(task: VerifyTaskDto): Promise<VerifyTaskDto> {
        const res = await client.post<VerifyTaskDto>("/api/verify/tasks", task);
        return res.data;
    },

    async updateTask(id: string, task: VerifyTaskDto): Promise<VerifyTaskDto> {
        const res = await client.put<VerifyTaskDto>(`/api/verify/tasks/${enc(id)}`, task);
        return res.data;
    },

    async deleteTask(id: string): Promise<void> {
        await client.delete(`/api/verify/tasks/${enc(id)}`);
    },

    async runTask(id: string): Promise<VerifyTaskDto> {
        const res = await client.post<VerifyTaskDto>(`/api/verify/tasks/${enc(id)}/run`, undefined, { timeout: 2 * 60 * 60_000 });
        return res.data;
    },

    async runTasks(taskIds: string[]): Promise<VerifyTaskRunResult[]> {
        const res = await client.post<VerifyTaskRunResult[]>("/api/verify/tasks/run-batch", taskIds, { timeout: 2 * 60 * 60_000 });
        return res.data;
    },

    /** 只等待后台预约，不随浏览器断开中断长时间测试。状态由 listTasks 轮询读取。 */
    async startTask(id: string): Promise<VerifyTaskDto> {
        return (await client.post<VerifyTaskDto>(`/api/verify/tasks/${enc(id)}/start`, undefined, { timeout: 30_000 })).data;
    },

    async startTasks(taskIds: string[]): Promise<VerifyTaskRunResult[]> {
        return (await client.post<VerifyTaskRunResult[]>("/api/verify/tasks/start-batch", taskIds, { timeout: 30_000 })).data;
    },

    /** 导出最近一次已结束运行的实测、评分、配置快照及证据。 */
    async exportTaskResult(id: string): Promise<{ blob: Blob; fileName: string }> {
        const res = await client.get(`/api/verify/tasks/${enc(id)}/export`, {
            responseType: "blob",
        });
        const disposition = (res.headers?.["content-disposition"] ?? "") as string;
        const star = /filename\*\s*=\s*UTF-8''([^;]+)/i.exec(disposition);
        const plain = /filename\s*=\s*("?)([^";]+)\1/i.exec(disposition);
        const fileName = star?.[1]
            ? decodeURIComponent(star[1].trim().replace(/(^"|"$)/g, ""))
            : (plain?.[2]?.trim() ?? `验证报告-${id}.xlsx`);
        return { blob: res.data as Blob, fileName };
    },
    async exportTaskPdf(id: string): Promise<{ blob: Blob; fileName: string }> {
        const res = await client.get(`/api/verify/tasks/${enc(id)}/export`, {
            params: { format: 'pdf' }, responseType: 'blob',
        });
        return { blob: res.data as Blob, fileName: `验证报告-${id}.pdf` };
    },
};
