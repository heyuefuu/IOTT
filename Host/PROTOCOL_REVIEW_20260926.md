# 全协议实现来源与问题审查

> 后续修复已按用户提供的 HslCommunicationDemo 对照落地，当前状态见文末“修复记录”。以下问题章节保留为修复前基线。

- 日期：2026-09-26；基线提交：3776aeeb453f339685bb01128b2aad8da0d6bd16。
- 范围：Host 实际注册的 CNC、PLC、机器人、文件传输驱动。审查源码、依赖、注册关系及异常路径；未连接真实设备。
- 统计单位是注册协议入口，同一底层 SDK 的多个入口分别计数。所有入口都包含项目自己编写的适配代码；“接厂家 SDK”不等于适配层已经获得厂家认证。
- 来源依据：`Host/src/IndustrialIoT.Host/ProtocolDriverRegistration.cs:29` 起的 29 个 Register 调用、各项目 csproj、实际客户端构造及 DLL 导入。没有按未调用的旧协议文件计数。

## 1. 来源统计

| 分类 | 入口数量 | 说明 |
| --- | ---: | --- |
| 接厂家 SDK | 5 | FOCAS、HncSdk、JingDiao、Gskrm、GskrmFileTransfer；对应 4 类厂家 SDK |
| 接标准组织 SDK | 1 | OpcUa：OPC Foundation 栈 |
| 接第三方通信库 | 14 | HSL、FluentModbus、FluentFTP、SMBLibrary；不是设备厂家官方 SDK |
| 项目自行实现协议或应用客户端 | 7 | SiemensS7、NCLink、NCLinkApi、MTConnect、HaasMdc、GskWebServer、Serial |
| 网关或系统挂载适配 | 2 | Profibus、NFS，不是原生协议栈实现 |
| 合计 | 29 | CAN 只有枚举没有实际驱动注册；Simulator 为模拟器，不算真实协议 |

## 2. 逐协议清单

问题编号见下一节；“未确认具体缺陷”仅表示本轮未定位到，不等于实机验收通过。

| 协议 | 底层来源 / 实际路径 | 本轮结论 |
| --- | --- | --- |
| FOCAS | 厂家 `Fwlib64.dll`，自写 P/Invoke 和地址适配 | 未确认具体缺陷；仍需对应机型 SDK 联调 |
| HncSdk | 厂家 `HncNetDllNoAdapterCSharp.dll` 等，x86 Shim | F10：空值、转换失败被当作正常值 |
| JingDiao | 厂家 `NcMonIO.dll`，Shim + 自写适配 | F10：转换溢出返回 0/Good |
| Gskrm | 厂家 `gskrm.dll`，Native / IPC 适配 | F11：列出的急停点位实际不支持 |
| GskrmFileTransfer | 同一 `gskrm.dll` 的文件接口 | 未确认具体缺陷；依赖原生运行库 |
| OpcUa | `OPCFoundation.NetStandard.Opc.Ua 1.5.378.134` | F7、F8：端点预探测、断线释放 |
| ModbusTCP | `FluentModbus 5.3.2` | F2：显式长度与标量类型不匹配会错误写入 |
| ModbusRTU | `HslCommunication 12.6.4` | F1、F13、F14：错区域写、数量丢失、坏点中断整批 |
| Inovance | HSL `InovanceTcpNet` | F3、F14 |
| InovanceSerial | HSL `InovanceSerial` | F3、F14 |
| InovanceSerialOverTcp | HSL `InovanceSerialOverTcp` | F3、F14 |
| FINS | HSL `OmronFinsNet` | F3 |
| OmronHostLink | HSL `OmronHostLink` / `OmronHostLinkOverTcp` | F3 |
| Mewtocol | HSL `PanasonicMewtocolOverTcp` | F3 |
| MewtocolSerial | HSL `PanasonicMewtocol` | F3 |
| FanucRobot | HSL `FanucInterfaceNet` | F9：心跳超时后底层读任务遗留 |
| HuazhongRobot | HSL Modbus + 项目配置的机器人地址映射 | 未确认具体缺陷；映射需匹配现场控制器 |
| EstunRobot | HSL `EstunTcpNet` | 未确认具体缺陷 |
| FTP | `FluentFTP 54.0.2` | 未确认具体缺陷 |
| SMB | `SMBLibrary 1.5.6.2` | 未确认具体缺陷 |
| SiemensS7 | 项目 `S7TcpClient` 自行编解码 COTP/S7 | F4、F5、F6、F15 |
| NCLink | MQTTnet 提供传输，项目实现 NC-Link 消息与模型 | 未确认具体缺陷 |
| NCLinkApi | 项目 HTTP 客户端，对接外部 NC-Link API Server | 未确认具体缺陷；不是本项目内置厂家 SDK |
| MTConnect | 项目 HTTP/XML 客户端 | F12：数值解析失败仍为 Good |
| HaasMdc | 项目 TCP / MDC 命令实现 | F12：错误响应仍为 Good |
| GskWebServer | 项目 HTTP / WebSocket 客户端 | 未确认具体缺陷 |
| Serial | System.IO.Ports + 项目 STX/序号/CRC/ACK 帧协议 | F16：下载超时无限重试；对端须支持该自定义帧 |
| Profibus | 包装 ModbusTcpDriver，经 PROFIBUS↔Modbus 网关 | 继承 F2；不是直接连接 PROFIBUS-DP 总线 |
| NFS | 对操作系统预挂载目录做文件 IO | 未确认具体缺陷；不负责 NFS 挂载或网络协议协商 |

