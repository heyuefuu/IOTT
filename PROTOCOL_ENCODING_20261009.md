# 全协议编码方式盘点（依据 Host 源码还原）

- 日期：2026-10-09；范围：`Host/src` 下实际注册的 CNC / PLC / 机器人 / 文件传输驱动。
- 手段：只读源码还原（address 解析、DataType→字节/寄存器映射、字节序、字符串编码、帧格式、常量与异常路径）。**未连接真实设备**，以下为源码推断，不代表实机验收。
- 依据入口：`Host/src/IndustrialIoT.Host/ProtocolDriverRegistration.cs` 的 29 个注册项。
- 公共类型枚举 `DataType`（`Host/src/IndustrialIoT.Domain/Enums/DataType.cs`）：`Bool, Int8, UInt8, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float, Double, String, ByteArray`。
- 文本类共用的数值转换：`Host/src/IndustrialIoT.Protocols/Models/TagValueConversion.cs`（null/非有限值/ByteArray 非字节数组 → `FormatException`；数值用 `InvariantCulture`；`UInt64` 以字符串返回）。

> 说明：`编码方式` 指“地址语法 + 数据类型到线缆字节/寄存器/消息字段的映射 + 字节序与字符串编码”。下文每条尽量给出 `文件:行` 证据。

---

## 0. 总览

| 协议 | 传输 / 底层库 | 编码执行位置 | 字节序 | Int8/UInt8 | 备注 |
| --- | --- | --- | --- | --- | --- |
| SiemensS7 | 自写 TPKT/COTP/S7comm | 本项目 `S7ValueCodec`/`S7TcpClient` | 大端（无字/字节交换） | 支持 | 唯一支持位寻址与 24 位偏移 |
| ModbusTCP | FluentModbus 5.3.2 | 本项目 `BinaryPrimitives.*BigEndian` | 固定大端 ABCD | 拒绝（抛异常） | 无字交换配置 |
| ModbusRTU | HslCommunication 12.6.4 | HSL 内部 | HSL `DataFormat`（默认 ABCD） | 拒绝 | 可由扩展属性调序 |
| Inovance ×3 | HslCommunication | HSL 内部 | HSL `DataFormat` | 拒绝 | 需 `Series` |
| Profibus | FluentModbus（复用 ModbusTCP） | 同 ModbusTCP | 大端 ABCD | 拒绝 | 实为 Modbus 网关桥接 |
| FINS | HSL `OmronFinsNet` | HSL | HSL `DataFormat` | 拒绝 | 字符串 UTF-8 |
| OmronHostLink | HSL `OmronHostLink[OverTcp]` | HSL | HSL `DataFormat` | 拒绝 | 字符串 UTF-8 |
| Mewtocol(TCP/Serial) | HSL `PanasonicMewtocol*` | HSL | HSL `DataFormat` | 拒绝 | 字符串 ASCII |
| FANUC Robot | HSL `FanucInterfaceNet` | HSL | 无 | 静默→ushort | 写忽略类型 |
| Estun Robot | HSL `EstunTcpNet` | HSL | 无 | 不支持 | 快照 JSON + 命令 |
| Huazhong Robot | HSL `ModbusTcpNet` | 本项目映射+HSL | `client.DataFormat` | 构造期拒绝 | 字符串 UTF-8 |
| OPC UA | OPC Foundation 栈 | 本项目 `OpcUaValueCodec` | 无 | SByte→Int16 / Byte→UInt16 | UInt64→String |
| NCLink | MQTT + JSON | 本项目消息模型 | n/a | 依 `GetValue<T>` | 严格转换 |
| NCLinkApi | HTTP + JSON | 本项目 | n/a | 依 `GetValue<T>` | 兼容旧记录 |
| MTConnect | HTTP + XML | 本项目解析器 | n/a | 数值/事件区分 | 只读+白名单写 |
| Haas | TCP + ASCII 命令 | 本项目 `HaasCommandMap` | n/a | 文本数值 | `?Q…`/宏变量 |
| GSK (Gskrm) | 原生 DLL / x86 IPC-HTTP | 本项目映射器 | n/a | 整数/浮点/字节 | 无扩展属性 |
| GSK (WebServer) | HTTP/JSON + WebSocket | 本项目 | n/a | JSON 数值 | 路径可配 |
| FOCAS | 原生 `Fwlib64.dll` | 本项目 `FocasAddressMapper` | n/a | 整数缩放/宏缩放 | 路径式地址 |
| HncSdk | HTTP/JSON → x86 Shim | 本项目 + 反射网关 | n/a | double/int/string | 反射选重载 |
| JingDiao | HTTP/JSON → x86 Shim | 本项目 + 原生 StdCall | n/a | double | 哨兵值检测 |
| FTP | FluentFTP | 库内部 | n/a | n/a | 二进制 |
| SMB | SMBLibrary | 库内部 | n/a | n/a | 二进制，无校验和 |
| NFS | OS 预挂载目录 | 本项目 FileStream | n/a | n/a | 无 NFS 协议实现 |
| Serial | System.IO.Ports | 本项目自定义帧 | 大端（帧内） | n/a | CRC-16/CCITT-FALSE |

