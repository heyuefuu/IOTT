import axios from "axios";
import { downloadBlob } from "./browserDownload";

const baseURL =
    import.meta.env.VITE_MACHINE_CONNECTION_API ?? "/machine-connection";

/** 与 Industrial IoT ProgramTransferResponse（camelCase）一致 */
export interface ProgramTransferResponse {
    transferId: string;
    deviceId: string;
    fileName: string;
    remotePath: string;
    direction: "Upload" | "Download";
    status: "Pending" | "InProgress" | "Paused" | "Completed" | "Failed";
    fileSize: number;
    bytesTransferred: number;
    checksum?: string | null;
    startedAt: string;
    completedAt?: string | null;
    durationMs?: number | null;
    errorMessage?: string | null;
}

export interface ProgramTransferFileItem {
    name: string;
    path: string;
    nodeType: "folder" | "file";
    sizeBytes?: number;
    modifiedAt?: string;
}

export type BatchTransferTaskStatus =
    | "Pending"
    | "Running"
    | "Completed"
    | "PartialSuccess"
    | "Failed";

export interface BatchTransferTaskDto {
    taskId: string;
    deviceId: string;
    status: BatchTransferTaskStatus;
    artifactReady: boolean;
    artifactFileName?: string | null;
    completedFiles?: number;
    failedFiles?: number;
    totalFiles?: number;
}

/** 与 IndustrialIoT ProgramTransferCapability 一致（枚举为 JSON 字符串） */
export interface ProgramTransferCapability {
    protocol: string;
    supportsProgramTransfer: boolean;
    supportsBrowse: boolean;
    supportsResumeUpload: boolean;
    supportsFullRestartUpload: boolean;
    resumeMode: string;
    limitation?: string | null;
}

const jsonClient = axios.create({
    baseURL,
    timeout: 300_000,
    headers: { "Content-Type": "application/json" },
});

/** multipart 上传勿设置默认 Content-Type，由浏览器带 boundary */
const uploadClient = axios.create({
    baseURL,
    timeout: 300_000,
});

function parseContentDispositionFileName(
    header: string | undefined | null,
): string | null {
    if (!header) return null;
    const star = /filename\*=UTF-8''([^;\r\n]+)/i.exec(header);
    if (star?.[1]) {
        try {
            return decodeURIComponent(star[1].trim());
        } catch {
            return star[1].trim();
        }
    }
    const plain = /filename="([^"]+)"/i.exec(header)
        ?? /filename=([^;\r\n]+)/i.exec(header);
    const raw = plain?.[1]?.trim();
    return raw ? raw.replace(/^"|"$/g, "") : null;
}

async function readBlobAsText(blob: Blob): Promise<string> {
    return new Promise((resolve, reject) => {
        const r = new FileReader();
        r.onload = () => resolve(String(r.result ?? ""));
        r.onerror = () => reject(r.error);
        r.readAsText(blob);
    });
}

function sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
}

