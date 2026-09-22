# 全协议检查与修复记录（2026-09-22）

已逐项检查 `ProtocolType` 的 31 个枚举：正式 Host 注册了 29 个真实驱动，均可通过真实 DI 工厂创建；CAN 和 Simulator 未在正式 Host 注册。
本轮完成本机协议收发、文件系统、SDK 接口回归及部署依赖检查。下列通过范围有明确边界，**不代表 31 项全部完成控制器实机验收**。

测试环境：Windows，.NET 8 目标框架 / SDK 9.0.307；PLC 对端使用 Hsl 服务端，Modbus 另用独立 pymodbus 和原始 TCP 报文交叉验证；FTP 使用 pyftpdlib；OPC UA 使用 OPC Foundation 本机服务。
正式 Host 仍使用真实驱动，测试对端和 SDK 替身均未接入产品注册。

## 逐项验证矩阵

| 协议 | 本轮实际验证结果 | 尚需验证 / 能力边界 |
| --- | --- | --- |
| NCLink | MQTT 连接、Ping、Probe 浏览、Set/Query、双设备同时连接通过 | 设备实际模型及文件传输未实机验证 |
| CAN | 正式 Host 未注册 | 没有正式可用入口，不能标记通过 |
| Profibus | Modbus TCP 网关侧连接、8 类型读写、批读、浏览通过 | 需 PROFIBUS→Modbus TCP 网关及现场映射；未测 DP 总线 |
| ModbusTCP | 独立服务端 8 类型读写、Ping、批读、浏览通过；报文字节序通过 | 现场 UnitId、地址与多寄存器字序仍须匹配设备 |
| ModbusRTU | 驱动注册与创建通过 | 无真实 COM/RS-485 对端，本轮未收发验证 |
| Inovance | H3U 的 TCP 路径：连接、8 类型读写、Ping、批读、浏览通过 | 其他汇川系列及现场映射未验证 |
| FINS | TCP 连接、8 类型读写、Ping、批读、浏览通过 | 现场节点号、区域与访问权限未验证 |
| Mewtocol | TCP 连接、8 类型读写、Ping、批读、浏览通过 | 现场站号与控制器兼容性未验证 |
| FOCAS | SDK 替身下批读、文件分块/忙重试、PMC 错误阻断与位保留通过 | 原生控制器连接未验证；部分能力受 SDK 支持限制，无续传 |
| NFS | 本地真实文件系统：新目录上传、列表、下载比对、续传、路径限制通过 | 驱动操作预挂载目录，不负责网络挂载；未测远端 NFS |
| SMB | SDK 接口替身下正常传输、短写、零写、读取错误、提前 EOF 通过 | SMB 协商、认证和真实共享未验证；使用默认 DirectTCP 端口 |
| FTP | 本机真实网络：连接/Ping、上传、列表、下载比对、续传、Skipped 状态处理通过 | 现场 FTP/FTPS、账号权限与服务器差异未验证 |
| Serial | 实际驱动源码配内存串口替身：最终 ACK/NAK、超时、重传、结束序号通过 | 自定义 STX/序号/长度/CRC16/ETX 协议；未测 COM、流控与设备兼容 |
| OpcUa | 本机服务 49 项检查通过，含 10 类型读写、批读、浏览、错误 NodeId/类型和断开 | 仅 None + Anonymous；实机、加密及身份认证未验证 |
| Simulator | 正式 Host 未注册 | 属于测试能力，不计入正式协议通过数 |
| InovanceSerial | 驱动注册与创建通过 | 无真实串口对端，本轮未收发验证 |
| InovanceSerialOverTcp | RTU over TCP：连接、8 类型读写、Ping、批读、浏览通过 | 未测现场串口网关和实际 PLC |
| OmronHostLink | TCP 模式：连接、8 类型读写、Ping、批读、浏览通过 | 串口模式和实际控制器未验证 |
| MewtocolSerial | 驱动注册与创建通过 | 无真实串口对端，本轮未收发验证 |
| FanucRobot | SDK 能力核对与 CRX/UI 只读元数据回归通过 | 未连接机器人；无程序文件传输；SDI 等既有可写区域保留 |
| MTConnect | HTTP probe/current、数据读取、嵌套组件浏览通过 | 标准采集路径只读；厂商写适配器未配置/验证 |
| HaasMdc | 本机 TCP Ping、宏变量写/回读、分片与 CRLF、半帧和超时通过 | 实机须启用 MDC；无程序文件传输 |
| HuazhongRobot | 本机 TCP 映射读取及自定义健康点 Ping 通过 | 无通用默认点表；其余现场数据/写入需按地址映射验收 |
| Gskrm | 驱动创建、x86 原生 DLL 加载通过；探活改为 SDK 状态查询 | SDK ABI 与控制器读写/探活尚未实机验证 |
| GskrmFileTransfer | SDK 接口替身下上传字节/校验和、文件释放、目录错误处理通过；DLL 加载通过 | 原生控制器文件传输未验证；无续传、无分层目录 |
| GskWebServer | 本机 HTTP 健康检查、WebSocket 实时读取、收帧时间和断流降质通过 | HTTP 写/文件接口及 G3IOT 实机未验证；帧 TTL 为 30 秒 |
| HncSdk | 本机 IPC 连接/Ping、目录成功/失败通过；x86 Shim 构建及 5 个 SDK 依赖加载通过 | 控制器 SDK 收发未验证；Host 与 Shim 传本地文件路径，需同机或同路径共享 |
| NCLinkApi | HTTP 状态检查、读写/批读、文件上传/列表/下载比对、离线与超时拒绝通过 | 实际 API Server→MQTT→控制器链路未验证 |
| JingDiao | 本机 IPC 连接/Ping、目录成功/失败通过；x86 DLL 加载通过 | 原生控制器读/文件传输未验证；tag 只读，无续传 |
| SiemensS7 | TCP 连接、8 类型读写、Ping、批读、浏览通过 | 现场 CPU、机架/插槽、DB 权限未验证 |
| EstunRobot | 本机 Modbus 寄存器读写、状态快照、Ping、IO 批读、浏览通过 | 真实机器人交换区/命令写入未验证；快照 DI/DO/AI/AO 为只读投影 |