---

## 1. 自研二进制协议

### 1.1 SiemensS7（自写 COTP/S7comm）

- 传输：自建 TPKT/COTP + S7comm，裸 `TcpClient`（`IndustrialIoT.Protocols.SiemensS7/S7TcpClient.cs`）。不使用 Snap7/HSL。值编解码在 `S7ValueCodec.cs`，地址在 `S7Address.cs`。
- 地址语法（`S7Address.Parse`，`S7Address.cs:24`）：去 `%`、大写；前缀分派——
  - `DB<n>.<off>`（DataBlock 0x84；可选 `DBX/DBB/DBW/DBD` 后缀），如 `DB1.DBX0.0`、`DB1.DBW4`；
  - `SM`(0x05)、`AI`(0x06)、`AQ`(0x07)、`P`(0x80)、`I`(0x81)、`Q`(0x82)、`M`(0x83)；
  - `V` → 映射到 DB1（S7-200 风格）；
  - `T`(0x1F 定时器)、`C`(0x1E 计数器)。
  - 偏移：十进制 `字节[.位]`，位 0–7，字节 0–2,097,151；合成位地址 ≤ `0xFFFFFF`（24 位）（`S7Address.cs:85`）。
- 类型→字节（全部**大端**，`S7ValueCodec.cs:11`）：

| 类型 | S7 长度 | 字节/位 | 编码 |
| --- | --- | --- | --- |
| Bool | Bit | 1 位 | `[0/1]` |
| Int8 / UInt8 | Byte | 1 | `sbyte` / `byte` |
| Int16 / UInt16 | Word | 2 | `*Int16/UInt16BigEndian` |
| Int32 / UInt32 | DWord | 4 | `*Int32/UInt32BigEndian` |
| Float | DWord | 4 | `SingleToInt32Bits`→大端 |
| Int64 / UInt64 | LWord | 8 | `*Int64/UInt64BigEndian` |
| Double | LWord | 8 | `DoubleToInt64Bits`→大端 |
| String | BytesOf(len) | N（默认 16） | 裸 ASCII，写补 `\0`，读 `TrimEnd('\0',' ')` |
| ByteArray | Byte | 读 1 字节 / 写取数组长度 | 原样拷贝 |

- 关键点：String **非**原生 S7 STRING（不发 2 字节字符串头）；`StringLength` 扩展属性默认 16（`SiemensS7Driver.cs:17`）。未识别类型默认按 **UInt16/Word**（`S7ValueCodec.cs:20,38,56`）。
- 常量：PDU 默认 200（协商后 `response[-2]-28`）；读**每次一项**（逐点循环，无批量）；写长度上限 `min(PDU-7, 8191)`；传输尺寸位=0x01/字节=0x02/计数器定时器=0x1C；Rack 0–7，Slot 0–31（S300 默认 2、S400 默认 3，否则 0）。

### 1.2 ModbusTCP（FluentModbus 5.3.2）

