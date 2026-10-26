# 数控/PLC 协议实现自查报告（2026-10-14）

> 范围：`Host/src/IndustrialIoT.Protocols*` 各协议驱动。依据**公开规范/主流开源实现**（S7.Net、Snap7、FANUC Fwlib 手册、Haas/MTConnect/NC-Link 资料、Omron/Panasonic 手册等）与代码自洽性静态审查。
> 基线：**GSK WebServer 已真机跑通**，未列入问题。
> 标注：✅ = 已人工复核确认；⚠️ = 需现场抓包/厂商手册核实（不确定）。
> 本报告仅记录问题，未改动任何仓库代码。

---

## 一、高危（建议优先修复）

| # | 协议 | 位置 | 问题 | 正确做法 |
|---|------|------|------|----------|
| 1 | Siemens S7 | `SiemensS7/S7TcpClient.cs:96,102` | ✅ COTP CR 的**目的 TSAP 首字节用 0x01(PG)**，应为 **0x03(S7 基本通讯)**；同文件 S200 分支却写死 `03`，自相矛盾。主流 S7-1200/1500/300/400 很可能拒绝连接 | dst TSAP 首字节改 `0x03`；源 TSAP 常见 `0x0100`（现 `0x0102`）一并核 |
| 2 | FANUC | `FANUC/FocasAddressMapper.cs:20-23,523-529` | ✅ PMC `adr_type` 映射错：`DI=0/DO=1/RI=5/RO=6`；FOCAS 实际 **0=G,1=F,2=Y,3=X,4=A,5=R,6=T** → DI 读 G 区、DO 读 F 区、RI/RO 落 R/T | 输入→X(3)、输出→Y(2)（或按机床 PMC 分配）；RI/RO 须可配置 |

---

## 二、中危（按协议）

### 共享 / 选型
- ✅ **设备推荐协议名未归一化**：`DeviceTemplatesController.cs:123,126` 返回 `"Modbus"`，枚举只有 `ModbusTCP`，且 `DeviceUpstreamSyncService.cs:26-32` 别名表**无 `Modbus`** → 同步到 Host 无法解析。建议统一返回枚举名。

### Modbus / Inovance / Profibus
- `ModbusTcpDriver.cs:411-423,479-510` **32/64 位与浮点字序不可配**（仅 ABCD；RTU 有 `DataFormat`）。
- `ModbusTcpDriver.cs:59-66` 连接超时**泄漏旧连接**（`Task.Run(Connect)+WaitAsync` 超时后后台仍连上，旧 socket 未释放）。
- `ModbusTcpDriver.cs:56,296`、`ModbusRtuDriver.cs:38,191` 配置键**大小写敏感**（前端小写键静默落默认站号）。
- `ModbusTcpDriver.cs:198-243` `BatchRead` **未合批**（逐点请求）。
- 低：`ModbusTcpDriver.cs:318-319` 异常过滤器死分支（`ExceptionCode<0` 恒假）；Connect/Disconnect 未持 `_semaphore`。
- `InovanceAddressSpace.cs:123` 位序号用 `\d+`（应 `[0-7]+`，`X7.9` 会被放过，靠 HSL 兜底）。

### Siemens S7
- `S7TcpClient.cs:156-157` Timer 的 transport size 用了 **0x1C(Counter)**，应为 **0x1D**。
- `S7Address.cs:88` + `S7TcpClient.cs:120,137` Counter/Timer **长度按字节而非元素**（会多读一个、末地址可能越界）。
- `SiemensS7Driver.cs:31-34` 重连未释放旧 client；`S7ValueCodec.cs:19,54` String 当裸 ASCII（对 TIA `STRING` 会读到长度头）；`S7TcpClient.cs:195-201` 错误码表不全；`SiemensS7Driver.cs:174-179` S7-400 默认 slot=3（常见 2）。
- ✅ **已确认正确**（勿误改）：Any 指针按位编码（`S7TcpClient.cs:151-153`）、12 字节响应头偏移、请求/数据项长度语义——与 S7.Net 一致。

### FINS / Mewtocol
- `FinsProtocol.cs`、`MewtocolProtocol.cs` **全仓未被调用（死代码）**，运行期走 HSL → 建议删除或接线并测试。
- 配置键大小写敏感；`Bool` 无 `.bit` 默认取 bit0；字符串用 UTF8（Omron 应 ASCII）；`ByteArray` 固定读 1 字；`BatchRead` 未合批。
- ⚠️ Mewtocol WCS/RT/RC 帧与"设定+当前两字"语义仅存在于死代码。

