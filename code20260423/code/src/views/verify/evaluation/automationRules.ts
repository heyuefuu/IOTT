import type { EvaluationAutomationRule, EvaluationItem, EvaluationTestSettings, EvaluationUnit } from "@/api/evaluation";

export const metricNames: Record<string, string> = {
	"industrial-protocol": "工控协议覆盖率", "communication-stability": "通讯稳定性", "max-connections": "最大并发连接数",
	"transfer-protocol": "传输协议覆盖数", "file-integrity": "文件完整性", "transfer-speed": "传输速度", "file-size": "文件大小",
};
export const readDataTypes = ["Bool", "Int8", "UInt8", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Float", "Double", "String", "ByteArray"];
export const defaultTestSettings = (): EvaluationTestSettings => ({
	durationMinutes: 30, sampleIntervalSeconds: 5, maxConnections: 4, failureLimit: 3, concurrencyMode: "devices",
	readAddress: "", readDataType: "String", targetDirectory: "/",
});

// Mirrors the seven explicit server defaults; never infer a binding or rule from descriptive text.
export function defaultFor(metricId?: string | null): EvaluationAutomationRule | null {
	const defaults: Record<string, [EvaluationUnit, [number, number][]]> = {
		"industrial-protocol": ["%", []],
		"communication-stability": ["min", [[30, 100], [20, 80], [10, 60], [0, 0]]],
		"max-connections": ["count", [[4, 100], [3, 80], [2, 60], [0, 0]]],
		"transfer-protocol": ["count", [[2, 100], [1, 60], [0, 0]]],
		"file-integrity": ["%", [[100, 100], [75, 80], [50, 60], [0, 0]]],
		"transfer-speed": ["MB/s", [[10, 100], [5, 80], [2, 60], [0, 0]]],
		"file-size": ["MB", [[200, 100], [10, 80], [1, 60], [0, 0]]],
	};
	const entry = Object.prototype.hasOwnProperty.call(defaults, metricId ?? "") ? defaults[metricId ?? ""] : undefined;
	return entry ? {
		unit: entry[0], passRule: null, scoringMode: metricId === "industrial-protocol" ? "linear" : "bands",
		scoreBands: entry[1].map(([min, score]) => ({ min, score })), test: defaultTestSettings(),
	} : null;
}

const units: Record<EvaluationUnit, [string, number]> = {
	"%": ["percent", 1], count: ["count", 1], s: ["time", 1], min: ["time", 60], h: ["time", 3600],
	B: ["bytes", 1], KB: ["bytes", 1024], KiB: ["bytes", 1024], MB: ["bytes", 1048576], MiB: ["bytes", 1048576],
	"B/s": ["speed", 1], "KB/s": ["speed", 1024], "KiB/s": ["speed", 1024], "MB/s": ["speed", 1048576], "MiB/s": ["speed", 1048576],
};
export function compatibleUnits(unit: EvaluationUnit): EvaluationUnit[] {
	return (Object.keys(units) as EvaluationUnit[]).filter(candidate => units[candidate][0] === units[unit]?.[0]);
}
export function convert(value: number, from: EvaluationUnit, to: EvaluationUnit): number | null {
	if (!Number.isFinite(value) || !units[from] || !units[to] || units[from][0] !== units[to][0]) return null;
	const converted = value * (units[from][1] / units[to][1]);
	return Number.isFinite(converted) ? converted : null;
}
export function changeUnit(rule: EvaluationAutomationRule, unit: EvaluationUnit): void {
	if (convert(1, rule.unit, unit) === null) return;
	const converted = rule.scoreBands.map(band => convert(band.min, rule.unit, unit));
	if (converted.some(value => value === null)) return;
	rule.scoreBands = rule.scoreBands.map((band, index) => ({ ...band, min: converted[index]! }));
	rule.unit = unit;
}
function validValue(value: number, unit: EvaluationUnit): boolean {
	return !!units[unit] && Number.isFinite(value) && value >= 0
		&& (unit !== "%" || value <= 100) && (unit !== "count" || Number.isInteger(value));
}
export function validate(item: EvaluationItem): string | undefined {
	const manual = item.manualPassRule;
	if (manual) {
		if (item.metricId) return "自动测试项目请配置自动达标阈值，不要同时配置人工阈值";
		if (!["gte", "eq", "lte"].includes(manual.comparison) || !Number.isFinite(manual.threshold)
			|| typeof manual.unit !== "string" || !manual.unit.trim() || manual.unit.length > 50
			|| /[\u0000-\u001f\u007f-\u009f]/u.test(manual.unit)) return "请填写有效的人工评价阈值、比较方式和单位";
	}
	const rule = item.automation;
	if (!rule) return;
	const defaults = defaultFor(item.metricId);
	if (!defaults || convert(1, defaults.unit, rule.unit) === null) return "测量单位与自动测试项目不兼容";
	if (!["linear", "bands"].includes(rule.scoringMode) || !Array.isArray(rule.scoreBands) || rule.scoreBands.length > 100) return "评分模式或档位无效";
	if (rule.scoringMode === "linear" && (rule.unit !== "%" || rule.scoreBands.length)) return "线性评分仅适用于百分比且不能同时设置档位";
	if (rule.scoringMode === "bands" && !rule.scoreBands.length) return "请至少配置一个评分档位";
	if (rule.scoreBands.some(band => !band)) return "评分档位不能为空";
	const bands = [...rule.scoreBands].sort((a, b) => a.min - b.min);
	for (const [index, band] of bands.entries()) {
		const previous = bands[index - 1];
		if (!validValue(band.min, rule.unit) || !Number.isFinite(band.score) || band.score < 0 || band.score > 100
			|| (previous && (previous.min === band.min || previous.score > band.score))) return "档位下限应合法且不重复，分数应在 0–100 之间并随下限递增";
	}
	const pass = rule.passRule;
	if (pass && (!["gte", "eq", "lte"].includes(pass.comparison) || !validValue(pass.threshold, pass.unit)
		|| convert(pass.threshold, pass.unit, rule.unit) === null)) return "达标线应使用合法阈值、比较方式及兼容单位";
	const test = rule.test;
	if (!test || !Number.isFinite(test.durationMinutes) || test.durationMinutes <= 0 || test.durationMinutes > 1440
		|| !Number.isFinite(test.sampleIntervalSeconds) || test.sampleIntervalSeconds <= 0 || test.sampleIntervalSeconds > 86400
		|| !Number.isInteger(test.maxConnections) || test.maxConnections < 1 || test.maxConnections > 64
		|| !Number.isInteger(test.failureLimit) || test.failureLimit < 1 || test.failureLimit > 100
		|| !["devices", "connections", "sessions"].includes(test.concurrencyMode)) return "请检查测试时长、采样间隔、并发上限与失败次数";
	if (typeof test.readAddress !== "string" || test.readAddress.length > 500 || /[\u0000-\u001f\u007f-\u009f]/u.test(test.readAddress)
		|| !readDataTypes.includes(test.readDataType)) return "读取地址或数据类型无效";
	if (typeof test.targetDirectory !== "string" || !test.targetDirectory.trim() || test.targetDirectory.length > 1024
		|| /[\u0000-\u001f\u007f-\u009f]/u.test(test.targetDirectory) || test.targetDirectory.replace(/\\/g, "/").split("/").some(part => part === "." || part === "..")) return "测试目录不能为空、含控制字符或相对跳转路径";
}

export function normalizeProtocol(protocol: string): string {
	const key = protocol.replace(/[^\p{L}\p{N}]/gu, "").toUpperCase();
	const aliases: Record<string, string> = {
		OPCUA: "OPC UA", OPCOPCUA: "OPC UA", MODBUS: "Modbus", MODBUSTCP: "Modbus", MODBUSRTU: "Modbus",
		NCLINK: "NC-Link", NCLINKAPI: "NC-Link", GSK: "GSK", GSKWEBSERVER: "GSK", GSKRM: "GSK",
		ETHERNETIP: "EtherNet/IP", PROFINET: "Profinet", PROFIBUS: "Profibus", PROFIBUSDP: "Profibus", MTCONNECT: "MTConnect",
		SIEMENSS7: "S7", S7COMM: "S7", S7: "S7", FANUC: "FOCAS", FANUCFOCAS: "FOCAS", FOCAS: "FOCAS",
	};
	return aliases[key] ?? key;
}

export function thresholdSummary(item: EvaluationItem): string {
	const pass = item.metricId ? item.automation?.passRule : item.manualPassRule;
	if (!pass) return "未配置";
	const comparison = { gte: "≥", eq: "=", lte: "≤" };
	return `${comparison[pass.comparison]} ${pass.threshold} ${pass.unit}${item.metricId ? "" : "（人工核验）"}`;
}

export function ruleSummary(item: EvaluationItem): string {
	const rule = item.automation;
	if (!rule) return item.metricId ? "未启用结构化规则，不自动评分"
		: `人工评价项目，未关联自动测试；达标阈值：${thresholdSummary(item)}`;
	const pass = rule.passRule;
	const comparison = { gte: "≥", eq: "=", lte: "≤" };
	const scoring = rule.scoringMode === "linear" ? "百分比直接映射 0–100 分"
		: [...rule.scoreBands].sort((a, b) => b.min - a.min).map(band => `≥${Number(band.min.toPrecision(10))} ${rule.unit} → ${band.score}分`).join("；");
	const test = rule.test;
	const point = `读取 ${test.readAddress || "设备已有点位（无则不测）"}，类型 ${test.readDataType}`;
	const scope = [...new Set((item.protocols ?? []).map(normalizeProtocol))].join("、") || "未选择协议";
	const settings = item.metricId === "communication-stability"
		? `测试 ${test.durationMinutes} min，采样间隔 ${test.sampleIntervalSeconds} s；${point}`
		: item.metricId === "max-connections"
			? `${test.concurrencyMode === "devices" ? "显式多设备" : "独立会话"}，上限 ${test.maxConnections}，连续失败 ${test.failureLimit} 次停止；${point}`
			: item.metricId === "industrial-protocol" ? `标准集合 ${scope}；${point}`
				: `${item.metricId === "transfer-protocol" ? `标准集合 ${scope}；` : ""}文件测试目录 ${test.targetDirectory}`;
	return `达标线：${pass ? `${comparison[pass.comparison]} ${pass.threshold} ${pass.unit}` : "未配置（不能自动判通过）"}。评分：${scoring}。测试设置：${settings}。`;
}
