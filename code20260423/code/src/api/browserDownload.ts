export interface BrowserDownloadDeps {
    document?: Document;
    urlApi?: Pick<typeof URL, "createObjectURL" | "revokeObjectURL">;
    setTimeout?: (handler: () => void, timeout: number) => unknown;
    revokeDelayMs?: number;
}

const DEFAULT_REVOKE_DELAY_MS = 60_000;

export function downloadBlob(
    blob: Blob,
    fileName: string,
    deps: BrowserDownloadDeps = {},
): void {
    if (blob.size <= 0) {
        throw new Error("下载响应为空，请检查接口响应或代理配置");
    }

    const doc = deps.document ?? document;
    const urlApi = deps.urlApi ?? URL;
    const schedule =
        deps.setTimeout ?? ((handler, timeout) => window.setTimeout(handler, timeout));
    const url = urlApi.createObjectURL(blob);
    const a = doc.createElement("a");
    a.href = url;
    a.download = fileName;
    doc.body.appendChild(a);
    a.click();
    a.remove();
    schedule(() => urlApi.revokeObjectURL(url), deps.revokeDelayMs ?? DEFAULT_REVOKE_DELAY_MS);
}