- 编码：用 FluentModbus 的**原始字节**重载，自己用 `BinaryPrimitives.*BigEndian` 转换（`ModbusTcpDriver.cs:337-423`）；连接固定 `ModbusEndianness.BigEndian`。FluentModbus 的 `SwapBytes` 只影响其**泛型**API，不影响原始 API。
- 地址语法（`ModbusAddressParser.cs:79`）四种互通：
  - `HR/IR/C/DI<n>`（0 基）；`4x/3x/1x/0x<n>`（数字按十进制，`0x40`=线圈 40）；5/6 位引用（1 基，`40001`→HR0）；裸数字→HR0 基。
  - `;N` 显式数量：寄存器区按 N×16 位、位区按 N 位（如 `40001;100`、`DI0;64`）。
- 类型→寄存器数（`GetRegisterCount`）：Bool/Int16/UInt16=1；Int32/UInt32/Float=2；Int64/UInt64/Double=4；**String/ByteArray=1（需 `;N` 覆盖）**；位区恒 1；**Int8/UInt8 抛异常**。
- 字节序：**固定大端 ABCD（高字在前）**；Float=`Int32BitsToSingle`，Double/Int64=`Int64BigEndian`；String=`Encoding.ASCII.GetString(...).TrimEnd('\0')`；ByteArray=整段字节。
- 常量：`MaxRegistersPerRequest=125`、`MaxBitsPerRequest=2000`（仅对 `;N` 生效）；逐点一次请求，无合并；单元号取 `UnitId`/`Station` 默认 1。

### 1.3 ModbusRTU（HslCommunication 12.6.4）

- 编码全在 HSL（`ReadInt16Async`/`ByteTransform` 等）；地址用**同一个** `ModbusAddressParser`。
- 扩展属性调序：`AddressStartWithZero`、`IsStringReverse`、`DataFormat`(ABCD/BADC/CDAB/DCBA)、`Crc16CheckEnable`、`StationCheckMatch`、`BroadcastStation`（`ModbusRtuDriver.cs:186`）。InputRegister 读用 `"x=4;<start>"` 强制 FC04。
- 类型：Bool→`ReadUInt16>0`；String→`ReadStringAsync(16, ASCII)`；ByteArray→`ReadAsync(1)`（1 字节）；Int8/UInt8→`NotSupportedException`。`;N` 数组读/写同 TCP。

### 1.4 Inovance TCP / Serial / SerialOverTcp（HSL）

- 底层：`InovanceTcpNet` / `InovanceSerial` / `InovanceSerialOverTcp`，经 `IInovanceClient` 适配。
- 地址：可选站号前缀 `s=<n>;`；区域**随 Series 变化**（必须提供 `ExtendedProperties["Series"]`）：AM（`Q/QX/IX/I/MX/MW/MD/MB/SM/SD`）、H3U/XP、H5U、Easy（`M/S/T/C/D/SD/R/B` + 八进制 `X/Y`）；位别名 `Q5`→`Q5.0`。
- 类型：Bool→位区读写；其余数值走 HSL 类型读写，字节序由 HSL `DataFormat` 控制；String=ASCII 16；ByteArray 读 1 字节；**Int8/UInt8 拒绝**；UInt64 以字符串返回。

### 1.5 Profibus

- **不是 PROFIBUS 协议栈**，是包装 `ModbusTcpDriver` 的薄壳（`ProfibusDriver.cs:32`）：经 “PROFIBUS↔以太网网关” 把 DP I/O 映射成 Modbus-TCP 寄存器。因此编码/地址/字节序与 1.2 完全一致。

---

## 2. 第三方库（HSL）/ OPC 包装类

> 以下驱动只做“地址归一化 + 类型到 HSL 调用的选择”，实际字节编码在 HSL 内部。字节序/字符串顺序仅在给出对应扩展属性时生效，否则用 HSL 默认（通常大端 ABCD）。

### 2.1 FINS（`OmronFinsNet`）

