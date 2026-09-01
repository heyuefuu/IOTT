import {
	machineConnectionPointsApi,
	type ReadTagResult,
} from "@/api/machineConnectionPoints";

/**
 * 埃斯顿机器人（ESTUN ER / ProNet）专用 API 门面。
 *
 * 全部走通用点位通道 `POST /api/data/{deviceId}/read|write`（网关对非 NC-Link 设备为纯透传），
 * 由上游 Industrial IoT 的 `EstunRobotDriver` 解析地址。地址语义与该驱动一一对应：
 *   DATA:<字段>  → EstunData 快照字段（源自 HslCommunication EstunTcpNet.ReadRobotData）
 *   CMD:<指令>   → 只写，触发机器人动作
 *   ESTUN_DATA   → 整机快照 JSON 字符串
 *   纯数字/Hsl 语法 → 原始 Modbus 寄存器直传（如 "36"、"0x0040"）
 *
 * 快照字段一次性批量读取：驱动侧对同一批次只发一次 ReadRobotData 报文，
 * 因此下面 16 个点位合计只有 1 次 HTTP 往返 + 1 次机器人往返。
 */

/** 机器人状态快照，字段与 HslCommunication EstunData 对齐 */
export interface EstunSnapshot {
	/** 错误状态（true = 有错误） */
	errorStatus: boolean
	/** 使能状态 */
	enableStatus: boolean
	/** 运行状态 */
	runStatus: boolean
	/** 程序运行状态 */
	programRunStatus: boolean
	/** 机器人正在动作 */
	robotMoving: boolean
	/** 手动模式 */
	manualMode: boolean
	/** 自动模式 */
	autoMode: boolean
	/** 远程模式 */
	remoteMode: boolean
	/** 全局速度值 */
	globalSpeedValue: number
	/** 读写标志位 */
	readWriteFlag: number
	/** 机器人执行命令状态（16 位，界面按十六进制展示） */
	robotCommandStatus: number
	/** 当前加载的工程名 */
	projectName: string
	/** SimDI，64 位 */
	diBits: boolean[]
	/** SimDout，64 位 */
	doBits: boolean[]
	/** 用户 AI，32 个 */
	aiValues: number[]
	/** 用户 AO，32 个 */
	aoValues: number[]
	/** 本次读取时间 */
	readAt: Date
}

/** 可下发的机器人指令，与 EstunRobotDriver 的 CMD: 地址一致 */
export type EstunCommand =
	| "Start"
	| "Stop"
	| "ResetError"
	| "UnregisterProject"
	| "CommandStatusRestart"

export const ESTUN_COMMAND_LABELS: Record<EstunCommand, string> = {
	Start: "启动程序",
	Stop: "停止程序",
	ResetError: "错误复位",
	UnregisterProject: "卸载工程",
	CommandStatusRestart: "状态指令集重置",
}

/** 快照批量读取的点位清单（address → dataType） */
const SNAPSHOT_TAGS: ReadonlyArray<{ address: string; dataType: string }> = [
	{ address: "DATA:ErrorStatus", dataType: "Bool" },
	{ address: "DATA:EnableStatus", dataType: "Bool" },
	{ address: "DATA:RunStatus", dataType: "Bool" },
	{ address: "DATA:ProgramRunStatus", dataType: "Bool" },
	{ address: "DATA:RobotMoving", dataType: "Bool" },
	{ address: "DATA:ManualMode", dataType: "Bool" },
	{ address: "DATA:AutoMode", dataType: "Bool" },
	{ address: "DATA:RemoteMode", dataType: "Bool" },
	{ address: "DATA:GlobalSpeedValue", dataType: "Int16" },
	{ address: "DATA:ReadWriteFlag", dataType: "Int16" },
	{ address: "DATA:RobotCommandStatus", dataType: "UInt16" },
	{ address: "DATA:ProjectName", dataType: "String" },
	{ address: "DATA:DI", dataType: "String" },
	{ address: "DATA:DO", dataType: "String" },
	{ address: "DATA:AI", dataType: "String" },
	{ address: "DATA:AO", dataType: "String" },
]

function isBad(tag: ReadTagResult | undefined): boolean {
	if (!tag) return true
	return tag.quality !== "Good" || !!tag.errorMessage
}

function asBool(v: unknown): boolean {
	if (typeof v === "boolean") return v
	if (typeof v === "number") return v !== 0
	if (typeof v === "string") return v === "true" || v === "True" || v === "1"
	return false
}

function asNumber(v: unknown): number {
	if (typeof v === "number") return v
	const n = Number(v)
	return Number.isFinite(n) ? n : 0
}

function asString(v: unknown): string {
	return v == null ? "" : String(v)
}

