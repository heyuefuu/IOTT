# DeviceSimGateway — 模拟设备网关

把一台普通 PC 当作**数控机床 / PLC 的服务端**：以标准协议在内网/端口上对外服务，供目标机的 `IndustrialIoT.Host` 作为"设备"连接；并带一个网页管理台用于**动态开关各协议服务、改端口、配置并生成模拟数据**。

## 运行

```powershell
dotnet run --project tools/DeviceSimGateway/DeviceSimGateway.csproj
```

- 管理台：`http://<本机IP>:8090/`（页面内可开关服务、改端口、编辑模拟数据、看实时数据曲线）。
- 配置持久化：`bin/<配置>/net8.0/sim-config.json`（服务端口/启用、生成器参数）。
- 自包含发布（目标机免装运行时，可选）：
  `dotnet publish tools/DeviceSimGateway/DeviceSimGateway.csproj -c Release -r win-x64 --self-contained -o dist/simgateway`

## 端口一览（本机 IP + 下列端口）

| 服务 key | 协议 | 默认端口 |
|---|---|---|
| modbus | Modbus TCP | 502 |
| mtconnect | MTConnect Agent | 5000 |
| s7 | Siemens S7 | 102 |
| opcua | OPC UA | 4840 |
| gskws | GSK WebServer | 11520 |
| fins | Omron FINS | 9600 |
| ncLink | NC-Link (MQTT) | 1883 |
| ncLinkApi | NC-Link API（华中 HTTP） | 19001 |
| ftp | FTP Server（程序库） | 21 |
| mewtocol | Panasonic Mewtocol（TCP ASCII） | 5007 |
| haas | Haas MDC（TCP 9999 ASCII） | 9999 |
| jingdiao | 北京精雕 shim mock（HTTP，需在目标机本机跑） | 7080 |
| （管理台） | HTTP | 8090 |

- **自动生成文档**：`GET http://<本机IP>:8090/api/doc`（Markdown，含端口/状态/端点示例，随运行配置实时生成）。
- **程序化查看端口**：`GET /api/ports`（JSON）；或打开管理台页面。
- 端口与启用状态**可改**（管理台或 `PUT /api/services/{key}`），持久化于 `sim-config.json`；启动日志也会打印"端口映射"。

## 已实现协议（严格按协议）

| 协议 | 端口(默认) | 说明 |
|---|---|---|
| **Modbus TCP** | 502 | 自带 MBAP + 功能码 **01/02/03/04/05/06/0F/10** 与标准异常码(01/02/03/04)。保持寄存器可读可写；输入寄存器/离散输入由生成器驱动；线圈可读可写。 |
| **MTConnect 1.3** | 5000 | 只读 Agent：`/probe`（设备树）、`/current`（快照），默认命名空间、`dataItemId` 与 probe 对应；点值取自共享数据区。 |
| **Siemens S7** | 102 | 自研 S7comm/ISO-TSAP 服务端：COTP CR→CC、Setup Communication、Read/Write Var（DB/M/I/Q；BIT/BYTE/WORD/DWORD/REAL）。DB→保持寄存器、M→输入寄存器。已用 S7.Net 客户端回归。 |
| **OPC UA** | 4840 | 基于官方 UA-.NETStandard 1.5 的匿名/None 端点，命名空间 `urn:DeviceSimGateway:Sim`，暴露 `Sim.X/Y/Z/Spindle/Feed/PartCount`，值绑定共享数据区并定时刷新。已用官方 UA 客户端回归。 |
| **GSK WebServer** | 11520 | 广数 G3IOT 模拟：HTTP `GET /api/v1/{sn}/mc`、`/macro-values`、`/params`、`/info/diags`、`/info/alarms`、`/mc/gcode`、`/plc/regs`、`/tool-offsets`… + WebSocket `/ws/{sn}` 实时帧（字段对齐 `GskWebServerDriver`）。HTTP 已验证。 |
| **Omron FINS** | 9600 | FINS/TCP 服务端：节点地址交换 + 命令 0101/0102（区 CIO/WR/HR/AR/DM，字/位）。 |
| **NC-Link (MQTT)** | 1883 | 内置 MQTT Broker + 设备应答（Query/Set/Probe/Version/Register + `Sample/{guid}/{ch}`），topic/字段对齐 `NCLinkProtocol`（guid=`sim-device-001`）。 |
| **FTP Server** | 21 | 模拟数控程序库：支持 LIST/NLST/MLSD、RETR、STOR、DELE、SIZE、MKD/RMD（PASV/EPSV）。供 Host 的 `FtpTransferDriver` 列举/上传/下载。 |
| **NC-Link API（华中）** | 19001 | HTTP 服务端：`POST /v1/{deviceId}/data/`（get_value/get_keys/set_value…，响应 `value=[[..]]`）、`GET\|POST /v1/{deviceId}/file/`、`GET /api/tools/check-device`。对齐 `NCLinkApiClient`/`NCLinkApiDriver`。 |

