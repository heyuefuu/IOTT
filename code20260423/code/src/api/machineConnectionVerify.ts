import axios from "axios";

const baseURL =
    import.meta.env.VITE_MACHINE_CONNECTION_API ?? "/machine-connection";

const client = axios.create({
    baseURL,
    timeout: 300_000,
    headers: { "Content-Type": "application/json" },
});

export interface VerifyRunRequest {
    taskId?: string;
    taskName?: string;
    metricIds: string[];
}

export interface VerifyMetricResult {
    metricId: string;
    code: string;
    name: string;
    status: "passed" | "failed" | "pending" | "running";
    result: string;
    value: string;
    reference: string;
    detail: string;
    evidence: string[];
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
}

export interface VerifyTaskDto {
    id: string;
    name: string;
    type: string;
    status: string;
    priority: string;
    deviceId: string;
    machineId: string;
    metricIds: string[];
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

export const machineConnectionVerifyApi = {
    async run(body: VerifyRunRequest): Promise<VerifyRunResponse> {
        const res = await client.post<VerifyRunResponse>("/api/verify/run", body);
        return res.data;
    },

    async listTasks(): Promise<VerifyTaskDto[]> {
        const res = await client.get<VerifyTaskDto[]>("/api/verify/tasks");
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
        const res = await client.post<VerifyTaskDto>(`/api/verify/tasks/${enc(id)}/run`);
        return res.data;
    },

    /** 导出最近一次运行结果为 Excel（含实测值/参考值对比与空白「人工评分」列） */
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
};
