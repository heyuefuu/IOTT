/// <reference types="vite/client" />

interface ImportMetaEnv {
	readonly VITE_APP_TITLE: string
	readonly VITE_APP_API_BASE_URL: string
	readonly VITE_APP_DEBUG: string
	readonly VITE_MACHINE_CONNECTION_API?: string
	readonly VITE_MACHINE_CONNECTION_PROXY_TARGET?: string
	readonly VITE_MACHINE_CONNECTION_PORT?: string
	readonly VITE_INDUSTRIAL_IOT_PORT?: string
	readonly VITE_INDUSTRIAL_IOT_API?: string
	readonly VITE_INDUSTRIAL_IOT_PROXY_TARGET?: string
}

interface ImportMeta {
	readonly env: ImportMetaEnv
}