> 说明：**Profibus** 在本仓库是 **Modbus-TCP 的薄封装**（DP 从站镜像为 Modbus-TCP），故用本网关的 Modbus 服务即可覆盖。
> **原生 SDK 类协议**（FANUC FOCAS、广数 Gskrm、华中 HncSdk、FanucRobot 等）Host 通过厂商 DLL 通信、非可仿真的线协议，本网关无法作为其"设备端"。
> **例外-走 shim 的**：北京精雕(`JingDiao`)、华中(`HncSdk`) 驱动通过 **本机 loopback HTTP shim** 通信 → 可用本网关的 mock shim 对接（须在目标机本机运行）。

### 自测（独立客户端回归）
```powershell
# S7（需先启用 s7 服务；S7.Net 固定端口 102）
dotnet run --project tools/S7SelfTest
# OPC UA（需先启用 opcua 服务）
dotnet run --project tools/OpcUaSelfTest
```

## 扩展占位（列出但未实现，可插拔）

`Mewtocol(5007)`（帧格式待按松下手册对齐；已用 HSL 客户端抓得样例帧 `%01#RDD…<CR>`）。

各协议开源的**服务端**参考：
- S7：Snap7（自带 S7Server）
- OPC UA：open62541 / UA-.NETStandard(Reference Server) / node-opcua
- MTConnect：MTConnect 参考 Agent
- FINS/Mewtocol/GSK/NC-Link/Haas：多为客户端库或私有，可依规范自实现

## 模拟数据生成

- 保持寄存器：上电按"种子模式"填一次（默认 Ramp），之后不被生成器覆盖（保留客户端写入），除非勾选"生成器也驱动保持寄存器"。
- 输入寄存器 / 离散输入：生成器持续驱动。
- 线圈：生成器按周期翻转（默认 Toggle），也接受客户端读写。
- 模式：`Constant / Ramp / Sine / Random / Counter`（寄存器），`Constant / Toggle / Random`（位）。
- 参数（各区大小、刷新周期、Base/幅值/周期/步长）均在网页中可改，"应用"即生效并持久化。

## 与目标机对接

1. 本机网卡设为目标机设备网段（如 `192.168.1.50/24`），网线直连目标机或同交换机。
2. 目标机"设备管理"新增设备：
   - Modbus：协议 `ModbusTCP`、IP=本机IP、端口=502（或你在页面改的端口）
   - MTConnect：协议 `MTConnect`、IP=本机IP、端口=5000
3. 目标机 Host 即连上并采集到模拟数据（点地址示例：`HR0`、`IR0`、`C0`；MTConnect 见 `/probe` 的 dataItemId）。
4. 若本机开了 Clash/TUN，请把该网段加入**直连**，避免被假 IP 劫持。

## 新增协议（扩展方式）

实现 `Core/IDeviceService`（`Key/Name/Description/DefaultPort/Implemented/State/Message/Port` + `StartAsync/StopAsync`），在 `Program.cs` 的服务清单里注册即可，网页会自动出现开关与端口配置。TCP 类协议用 `TcpListener`，HTTP 类协议可用独立 `WebApplication`（参考 `MtconnectService`）。

## 目录

```
tools/DeviceSimGateway/
  Core/            Models, SimDataStore, DataGenerator, IDeviceService, ServiceRegistry
  Services/        ModbusTcpService, MtconnectService, PlaceholderService
  wwwroot/         index.html（管理台：开关/配置/实时数据/曲线）
  Program.cs       管理台 Web + REST API（/api/services、/api/data、/api/generator）
```

## REST API 摘要
- `GET /api/services` 列表；`POST /api/services/{key}/start|stop`；`PUT /api/services/{key}` `{port,enabled}`
- `GET /api/data?count=32` 实时数据快照
- `GET|PUT /api/generator` 模拟数据生成配置
