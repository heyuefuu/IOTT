import { onBeforeUnmount, readonly, ref } from "vue";
import {
	HubConnectionBuilder,
	HubConnectionState,
	LogLevel,
	type HubConnection,
} from "@microsoft/signalr";

/** 与 IndustrialIoT.Host SignalR 推送的 CollectedDataBatch（camelCase，枚举为字符串）一致 */
export interface CollectedTagValue {
	address: string;
	dataType: string;
	value: unknown;
	quality: string;
	timestamp: string;
	errorMessage?: string | null;
}

export interface CollectedDataBatch {
	deviceId: string;
	groupName: string;
	values: CollectedTagValue[];
	collectedAt: string;
	/** TimeSpan 序列化为 "00:00:00.1234567" */
	collectionDuration?: string;
}

export type HubState =
	| "disconnected"
	| "connecting"
	| "connected"
	| "reconnecting";

const hubBase = import.meta.env.VITE_INDUSTRIAL_IOT_API ?? "/industrial-iot";

/**
 * IndustrialIoT.Host /hubs/device-data 实时推送客户端。
 * 服务端方法：SubscribeDevice / UnsubscribeDevice；推送事件：DeviceDataReceived。
 * 组件卸载时自动断开；断线自动重连并恢复订阅。
 */
export function useDeviceDataHub(
	onBatch: (batch: CollectedDataBatch) => void,
) {
	const state = ref<HubState>("disconnected");
	const subscribed = new Set<string>();
	let connection: HubConnection | null = null;

	const connect = async (): Promise<void> => {
		if (connection) return;
		const conn = new HubConnectionBuilder()
			.withUrl(`${hubBase}/hubs/device-data`)
			.withAutomaticReconnect()
			.configureLogging(LogLevel.Warning)
			.build();

		conn.on("DeviceDataReceived", (batch: CollectedDataBatch) => {
			onBatch(batch);
		});
		conn.onreconnecting(() => {
			state.value = "reconnecting";
		});
		conn.onreconnected(() => {
			state.value = "connected";
			// 重连后 SignalR 组关系丢失，恢复订阅
			for (const id of subscribed) {
				void conn.invoke("SubscribeDevice", id).catch(() => undefined);
			}
		});
		conn.onclose(() => {
			state.value = "disconnected";
		});

		connection = conn;
		state.value = "connecting";
		try {
			await conn.start();
			state.value = "connected";
		} catch (e) {
			connection = null;
			state.value = "disconnected";
			throw e;
		}
	};

	const subscribeDevice = async (deviceId: string): Promise<void> => {
		await connect();
		if (!connection) return;
		await connection.invoke("SubscribeDevice", deviceId);
		subscribed.add(deviceId);
	};

	const unsubscribeDevice = async (deviceId: string): Promise<void> => {
		subscribed.delete(deviceId);
		if (connection?.state === HubConnectionState.Connected) {
			await connection
				.invoke("UnsubscribeDevice", deviceId)
				.catch(() => undefined);
		}
	};

	const disconnect = async (): Promise<void> => {
		const conn = connection;
		connection = null;
		subscribed.clear();
		if (conn) {
			await conn.stop().catch(() => undefined);
		}
		state.value = "disconnected";
	};

	onBeforeUnmount(() => {
		void disconnect();
	});

	return {
		state: readonly(state),
		connect,
		disconnect,
		subscribeDevice,
		unsubscribeDevice,
	};
}
