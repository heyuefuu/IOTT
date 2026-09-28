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
	if (!["FTP", "SMB", "NFS", "GskrmFileTransfer", "Serial"].includes(protocol)) return undefined;

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

	if (protocol === "Serial") {
		const settings = form.transferExtendedProperties ?? {};
		for (const [key, fallback] of Object.entries({
			PortName: "", BaudRate: "9600", DataBits: "8", Parity: "None", StopBits: "One",
		})) extendedProperties[key] = String(settings[key] ?? fallback).trim();
	}
	const localTransport = ["GskrmFileTransfer", "Serial"].includes(protocol);
	return {
		protocol,
		host: protocol === "Serial" ? "localhost" : String(form.transferHost ?? "").trim(),
		port: localTransport ? 0 : Number(form.transferPort ?? 0),
		username: localTransport ? undefined : String(form.transferUsername ?? "").trim() || undefined,
		password: localTransport ? undefined : String(form.transferPassword ?? "").trim() || undefined,
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
