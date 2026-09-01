import axios from "axios";

const baseURL =
    import.meta.env.VITE_MACHINE_CONNECTION_API ?? "/machine-connection";

const client = axios.create({
    baseURL,
    timeout: 120_000,
    headers: { "Content-Type": "application/json" },
});

/**
 * 采集协议类型。
 * - IndustrialIoT：默认，走上游 IndustrialIoT.Host 的 /api/data/{id}/read。
 * - NCLinkApi：华中 NC-Link，走 nclink-api-server 的 /v1/{deviceId}/data/。
 */
export type DatacollectionProtocol = "IndustrialIoT" | "NCLinkApi";

export interface DatacollectionRow {
    id: number;
    name: string;
    path: string;
    datatype: string;
    collectionFrequency: number;
    /** ISO 日期字符串或 null */
    datetime?: string | null;
    protocol?: DatacollectionProtocol;
}

export interface DatacollectionSyncItem {
    name: string;
    path: string;
    datatype: string;
    collectionFrequency: number;
    /** 未传时后端按 IndustrialIoT 处理；华中机床请传 "NCLinkApi" */
    protocol?: DatacollectionProtocol;
}

export const datacollectionApi = {
    async getPaths(deviceId: string): Promise<string[]> {
        const res = await client.get<string[]>("/api/datacollection/paths", {
            params: { deviceId },
        });
        return res.data;
    },

    async list(deviceId: string): Promise<DatacollectionRow[]> {
        const res = await client.get<DatacollectionRow[]>("/api/datacollection", {
            params: { deviceId },
        });
        return res.data;
    },

    async sync(body: {
        deviceId: string;
        items: DatacollectionSyncItem[];
        visiblePaths?: string[];
    }): Promise<{ saved: number }> {
        const res = await client.post<{ saved: number }>(
            "/api/datacollection/sync",
            body,
        );
        return res.data;
    },
};
