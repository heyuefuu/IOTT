import type { DataTypeApi, ReadTagRequest } from "@/api/machineConnectionPoints";

export interface ProbeArea {
	id: string;
	label: string;
	dataType: DataTypeApi;
	address: (index: number, dbNumber: number) => string;
	needsDb?: boolean;
}

export interface ProbeProfile {
	areas: ProbeArea[];
	note: string;
}

const words = (prefixes: string[], dataType: DataTypeApi = "UInt16", step = 1): ProbeArea[] =>
	prefixes.map((prefix) => ({ id: prefix, label: `${prefix} (${dataType})`, dataType,
		address: (index) => `${prefix}${index * step}` }));

const bits = (prefixes: string[], radix = 10, wordSize = 0): ProbeArea[] =>
	prefixes.map((prefix) => ({ id: prefix, label: `${prefix} (Bool)`, dataType: "Bool",
		address: (index) => wordSize
			? `${prefix}${Math.floor(index / wordSize)}.${(index % wordSize).toString(radix).toUpperCase()}`
			: `${prefix}${index.toString(radix).toUpperCase()}` }));

const modbusAreas = [...words(["HR", "IR"]), ...bits(["C", "DI"])];
const omronAreas = words(["DM", "CIO", "WR", "HR", "AR"]);
const s7Areas: ProbeArea[] = [
	...words(["IB", "QB", "MB", "VB", "SMB", "PB"], "UInt8"),
	...words(["AIW", "AQW"], "UInt16", 2),
	{ id: "DB", label: "DB 数据块 (UInt8)", dataType: "UInt8", needsDb: true,
		address: (index, dbNumber) => `DB${dbNumber}.DBB${index}` },
	...words(["T", "C"]),
];

export function getProbeProfile(protocol: string, properties: Record<string, string> = {}): ProbeProfile | undefined {
	if (["ModbusTCP", "ModbusRTU", "Profibus"].includes(protocol)) return {
		areas: modbusAreas,
		note: protocol === "Profibus" ? "读取已配置网关的 Modbus 映射区，索引从 0 开始。" : "索引是从 0 开始的协议地址。",
	};
	if (["FINS", "OmronHostLink"].includes(protocol)) return {
		areas: omronAreas, note: "索引按字递增，以 UInt16 读取各区域的原始字值。",
	};
	if (protocol === "SiemensS7") return {
		areas: s7Areas, note: "I/Q/M/V/DB 等区域按字节偏移读取；AIW/AQW 按字序号生成偶数字节偏移；T/C 按编号读取。DB 块号须来自设备组态。",
	};
	return getVendorProbeProfile(protocol, properties);
}

function getVendorProbeProfile(protocol: string, properties: Record<string, string>): ProbeProfile | undefined {
	if (["Mewtocol", "MewtocolSerial"].includes(protocol)) return {
		areas: [...words(["DT", "LD", "T", "C"]), ...bits(["X", "Y", "R", "L"], 16, 16)],
		note: "DT/LD/T/C 按字或编号递增；X/Y/R/L 输入连续位序号，自动转换为字号和十六进制位号。",
	};
	if (!["Inovance", "InovanceSerial", "InovanceSerialOverTcp"].includes(protocol)) return undefined;
	const series = Object.entries(properties).find(([key]) => key.toLowerCase() === "series")?.[1]?.trim().toUpperCase();
	if (["AM", "AM400", "AM400-800", "AM400_800", "AM600", "AM800", "AC", "AP"].includes(series ?? "")) return {
		areas: [...words(["MW", "SD"]), ...bits(["Q", "IX", "MX"], 10, 8)],
		note: "AM 系列：MW/SD 按地址索引读取；Q/IX/MX 输入连续位序号，自动转换为字节号和位号。",
	};
	if (["H3U", "XP", "H5U", "EASY"].includes(series ?? "")) return {
		areas: [...words(series === "H3U" || series === "XP" ? ["D", "SD", "R"] : ["D", "R"]),
			...bits(series === "H3U" || series === "XP" ? ["M", "SM", "S", "T", "C"] : ["M", "B", "S"]), ...bits(["X", "Y"], 8)],
		note: `${series} 系列：输入十进制连续序号；X/Y 自动转换为八进制编号，例如序号 8 对应 X10/Y10。`,
	};
	return { areas: [], note: "请先在设备配置中选择汇川 PLC 系列（H3U、H5U、AM 或 Easy），再进行探测。" };
}

export function createProbeTags(area: ProbeArea, start: number, end: number, dbNumber: number): ReadTagRequest[] {
	if (!Number.isInteger(start) || !Number.isInteger(end) || start < 0 || end > 65535 || end < start || end - start + 1 > 64) {
		throw new Error("请输入 0–65535 内连续且不超过 64 个点的索引范围");
	}
	if (area.needsDb && (!Number.isInteger(dbNumber) || dbNumber < 1 || dbNumber > 65535)) {
		throw new Error("DB 块号必须是 1–65535 内的整数");
	}
	return Array.from({ length: end - start + 1 }, (_, index) => ({
		address: area.address(start + index, dbNumber), dataType: area.dataType,
	}));
}