## 3. 已确认问题

### P1：优先处理错误写入与采集阻塞

**F1 — ModbusRTU 非 Bool 线圈写入被转为寄存器写入。** `C0 + UInt16 + 7` 丢失 Coil 区域，调用 HSL `WriteAsync("0", ushort)`，目标成保持寄存器。内存记录型 HSL 客户端已复现 `Success=True Call=ushort@0=7`。证据：`Host/src/IndustrialIoT.Protocols.Modbus/ModbusRtuDriver.cs:134`、`:138`。应先校验地址区与类型兼容性。

**F2 — ModbusTCP / Profibus 标量写入可覆盖相邻寄存器。** `HR0;4 + Float=1.5` 分配 8 字节，只填前 4 字节，后两个寄存器写 0；`HR0;1 + Float=1.5` 则转为整数 2。实际 parser 已验证两种长度均放行；写入后果由实际编码路径确认，未向设备发写命令。证据：`Host/src/IndustrialIoT.Protocols.Modbus/ModbusTcpDriver.cs:436`、`:449`、`:477`。

**F3 — 7 个 HSL PLC 封装漏掉 Int8 / UInt8 / UInt64，默认按 UInt16 读写。** 影响三个 Inovance、FINS、HostLink、两个 Mewtocol。读结果仍标原请求类型和 Good，写入可能写错宽度或溢出。前端已允许选择这些类型，实际可达。证据：`Host/src/IndustrialIoT.Protocols.Modbus/InovanceDriver.cs:109`、`:127`；`Host/src/IndustrialIoT.Protocols.FINS/FinsDriver.cs:136`、`:181`；`Host/src/IndustrialIoT.Protocols.FINS/OmronHostLinkDriver.cs:150`、`:184`；`Host/src/IndustrialIoT.Protocols.Mewtocol/MewtocolDriver.cs:167`、`:225`；`Host/src/IndustrialIoT.Protocols.Mewtocol/MewtocolSerialDriver.cs:127`、`:173`。其余两个汇川封装有同样 default 分支。应实现准确宽度或明确拒绝。

**F4 — 自写 S7 地址越界会静默回绕。** 已调用真实 Release DLL 验证 `MB2097152` 被接受，写报文地址编码为 0；`MB-1` 也被接受。证据：`Host/src/IndustrialIoT.Protocols.SiemensS7/S7Address.cs:99`、`Host/src/IndustrialIoT.Protocols.SiemensS7/S7TcpClient.cs:114`。应在编码前校验非负值、24 位范围及操作长度，避免写到合法但错误的地址。

**F5 — S7 握手与异步收包没有有效超时。** 只有 TCP 建连受 timeout 约束，后续 NetworkStream.ReadAsync 仅依赖外部 ct，配置 ReadTimeout 未生效。回环假服务只接受连接不响应，ConnectTimeout=50ms 时 250ms 后任务仍未结束，须外部取消。证据：`Host/src/IndustrialIoT.Protocols.SiemensS7/S7TcpClient.cs:15`、`:52`、`:185`。持续采集组和驱动锁可能长期被占用。

### P2：配置失效、结果质量及资源管理