## 已修复的确定问题

- Modbus TCP/Profibus：修正单寄存器写入字节序，`-123` 的 FC06 数据由错误 `85ff` 改为 `ff85`。
- NC-Link API：连接前实际请求设备状态，离线、HTTP 失败和连接超时不会报告连接成功；失败后释放客户端。
- NC-Link MQTT：ClientId 不再截断机器名后丢失随机部分，避免长机器名上的设备连接相互踢线。双设备本地收发通过；原长机器名碰撞未在本机复现。
- MTConnect：修复 Controller/Path 等嵌套组件的 DataItems 丢失。
- FOCAS：PMC 读改写在读取失败时停止，避免零缓冲覆盖其他位。
- FANUC Robot：CRX/UI 信号与当前 Hsl SDK 的实际只读能力一致。
- Haas：按完整行处理 TCP 分片，保存剩余字节，落实异步超时和长度上限；失败后清理连接与接收缓存。
- GSK：上传临时文件先关闭再交给 SDK；目录 SDK 错误保留；原生探活实际查询状态；WebSocket 断流/过期不再持续返回旧 Good 值，锁内复制 JSON 避免释放竞争。时间戳是本地收帧时间。
- HNC/精雕：目录错误不再伪装成空目录。HNC 的 5 个既有 SDK 运行文件放入项目 `native/`，移除失效的解压目录引用，修复独立 Shim 构建失败。
- 华中机器人：支持 `ExtendedProperties.PingAddress`，未指定时优先使用已配置点位；无点位配置时保留原 `100` 默认值。
- FTP/SMB：续传返回状态、实际写入字节数和下载长度均检查，失败或不完整传输不再报告成功。
- NFS：上传统一按目录拼文件名，根目录与文件名校验，拒绝越根和根内链接穿越；断连后禁止文件访问。
- Serial：最终帧必须 ACK，允许有界重传；结束帧也必须满足序号校验。
- 前端：纯 OPC UA 设备可不配置文件通道；NFS MountPoint 输入、保存、回填和文件树路由完整，保留原 CNC 筛选。

## 可重复执行的回归

仓库根目录执行；这些项目沿用现有 console 回归形式，失败退出码非零：

```powershell
dotnet run --project Host/tests/IndustrialIoT.Protocols.Tests/IndustrialIoT.Protocols.Tests.csproj
dotnet run --project Host/tests/IndustrialIoT.Protocols.FANUC.Tests/IndustrialIoT.Protocols.FANUC.Tests.csproj
dotnet run --project Host/tests/IndustrialIoT.Protocols.FileTransfer.Tests/IndustrialIoT.Protocols.FileTransfer.Tests.csproj
node --test code20260423/code/tests/device-transfer-routing.test.mjs
dotnet build Host/src/IndustrialIoT.Host/IndustrialIoT.Host.csproj
dotnet build Host/src/IndustrialIoT.HncSdkShim/IndustrialIoT.HncSdkShim.csproj
```

前端构建在 `code20260423/code` 执行 `npm run build`。文件传输回归默认执行 16 项；增加 `-- --ftp <本机端口>` 执行 18 项，需要预先启动本机 FTP fixture（账号 `audit` / `audit-fixture`）。

本轮证据目录为 `tmp/protocol-all-20260922/`（本地保留，不提交）：

| 证据 | 结果 |
| --- | --- |
| `inventory.json` | 31 项盘点，29 个真实驱动创建成功 |
| `plc-results.json`、`modbus-independent-results.json` | 8 个 PLC/网关 TCP 路径；独立 Modbus 交叉验证通过 |
| `http-results.json`、`mqtt-results.json`、`robot-results.json` | HTTP/MQTT/Estun 本机收发通过 |
| `opc-check/results.json` | 49/49 检查通过 |
| `regression-final.log` | 11/11 组协议错误路径与收发回归通过 |
| `fanuc-run.log` | FOCAS 批读、文件传输和 PMC 回归通过 |
| `file-transfer-final.log` | 18/18 通过；FTP 为真实网络，SMB 为 SDK fixture，NFS 为文件系统 |
| `serial-replay-before.log`、`serial-replay-after.log` | 修复前 0/4，修复后 4/4 |
| `frontend-fix.md`、`frontend-build.log` | 前端 18/18 回归及构建通过 |
| `final-host-build.log` | Host 构建 0 错误；15 个过时 API/可空性警告 |
| `hnc-shim-build-after.log`、`native-load/run.log` | HNC Shim 构建通过；HNC/GSK/精雕共 7 个运行依赖加载通过 |
| `gsk-shim-build.log`、`jd-shim-build.log` | GSK 与精雕 x86 Shim 构建通过，均 0 警告、0 错误 |

下一轮现场验收重点是实际串口、SMB 共享、厂商 SDK/选件、机器人交换区和 OPC UA 安全配置。每项必须使用真实设备收发结果单独关闭，不能用注册成功或本机 fixture 成功代替。
`旧/` 仅保留本地，未纳入本次提交。
