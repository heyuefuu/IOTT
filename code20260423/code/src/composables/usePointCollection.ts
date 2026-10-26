import { reactive } from "vue";
import {
	machineConnectionCollectionApi,
	type CollectionGroupConfig,
} from "@/api/machineConnectionCollection";
import { machineConnectionPointsApi } from "@/api/machineConnectionPoints";
import {
	telemetryInfluxApi,
	type TelemetryBatchResult,
} from "@/api/telemetryInflux";

export interface PointCollectionRow {
	name: string;
	path: string;
	address: string;
	dataType: string;
	value: string;
	quality: string;
	status: string;
	errorMessage: string;
	time: string;
}

export interface PointCollectionGroupInfo {
	groupName: string;
	intervalMs: number;
	tagCount: number;
}

export interface PointCollectionSession {
	deviceId: string;
	deviceName: string;
	running: boolean;
	starting: boolean;
	profileNames: string[];
	groups: PointCollectionGroupInfo[];
	rows: PointCollectionRow[];
	lastCollectedAt: string;
	lastBatch: TelemetryBatchResult | null;
	storageError: string;
	error: string;
}

const sessions = reactive<Record<string, PointCollectionSession>>({});
const timers = new Map<string, number[]>();
const inFlight = new Set<string>();

function emptySession(deviceId: string, deviceName: string): PointCollectionSession {
	return {
		deviceId,
		deviceName,
		running: false,
		starting: false,
		profileNames: [],
		groups: [],
		rows: [],
		lastCollectedAt: "",
		lastBatch: null,
		storageError: "",
		error: "",
	};
}

export function sessionFor(deviceId: string, deviceName = ""): PointCollectionSession {
	if (!sessions[deviceId]) sessions[deviceId] = emptySession(deviceId, deviceName);
	else if (deviceName) sessions[deviceId].deviceName = deviceName;
	return sessions[deviceId];
}

function normalizeAddress(address: string) {
	return address.trim().toLowerCase();
}

function stringifyValue(value: unknown): string {
	if (value === null || value === undefined) return "-";
	if (typeof value === "string") return value;
	if (typeof value === "number" || typeof value === "boolean") return String(value);
	try {
		return JSON.stringify(value);
	} catch {
		return String(value);
	}
}

function getErrorMessage(error: unknown, fallback: string): string {
	const ax = error as { response?: { data?: { error?: string; message?: string } }; message?: string };
	return ax.response?.data?.error || ax.response?.data?.message || ax.message || fallback;
}

function clearTimers(deviceId: string) {
	for (const timer of timers.get(deviceId) ?? []) window.clearInterval(timer);
	timers.delete(deviceId);
}

async function readGroup(deviceId: string, group: CollectionGroupConfig, groupIndex: number) {
	const session = sessionFor(deviceId);
	if (!session.running) return;
	const flightKey = `${deviceId}:${groupIndex}`;
	if (inFlight.has(flightKey)) return;
	inFlight.add(flightKey);
	try {
		const response = await machineConnectionPointsApi.readTags(deviceId, {
			tags: group.tags.map((tag) => ({ address: tag.address, dataType: tag.dataType })),
		});
		if (!session.running) return;
		const byAddress = new Map(response.tags.map((tag) => [normalizeAddress(tag.address), tag]));
		const now = new Date().toLocaleString();
		const nextRows = new Map(session.rows.map((row) => [row.path, row]));
		const points = group.tags.map((tag) => {
			const result = byAddress.get(normalizeAddress(tag.address));
			const failed = !result || Boolean(result.errorMessage?.trim());
			const row: PointCollectionRow = {
				name: tag.displayName || tag.address,
				path: tag.address,
				address: tag.address,
				dataType: tag.dataType,
				value: failed ? "-" : stringifyValue(result?.value),
				quality: result?.quality || "",
				status: failed ? "失败" : "成功",
				errorMessage: failed ? result?.errorMessage?.trim() || "未返回该点位" : "",
				time: now,
			};
			nextRows.set(row.path, row);
			return {
				name: row.name,
				path: row.path,
				dataType: row.dataType,
				value: failed ? null : result?.value ?? null,
				quality: row.quality,
				timestamp: result?.timestamp || "",
				status: row.status,
				errorMessage: row.errorMessage || null,
			};
		});
		session.rows = [...nextRows.values()];
		session.lastCollectedAt = now;
		try {
			const batch = await telemetryInfluxApi.writeBatch({
				deviceId,
				collectedAt: new Date().toISOString(),
				points,
			});
			session.lastBatch = batch;
			session.storageError = batch.storageEnabled
				? batch.storageError || ""
				: "Influx 存储未启用";
		} catch (error: unknown) {
			session.storageError = getErrorMessage(error, "采集数据写入 Influx 失败");
		}
	} catch (error: unknown) {
		session.error = getErrorMessage(error, "读取采集点位失败");
	} finally {
		inFlight.delete(flightKey);
	}
}

async function start(deviceId: string, deviceName = "") {
	const session = sessionFor(deviceId, deviceName);
	if (session.running || session.starting) return;
	session.starting = true;
	session.error = "";
	session.storageError = "";
	session.rows = [];
	session.lastBatch = null;
	clearTimers(deviceId);
	try {
		const profiles = await machineConnectionCollectionApi.listProfiles(deviceId);
		const enabled = profiles.filter((profile) => profile.isEnabled);
		const groups = enabled.flatMap((profile) =>
			profile.groups.filter((group) => group.tags.length > 0),
		);
		if (groups.length === 0) {
			throw new Error("该设备没有启用的采集点位，请先在采集配置/导入点位中保存点位");
		}
		session.profileNames = enabled.map((profile) => profile.name);
		session.groups = groups.map((group) => ({
			groupName: group.groupName,
			intervalMs: group.intervalMs,
			tagCount: group.tags.length,
		}));
		const rows = new Map<string, PointCollectionRow>();
		for (const group of groups) {
			for (const tag of group.tags) {
				rows.set(tag.address, {
					name: tag.displayName || tag.address,
					path: tag.address,
					address: tag.address,
					dataType: tag.dataType,
					value: "-",
					quality: "",
					status: "待采集",
					errorMessage: "",
					time: "",
				});
			}
		}
		session.rows = [...rows.values()];
		session.running = true;
		const newTimers: number[] = [];
		groups.forEach((group, index) => {
			void readGroup(deviceId, group, index);
			const interval = Math.min(3_600_000, Math.max(200, Number(group.intervalMs) || 1000));
			newTimers.push(window.setInterval(() => void readGroup(deviceId, group, index), interval));
		});
		timers.set(deviceId, newTimers);
	} catch (error: unknown) {
		session.error = getErrorMessage(error, "启动采集失败");
		session.running = false;
	} finally {
		session.starting = false;
	}
}

function stop(deviceId: string) {
	clearTimers(deviceId);
	const session = sessionFor(deviceId);
	session.running = false;
	session.starting = false;
}

function stopAll() {
	for (const deviceId of [...timers.keys()]) stop(deviceId);
	for (const session of Object.values(sessions)) {
		session.running = false;
		session.starting = false;
	}
}

function isRunning(deviceId: string) {
	return Boolean(sessions[deviceId]?.running);
}

export function usePointCollection() {
	return { sessionFor, start, stop, stopAll, isRunning };
}
