import axios from "axios";

export type AddressNodeType = "Folder" | "Variable";

export interface AddressNode {
    path: string;
    displayName: string;
    nodeType: AddressNodeType;
    dataType?: string | null;
    unit?: string | null;
    engineeringUnit?: string | null;
    isReadable?: boolean;
    isWritable?: boolean;
    sourceId?: string | null;
    children?: AddressNode[] | null;
}

export type DataTypeApi =
    | "Bool"
    | "Int16"
    | "Int32"
    | "Int64"
    | "UInt16"
    | "UInt32"
    | "Float"
    | "Double"
    | "String"
    | "ByteArray";

export interface ReadTagRequest {
    address: string;
    dataType: string;
    sourceId?: string | null;
}

export interface ReadTagsRequest {
    tags: ReadTagRequest[];
}

export interface ReadTagResult {
    address: string;
    dataType: string;
    value: unknown;
    quality: string;
    timestamp: string;
    errorMessage?: string | null;
}

export interface ReadTagsResponse {
    deviceId: string;
    tags: ReadTagResult[];
}

export interface WriteTagRequest {
    address: string;
    dataType: string;
    value: unknown;
}

export interface WriteTagsRequest {
    tags: WriteTagRequest[];
}

export interface WriteTagResult {
    address: string;
    success: boolean;
    errorMessage?: string | null;
    timestamp: string;
}

export interface WriteTagsResponse {
    deviceId: string;
    totalCount: number;
    successCount: number;
    results: WriteTagResult[];
}

const baseURL =
    import.meta.env.VITE_MACHINE_CONNECTION_API ?? "/machine-connection";

const client = axios.create({
    baseURL,
    timeout: 120_000,
    headers: { "Content-Type": "application/json" },
});

export const machineConnectionPointsApi = {
    async browseAddressSpace(
        deviceId: string,
        parentPath?: string,
        protocol?: string,
    ): Promise<AddressNode[]> {
        const res = await client.get<AddressNode[]>(
            `/api/addressspace/${encodeURIComponent(deviceId)}`,
            {
                params: {
                    ...(parentPath ? { parentPath } : {}),
                    ...(protocol ? { protocol } : {}),
                },
            },
        );
        return res.data;
    },

    async exportAddressSpace(
        deviceId: string,
        format: "CSV" | "JSON" | "Excel" = "CSV",
    ): Promise<{ blob: Blob; fileName: string; contentType: string }> {
        const res = await client.get(
            `/api/addressspace/${encodeURIComponent(deviceId)}/export`,
            {
                params: { format },
                responseType: "blob",
            },
        );

        const contentType =
            res.headers?.["content-type"] ?? "application/octet-stream";
        const disposition = res.headers?.["content-disposition"] as
            | string
            | undefined;

        const fileName =
            extractFileName(disposition) ??
            `address_space_${deviceId}.${format.toLowerCase()}`;

        return { blob: res.data as Blob, fileName, contentType };
    },

    /**
     * 与仓库内《FANUC-Swagger请求体.md》第 5 节一致，对应 Industrial IoT:
     * `POST /api/data/{deviceId}/read`，请求体为 `{ tags: { address, dataType }[] }`。
     * 法那科 FOCAS 的 address 为斜杠路径（如 `/CNC/Fanuc/Program/Name`），`dataType` 为
     * `String` | `Double` | `Bool` 等与后端 `DataType` 枚举一致（不区分大小写）。
     */
    async readTags(deviceId: string, body: ReadTagsRequest): Promise<ReadTagsResponse> {
        const res = await client.post<ReadTagsResponse>(
            `/api/data/${encodeURIComponent(deviceId)}/read`,
            body,
        );
        return res.data;
    },

    async writeTags(
        deviceId: string,
        body: WriteTagsRequest,
    ): Promise<WriteTagsResponse> {
        const res = await client.post<WriteTagsResponse>(
            `/api/data/${encodeURIComponent(deviceId)}/write`,
            body,
        );
        return res.data;
    },
};

function extractFileName(contentDisposition?: string): string | null {
    if (!contentDisposition) return null;
    // content-disposition: attachment; filename="xxx"; filename*=UTF-8''xxx
    const starMatch = /filename\*\s*=\s*UTF-8''([^;]+)/i.exec(contentDisposition);
    if (starMatch?.[1]) return decodeURIComponent(starMatch[1].trim().replace(/(^"|"$)/g, ""));
    const match = /filename\s*=\s*("?)([^";]+)\1/i.exec(contentDisposition);
    return match?.[2]?.trim() ?? null;
}

