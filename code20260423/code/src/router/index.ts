import { createRouter, createWebHashHistory } from 'vue-router'
import type { RouteRecordRaw } from 'vue-router'
import { ElMessage } from 'element-plus'
import { useAuthStore } from '@/stores/auth'
import { requiredPermission } from '@/auth/permissions'

const routes: RouteRecordRaw[] = [
    {
        path: '/login',
        name: 'Login',
        component: () => import('../views/LoginView.vue')
    },
    {
        path: '/',
        name: 'Home',
        component: () => import('../views/HomeView.vue')
    },

    // 工控协议通讯验证模块
    {
        path: '/industrial',
        name: 'Industrial',
        redirect: '/industrial/device',
        children: [
            {
                path: 'device',
                name: 'IndustrialDevice',
                component: () => import('../views/industrial/DeviceView.vue')
            },
            {
                path: 'property',
                name: 'IndustrialProperty',
                component: () => import('../views/industrial/MachinePropertyView.vue')
            }
        ]
    },

    // 并行连接验证模块
    {
        path: '/parallel',
        name: 'ParallelConnection',
        component: () => import('../views/ParallelConnectionView.vue')
    },

    // 采集任务管理模块（配置管理 + 任务启停 + SignalR 实时数据）
    {
        path: '/collection/manage',
        name: 'CollectionManage',
        component: () => import('../views/collection/CollectionManageView.vue')
    },

    // 数控程序传输验证模块
    {
        path: '/transfer',
        name: 'Transfer',
        redirect: '/transfer/device',
        children: [
            {
                path: 'device',
                name: 'TransferDevice',
                component: () => import('../views/transfer/DeviceView.vue')
            },
            {
                path: 'browser',
                name: 'TransferBrowser',
                component: () => import('../views/transfer/FileBrowserView.vue')
            },
            {
                path: 'records',
                name: 'TransferRecords',
                component: () => import('../views/transfer/TransferRecordView.vue')
            }
        ]
    },

    // PLC信号通讯验证模块
    {
        path: '/plc',
        name: 'PLC',
        redirect: '/plc/device',
        children: [
            {
                path: 'device',
                name: 'PLCDevice',
                component: () => import('../views/plc/DeviceConfigView.vue')
            },
            {
                path: 'address',
                name: 'PLCAddress',
                component: () => import('../views/plc/AddressBrowserView.vue')
            },
            {
                path: 'import',
                name: 'PLCImport',
                component: () => import('../views/plc/CollectionImportView.vue')
            },
            {
                path: 'rw',
                name: 'PLCRW',
                component: () => import('../views/plc/ReadWriteView.vue')
            },
            {
                path: 'status',
                name: 'PLCStatus',
                component: () => import('../views/plc/StatusMonitorView.vue')
            }
        ]
    },

    // 机器人通讯协议验证模块
    {
        path: '/robot',
        name: 'Robot',
        redirect: '/robot/device',
        children: [
            {
                path: 'device',
                name: 'RobotDevice',
                component: () => import('../views/robot/DeviceConfigView.vue')
            },
            {
                path: 'data',
                name: 'RobotData',
                component: () => import('../views/robot/DataBrowserView.vue')
            },
            {
                path: 'rw',
                name: 'RobotRW',
                component: () => import('../views/robot/ReadWriteView.vue')
            },
            {
                path: 'modbus',
                name: 'RobotModbusRegisters',
                component: () => import('../views/robot/ModbusRegisterView.vue')
            },
            {
                path: 'status',
                name: 'RobotStatus',
                component: () => import('../views/robot/StatusMonitorView.vue')
            },
            {
                path: 'estun',
                name: 'RobotEstun',
                component: () => import('../views/robot/EstunControlView.vue')
            }
        ]
    },

    // Client/Server 模式通讯验证模块
    {
        path: '/cs',
        name: 'CS',
        redirect: '/cs/gateway',
        children: [
            {
                path: 'gateway',
                name: 'Gateway',
                component: () => import('../views/cs/GatewayManageView.vue')
            },
            {
                path: 'client',
                name: 'Client',
                component: () => import('../views/cs/ClientDataSourceView.vue')
            },
            {
                path: 'server',
                name: 'Server',
                component: () => import('../views/cs/ServerServiceView.vue')
            }
        ]
    },

    // 权限、日志功能模块
    {
        path: '/system',
        name: 'System',
        redirect: '/system/log',
        children: [
            {
                path: 'log',
                name: 'Log',
                component: () => import('../views/system/LogManageView.vue')
            },
            {
                path: 'permission',
                name: 'Permission',
                component: () => import('../views/system/PermissionManageView.vue')
            }
        ]
    },

    // 验证管理模块
    {
        path: '/verify',
        name: 'Verify',
        redirect: '/verify/task',
        children: [
            {
                path: 'task',
                name: 'VerifyTask',
                component: () => import('../views/verify/TaskManageView.vue')
            },
            {
                path: 'metric',
                name: 'VerifyMetric',
                component: () => import('../views/verify/MetricManageView.vue')
            },
            {
                path: 'report',
                name: 'VerifyReport',
                component: () => import('../views/verify/ReportTemplateView.vue')
            },
            {
                path: 'visualization',
                name: 'VerifyVisualization',
                component: () => import('../views/verify/DataVisualizationView.vue')
            }
        ]
    },

    // 404页面
    {
        path: '/:pathMatch(.*)*',
        name: 'NotFound',
        component: () => import('../views/NotFoundView.vue')
    }
]

const router = createRouter({
    history: createWebHashHistory(),
    routes
})

// 登录守卫 + 按权限映射拦截（映射见 src/auth/permissions.ts，与侧边栏过滤一致）
router.beforeEach((to) => {
    const auth = useAuthStore()
    if (to.path === '/login') {
        return auth.isLoggedIn ? { path: '/' } : true
    }
    if (!auth.isLoggedIn) {
        return { path: '/login', query: { redirect: to.fullPath } }
    }
    const permission = requiredPermission(to.path)
    if (permission && !auth.hasPermission(permission)) {
        ElMessage.warning('当前账号没有访问该页面的权限')
        return { path: '/' }
    }
    return true
})

export default router