### FANUC（其余）
- ⚠️ `NativeFocasApi.cs:448` `cnc_allclibhndl3` 超时**疑似单位用秒而传毫秒**。
- `NativeFocasApi.cs:157-164` `ODBAXIS` 少一个 `dummy short`，`data[0]` 与 `type` 重叠（当前死代码）。
- `NativeFocasApi.cs:80-121,760-807` `cnc_*4` 家族用 `object`+`AsAny` 编组指针；`cnc_upstart4.fileName` 误标 `[Out]`。
- ⚠️ `NativeFocasApi.cs:507,701` `cnc_rdposition/rdspeed` 传 `type=-1`。
- `NativeFocasApi.cs:17-18` 能力标志与已实现地址不符（`/CNC/Program/Block` 等被 `NotSupported`）。

### Haas MDC
- ⚠️ `HaasMdcDriver.cs:189,202-226` 查询疑似缺 `?` 前缀、未按 `>` 提示符同步分帧。
- `HaasMdcDriver.cs:193-198` 任一读异常即**断链置 Faulted**。
- ⚠️ `HaasCommandMap.cs:42-46` **机械/工件坐标宏号疑似对调**（按 Haas 宏表核实）。

### MTConnect
- ✅ `MTConnectXmlParser.cs:22,75` 只用 `GetDefaultNamespace()`；Agent 输出**带前缀**时全匹配失败 → 应用 `root.Name.Namespace`。
- 不处理 `units`；`/sample` 未实现；Condition 同名覆盖未按级别聚合。

### NC-Link (MQTT) / NC-Link API (HTTP)
- `NCLinkProtocol.cs:143-159` vs `:351-364` `values`/`results` 字段不统一，可能**漏检 item 级错误**。
- `NCLinkDriver.cs:557-573` `GetValue<bool/int>()` 对 `0/1`、字符串数值**过严**整点判 Bad。
- `NCLinkApiContracts.cs:67-68` `IsSuccess` 要求 `status=="SUCCESS"` 过窄。
- `NCLinkApiSampleSession.cs:132-139` 采样 `SYS_SMPL` 假设 JSON int 数组（手册若小端字节流则取错）。
- `NCLinkApiClient.cs:57-65` data 接口统一用 POST（手册示例 GET）。

### OPC UA
- `OpcUaDriver.cs:329-362` 批量读**无容错且未校验 Results 数量**（可能 `IndexOutOfRange`）。
- `OpcUaDriver.cs:376` 写入 `new Variant(value)` 按运行时类型推断，可能写错类型。
- `OpcUaDriver.cs:685-700` NodeId 无 ns 前缀默认 ns=0。

### HNC SDK / 机器人 / 精雕
- `ReflectionHncSdkGateway.cs:194-225` 枚举命名空间只按 `HNCAPI_INTERFACE.*`（若 SDK 在另一命名空间则 chan/axis/crds/var **读写全坏**）。
- `ReflectionHncSdkGateway.cs:427-437` 文本 out 用 `ref string`，无法匹配 `StringBuilder`；`HncSdkFtpApi.cs:20-24` 上传 ANSI 传中文路径；shim 丢弃 `TimeoutMs`。
- **华中机器人** `HuazhongRobotDriver.cs:26,63-94,230-244`：整机当**纯 Modbus/TCP（默认 502/站号1）且无单位换算**；`HuazhongRobotAddressSpace.cs:49-51` 直传地址默认"可写"。
- **埃斯顿** `EstunRobotDriver.cs:372-401`：`ReadRobotData` 超时后**仍复用同一 socket**；`:448-474` `ReadRawAsync` 缺 UInt64/ByteArray。
- ✅ **精雕** `JingDiaoDriver.cs:34-36` `Capabilities` **缺 `Write`**（但实现了宏写）；`JingDiaoShim/Program.cs:60-62,77,84` shim **定长数组可能越界写**。

### 文件传输
- **FTP** `FtpTransferDriver.cs:407-421` 传输模式（PASV/EPSV、ASCII/BINARY）**不可配**；`:142-151` `Checksum` 只是本地源哈希**未与远端比对**；`:253-254` 续传 `offset` 未与远端长度核对。
- **SMB** `SmbTransferDriver.cs:49` 忽略 `config.Port`；`:553-578` 目录创建失败静默；无回读校验。
- **NFS** `NfsTransferDriver.cs:12-22,41-67` **非协议级 NFS**，要求预先挂载。
- **串口** `SerialTransferDriver.cs:13-18,484-499` 自定义 `[STX][Seq][Len][Data][CRC16-CCITT-FALSE][ETX]`+ACK/NAK，**与 XMODEM/YMODEM 不兼容**；`:34` `AckTimeoutMs` 定义**未使用**（`ReadTimeout=0` 会永久阻塞）。

