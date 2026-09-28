import type { EvaluationItem, EvaluationSection, EvaluationSubcategory } from "@/api/evaluation";

export type MetricNode = EvaluationSection | EvaluationSubcategory | EvaluationItem;
export type MetricLevel = "section" | "subcategory" | "item";
export interface MetricRow {
	id: string;
	level: MetricLevel;
	node: MetricNode;
	parentId?: string;
	parentName?: string;
	siblingWeight: number;
	children?: MetricRow[];
}
export interface MetricEdit {
	level: MetricLevel;
	node: MetricNode;
	parentId?: string;
	isNew: boolean;
}
export const protocolOptions = ["GSK", "Modbus", "OPC UA", "Profibus", "S7", "NC-Link", "MTConnect", "FOCAS", "EtherNet/IP", "Profinet", "MQTT", "FTP", "NFS", "SMB"];
export const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value));
export const sumWeights = (nodes: MetricNode[]) => Number(nodes.reduce((sum, node) => sum + node.weight, 0).toFixed(10));
export const weightBalanced = (weight: number) => Math.abs(weight - 100) < 0.05;
export const levelLabels: Record<MetricLevel, string> = { section: "一级分类", subcategory: "二级分类", item: "评价项目" };

// Called only for an explicit edit/save, never from a computed value or watcher.
export function normalizeWeights(sections: EvaluationSection[]): void {
	const balance = (nodes: MetricNode[]) => {
		if (!nodes.length || nodes.some(node => !Number.isFinite(node.weight) || node.weight < 0 || node.weight > 100)) return;
		const difference = 100 - nodes.reduce((sum, node) => sum + node.weight, 0);
		if (!difference || Math.abs(difference) >= 2) return;
		const last = nodes[nodes.length - 1]!;
		const adjusted = Number((last.weight + difference).toFixed(10));
		if (adjusted >= 0 && adjusted <= 100) last.weight = adjusted;
	};
	balance(sections);
	for (const section of sections) {
		balance(section.children);
		for (const child of section.children) balance(child.items);
	}
}

export function createMetric(level: MetricLevel): MetricNode {
	const base = { id: crypto.randomUUID(), name: "", weight: 0 };
	if (level === "section") return { ...base, children: [] };
	if (level === "subcategory") return { ...base, items: [] };
	return { ...base, desc: "", method: "", evidenceType: "standard", evidence: "", standards: "", files: [], protocols: [], scoring: {} };
}

export function updateMetric(sections: EvaluationSection[], edit: MetricEdit): void {
	if (edit.level === "section") {
		const section = edit.node as EvaluationSection;
		const index = sections.findIndex(node => node.id === section.id);
		if (edit.isNew) sections.push(section);
		else if (index >= 0) sections.splice(index, 1, section);
		return;
	}
	for (const section of sections) {
		if (edit.level === "subcategory" && section.id === edit.parentId) {
			const child = edit.node as EvaluationSubcategory;
			const index = section.children.findIndex(node => node.id === child.id);
			if (edit.isNew) section.children.push(child);
			else if (index >= 0) section.children.splice(index, 1, child);
		}
		for (const child of section.children) {
			if (edit.level !== "item" || child.id !== edit.parentId) continue;
			const item = edit.node as EvaluationItem;
			const index = child.items.findIndex(node => node.id === item.id);
			if (edit.isNew) child.items.push(item);
			else if (index >= 0) child.items.splice(index, 1, item);
		}
	}
}

export function removeMetric(sections: EvaluationSection[], id: string): EvaluationSection[] {
	return sections.filter(section => section.id !== id).map(section => ({
		...section, children: section.children.filter(child => child.id !== id).map(child => ({
			...child, items: child.items.filter(item => item.id !== id),
		})),
	}));
}

export function metricRows(sections: EvaluationSection[]): MetricRow[] {
	return sections.map(section => ({
		id: section.id, level: "section", node: section, siblingWeight: sumWeights(sections),
		children: section.children.map(child => ({
			id: child.id, level: "subcategory", node: child, parentId: section.id,
			parentName: section.name, siblingWeight: sumWeights(section.children),
			children: child.items.map(item => ({
				id: item.id, level: "item", node: item, parentId: child.id,
				parentName: child.name, siblingWeight: sumWeights(child.items),
			})),
		})),
	}));
}
