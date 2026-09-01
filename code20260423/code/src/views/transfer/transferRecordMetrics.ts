import type { ProgramTransferResponse } from "@/api/machineConnectionProgramTransfer";

/** 文件传输支持的协议（上游 IndustrialIoT ProtocolType 真实枚举：FTP/SMB/NFS）。 */
export const TRANSFER_PROTOCOLS: readonly string[] = ["FTP", "SMB", "NFS"];

export type IntegrityState = "pending" | "verified" | "size-matched" | "failed" | "unknown";

function getDurationMs(row: ProgramTransferResponse, nowMs = Date.now()): number {
	if (row.durationMs != null && row.durationMs > 0) return row.durationMs;
	const startedAt = Date.parse(row.startedAt);
	if (!Number.isFinite(startedAt)) return 0;
	const completedAt = row.completedAt ? Date.parse(row.completedAt) : nowMs;
	return Number.isFinite(completedAt) ? Math.max(0, completedAt - startedAt) : 0;
}

export function calculateTransferSpeed(
	row: ProgramTransferResponse,
	nowMs = Date.now(),
): number {
	const durationMs = getDurationMs(row, nowMs);
	if (durationMs <= 0 || row.bytesTransferred <= 0) return 0;
	const speed = row.bytesTransferred / 1024 / 1024 / (durationMs / 1000);
	return Math.round(speed * 100) / 100;
}

export function getIntegrityState(row: ProgramTransferResponse): IntegrityState {
	if (row.status !== "Completed") return row.status === "Failed" ? "failed" : "pending";
	if (row.fileSize <= 0) return "unknown";
	if (row.bytesTransferred !== row.fileSize) return "failed";
	return row.checksum?.trim() ? "verified" : "size-matched";
}

export function getTransferPercent(row: ProgramTransferResponse): number {
	if (row.status === "Completed") return 100;
	if (row.fileSize <= 0) return 0;
	return Math.min(100, Math.max(0, Math.round(row.bytesTransferred / row.fileSize * 100)));
}

export function formatFileSize(size: number): string {
	if (!Number.isFinite(size) || size < 0) return "-";
	if (size < 1024) return `${size} B`;
	if (size < 1024 * 1024) return `${(size / 1024).toFixed(2)} KB`;
	if (size < 1024 * 1024 * 1024) return `${(size / 1024 / 1024).toFixed(2)} MB`;
	return `${(size / 1024 / 1024 / 1024).toFixed(2)} GB`;
}
