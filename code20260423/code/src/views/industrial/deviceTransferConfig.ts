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
	if (protocol !== "FTP" && protocol !== "SMB") return undefined;

	const extendedProperties: Record<string, string> = {};
	if (protocol === "SMB") {
		const shareName = String(form.transferShareName ?? "").trim();
		if (shareName) extendedProperties.ShareName = shareName;
	}

	return {
		protocol,
		host: String(form.transferHost ?? "").trim(),
		port: Number(form.transferPort ?? 0),
		username: String(form.transferUsername ?? "").trim() || undefined,
		password: String(form.transferPassword ?? "").trim() || undefined,
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