- 地址正则 `^(DM|CIO|WR|HR|AR)(\d+)(\.(\d+))?$`；映射 `DM→D、CIO→C、WR→W、HR→H、AR→A`；Bool→`{p}{word}.{bit}`（位 0–15）；不匹配则原样透传（**typo 不报错**，`FinsDriver.cs:296`）。
- 类型：各数值→HSL 对应 `Read*/Write*`；String→`ReadStringAsync(16, UTF8)`；ByteArray→`ReadAsync(GetWordCount)`（Int32/Float=2、Int64/Double=4、其余=1）；**Int8/UInt8 拒绝**。
- 编码开关：`DataFormat`、`IsStringReverseByteWord`/`StringReverse`；另 `NodeAddress/SA1`、`SNA/SA2/DNA/DA1/DA2`、`PlcType`、`ReceiveUntilEmpty`。

### 2.2 OmronHostLink（`OmronHostLink` / `OverTcp`）

- 地址正则含长/短名（`DM|D`、`CIO|C` 等）；Bool→`.bit`；模式由 `PortName` 决定串口或 TCP。
- 类型同 FINS；String=UTF8 16；ByteArray 读固定 1 字；Int8/UInt8 拒绝。
- 串口默认 **7/Two/Even**（与 Mewtocol 的 8/One/Odd 不一致）；扩展属性 `UnitNumber/ICF/DA2/SA2/SID/DataFormat/IsStringReverseByteWord`。

### 2.3 Mewtocol（`PanasonicMewtocolOverTcp` / `PanasonicMewtocol`）

- 地址：数字式 `DT/SR/X/Y/R/T/C`，`DT→D{n}`；TCP 支持 `s=NN;` 站号前缀（串口不支持）；原生式 `SR|X|Y|R|T|C|D|LD|L|F`。
- 类型：Bool 仅对 X/Y/R/L/LD/D/F 用 `ReadBoolAsync`，其余 Bool→`ReadUInt16>0`；String=**ASCII** 16；ByteArray 读 1 字；Int8/UInt8 拒绝；UInt64 支持。
- 定时器/计数器 `T*/C*` 仅 Bool/UInt16。默认站号 238。`DataFormat`/`IsStringReverseByteWord` 可调。

### 2.4 FANUC Robot（`FanucInterfaceNet`）

- 地址：位区 `SDO/SDI/RDO/RDI/SO/SI/UO/UI`，字区 `GO/GI`；`WO/WI→SDO/SDI+8000`、`AO/AI→GO/GI+1000`；`FANUC_DATA`=整帧 JSON；`DATA:<prop>`=同步 `ReadString`；CRX 别名 `CRX/Start→UI6`。
- 类型：位区返回 `bool[]`（仅 `DataType.Bool` 取首元素），字区 `Convert.ChangeType(Content[0], TypeForWord(...))`；`TypeForWord` 对 **Int8/UInt8/Int64/UInt64/Float/Double/String/ByteArray 一律回落 ushort**（无拒绝、无校验）。**写忽略 dataType**。
- 编码开关：`Encoding`→`client.StringEncoding`（默认 UTF-8）；`PingTimeout` 默认 3000；无 `DataFormat`。

### 2.5 Estun Robot（`EstunTcpNet`）

- 地址：`ESTUN_DATA`(整帧 JSON)、`DATA:<prop>`、`DI/DO/AI/AO<n>`、`CMD:<name>`，其余按 HSL 原始 Modbus 地址透传。
- 快照读 `ReadRobotData()`（保持寄存器 40001–40100）带 TTL 缓存（默认 200ms）。`DATA:` 返回字段自身类型；IO 固定 Bool/Float；命令 `RobotStartPrograme/...`、`SetGlobalSpeed` 转 `Int16`。
- 原始 Modbus 读写支持 Bool/Int16/UInt16/Int32/UInt32/Int64/Float/Double/String(ASCII 16)；**无 UInt64/ByteArray/Int8/UInt8**。
- 已知 HSL bug：`EstunTcpNet(ip,port,station)` 丢弃 station，驱动重设 `client.Station`；用反射关掉 HSL keep-alive Timer。

### 2.6 Huazhong Robot（`ModbusTcpNet`）

