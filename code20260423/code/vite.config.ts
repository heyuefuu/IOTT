import { defineConfig, loadEnv } from 'vite'
import vue from '@vitejs/plugin-vue'
import { resolve } from 'path'

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
    const env = loadEnv(mode, process.cwd(), '')
    const machineConnectionTarget =
        env.VITE_MACHINE_CONNECTION_PROXY_TARGET || 'http://localhost:5087'
    const industrialIoTTarget =
        env.VITE_INDUSTRIAL_IOT_PROXY_TARGET || 'http://localhost:5173'

    return {
        plugins: [vue()],
        resolve: {
            alias: {
                '@': resolve(__dirname, 'src'),
            },
        },
        server: {
            port: 4171,
            host: '0.0.0.0',
            open: true,
            proxy: {
                '/machine-connection': {
                    target: machineConnectionTarget,
                    changeOrigin: true,
                    rewrite: (path) =>
                        path.replace(/^\/machine-connection/, ''),
                },
                // SignalR 实时推送直连 IndustrialIoT.Host（ws 升级）
                '/industrial-iot': {
                    target: industrialIoTTarget,
                    changeOrigin: true,
                    ws: true,
                    rewrite: (path) =>
                        path.replace(/^\/industrial-iot/, ''),
                },
            },
        },
        preview: {
            port: 4171,
            host: '0.0.0.0',
            proxy: {
                '/machine-connection': {
                    target: machineConnectionTarget,
                    changeOrigin: true,
                    rewrite: (path) =>
                        path.replace(/^\/machine-connection/, ''),
                },
                '/industrial-iot': {
                    target: industrialIoTTarget,
                    changeOrigin: true,
                    ws: true,
                    rewrite: (path) =>
                        path.replace(/^\/industrial-iot/, ''),
                },
            },
        },
        build: {
            outDir: 'dist',
            assetsDir: 'assets',
            emptyOutDir: true,
            sourcemap: false,
        },
        base: './',
    }
})
