import { createMachineConnectionClient } from './machineConnectionClient'
import type { VerifyMetricResult } from './machineConnectionVerify'

export type EvaluationCategory = 'machine' | 'machining'
export type EvaluationUnit = '%' | 'min' | 's' | 'h' | 'count' | 'B' | 'KB' | 'KiB' | 'MB' | 'MiB' | 'B/s' | 'KB/s' | 'KiB/s' | 'MB/s' | 'MiB/s'
export interface EvaluationPassRule { comparison: 'gte' | 'eq' | 'lte'; threshold: number; unit: EvaluationUnit }
export interface EvaluationManualPassRule { comparison: 'gte' | 'eq' | 'lte'; threshold: number; unit: string }
export interface EvaluationScoreBand { min: number; score: number }
export interface EvaluationTestSettings {
    durationMinutes: number; sampleIntervalSeconds: number; maxConnections: number; failureLimit: number
    concurrencyMode: 'devices' | 'connections' | 'sessions'; readAddress: string; readDataType: string; targetDirectory: string
}
export interface EvaluationAutomationRule {
    unit: EvaluationUnit; passRule: EvaluationPassRule | null; scoringMode: 'linear' | 'bands'
    scoreBands: EvaluationScoreBand[]; test: EvaluationTestSettings
}
export const MAX_EVALUATION_ATTACHMENT_BYTES = 200 * 1024 * 1024
export interface EvaluationFile { id?: string | null; name: string; size: string; sizeBytes?: number | null }
export interface EvaluationItem {
    id: string; name: string; weight: number; desc: string; method: string
    evidenceType: 'standard' | 'protocol' | 'file'; evidence: string; standards: string
    files: EvaluationFile[]; protocols: string[]; scoring: Record<string, string>; metricId?: string | null
    automation?: EvaluationAutomationRule | null
    manualPassRule?: EvaluationManualPassRule | null
}
export interface EvaluationSubcategory { id: string; name: string; weight: number; items: EvaluationItem[] }
export interface EvaluationSection { id: string; name: string; weight: number; children: EvaluationSubcategory[] }
export interface EvaluationConfig {
    category: EvaluationCategory; version: number; indicators: EvaluationSection[]; updatedAt: string
}
export interface KnowledgeRecord {
    id: string; machineName: string; machineNo: string; machineModel: string; controlSystem: string
    partName: string; partFeature: string; testLocation: string; testDate: string; tester: string
    dataSource: 'manual' | 'sync' | 'import'; category: EvaluationCategory; snapshot: EvaluationConfig
    version: number; categoryWeights: Record<string, number>; subCategoryWeights: Record<string, number>
    scores: Record<string, number | null>; weights: Record<string, number>
    testResults: Record<string, string>; remarks: Record<string, string>
    conclusion: string; suggestion: string; syncTaskId: string | null
    syncRunId?: string | null
    createdAt: string; updatedAt: string; totalScore: number | null
}
export interface KnowledgeTask {
    id: string; name: string; machineName: string; machineNo: string; machineModel: string
    controlSystem: string; testDate: string; status: string; result: string
    category?: EvaluationCategory
    metrics: VerifyMetricResult[]; totalScore: number | null; passRate: number | null
}
export interface KnowledgeImportResult { total: number; success: number; failed: number; errors: string[] }

const client = createMachineConnectionClient({
    baseURL: import.meta.env.VITE_MACHINE_CONNECTION_API ?? '/machine-connection',
    timeout: 120_000,
})
const base = '/api/evaluation'
const enc = encodeURIComponent
export const evaluationApi = {
    async getConfig(category: EvaluationCategory): Promise<EvaluationConfig> {
        return (await client.get<EvaluationConfig>(`${base}/indicators/${category}`)).data
    },
    async saveConfig(config: EvaluationConfig): Promise<EvaluationConfig> {
        return (await client.put<EvaluationConfig>(`${base}/indicators/${config.category}`, config)).data
    },
    async uploadAttachment(file: File, signal?: AbortSignal): Promise<EvaluationFile> {
        if (file.size <= 0 || file.size > MAX_EVALUATION_ATTACHMENT_BYTES) throw new Error('单个测试文件应大于 0 且不超过 200 MiB')
        const form = new FormData()
        form.append('file', file)
        return (await client.post<EvaluationFile>(`${base}/attachments`, form, { timeout: 30 * 60_000, signal })).data
    },
    async downloadAttachment(id: string, signal?: AbortSignal): Promise<Blob> {
        return (await client.get(`${base}/attachments/${enc(id)}`, { responseType: 'blob', timeout: 30 * 60_000, signal })).data
    },
    async listRecords(): Promise<KnowledgeRecord[]> {
        return (await client.get<KnowledgeRecord[]>(`${base}/records`)).data
    },
    async createRecord(record: KnowledgeRecord): Promise<KnowledgeRecord> {
        return (await client.post<KnowledgeRecord>(`${base}/records`, record)).data
    },
    async updateRecord(record: KnowledgeRecord): Promise<KnowledgeRecord> {
        return (await client.put<KnowledgeRecord>(`${base}/records/${enc(record.id)}`, record)).data
    },
    async deleteRecord(id: string): Promise<void> {
        await client.delete(`${base}/records/${enc(id)}`)
    },
    async listTasks(): Promise<KnowledgeTask[]> {
        return (await client.get<KnowledgeTask[]>(`${base}/tasks`)).data
    },
    async taskDraft(id: string, category?: EvaluationCategory): Promise<KnowledgeRecord> {
        return (await client.get<KnowledgeRecord>(`${base}/tasks/${enc(id)}/draft`, { params: { category } })).data
    },
    async downloadTemplate(category?: EvaluationCategory): Promise<Blob> {
        return (await client.get(`${base}/records/template`, { params: { category }, responseType: 'blob' })).data
    },
    async importRecords(file: File): Promise<KnowledgeImportResult> {
        const form = new FormData()
        form.append('file', file)
        return (await client.post<KnowledgeImportResult>(`${base}/records/import`, form)).data
    },
    async exportRecord(id: string, format: 'xlsx' | 'pdf'): Promise<Blob> {
        return (await client.get(`${base}/records/${enc(id)}/export`, { params: { format }, responseType: 'blob' })).data
    },
}
