# 华中与广数 CNC 链路修复记录（2026-09-23）

本轮覆盖前端设备配置、MachineConnectionApi 网关、Host 驱动，以及读写、采集和程序文件传输。

| 设备入口 | 前端协议 | Host 驱动项目 | 新增默认端口 |
| --- | --- | --- | --- |
| 华中数控 | `NCLinkApi` | `IndustrialIoT.Protocols.NCLinkApi` | `19001` |
| 广州数控 WebServer | `GskWebServer` | `IndustrialIoT.Protocols.Gsk` | HTTP `11520`，HTTPS `443` |
| 广数 SDK 采集 | `Gskrm` | `IndustrialIoT.Protocols.Gsk` | 表单不要求端口，配置以 `0` 表示由 SDK 管理 |
| 广数 SDK 文件通道 | `GskrmFileTransfer` | `IndustrialIoT.Protocols.Gsk` | 表单不要求端口，配置以 `0` 表示由 SDK 管理 |

广数原生 `GSKRM_CreateInstance` 只接收 IP 和连接类型，不接收 TCP 端口。`0` 是应用配置标记，不是实际通信端口。

## 修复结果

- 华中浏览、读写、采集、文件及批量任务统一按设备 ID 转交 Host，移除网关全局 NCLink HTTP/MQTT 旁路和无引用客户端。每台设备使用自己的地址、SN 和超时配置。
- `VARIABLE@SYS`、`VARIABLE@REG_X` 等节点正常通过前端筛选。Host 兼容旧 `/NC_LINK_ROOT` 模型前缀；批量读取保留地址中的单点 `timeout`。
- 华中写入响应为外层成功、内层 `false` 时保留失败结果。采集缺少返回值或质量为 Bad 时不再报告成功。
- 广数 HTTP 与 WebSocket 读取结果保留请求的原始地址，避免 `/Macro:100`、`/Realtime.Mode` 与已保存点位匹配失败。
- 选择品牌或协议时更新标准端口；编辑已有设备保留自定义端口、未显示的扩展配置及同一文件通道的参数。补齐广数 WebServer 账号入口。
- FTP、SMB、NFS 和广数 SDK 独立文件通道优先于主协议。取消原独立通道时，前端发送 `clearTransfer`，并从完整扩展配置中移除旧 `transferDeviceId` 关联；网关与 Host 同步清除。普通部分更新继续保留未提供的配置。
- 华中单文件完整 key 保持原样；目录以 `/` 结尾，根目录使用 `/`。批量上传按目录拼接文件名，华中和广数空文件列表也提供可选根目录。
- 华中批量下载开放；网关在释放上游响应前读完文件字节，兼容二进制、文本和 ZIP。跨目录同名文件分别落地，ZIP 内重名自动加编号。
- TCP 可达只显示端口探测结果，不修改在线状态和最近设备通讯时间。广数 SDK 只接受驱动验证，不执行任意 TCP 端口回退。

## 验证

| 检查 | 结果 |
| --- | --- |
| 前端实际 Vue 处理函数回归 | 49/49 通过 |
| `vue-tsc` 与 Vite 构建 | 通过；有大于 500 kB 的分包提示 |
| 网关全部 console 回归 | 通过，含 CNC 转发、文件字节、旧采集记录、连接状态和通道清除 |
| Host 协议及服务回归 | 15/15 通过，含实际 HTTP/WebSocket 对端和真实批量任务服务 |
| Host 与网关构建 | 通过；Host 首次完整构建有 15 条 OPC UA/FANUC 过时 API 或可空性警告 |

可在仓库根目录复验：

```powershell
node --test code20260423/code/tests/device-transfer-routing.test.mjs
npm --prefix code20260423/code run build
dotnet build Host/tests/IndustrialIoT.Protocols.Tests/IndustrialIoT.Protocols.Tests.csproj -p:UseAppHost=false -o tmp/cnc-host-build
dotnet tmp/cnc-host-build/IndustrialIoT.Protocols.Tests.dll
dotnet build MachineConnectionApi20260423/MachineConnectionApi/MachineConnectionApi.Tests/MachineConnectionApi.Tests.csproj -p:UseAppHost=false -o tmp/cnc-gateway-build
dotnet tmp/cnc-gateway-build/MachineConnectionApi.Tests.dll
```

本轮日志位于 `tmp/cnc-routing-fix-20260923/`，未纳入提交。
测试使用本地 HTTP/WebSocket/TCP 对端、内存仓储与 SDK 接口替身；独立 FTP/SMB/NFS 通道回归验证的是路由和配置传递。
实际 NCLink API Server→MQTT→机床、广数 WebServer/原生 SDK、现场文件服务仍需实机验收。
`旧/` 保留本地，未纳入提交。