**F6 — S7 S300/S400 忽略配置 Rack/Slot。** 算完目标 TSAP 后强制覆盖为 2/3。离线已验证 rack=1、slot=0/2/5 都得到固定低字节。证据：`Host/src/IndustrialIoT.Protocols.SiemensS7/S7TcpClient.cs:63`。

**F7 — OPC UA EndpointUrl 的主机覆盖不完整。** 真正端点来自 EndpointUrl，TCP 预探测仍使用 config.Host；两者不一致时，正确端点在线也可能提前连接失败。证据：`Host/src/IndustrialIoT.Protocols.NCLink/OpcUaDriver.cs:62`、`:171`。

**F8 — OPC UA 已断线 session 跳过释放。** DisconnectAsync 只处理 Connected=true 的 session；断线后不会退订 KeepAlive、Dispose 或清空引用。证据：`Host/src/IndustrialIoT.Protocols.NCLink/OpcUaDriver.cs:246`。

**F9 — FANUC 机器人心跳超时后遗留未完成读任务。** Task.WhenAny 超时便返回 false 并释放驱动锁，没有结束或等待 ReadFanucDataAsync。后续操作/断开清理可与该读取重叠；底层库是否额外串行化会影响表现，不能据此断言已经发生混帧。证据：`Host/src/IndustrialIoT.Protocols.FANUC/FanucRobotDriver.cs:106`；连接池连续失败会释放连接，见 `Host/src/IndustrialIoT.Infrastructure/BackgroundServices/ConnectionPoolService.cs:140`。

**F10 — HNC / 精雕 SDK 适配把转换失败当正常采集值。** 反射执行当前 DLL：JingDiao 的 40000 转 Int16 得到 0/Good；HncSdk 的 JSON null 按 Double 解码也得到 0/Good。SDK 成功返回码不足以证明数据有效。证据：`Host/src/IndustrialIoT.Protocols.JingDiao/JingDiaoDriver.Read.cs:134`、`:154`；`Host/src/IndustrialIoT.Protocols.HncSdk/HncSdkDriver.cs:128`、`:303`、`:329`。

**F11 — GSKRM 浏览器列出实际不支持的急停点。** 列出的 Status.Estop 最终调用 NativeGskrmApi.GetEspState，有效句柄也恒返回 NotSupported，因此真实 Native/IPC 路径读该点必然失败。证据：`Host/src/IndustrialIoT.Protocols.Gsk/GskrmDriver.cs:201`、`Host/src/IndustrialIoT.Protocols.Gsk/NativeGskrmApi.Status.cs:86`。应实现接口或按能力隐藏该点。

**F12 — Haas / MTConnect 存在伪 Good 数据。** Haas 的 `?`、`ERROR` 非空错误响应会按 Good 返回；MTConnect 数值项收到 `abc` 时保留字符串，却仍标 Float/Good。证据：`Host/src/IndustrialIoT.Protocols.Haas/HaasMdcDriver.cs:105`、`HaasCommandMap.cs:126`；`Host/src/IndustrialIoT.Protocols.MTConnect/MTConnectXmlParser.cs:114`、`MTConnectDriver.cs:206`。应传播协议错误并校验转换结果。

**F13 — ModbusRTU 忽略地址的显式数量。** Parser 接受 `C0;64`、`HR0;10`，读取只取一个标量；String 固定长度16、ByteArray固定1寄存器。证据：`Host/src/IndustrialIoT.Protocols.Modbus/ModbusRtuDriver.cs:118`。应按数量读数组或拒绝不支持的数量。

**F14 — ModbusRTU / 三个汇川驱动单个非法地址中断整批。** Parse/Normalize 在单点 try 外，批量直接 await，任何一个抛出会让正常点也失去该轮输出。证据：`Host/src/IndustrialIoT.Protocols.Modbus/ModbusRtuDriver.cs:85`、`InovanceDriver.cs:82`、`:92`；两个汇川串口封装相同。应保留取消异常，其余点位错误隔离为 Bad。

**F15 — S7 ByteArray 写入的声明长度和实际载荷不一致。** 类型长度固定1，写 `[1,2,3,4]` 时参数声明1个BYTE，却携带4字节；已反射检查实际命令。证据：`Host/src/IndustrialIoT.Protocols.SiemensS7/S7ValueCodec.cs:19`、`SiemensS7Driver.cs:108`、`S7TcpClient.cs:100`。应按载荷长度构造地址和报文。

