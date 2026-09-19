import axios, { type CreateAxiosDefaults } from "axios";

export const AUTH_TOKEN_KEY = "mc.auth.token";

export function createMachineConnectionClient(config: CreateAxiosDefaults) {
    const client = axios.create(config);
    client.interceptors.request.use((request) => {
        const token = localStorage.getItem(AUTH_TOKEN_KEY);
        if (token) request.headers.set("X-Auth-Token", token);
        return request;
    });
    return client;
}
