export async function reportExportError(error: unknown): Promise<never> {
    const response = (error as { response?: { data?: unknown } } | null)?.response;
    if (response?.data instanceof Blob) {
        try {
            const data = JSON.parse(await response.data.text()) as { error?: string; message?: string };
            if (typeof data.error === 'string' || typeof data.message === 'string')
                throw new Error(data.error || data.message);
        } catch (parsed) {
            if (parsed instanceof Error && !(parsed instanceof SyntaxError)) throw parsed;
        }
    }
    throw error;
}
