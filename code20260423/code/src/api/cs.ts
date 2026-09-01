import axios from "axios";

/** 与 machineConnection* 系列一致：经 /machine-connection 反代到 MachineConnectionApi。 */
const baseURL =
    import.meta.env.VITE_MACHINE_CONNECTION_API ?? "/machine-connection";

/** 独立实例，避免 @/api 的全局响应拦截器（其按 res.code===200 判定，会破坏原始 JSON 返回）。 */
const client = axios.create({
    baseURL,
    timeout: 300_000,
    headers: { "Content-Type": "application/json" },
});

export interface CsGateway {
    id: string;
    name: string;
    ip: string;
    port: number;
    type: string;
    username?: string;
    password?: string;
    remotePath?: string;
    description?: string;
    status: string;
    lastHeartbeat?: string;
}

export interface CsDataSource {
    id: string;
    name: string;
    type: string;
    gatewayId: string;
    config?: string;
    updateInterval: number;
    status: string;
    lastUpdate?: string;
    lastResult?: string;
}

export interface CsServerService {
    id: string;
    name: string;
    type: string;
    username?: string;
    password?: string;
    port: number;
    description?: string;
    maxClients: number;
    status: string;
    clientCount: number;
    lastAccess?: string;
}

export interface CsProbeResult {
    success: boolean;
    rttMs: number;
    message: string;
    timestamp: string;
}

export interface CsServerConnection {
    remoteEndpoint: string;
    connectedAt: string;
    bytesReceived: number;
}

export interface CsParallelTestRequest {
    startIp: string;
    port: number;
    deviceCount: number;
    concurrentCount: number;
    timeoutMs: number;
    holdMs?: number;
    protocol?: string;
    mqttUseTls?: boolean;
    mqttUsername?: string;
    mqttPassword?: string;
    mqttClientId?: string;
}

export interface CsParallelFailure {
    deviceIp: string;
    error: string;
    time: string;
}

export interface CsParallelTestResult {
    total: number;
    success: number;
    failure: number;
    successRate: number;
    avgRttMs: number;
    maxRttMs: number;
    failures: CsParallelFailure[];
    finishedAt: string;
}

export interface CsParallelReportRequest {
    request: CsParallelTestRequest;
    result: CsParallelTestResult;
    connectionMode?: string;
    durationSeconds?: number;
    generatedAt?: string;
}

const P = "/api/cs";
const enc = encodeURIComponent;

export const csApi = {
    // ---- 网关（客户端连接目标）----
    listGateways: async (): Promise<CsGateway[]> =>
        (await client.get<CsGateway[]>(`${P}/gateways`)).data ?? [],
    createGateway: async (g: Partial<CsGateway>): Promise<CsGateway> =>
        (await client.post<CsGateway>(`${P}/gateways`, g)).data,
    updateGateway: async (id: string, g: Partial<CsGateway>): Promise<CsGateway> =>
        (await client.put<CsGateway>(`${P}/gateways/${enc(id)}`, g)).data,
    deleteGateway: async (id: string): Promise<void> => {
        await client.delete(`${P}/gateways/${enc(id)}`);
    },
    /** 启动网关 = 真实探测目标连通性。 */
    probeGateway: async (id: string): Promise<CsProbeResult> =>
        (await client.post<CsProbeResult>(`${P}/gateways/${enc(id)}/probe`)).data,

    // ---- 客户端数据源（周期探测）----
    listDataSources: async (): Promise<CsDataSource[]> =>
        (await client.get<CsDataSource[]>(`${P}/datasources`)).data ?? [],
    createDataSource: async (d: Partial<CsDataSource>): Promise<CsDataSource> =>
        (await client.post<CsDataSource>(`${P}/datasources`, d)).data,
    updateDataSource: async (id: string, d: Partial<CsDataSource>): Promise<CsDataSource> =>
        (await client.put<CsDataSource>(`${P}/datasources/${enc(id)}`, d)).data,
    deleteDataSource: async (id: string): Promise<void> => {
        await client.delete(`${P}/datasources/${enc(id)}`);
    },
    enableDataSource: async (id: string): Promise<void> => {
        await client.post(`${P}/datasources/${enc(id)}/enable`);
    },
    disableDataSource: async (id: string): Promise<void> => {
        await client.post(`${P}/datasources/${enc(id)}/disable`);
    },

    // ---- 服务端服务（TcpListener 监听）----
    listServers: async (): Promise<CsServerService[]> =>
        (await client.get<CsServerService[]>(`${P}/servers`)).data ?? [],
    createServer: async (s: Partial<CsServerService>): Promise<CsServerService> =>
        (await client.post<CsServerService>(`${P}/servers`, s)).data,
    updateServer: async (id: string, s: Partial<CsServerService>): Promise<CsServerService> =>
        (await client.put<CsServerService>(`${P}/servers/${enc(id)}`, s)).data,
    deleteServer: async (id: string): Promise<void> => {
        await client.delete(`${P}/servers/${enc(id)}`);
    },
    startServer: async (id: string): Promise<void> => {
        await client.post(`${P}/servers/${enc(id)}/start`);
    },
    stopServer: async (id: string): Promise<void> => {
        await client.post(`${P}/servers/${enc(id)}/stop`);
    },
    serverConnections: async (id: string): Promise<CsServerConnection[]> =>
        (await client.get<CsServerConnection[]>(`${P}/servers/${enc(id)}/connections`)).data ?? [],

    // ---- 并发连接压测（真实并发 TCP 建连）----
    runParallelTest: async (
        req: CsParallelTestRequest,
        signal?: AbortSignal,
    ): Promise<CsParallelTestResult> =>
        (await client.post<CsParallelTestResult>(`${P}/parallel-test`, req, { signal })).data,
    downloadParallelReport: async (
        format: "pdf" | "html",
        req: CsParallelReportRequest,
    ): Promise<Blob> =>
        (await client.post(`${P}/parallel-test/report/${format}`, req, {
            responseType: "blob",
        })).data,
};