/**
 * 驱动把 DI/DO/AI/AO 数组序列化成 JSON 字符串返回（DataType.String），
 * 这里解析回数组；解析失败或类型不符时返回空数组，由界面显示"无数据"。
 */
function asArray<T>(v: unknown, coerce: (item: unknown) => T): T[] {
	let raw: unknown = v
	if (typeof v === "string") {
		try {
			raw = JSON.parse(v)
		} catch {
			return []
		}
	}
	return Array.isArray(raw) ? raw.map(coerce) : []
}

export const machineConnectionEstunApi = {
	/**
	 * 读取整机状态快照（1 次 HTTP + 1 次机器人报文）。
	 * 任一关键点位失败即抛出，错误信息取第一条失败点位的 errorMessage。
	 */
	async readSnapshot(deviceId: string): Promise<EstunSnapshot> {
		const res = await machineConnectionPointsApi.readTags(deviceId, {
			tags: SNAPSHOT_TAGS.map((t) => ({ ...t })),
		})

		const byAddress = new Map<string, ReadTagResult>()
		for (const tag of res.tags) byAddress.set(tag.address, tag)

		const firstBad = SNAPSHOT_TAGS.map((t) => byAddress.get(t.address)).find(isBad)
		if (firstBad) {
			throw new Error(
				firstBad.errorMessage ?? "读取埃斯顿机器人快照失败（点位质量不佳）",
			)
		}

		const val = (address: string): unknown => byAddress.get(address)?.value

		return {
			errorStatus: asBool(val("DATA:ErrorStatus")),
			enableStatus: asBool(val("DATA:EnableStatus")),
			runStatus: asBool(val("DATA:RunStatus")),
			programRunStatus: asBool(val("DATA:ProgramRunStatus")),
			robotMoving: asBool(val("DATA:RobotMoving")),
			manualMode: asBool(val("DATA:ManualMode")),
			autoMode: asBool(val("DATA:AutoMode")),
			remoteMode: asBool(val("DATA:RemoteMode")),
			globalSpeedValue: asNumber(val("DATA:GlobalSpeedValue")),
			readWriteFlag: asNumber(val("DATA:ReadWriteFlag")),
			robotCommandStatus: asNumber(val("DATA:RobotCommandStatus")),
			projectName: asString(val("DATA:ProjectName")),
			diBits: asArray(val("DATA:DI"), asBool),
			doBits: asArray(val("DATA:DO"), asBool),
			aiValues: asArray(val("DATA:AI"), asNumber),
			aoValues: asArray(val("DATA:AO"), asNumber),
			readAt: new Date(),
		}
	},

	/** 读取驱动原始快照 JSON（调试用，对应 ESTUN_DATA 地址） */
	async readRawSnapshot(deviceId: string): Promise<string> {
		const res = await machineConnectionPointsApi.readTags(deviceId, {
			tags: [{ address: "ESTUN_DATA", dataType: "String" }],
		})
		const tag = res.tags[0]
		if (isBad(tag)) throw new Error(tag?.errorMessage ?? "读取整机快照失败")
		return asString(tag?.value)
	},

	/** 下发无参指令（启动/停止/复位/卸载工程/状态重置） */
	async sendCommand(deviceId: string, command: EstunCommand): Promise<void> {
		await writeOne(deviceId, `CMD:${command}`, "Bool", true)
	},

	/** 装载工程文件（对应 RobotLoadProject） */
	async loadProject(deviceId: string, projectName: string): Promise<void> {
		const name = projectName.trim()
		if (!name) throw new Error("工程名不能为空")
		await writeOne(deviceId, "CMD:LoadProject", "String", name)
	},

	/** 设置全局速度（对应 RobotSetGlobalSpeedValue） */
	async setGlobalSpeed(deviceId: string, value: number): Promise<void> {
		await writeOne(deviceId, "DATA:GlobalSpeedValue", "Int16", Math.trunc(value))
	},

	/**
	 * 原始 Modbus 寄存器直写。
	 * 用于 HslCommunication 示例里的"强制下载 0x801"（`estun.Write("36", (short)0x801)`）
	 * 等厂商调试动作，地址走 Hsl 原生语法。
	 */
	async writeRawRegister(
		deviceId: string,
		address: string,
		value: number,
		dataType: string = "Int16",
	): Promise<void> {
		await writeOne(deviceId, address.trim(), dataType, Math.trunc(value))
	},
}

async function writeOne(
	deviceId: string,
	address: string,
	dataType: string,
	value: unknown,
): Promise<void> {
	const res = await machineConnectionPointsApi.writeTags(deviceId, {
		tags: [{ address, dataType, value }],
	})
	const r = res.results[0]
	if (!r?.success) throw new Error(r?.errorMessage ?? `写入 ${address} 失败`)
}
