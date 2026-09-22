export interface ProgramTransferFormFields {
	deviceType?: string;
	transferProtocol?: string;
	transferHost?: string;
	transferPort?: number;
	transferUsername?: string;
	transferPassword?: string;
	transferConnectTimeoutMs?: number;
	transferReadTimeoutMs?: number;
	transferShareName?: string;
	transferMountPoint?: string;
	originalTransferProtocol?: string;
	transferExtendedProperties?: Record<string, string | undefined>;
}

export interface ProgramTransferConfig {
	protocol: string;
	host: string;
	port: number;
	username?: string;
	password?: string;
	connectTimeoutMs: number;
	readTimeoutMs: number;
	extendedProperties: Record<string, string>;
}

export function buildProgramTransferConfig(
	form: ProgramTransferFormFields,
): ProgramTransferConfig | undefined {
	if (form.deviceType !== "CNC") return undefined;

	const protocol = String(form.transferProtocol ?? "").trim();
	if (!protocol) return undefined;
	if (!["FTP", "SMB", "NFS", "GskrmFileTransfer"].includes(protocol)) return undefined;

	const extendedProperties: Record<string, string> = {};
	if (protocol === form.originalTransferProtocol) {
		for (const [key, value] of Object.entries(form.transferExtendedProperties ?? {})) {
			if (typeof value === "string") extendedProperties[key] = value;
		}
	}
	if (protocol === "SMB") {
		delete extendedProperties.ShareName;
		const shareName = String(form.transferShareName ?? "").trim();
		if (shareName) extendedProperties.ShareName = shareName;
	}
	if (protocol === "NFS") {
		delete extendedProperties.MountPoint;
		const mountPoint = String(form.transferMountPoint ?? "").trim();
		if (mountPoint) extendedProperties.MountPoint = mountPoint;
	}

	return {
		protocol,
		host: String(form.transferHost ?? "").trim(),
		port: protocol === "GskrmFileTransfer" ? 0 : Number(form.transferPort ?? 0),
		username: protocol === "GskrmFileTransfer" ? undefined : String(form.transferUsername ?? "").trim() || undefined,
		password: protocol === "GskrmFileTransfer" ? undefined : String(form.transferPassword ?? "").trim() || undefined,
		connectTimeoutMs:
			typeof form.transferConnectTimeoutMs === "number" && form.transferConnectTimeoutMs > 0
				? form.transferConnectTimeoutMs
				: 10000,
		readTimeoutMs:
			typeof form.transferReadTimeoutMs === "number" && form.transferReadTimeoutMs > 0
				? form.transferReadTimeoutMs
				: 5000,
		extendedProperties,
	};
}