- 地址：路径映射 `/Robot/Joint/J1` →（`ModbusAddress`、`DataType`、可写）；未命中且非 `/` 前缀则按原始 Modbus 透传。映射构造期即拒绝 `Int8/UInt8`，并要求类型与请求一致。
- 类型：Bool/各数值→HSL；**UInt64 读→字符串、写→ulong（不对称）**；String 用 `_stringLength`（默认 16，最大 250）**UTF-8**；ByteArray 默认 2 字节（偶数）。
- 编码开关：`Station`(1–255)、`AddressMap`(JSON)、`PingAddress/PingDataType`、`StringLength`、`ByteArrayLength`、`DataFormat`（设在 `client.DataFormat`）。

### 2.7 OPC UA（OPC Foundation 栈）

- 地址 `ParseNodeId`：`ns=/i=/s=` 交给 `NodeId.Parse`；含 `:` 且首段为数字→`new NodeId(parts[1], ns)`；否则 `new NodeId(address, 2)`（**命名空间硬编码 2**）。
- 类型解码 `OpcUaValueCodec`：内置类型映射中 **SByte→Int16、Byte→UInt16，UInt64 无分支→String**；`DateTime`→ISO-8601、大整数→String、非有限浮点→String、数组/结构→JSON String。写 `dataType` 被忽略，直接 `new Variant(value)`。
- 扩展属性：`EndpointUrl`、`UseSecurity`（默认 true）、`AutoAcceptUntrustedCerts`、`RejectSHA1SignedCertificates`、`SuppressNonceValidationErrors`。读失败 `Value=null!`；批量读无逐项容错（一点失败整批中断）。

---

## 3. 文本 / JSON / XML / 厂家 SDK 类

### 3.1 NCLink（MQTT + JSON）

- 传输：MQTTnet，UTF-8 JSON；主题 `Query/Request/{guid}`、`Set/Request/{guid}`、`Probe/...`、`Sample/{guid}/{channel}`；用 `@id` 关联请求/响应。
- 地址：`ResolveDataItemId` 优先级 = Probe 上报 DataItem ID → name 反查 → 品牌适配器（`DefaultBrandAdapter`/`HNCBrandAdapter`/`GSKBrandAdapter`）→ 原样透传。
- 解码：`Parse*` 出 `{id,value,quality,timestamp}`；`ToTagValue` 对 **null+空质量→Bad**（明确不伪 Good），quality 字符串 `good/bad` 否则 Uncertain；`ConvertValue` 用严格 `GetValue<T>`，ByteArray=Base64，类型不符即 Bad。
- 扩展属性：`DeviceGuid`(必填)、`Brand`、`MqttBroker*`、`UseTls`、`CaCertPath`、`SampleChannels`；文件 `FileDataItemId`、`TransferChunkSize`(≤64KiB)。

### 3.2 NCLinkApi（HTTP + JSON）

- 传输：`POST /v1/{deviceId}/data/`，响应 `{status,code,value:[[...]]}`；文件 `GET/POST /v1/{id}/file/`（multipart/字节流）。
- 地址：`path[?index=N|N1,N2][&offset][&key][&timeout]`；`NormalizePath` 去 `/NC_LINK_ROOT`、合并 `//`、补前导 `/`。
- 解码：取 `value[0][0]`；`null→Uncertain`、转换失败→Bad；ByteArray=Base64。
- 扩展属性：`ApiBaseUrl`(默认 19001)、`DeviceId`(必填)、`ApiTimeoutMs`。
- 已知：写成功判定宽松（非 `false` 皆视为成功）；批量缺项→Uncertain。

### 3.3 MTConnect（HTTP + XML）

- 传输：`GET /probe`、`/current`，XML；默认端口 5000；写为厂家适配 POST（JSON `{address,dataType,value}`，可带 Bearer）。
- 地址：DataItem `id` 即路径；`ResolveDataType` 把 MTConnect 类型→`DataType`，`EVENT/CONDITION`→String。
- 解码：缺 id→Bad；字面 `UNAVAILABLE`→空串 Uncertain；否则共用 `TagValueConversion`，转换异常→Bad；时间戳按 UTC 解析；ByteArray=Base64。
- 扩展属性：`WriteEndpointUrl`、`WriteBearerToken`、`WriteAddresses`（逗号/分号/换行分隔，**区分大小写**白名单）。标准端点只读。

