import axios from "axios";

const baseURL =
    import.meta.env.VITE_MACHINE_CONNECTION_API ?? "/machine-connection";

const client = axios.create({
    baseURL,
    timeout: 60_000,
    headers: { "Content-Type": "application/json" },
});

export interface InfluxTelemetryBatchPoint {
    name: string;
    path: string;
    dataType: string;
    value: unknown;
    quality: string;
    timestamp: string;
    status: string;
    errorMessage?: string | null;
}

export interface InfluxTelemetryHistoryItem {
    deviceId: string;
    name: string;
    path: string;
    dataType: string;
    value: unknown;
    quality: string;
    status: string;
    errorMessage?: string | null;
    time: string;
}

export interface InfluxTelemetryHistoryPageResult {
    items: InfluxTelemetryHistoryItem[];
    total: number;
    page: number;
    pageSize: number;
}

export const telemetryInfluxApi = {
    async writeBatch(body: {
        deviceId: string;
        collectedAt?: string;
        points: InfluxTelemetryBatchPoint[];
    }): Promise<{
        written: number;
        mqttPublished?: boolean;
        mqttError?: string;
        skipped?: boolean;
        reason?: string;
    }> {
        const res = await client.post<{
            written: number;
            mqttPublished?: boolean;
            mqttError?: string;
            skipped?: boolean;
            reason?: string;
        }>(
            "/api/telemetry/influx/batch",
            body,
        );
        return res.data;
    },

    async history(params: {
        deviceId: string;
        startTime: string;
        endTime: string;
        path?: string;
        page?: number;
        pageSize?: number;
    }): Promise<InfluxTelemetryHistoryPageResult> {
        const res = await client.get<InfluxTelemetryHistoryPageResult>(
            "/api/telemetry/influx/history",
            { params },
        );
        return res.data;
    },
};
