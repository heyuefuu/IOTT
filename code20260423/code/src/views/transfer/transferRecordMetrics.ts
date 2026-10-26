import type { ProgramTransferResponse } from "@/api/machineConnectionProgramTransfer";

/** 文件传输协议名与上游 ProtocolType 枚举一致。 */
export const TRANSFER_PROTOCOLS: readonly string[] = ["FTP", "SMB", "NFS", "Serial"];

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

export function formatDuration(seconds: number): string {
	if (!Number.isFinite(seconds) || seconds <= 0) return "-";
	const total = Math.round(seconds);
	if (total < 60) return `${total}s`;
	const minutes = Math.floor(total / 60);
	const restSeconds = total % 60;
	if (minutes < 60)
		return `${minutes}m${restSeconds.toString().padStart(2, "0")}s`;
	const hours = Math.floor(minutes / 60);
	return `${hours}h${(minutes % 60).toString().padStart(2, "0")}m`;
}

/** 预计剩余时间（秒）；仅对进行中/已暂停且已产生进度的记录返回，否则 null。 */
export function getTransferEtaSeconds(
	row: ProgramTransferResponse,
	nowMs = Date.now(),
): number | null {
	if (row.status !== "InProgress" && row.status !== "Paused") return null;
	if (row.fileSize <= 0 || row.bytesTransferred <= 0) return null;
	if (row.bytesTransferred >= row.fileSize) return 0;
	const durationMs = getDurationMs(row, nowMs);
	if (durationMs <= 0) return null;
	const bytesPerMs = row.bytesTransferred / durationMs;
	if (bytesPerMs <= 0) return null;
	return (row.fileSize - row.bytesTransferred) / bytesPerMs / 1000;
}

/** 进度条颜色：绿=成功、红=失败、黄=已暂停、蓝=传输中、灰=等待。 */
export function transferProgressColor(row: ProgramTransferResponse): string {
	switch (row.status) {
		case "Completed":
			return "#67C23A";
		case "Failed":
			return "#F56C6C";
		case "Paused":
			return "#E6A23C";
		case "InProgress":
			return "#409EFF";
		default:
			return "#909399";
	}
}

export function transferProgressStatus(
	row: ProgramTransferResponse,
): "success" | "exception" | "warning" | undefined {
	switch (row.status) {
		case "Completed":
			return "success";
		case "Failed":
			return "exception";
		case "Paused":
			return "warning";
		default:
			return undefined;
	}
}