### 3.4 Haas（TCP + ASCII 命令）

- 传输：TCP 默认 9999，命令以 `\r\n` 结束，行读上限 1024 字节。
- 地址：命名 `Q` 地址（`SerialNumber→Q100`…`LastAlarm→Q700`）；命名系统宏读 `Q600 NNNNN`；`Macro:{n}`→`Q600 %05d`（写 `E%05d value`，宏号 0<n<100000）；未知 `Q…` 原样透传。
- 解码：`ExtractValue` 去 `>`/`Q<code>` 前缀，`Q500` 保留复合值；值以 `?`/`ERROR`/`INVALID` 开头判为错误；空值→Uncertain，否则 Good。
- 写成功判定宽松（回显无错误即成功）。无扩展属性配置。

### 3.5 GSK（两套）

**Gskrm（原生 DLL / x86 IPC-HTTP）**
- 传输：默认进程内 P/Invoke `gskrm.dll`（x86，Cdecl，签名标注未验证）；或经 `GskrmIpcClient` HTTP JSON 调 x86 `IndustrialIoT.GskShim`。
- 地址：首 `:` 切分；族 `Status.*`、`Rate.*`、`Speed.*`、`Position.Abs|Machine|Relative[:axis]`、`Macro:n`、`Param:n[:axis]`、`Plc:ADDR[:len]`、`Tool.Offset`、`Part.Count`、`Time.*`。
- 值：位置 `float[]→double`、宏 `double`（**丢弃原生 empty 标志→空读成 0/Good**）、参数 `ldata`、PLC `byte[]`、时间 `(int)TotalSeconds`。写仅 Macro/Param/Plc。
- 已知：`Plc:ADDR[:len]` 与底层 `type:start:count` 格式不一致。

**GskWebServer（HTTP/JSON + WebSocket）**
- 传输：HTTP JSON（默认端口 11520，https 443），实时为 WebSocket；地址家族 `Realtime.*/Static.*/Macro/Param/Diagnose/SoftPlc/Program/Tool*/Alarm/History/Workshop`。
- 解码：`Unwrap(data|result|value)` 后导航 JSON；`ConvertValue` 按类型，ByteArray=Base64 或数字数组；`Running` 由 `state==2` 推导（字段缺失→按停止处理）。
- 扩展属性丰富（`Scheme/BaseUrl/ManagementBaseUrl/DeviceSn/各 Read/Write Path/AuthToken/...`）；认证 Basic 或 `X-Authorization-Token`。
- 已知：数值转换失败返回 `ToString()` 且标 Bad；未匹配地址可能返回整段 JSON 对象串。

### 3.6 FOCAS（原生 `Fwlib64.dll`）

- 传输：P/Invoke FOCAS2（`cnc_allclibhndl3` 等），默认端口 8193，`ushort` 句柄。
- 地址：小写层级路径 `/CNC/Axis/{label}[/Position]`、`/CNC/Fanuc/...`、`/CNC/Status/...`、`/CNC/PMC/{DI|DO|RI|RO}[n]`；机器人别名重写 `/Robot/*`→`/cnc/...`。
- 值：位置 `data * 10^-dec`、宏 `mcr_val * 10^-dec`、模态 `aux_data / 10^flag2`；可恢复异常整批重试，其余异常→Bad。
- 扩展属性：`AxisLabels`、`CncMemoryBrowseRoots`。写仅 `PMC DO/RO`（读改写 2 字节）。
- 已知：报警读取失败仍返回 `EW_OK` 并伪造文本；`ReadParameter` 恒按 8 字节 `ldata`。

### 3.7 HncSdk（HTTP/JSON → x86 Shim）

