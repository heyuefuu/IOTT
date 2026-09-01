import axios from "axios";

const baseURL =
	import.meta.env.VITE_MACHINE_CONNECTION_API ?? "/machine-connection";

const client = axios.create({
	baseURL,
	timeout: 120_000,
	headers: { "Content-Type": "application/json" },
});

export interface MetricDto {
	id: string;
	code: string;
	name: string;
	category: string;
	unit: string;
	statusLabel: string;
	statusType: string;
	description: string;
	reference: string;
	/** 达标阈值（实测值 ≥ 阈值判为达标）；为空时自动验证使用内置默认判据 */
	threshold?: number | null;
	createdAt: string;
}

export interface ReportTemplateDto {
	id: string;
	name: string;
	type: string;
	description: string;
	content: string;
	createdAt: string;
}

export interface TemplatePreviewResponse {
	html: string;
}

const enc = encodeURIComponent;

export const businessValidationApi = {
	async listMetrics(params: {
		category?: string;
		keyword?: string;
	} = {}): Promise<MetricDto[]> {
		const res = await client.get<MetricDto[]>("/api/metrics", { params });
		return res.data ?? [];
	},

	async createMetric(metric: MetricDto): Promise<MetricDto> {
		const res = await client.post<MetricDto>("/api/metrics", metric);
		return res.data;
	},

	async updateMetric(id: string, metric: MetricDto): Promise<MetricDto> {
		const res = await client.put<MetricDto>(`/api/metrics/${enc(id)}`, metric);
		return res.data;
	},

	async deleteMetric(id: string): Promise<void> {
		await client.delete(`/api/metrics/${enc(id)}`);
	},

	async listTemplates(params: {
		type?: string;
		keyword?: string;
	} = {}): Promise<ReportTemplateDto[]> {
		const res = await client.get<ReportTemplateDto[]>("/api/report-templates", {
			params,
		});
		return res.data ?? [];
	},

	async createTemplate(template: ReportTemplateDto): Promise<ReportTemplateDto> {
		const res = await client.post<ReportTemplateDto>(
			"/api/report-templates",
			template,
		);
		return res.data;
	},

	async updateTemplate(
		id: string,
		template: ReportTemplateDto,
	): Promise<ReportTemplateDto> {
		const res = await client.put<ReportTemplateDto>(
			`/api/report-templates/${enc(id)}`,
			template,
		);
		return res.data;
	},

	async deleteTemplate(id: string): Promise<void> {
		await client.delete(`/api/report-templates/${enc(id)}`);
	},

	async previewTemplate(id: string): Promise<TemplatePreviewResponse> {
		const res = await client.post<TemplatePreviewResponse>(
			`/api/report-templates/${enc(id)}/preview`,
		);
		return res.data;
	},
};