**F16 — 串口下载在对端静默时无限重试。** ReadFrameAsync 吞掉 TimeoutException 返回 invalid，下载 while(true) 对 invalid 发送 NAK 后继续；没有重试次数或总时限，只有外部取消可结束。证据：`Host/src/IndustrialIoT.Protocols.FileTransfer/SerialTransferDriver.cs:253`、`:259`、`:445`。上传的 MaxRetries 未用于此下载路径。

## 4. 验证与边界

- 当前 Release 协议回归：`dotnet Host/tests/IndustrialIoT.Protocols.Tests/bin/Release/net8.0/IndustrialIoT.Protocols.Tests.dll`，26/26 通过。这些测试没有覆盖本报告指出的全部异常路径。
- 本轮额外验证：S7 DLL 反射检查地址、TSAP、ByteArray 报文；S7 本机回环握手超时；Modbus parser 调用与 RTU 内存记录客户端；HNC/精雕实际 DLL 数据转换反射。
- 其余条目是已定位的源码路径问题，未通过真实控制器或串口设备重现。没有向现场设备发送写入命令。
- 不把 HSL 等第三方库等同于厂家 SDK；也不把调用 SDK 的项目适配代码当作自动正确。高优先级缺陷主要位于本项目的地址、类型、时序适配层。
- 修复顺序建议：F1/F2/F3/F4 错误写入 → F5 阻塞 → F10/F12 数据质量 → 其余配置、批量隔离及资源释放。
- 初次审查只新增此报告，之后用户授权按 Demo 修复，记录如下。

## 5. 对照 Demo 的修复记录

参考目录：`F:/AI/2026/xianyu/HslCommunicationDemo`，保持该参考项目原样，改动落在 IOTT。

- `DemoControl/UserControlReadWriteOp.cs:237`：检测 ReadByte / Write(byte) 能力；无此能力就禁用，未用 UInt16 替代。
- 同文件的 ReadUInt64 / Write(ulong) 示例，以及 `PLC/Omron/FormOmron.cs`、`FormOmronHostLink.cs` 的 UInt64 读写调用；同时核对本项目实际 HSL 12.6.4 API。
- `PLC/Siemens/FormSiemens.cs:103`：Rack/Slot 使用界面配置；本项目自写 S7 驱动同步保持该配置语义。
- FANUC 机器人沿用 HSL 客户端收包超时，完整等待读取结束后释放驱动锁。

| 问题 | 修复 |
| --- | --- |
| F1 / F2 | 校验写入区域、类型及精确标量宽度；无效请求在发送前拒绝 |
| F3 | 7 个 HSL PLC 封装补 UInt64 实际接口并用字符串传输完整数值；不支持的 Int8/UInt8 明确拒绝 |
| F4 / F6 / F15 | S7 校验地址与跨度、保留 Rack/Slot、按 ByteArray 实际长度组包并限制 PDU |
| F5 | TCP 建连及握手、每次读写分别受 deadline 约束；失效流关闭并将驱动标记 Faulted，供连接池重建 |
| F7 / F8 | OPC UA 预探测使用真实 EndpointUrl；断线 session 也退订及释放 |
| F9 | FANUC 心跳受 HSL ReceiveTimeOut 约束，不再丢下未完成读取任务 |
| F10 / F12 | CNC 文本和 SDK 数值严格转换，空值、溢出、无效数值、协议错误回复返回 Bad |
| F11 | 从 GSKRM 可浏览点位中移除未实现的 Status.Estop；手输该地址仍明确报不支持 |
| F13 / F14 | RTU 显式数量按数组读取；RTU/汇川的坏地址仅影响自身点位 |
| F16 | 串口下载对连续超时、无效帧和错序设置重试上限 |

验证结果：`dotnet build Host/tests/IndustrialIoT.Protocols.Tests/IndustrialIoT.Protocols.Tests.csproj --no-restore -m:1` 成功；新增异常路径测试 4/4，通过完整协议回归 30/30，`git diff --check` 通过。增量构建保留 1 条 OPC UA 测试构造器过时提示。

新增测试覆盖真实本机回环报文、内存 SDK 替身、HNC/精雕 HTTP Shim 响应、断线 OPC UA session 清理。FANUC 心跳修改通过编译和代码复核；串口重试上限仅做代码路径检查，尚无真实串口模拟。没有连接现场设备，各厂家 SDK 与机型兼容性仍需现场验证。本轮修改未提交或推送。