- 传输：驱动→HTTP JSON shim（默认 `127.0.0.1:39210`），shim 反射加载 `HncNetDllNoAdapterCSharp.dll`；FTP 文件用 `ftp.dll` + **GBK(936)**。
- 地址：`family:type…`：`sys/chan/axis/crds/var/param/tool/alarm/program`。
- 值：shim 返回 CLR `double/int/string`（按重载尝试），驱动经 `TagValueConversion`；非零返回码→Bad。写发 `dataType` 字符串 + `Convert.ToString(value)`。
- 扩展属性：`ShimBaseUrl`、`AutoStartShim`、`ShimPath`、`LocalIp/Port`、`ClientName`。
- 已知：反射取**第一个**成功重载，int/double/string 可能误选。

### 3.8 JingDiao（HTTP/JSON → x86 `NcMonIO.dll` Shim）

- 传输：驱动→HTTP JSON shim（默认 `127.0.0.1:39125`），shim 调 StdCall 导出（`_CreateJDMachMon@0`、`_GetMachPos@16`…），文件用 **GBK**。
- 地址：`Pos:{Mach|Abs|Rel}:{X|Y|Z}`、`Modal:*`、`State:*`、`Spindle:*`、`Rate:*`、`Macro:{number}`、`Program.Files`。
- 值：经共享 `TagValueConversion`，转换异常→Bad；写仅 `Macro:{非负整数}`（范围/有限/整数校验）。
- 已知：用哨兵值判定缺失（`Spindle` 用 `NaN`、`Rate` 用 `-1`、`Modal` 用 `null`），合法 NaN/-1 会被误判；`ReadState` 成功即 Good（0 与默认不可分）。

---

## 4. 文件传输

- 三套文件驱动实现 `INCProgramTransfer`；标签读写一律 `NotSupportedException`。
- 校验和差异：FTP=SHA-256(源流) 小写；NFS=SHA-256(流) 大写；SMB、Serial 无。均**二进制**传输，无 ASCII 模式。

### 4.1 FTP（FluentFTP）