export const machineConnectionProgramTransferApi = {
    async files(
        deviceId: string,
        path?: string,
        recursive?: boolean,
    ): Promise<ProgramTransferFileItem[]> {
        const params: string[] = [];
        if (typeof path === "string" && path.trim()) {
            params.push(`path=${encodeURIComponent(path.trim())}`);
        }
        if (typeof recursive === "boolean") {
            params.push(`recursive=${recursive ? "true" : "false"}`);
        }
        const query = params.length > 0 ? `?${params.join("&")}` : "";
        const res = await jsonClient.get<unknown>(
            `/api/program-transfer/${encodeURIComponent(deviceId)}/files${query}`,
        );
        const raw = (res.data as { items?: unknown })?.items ?? res.data;
        const list = Array.isArray(raw) ? raw : [];
        return list
            .map((item): ProgramTransferFileItem | null => {
                const r = item as Record<string, unknown>;
                const rawPath = String(r.path ?? r.remotePath ?? "").trim();
                const rawName = String(r.name ?? "").trim();
                const nodeTypeRaw = String(
                    r.nodeType ?? r.type ?? (r.isDirectory ? "folder" : "file"),
                ).toLowerCase();
                const nodeType: ProgramTransferFileItem["nodeType"] = nodeTypeRaw.includes("dir")
                    || nodeTypeRaw.includes("folder")
                    ? "folder"
                    : "file";
                const name = rawName || rawPath.split(/[\\/]/).filter(Boolean).pop() || rawPath;
                if (!rawPath) return null;
                const out: ProgramTransferFileItem = {
                    name,
                    path: rawPath,
                    nodeType,
                };
                if (Number.isFinite(Number(r.sizeBytes))) {
                    out.sizeBytes = Number(r.sizeBytes);
                }
                if (typeof r.modifiedAt === "string") {
                    out.modifiedAt = r.modifiedAt;
                }
                return out;
            })
            .filter((x): x is ProgramTransferFileItem => x != null);
    },

    async upload(
        deviceId: string,
        file: File,
        remotePath: string,
    ): Promise<ProgramTransferResponse> {
        const form = new FormData();
        form.append("file", file);
        form.append("remotePath", remotePath.trim());
        const res = await uploadClient.post<ProgramTransferResponse>(
            `/api/program-transfer/${encodeURIComponent(deviceId)}/upload`,
            form,
        );
        return res.data;
    },

    async download(deviceId: string, remotePath: string): Promise<void> {
        const res = await uploadClient.post<Blob>(
            `/api/program-transfer/${encodeURIComponent(deviceId)}/download`,
            { remotePath: remotePath.trim() },
            {
                responseType: "blob",
                validateStatus: () => true,
                headers: { "Content-Type": "application/json" },
            },
        );

        if (res.status >= 200 && res.status < 300) {
            const fn =
                parseContentDispositionFileName(
                    res.headers["content-disposition"],
                )
                ?? remotePath.replace(/^[\\/]+/, "").split(/[\\/]/).pop()
                ?? "download.nc";
            downloadBlob(res.data, fn);
            return;
        }

        let msg = `下载失败 (HTTP ${res.status})`;
        try {
            const text = await readBlobAsText(res.data);
            const j = JSON.parse(text) as { error?: string; detail?: string };
            msg = j.error?.trim() || j.detail?.trim() || text || msg;
        } catch {
            /* ignore */
        }
        throw new Error(msg);
    },

    async history(deviceId: string): Promise<ProgramTransferResponse[]> {
        const res = await jsonClient.get<ProgramTransferResponse[]>(
            `/api/program-transfer/${encodeURIComponent(deviceId)}/history`,
        );
        return Array.isArray(res.data) ? res.data : [];
    },

    /** 查询设备程序传输能力（是否支持断点续传等） */
    async capabilities(deviceId: string): Promise<ProgramTransferCapability> {
        const res = await jsonClient.get<ProgramTransferCapability>(
            `/api/program-transfer/${encodeURIComponent(deviceId)}/capabilities`,
        );
        return res.data;
    },

    /** 单条传输记录状态 */
    async transferStatus(transferId: string): Promise<ProgramTransferResponse> {
        const res = await jsonClient.get<ProgramTransferResponse>(
            `/api/program-transfer/${encodeURIComponent(transferId)}/status`,
        );
        return res.data;
    },

    /**
     * 断点续传上传：仅对 Failed/Paused 的上传记录有效；
     * offset 省略时后端默认从已传字节数续传。
     */
    async resume(
        deviceId: string,
        transferId: string,
        offset = 0,
    ): Promise<ProgramTransferResponse> {
        const res = await jsonClient.post<unknown>(
            `/api/program-transfer/${encodeURIComponent(deviceId)}/resume`,
            { transferId, offset },
            { validateStatus: () => true },
        );
        if (res.status >= 200 && res.status < 300) {
            return res.data as ProgramTransferResponse;
        }
        const body = res.data as {
            error?: string;
            detail?: string;
            errorMessage?: string;
            nextAction?: string;
        };
        const msg =
            body?.error?.trim() ||
            body?.errorMessage?.trim() ||
            body?.detail?.trim() ||
            `断点续传失败 (HTTP ${res.status})`;
        throw new Error(body?.nextAction ? `${msg}（${body.nextAction}）` : msg);
    },

    async queueDownloadBatch(
        deviceId: string,
        paths: string[],
    ): Promise<{ taskId: string; status?: BatchTransferTaskStatus }> {
        const res = await jsonClient.post<unknown>(
            `/api/program-transfer/${encodeURIComponent(deviceId)}/download-batch`,
            { paths },
        );
        const body = res.data as Record<string, unknown>;
        const taskId = String(body.taskId ?? body.TaskId ?? "").trim();
        if (!taskId) throw new Error("批量下载未返回 taskId");
        const statusRaw = body.status ?? body.Status;
        const status =
            typeof statusRaw === "string"
                ? (statusRaw as BatchTransferTaskStatus)
                : undefined;
        return { taskId, status };
    },

    async getBatchTransferTask(taskId: string): Promise<BatchTransferTaskDto> {
        const res = await jsonClient.get<BatchTransferTaskDto>(
            `/api/program-transfer/tasks/${encodeURIComponent(taskId)}`,
        );
        return res.data;
    },

    async waitForBatchTransferTaskComplete(
        taskId: string,
        options?: { pollIntervalMs?: number; timeoutMs?: number },
    ): Promise<BatchTransferTaskDto> {
        const pollIntervalMs = options?.pollIntervalMs ?? 1000;
        const timeoutMs = options?.timeoutMs ?? 300_000;
        const deadline = Date.now() + timeoutMs;
        while (Date.now() < deadline) {
            const task = await this.getBatchTransferTask(taskId);
            if (task.status === "Failed") {
                throw new Error("批量任务失败");
            }
            if (task.status === "Completed" || task.status === "PartialSuccess") {
                return task;
            }
            await sleep(pollIntervalMs);
        }
        throw new Error("批量任务等待超时");
    },

    async queueUploadBatch(
        deviceId: string,
        remotePath: string,
        files: File[],
    ): Promise<{ taskId: string; status?: BatchTransferTaskStatus }> {
        if (!files.length) throw new Error("请至少选择一个本地文件");
        const form = new FormData();
        form.append("remotePath", remotePath.trim());
        for (const f of files) form.append("files", f);
        const res = await uploadClient.post<unknown>(
            `/api/program-transfer/${encodeURIComponent(deviceId)}/upload-batch`,
            form,
            { validateStatus: () => true },
        );
        if (res.status < 200 || res.status >= 300) {
            let msg = `批量上传提交失败 (HTTP ${res.status})`;
            try {
                const d = res.data as { error?: string; detail?: string };
                msg = d.error?.trim() || d.detail?.trim() || msg;
            } catch {
                /* ignore */
            }
            throw new Error(msg);
        }
        const body = res.data as Record<string, unknown>;
        const taskId = String(body.taskId ?? body.TaskId ?? "").trim();
        if (!taskId) throw new Error("批量上传未返回 taskId");
        const statusRaw = body.status ?? body.Status;
        const status =
            typeof statusRaw === "string"
                ? (statusRaw as BatchTransferTaskStatus)
                : undefined;
        return { taskId, status };
    },

    /**
     * 多文件上传到同一设备目录；轮询至任务结束（无 ZIP 制品）。
     */
    async uploadBatchWait(
        deviceId: string,
        remotePath: string,
        files: File[],
        options?: { pollIntervalMs?: number; timeoutMs?: number },
    ): Promise<BatchTransferTaskDto> {
        const { taskId } = await this.queueUploadBatch(deviceId, remotePath, files);
        return await this.waitForBatchTransferTaskComplete(taskId, options);
    },

    /**
     * 提交批量下载、轮询至完成或失败，并在 artifactReady 时保存 ZIP。
     */
    async downloadBatchZip(
        deviceId: string,
        remotePaths: string[],
        options?: { pollIntervalMs?: number; timeoutMs?: number },
    ): Promise<void> {
        if (!remotePaths.length) throw new Error("没有可下载的路径");
        const { taskId } = await this.queueDownloadBatch(deviceId, remotePaths);
        const task = await this.waitForBatchTransferTaskComplete(taskId, options);
        if (task.artifactReady) {
            const res = await uploadClient.get<Blob>(
                `/api/program-transfer/tasks/${encodeURIComponent(taskId)}/artifact`,
                { responseType: "blob", validateStatus: () => true },
            );
            if (res.status < 200 || res.status >= 300) {
                let msg = `下载制品失败 (HTTP ${res.status})`;
                try {
                    const text = await readBlobAsText(res.data);
                    const j = JSON.parse(text) as { error?: string; detail?: string };
                    msg = j.error?.trim() || j.detail?.trim() || text || msg;
                } catch {
                    /* ignore */
                }
                throw new Error(msg);
            }
            const fn =
                parseContentDispositionFileName(
                    res.headers["content-disposition"],
                )
                ?? task.artifactFileName?.trim()
                ?? "batch-download.zip";
            downloadBlob(res.data, fn);
            return;
        }
        throw new Error("批量任务已结束，但没有可下载的制品（可能全部失败）");
    },
};