---

## 三、低危 / 一致性
- Modbus 裸 5 位引用号歧义（`00512` 被当线圈 512）。
- S7 经典 200 与 200Smart 共用 CR 分支。
- FINS `FinsProtocol.cs:180-194` 长度缺下界校验。
- Haas `HaasCommandMap.cs:143-149` 带逗号响应会截断。
- OPC UA sessionTimeout 硬编码 60s、`SelectEndpoint` 无 None 回退。
- 能力矩阵 `PlcCapabilitiesController` 仅 4 品牌（汇川/欧姆龙/松下/西门子），缺 FANUC/Haas/马扎克/GSK/华中/机器人。

---

## 四、已确认正确（勿误改）
- S7：Any 地址按位编码、TPKT+COTP+S7comm 帧与偏移、请求/数据项长度语义（对照 S7.Net）。
- Modbus：功能码/异常码/地址基准（0/1 基）/CRC16/MBAP 由成熟库承载，字节序有测试覆盖。
- 注册层：`DriverRegistry` 的 `"*"` 是**按协议内**回退（键 `(Protocol,Brand)`），不跨协议误配；解析顺序 `model→brand→"*"` 合理。
- GSK WebServer：真机联通验证通过。

---

## 五、需现场核实（不确定项清单）
- FOCAS `cnc_allclibhndl3` 超时单位；`cnc_rdposition/rdspeed` 是否接受 `type=-1`；`ODBPOS` 字段数。
- Haas：查询是否必须 `?`；响应是否以 `>` 分帧；`#5021/#5041` 宏号归属；主轴宏号。
- NC-Link：topic 命名、`@id/values/results` 字段字面量、Sample 结构、文件 operation 名、成功 status 字面量、`SYS_SMPL` 返回编码。
- 厂商 SDK：HNC 枚举命名空间与文本 out 形态、`ftp.dll GetDirInfo` 格式；精雕 `GetMachFileList`/`GetFileAttribute` 输出格式与端口默认；机器人端口/寄存器/单位约定。
- 各 S7 CPU 对 PG(0x01) TSAP 的容忍度（建议真机实测）。

---

## 六、建议处理顺序
1. S7 目的 TSAP `0x01→0x03`。
2. FANUC PMC `adr_type` 映射 + `cnc_allclibhndl3` 单位核实。
3. 设备推荐协议名归一化。
4. Modbus TCP 32 位字序可配 + 连接超时泄漏。
5. MTConnect 命名空间、NC-Link 字段/类型宽松化、OPC UA 批量读容错、精雕能力位与数组长度。
6. 清理死代码 `FinsProtocol`/`MewtocolProtocol`（删除或接线+测试）。

---

## 七、联调时新发现（模拟器 ↔ 目标机真实驱动，2026-10-14）

用 DeviceSimGateway 作为设备端、目标机(192.168.1.200) IIS 上的真实 Host 驱动联调，发现：

- ✅ **GSK WebServer / Modbus TCP / FINS / OPC UA / S7 / NC-Link 均可端到端读取**（目标机 Host `/api/data/{id}/read` 返回 Good）。
- ⚠️ **S7 响应项长度约定不一致（Host 客户端）**：`SiemensS7/S7TcpClient.cs` 的 `ParseReadResponse` 用 `byteLength = transportSize==0x03 ? 1 : (length+7)/8` —— 把非位传输的响应长度当**位**解析；而标准与 `S7.Net` 客户端按**字节**解析。为与目标机联调，模拟器已按"位"发送（`data.Length*8`）。**建议核对 Host 客户端此处的解析是否符合所用 CPU/PLC 约定**（若按标准应为字节，则该处为 Host 侧 bug）。
- 说明：模拟器为配合 Host 采用"位"约定；对标准 S7 客户端(S7.Net)可能表现为长度不符。两者不可同时满足，需按目标侧真实客户端取舍。

### 目标机环境要点
- 真实 MySQL 服务为 `MySQL80`(3306，自启)；本项目 `IOTTMySQL`(3307) 需另行注册为服务并自启（否则 Host 连 3307 失败 → 500）。
- IIS 中本项目站点：`IOTT-Web`(8082)/`IOTT-API`(5087)/`IOTT-Host`(5173)，应用池须为 No Managed Code；旧站点 `IOTT`(8081)/`API`(5087)/`Host`(5173) 已停止。
- 目标机还需 emqx(1883)、influxdb3(8181) 在运行。