- `AsyncFtpClient` + `NetworkCredential`（空用户→anonymous）；连接/数据传输超时分别映射。加密经 `FtpEncryptionMode/DataConnectionEncryption/ValidateAnyCertificate`。**不暴露 ASCII/被动模式**（默认二进制、自动数据连接）。
- 路径 `FileTransferRemotePath.CombineFtpDirectory`（`\`→`/`）。上传 `UploadStream(Overwrite, createRemoteDir)`；失败且 `NLST … not implemented for PASV` 时删除远端重试 `NoCheck`。下载 `DownloadStream` + 长度完整性检查。续传 `FtpRemoteExists.Resume`（`SupportsResume=true`）。

### 4.2 SMB（SMBLibrary, SMB2）

- `SMB2Client.Connect(DirectTCPTransport)` + `Login(domain,user,pass)`（空=guest）；共享来自 `ExtendedProperties["ShareName"]`。裸 `byte[]` 读写，分块 64KB。上传 `FILE_OVERWRITE_IF`，下载 `FILE_OPEN`（按 `STATUS_END_OF_FILE` 结束），续传 `FILE_OPEN_IF`+偏移。路径 `\` 归一。无校验和。

### 4.3 NFS（OS 预挂载目录）

- 无 NFS/RPC 实现，操作 `ExtendedProperties["MountPoint"]` 预挂载目录；`PingAsync=Directory.Exists`。分块 80KB，SHA-256（大写）。路径安全：拒绝绝对/UNC、单文件名校验、`ResolveWithinRoot` 防越权与符号链接/reparse 逃逸。

### 4.4 Serial（自定义帧，最值得注意）

- 串口：`Handshake.XOnXOff`；默认 `COM3/9600/None/8/One`；`SupportsResume=false`。
- **帧格式**（`BuildFrame` 356 / `ReadFrameAsync` 391）：

| 字段 | 长度 | 取值/顺序 |
| --- | --- | --- |
| STX | 1 | `0x02` |
| SeqNo(序号) | 2 | uint16 **大端** |
| Length | 2 | uint16 **大端**（数据字节数） |
| Data | N | 原始载荷（≤1024） |
| CRC-16 | 2 | **大端** |
| ETX | 1 | `0x03` |

- CRC：`ComputeCrc16` poly=`0x1021`、init=`0xFFFF`、MSB-first、无反转/无终异或 → **CRC-16/CCITT-FALSE**；覆盖 **SeqNo+Length+Data**（`4+N` 字节，不含 STX/ETX）。
- 常量：`STX=0x02/ETX=0x03/ACK=0x06/NAK=0x15`；`MaxChunkSize=1024`；`MaxRetries=3`；`AckTimeoutMs=5000`（**声明但未用**，实际用串口 `ReadTimeout`）。
- 上传：seq 从 0，逐 1024B 帧→等 ACK，NAK/超时同 seq 重发≤3 次；结束发**零长度帧**（下一 seq）并要 ACK。
- 下载：先发 `BuildFrame(0xFFFF, UTF8(path))` 请求帧（不等 ACK）；循环读帧：坏帧/错序→NAK+重试≤3；零长度=EOF→ACK 结束；正常帧写盘、`expectedSeq++`、ACK。ACK/NAK 为单字节、无 CRC。

---

## 5. 跨协议对照

### 5.1 类型处理

| 类型 | 处理差异 |
| --- | --- |
| Int8/UInt8 | 仅 S7 原生支持；Modbus/Inovance/FINS/HostLink/Mewtocol 明确拒绝；FANUC 静默回落 ushort；OPC UA 映射为 Int16/UInt16；Huazhong 构造期拒绝 |
| UInt64 | S7/Modbus/Inovance/FINS/HostLink/Mewtocol 支持（结果字符串化）；Hub 读字符串写 ulong；FANUC/Estun 不支持；OPC UA 无分支→String |
| ByteArray | 多数读 1（S7 读 1/写取数组长）；OPC UA=NByteString；JSON 类=Base64 |
| String | S7=裸 ASCII 定长；FINS/HostLink/Huazhong=UTF-8；Mewtocol/Inovance/Modbus=ASCII；长度默认均 16（Huazhong 可到 250） |
| 字节序开关 | ModbusRTU/Inovance/FINS/HostLink/Mewtocol 有 `DataFormat`(+`IsStringReverseByteWord`)；ModbusTCP/Profibus 硬编码大端 ABCD；S7 固定大端 |

### 5.2 数据质量与遮蔽（易伪 Good 的点）

- NCLink 明确拒绝把 null+空质量当 Good（较严格）。
- NCLinkApi：写成功判定宽松；批量缺项→Uncertain。
- FOCAS：报警读取失败仍 `EW_OK`+伪造文本；`ReadParameter` 恒 8 字节。
- JingDiao：哨兵值（NaN/-1/null）误判；`ReadState` 0 与默认不可分。
- HncSdk：反射重载可能选错；Gskrm：空宏读成 0/Good。
- GskWebServer：数值转换失败返回字符串且 Bad；`Running` 缺字段按停止。

### 5.3 字节序/校验和/分块

- 字节序：S7、ModbusTCP/Profibus 固定大端；HSL 系默认 ABCD，可按设备调 `DataFormat`；Serial 帧内序号/长度/CRC 均大端。
- 校验和：FTP/NFS=SHA-256（大小写不同）；SMB/Serial 无（Serial 用 CRC-16/CCITT-FALSE 做帧校验）；Haas/NCLink 等文本协议无。
- 分块：FTP 声明 80KB（未用，交给库）；SMB 64KB；NFS 80KB；Serial 1024B。

---

## 6. 结论与边界

- 高价值的“编码适配层”集中在：S7 地址/值编解码、Modbus 地址语法与类型宽度、HSL 包装的类型回落、OPC UA/CNC 的数值→字符串化、以及 Serial 自定义帧。
- 已知“编码相关”风险点：ModbusTCP 无字交换配置、FINS/HostLink/Mewtocol/Inovance 拒绝 Int8/UInt8、FANUC 忽略类型、若干 SDK 把无效值遮蔽为 Good（详见 `PROTOCOL_REVIEW_20260926.md`）。
- 本盘点为源码还原，未连真机；各厂家 SDK 与机型兼容性仍需现场验证。
