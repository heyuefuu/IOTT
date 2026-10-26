# CNC / PLC 监控模块内部逻辑

- 日期：2026-10-09；范围：前端 `code20260423/code/src`（Vue 3 + Element Plus）中的 CNC 模块（`/industrial`，菜单"CNC监控"）与 PLC 模块（`/plc`，菜单"PLC监控"），及其调用的 `MachineConnectionApi` 网关与 `IndustrialIoT.Host` 链路。
- 手段：只读源码还原，未运行前端。给出 `文件:行` 证据。
- 术语：仓库无字面 "CNC 检测/PLC 检测"，对应前端路由注释"工控协议通讯验证模块"（`router/index.ts:19`）与"PLC信号通讯验证模块"（`router/index.ts:82`）；本文统一称"CNC 监控/PLC 监控"。

---

## 0. 总体定位

- **同一套后端引擎，两个按设备类型过滤的前端壳**：CNC 与 PLC 都调用相同的网关接口（`/api/devices`、`/api/data`、`/api/addressspace`、`/api/collection*`、`/api/program-transfer`、`/api/plc/capabilities`），由网关代理到 **同一个** `IndustrialIoT.Host` 控制器；服务端没有 CNC/PLC 分支，差异仅是：设备类型过滤、（CNC）程序传输配置门控、（PLC）能力矩阵与 Inovance/串口配置。
- **页面构成**：
  - CNC：`/industrial/device`(`industrial/DeviceView.vue`)、`/industrial/property`(`industrial/MachinePropertyView.vue`)、`/industrial/import`(`plc/CollectionImportView.vue`, meta.deviceType=CNC)。
  - PLC：`/plc/device`(`plc/DeviceConfigView.vue`)、`/plc/address`(`plc/AddressBrowserView.vue`)、`/plc/rw`(`plc/ReadWriteView.vue`)、`/plc/status`(`plc/StatusMonitorView.vue`)、`/plc/import`(`plc/CollectionImportView.vue`)。
- **鉴权**：所有 API 经 `api/machineConnectionClient.ts:7-11` 注入 `X-Auth-Token`（localStorage key `mc.auth.token`）。

---

## 1. 共享的后端调用链（CNC 与 PLC 相同）

```
前端 axios(/machine-connection) ─▶ 网关 :5087 ─代理▶ Host :5173 ─▶ 协议驱动 ─▶ 设备
```

| 能力 | 前端 API | 网关路由 | Host 路由 |
| --- | --- | --- | --- |
| 设备列表 | `machineConnectionDevicesApi.list(type?)` | `GET /api/devices` | 本地 `devices.json` |
| 连接测试 | `testConnection(id)` | `POST /api/devices/{id}/test-connection` | `api/Devices/{id}/test-connection` → 驱动 `PingAsync` |
| 地址浏览 | `browseAddressSpace(id,parent,protocol)` | `GET /api/addressspace/{id}` | `api/addressspace/{id}` |
| 读/写 | `readTags/writeTags` | `POST /api/data/{id}/read|write` | 同路径 → 池化驱动 |
| 采集配置 | `machineConnectionCollectionApi.*`、`datacollectionApi` | `api/collection-config/*`、`api/collection/*`、`api/batch-import/tags/{id}` | 同路径 |
| 程序传输(CNC) | `machineConnectionProgramTransferApi.*` | `api/program-transfer/*` | 同路径 → `INCProgramTransfer` |
| PLC 能力矩阵 | `machineConnectionDiagnosticsApi.plcCapabilities()` | `api/plc/capabilities` | `PlcCapabilitiesController` |

---

## 2. CNC 监控模块（`industrial/DeviceView.vue`，约 5117 行）

### 2.1 组件结构与关键状态
- 布局：左侧设备树+搜索（`:7-15`）、操作栏（新增/批量/模板/导出/导入点位/导入设备/上游同步/NC-Link 诊断/历史库设置/搜索，`:20-74`）、设备卡片+分页（`:76-178`）、新增/编辑设备弹窗（含 CNC 程序传输区与协议扩展字段，`:184-409`）、设备详情弹窗（`:411-514`）、**点位弹窗**（树+表+工具条，`:516-610`）、程序传输弹窗（`:612-714`）、实时采集弹窗（`:715-732`）、采集历史弹窗（`:733-769`）、远端路径选择器（`:770-814`）、NC-Link 诊断弹窗（`:816-897`）。
- 关键 ref：`devices`/`devicesLoading`/`devicesError`（`:1244`）；点位弹窗 `pointDialogVisible/DeviceId/DeviceProtocol`、版本守卫 `pointRequestVersion/pointDialogVersion`（`:1310,1468`）；点位树/表 `pointTreeData/pointTableData/selectedPointRows/pointFrequencyEdits`（`:1456,1325`）；DB 已存点位缓存 `savedPathsInDb/savedPointConfigByPath`（`:1329`）；采集态 `savedCollectionPointsByDevice/collectionTimerIdsByDevice/collectionRunsByDevice/collectingDeviceIds`（`:1485-1494`）；传输态 `transferForm/transferRemotePath/transferSelectedFiles/transferHistory/transferSubmitting`（`:2390-2415`）；`deviceForm`（`:3833-3890`）；NC-Link `nclinkDialogVisible/nclinkProbe`（`:4277`）。
- 常量：`DEFAULT_COLLECTION_FREQUENCY_MS=1000`（`:1484`）；扁平化上限 `POINT_FLATTEN_MAX_VARIABLES=5000`、`POINT_FLATTEN_MAX_FOLDER_STEPS=2000`（`:1475-1478`）。
- 计算：`devicesFiltered`（`:1353-1373`，先按 `deviceType==='CNC'` 过滤）、`filteredDevices`（分页切片 `:1377-1380`）、`detailMqttSummary`（`:3765`）。

### 2.2 设备加载
- `onMounted → loadDevices()`（`:1299-1301`）→ `list()`（**不带 type**，`:1249-1253`）→ `mapDtoToUi`（`:1081-1136`，把 `extendedProperties` 映射为 brandKey/opcua/deviceGuid/mqtt 等，`status` 经 `mapStatus` `:1038`）。
- 错误分级给出运维提示：502（上游 IoT 不可达）、404（代理/基础地址错）、500、Network/ECONNREFUSED 等（`:1262-1293`）。

### 2.3 设备增/删/改/详情
- 新增 `openAddDeviceDialog`：重置表单 + `treeDefaultsForNewDevice()`（按品牌节点预置协议/端口，`:3776-3831`）。
- 编辑 `editDevice`：回填并保留 `originalProtocol/extendedProperties/originalTransferProtocol/transferExtendedProperties` 以便 diff（`:3992-4053`）；切换协议/品牌/方案/传输协议会自动设默认端口（`:3892-3923`）。
- 保存 `saveDevice`（`:4056-4192`）：先做必填/端口/扩展校验（见 2.10），构建 CNC 传输配置 `buildProgramTransferConfig`（`deviceTransferConfig.ts:27-76`，非 CNC 返回 undefined），构建 `extendedProperties`（`:1138-1242`），编辑时无传输则 `clearTransfer:true`（`:4157`），调用 `update/create`，按 `upstreamSynced/upstreamError` 提示上游同步结果，最后 reload。
- 删除 `deleteDevice`（`:4202-4217`）；详情 `viewDeviceDetail`（`:4195-4199`）。

### 2.4 连接检测
- `testConnection(id)`（`:4679-4702`）→ `POST /api/devices/{id}/test-connection`。
- 结果分级：`success && mode==='tcp'` → 警告"TCP 端口可达，协议尚未验证"（`:4687`）；`mode==='driver'` → 成功并显示 `latency`（`:4689`）；成功无 mode → 警告；失败 → 错误。

### 2.5 点位弹窗与地址树懒加载
- `openPointDialog(device)`（`:1572-1604`）：递增版本号、清缓存、加载根子节点。
- `loadAddressChildren`（`:1993-2022`）→ `browseAddressSpace(deviceId, parentPath, protocol)`（`GET /api/addressspace/{id}`）；节点映射 `mapAddressNodeToTreeNode`（`:2129`）；失败静默。
- 点击节点 `handlePointTreeNodeClick`（`:1517-1570`）：文件夹懒加载后**BFS 扁平化**所有后代变量（`:2042-2105`，超限告警），变量单行显示；应用已存勾选 `applySavedPathsToTable`（`:1918-1933`）。
- 已存点位默认勾选用 `refreshSavedPathsFromDb`（`:1891-1916`，`datacollectionApi.list`）。

### 2.6 读 / 写（单点与批量）
- 单点读 `handleReadSinglePoint`（`:1690-1711`）→ `readTags`（单元素 tags）。
- 批量读 `handleBatchReadPoints`（`:1810-1842`）：过滤可读变量，一次 `readTags`，按地址回填。
- 单点写 `handleWriteSinglePoint`（`:1713-1747`）：`parseWriteValue` 解析后写，成功后回读。
- 批量写 `handleBatchWritePoints`（`:1844-1889`）：弹框输入统一值 → `parseWriteValue` → `writeTags` → 成功再批量读。
- 值解析 `parseWriteValue`（`:2257-2297`）：Bool 词表、整数安全整数校验、Float/Double、ByteArray JSON。

### 2.7 保存采集配置（点位）
- `handleSavePointConfig`（`:1935-1991`）：要求已选变量 → `datacollectionApi.sync({deviceId, items, visiblePaths})`（`POST /api/datacollection/sync`），`datatype` 归一、频率默认 1000，成功后更新本地缓存。

### 2.8 采集启停（**客户端驱动**）
- `runCollection(deviceId?)`（`:4581-4676`）：打开实时弹窗 → `datacollectionApi.list(deviceId)` 取权威点位（`:4601`，`datatype` 经 `mapDbDatatypeToReadType` `:2250`）→ 按频率分组（`:4629`）→ 每组先立即读一次，再 `setInterval` 周期读（`:4644-4663`，`inFlight` 防堆积）。
- `runCollectionBatch`（`:4519-4579`）：批量 `readTags` → 以 `errorMessage` 有无判定 成功/失败 → 更新行 → `telemetryInfluxApi.writeBatch`（`POST /api/telemetry/influx/batch`），写库失败置 `collectionStorageError`。
- `stopCollection`/`stopCollectionTimers`（`:4366,4338`）清定时器；`onBeforeUnmount` 全部清理（`:4721`）。
- **注意**：CNC `DeviceView` **不调用**服务端采集 `api/collection/start|stop|status`，也**不使用 SignalR**（`useDeviceDataHub` 仅被 `views/collection/CollectionManageView.vue` 使用）。也就是说该页面的"开始采集"是前端定时轮询 + 直接写 Influx。

### 2.9 采集历史
- `queryCollectionHistory`（`:4423-4462`）：`formatRangeTime`（day/week/month/custom，校验自定义区间）→ `telemetryInfluxApi.history`（`GET /api/telemetry/influx/history`）→ 映射含 `quality` 列。

### 2.10 程序传输（CNC 专有）
- 打开 `openTransferDialog`（`:3309`）默认远端路径（`:2560`）并载入历史。
- `startTransfer`（`:3345-3503`）：前置校验（FOCAS 路径形状、FTP/SMB/NFS 形状、OPC UA NodeId、上传需文件等）→ 上传单/多文件（`upload`/`uploadBatchWait`）、地址空间/文件夹递归下载、批量 ZIP（`downloadBatchZip`）。
- 远端选择器：地址空间协议（OPC UA/FOCAS）用懒加载树（`:2828`），其余协议用文件树（`:3155`）。
- API 中 `resume/capabilities/transferStatus` 存在但 `DeviceView` **未调用**（无 UI 触发续传）。

### 2.11 NC-Link 诊断 / 导入导出 / 上游同步
- `openNclinkDialog`（`:4298`）仅 NCLink 设备可选，`loadNclinkDiagnostics`→`GET /api/nclink/{id}/probe`（`:4308`），错误提示改用 NCLinkApi。
- 模板/导出/导入：`exportDeviceTemplate`、`exportAllDevices`、`importDevices`（multipart）、`syncUpstream`（`:4225-4274`）。

### 2.12 校验与错误处理
- `saveDevice` 手动校验：name/code/brand/model/protocol/host 必填（`:4058`）；端口 1–65535（Gskrm 除外）（`:4082`）；串口 `PortName`（`:4086`）；传输 host/port；SMB `ShareName`/NFS `MountPoint`；NCLink `DeviceGuid`/NCLinkApi `DeviceId`/GskWebServer `DeviceSn`；MTConnect 写端点+地址成对（`:4120`）。弹窗未用 `el-form` rules，全部命令式。
- HTTP 错误映射：502/404/500/Network 专门文案（`:1269-1291`）；通用提取器 `getApiErrorMessage`（`:2299-2341`，兼容 string/errors/error/detail/message/title，截断 400）。
- **质量（quality）未分支**：`quality` 只透传到 Influx 与历史列；实时行"成功/失败"仅由 `errorMessage` 判定（`:4531`）。

### 2.13 定时器 / 实时
- 点位表自动刷新 `startPointAutoRefresh`（`:1606`，5s/10s，随弹窗开关）。
- 采集定时器每频率组一个（`:4644`）。
- 批量传输轮询 `waitForBatchTransferTaskComplete` 每 1s、最多 300s（`machineConnectionProgramTransfer.ts:281-299`）。
- 无 SignalR。

### 2.14 CNC 过滤与门控
- 卡片列表 `devicesFiltered` 先按 `deviceType==='CNC'`（`:1353-1354`），再品牌/关键字/分页。
- 程序传输配置节仅 `v-if="deviceForm.deviceType === 'CNC'"`（`:249-283`）；`buildProgramTransferConfig` 非 CNC 返回 undefined（`deviceTransferConfig.ts:30`）。
- `MachinePropertyView` 恒 `list("CNC")` 并创建/更新时写死 `type:"CNC"`（`:330,388`）。

---

## 3. PLC 监控模块

### 3.1 DeviceConfigView.vue（PLC设备管理）
- 能力矩阵：`loadCapabilities`（`:374-388`）→ `GET /api/plc/capabilities`，失败回退 `FALLBACK_CAPABILITIES`；`protocolGroups` 按品牌分组（`:408-417`）。
- 设备列表/树：`devices`、`deviceTree`、`filteredDevices`、分页（`:530,619,727,645`）；`mapToPlc`（`:572-601`）**大小写不敏感**读取 `extendedProperties`（兼容旧 `station` 与驱动 `Station`）。
- 新增/编辑 `saveDevice`（`:799-890`）：`ensureSerialHost()` 串口无 IP 时用 `127.0.0.1` 占位（后端 host 必填）；必填校验；构建 `extendedProperties`——Inovance 写 `Series/DataFormat/AddressStartWithZero/IsStringReverse`，串口写 `PortName/BaudRate/DataBits/StopBits/Parity`，OmronHostLink 写 `Mode`（`:825-852`）；创建写死 `type:"PLC"`（`:874`）；按 `upstreamSynced` 提示同步结果。
- 连接测试 `testConnection`（`:920-949`）。
- Inovance `Series` 选择与地址示例表（`:194-226,452-516`）；串口协议内联配置（`:166-193`，默认 9600/8/None/One `:462`）。

### 3.2 AddressBrowserView.vue（地址浏览 / 探测 / 读写 / 加入采集）
- 地址树懒加载 `loadAddressSpace`（`:667-703`）→ `browseAddressSpace(deviceId, undefined, protocol)`；`loadAddressChildren`（`:706-736`）；`ADDRESS_SPACE_BROWSING_NOT_SUPPORTED`→警告（`:695`）；版本守卫防竞态（`addressSpaceVersion` `:492`）。
- 搜索 `searchAddress`（`:855-879`）：**纯前端**对已加载节点扁平过滤。
- 候选地址探测 `startProbe`（`:574-621`）：由 `getProbeProfile`（`utils/plcProbe.ts:36-67`，按 Modbus/FINS/HostLink/S7/Mewtocol/Inovance Series 生成地址族）→ `createProbeTags`（`:69`，0–65535、≤64 点）→ **顺序**逐地址 `readTags(..., signal)`，间隔 100ms（`:614`），仅 `quality==='Good'` 记为可读；`AbortController` 取消（`:438-443`）。
- 单点读 `readAddressValue`（`:764`）；精确读 `readExactAddress`（`:822`，要求 device+address+dataType，`quality!=='Good'||errorMessage` 记错误）。
- 单点写 `writeAddressValue`（`:792`）。
- 加入采集 A：`saveProbeCollection`（`:623-652`）按 intervalMs 分组为 `CollectionGroupConfig`（`groupName=采样_{n}ms`）→ `createProfile(deviceId,...)`（`POST /api/collection-config/{id}`）→ 跳转 `/collection/manage`。
- 加入采集 B：`addSelectedToCollection`（`:914-942`）树勾选加入，类型不在 `directReadDataTypes` 的节点告警"复杂类型请通过采集配置导入"。

### 3.3 ReadWriteView.vue（读写）
- 连接=真实连接测试（`:332-353`）。
- `toApiDataType`（`:275-291`）、`coerceValue`（`:294-299`）；注意注释：Inovance `D/R/T/C` 用 `int16/uint16` 避免 Int32 双寄存器读（`:279-281`）。
- 单点读/写（`:378/420`）、批量读/写（`:467/500`，按地址回填、报告 `successCount/totalCount`），增删行（`:457/462`）。

### 3.4 StatusMonitorView.vue（状态监控）
- `startMonitoring`（`:242-251`）：立即 `refreshConnectionStatus()` 后 `setInterval(..., 10000)`（10s）。
- `refreshConnectionStatus`（`:219-240`）：`Promise.all` 对每台 `testConnection`，成功→"在线"、失败/异常→"告警"，记录 `latency`、`lastUpdate`，保留最近 5 条日志。
- `stopMonitoring`/`onUnmounted` 清定时器（`:253-262,270-272`）。

### 3.5 CollectionImportView.vue（共享，PLC 默认）
- `importDeviceType = route.meta.deviceType==='CNC' ? 'CNC':'PLC'`（`:255`）；TIA 选项仅 PLC 显示（`:34`）。
- 标准文件：原文包成 `File` → `importTags`（`POST /api/batch-import/tags/{id}`）。
- TIA：`parseTiaSymbolTableCsv`（`utils/tiaSymbolTable.ts:172-255`）解析→`toCollectionImportCsvFile` 生成标准 6 列 CSV→导入；支持 UTF-16/UTF-8/GB18030（`:21-38`）。
- 手工：`importManualConfig` 建单组单点 → `createProfile`。
- 预览：TIA 取前 20 行；标准**按行/逗号朴素切分（不 JSON 解析）**（`:360-387`）。
- 校验：频率/类型/必填/设备存在性（`:438-496`）。

### 3.6 校验 / 质量 / 错误
- **质量严格**：PLC 各读路径只有精确 `"Good"` 视为有效，其余（含 `"Uncertain"`）一律按 bad（`AddressBrowserView.vue:780,842`；`ReadWriteView.vue:402`；probe `:600`）；类型只建模 `"good"|"bad"`（`:380`）。
- 错误提取：各视图 `getErr`（`DeviceConfigView.vue:541`、`AddressBrowserView.vue:446`、`ReadWriteView.vue:261`）；`CollectionImportView` 的 `getImportError` 兼容纯字符串 body。
- **无专门 502/404 文案**：PLC 模块依赖通用 `getErr`；显式 502/404/500 提示仅在 CNC `industrial/DeviceView.vue:1269-1278`。

### 3.7 定时器
- StatusMonitor 10s 轮询；AddressBrowser 探测为可取消的顺序循环（无重复 interval）；其余视图无定时器。

---

## 4. CNC vs PLC 差异对照

| 维度 | CNC (`/industrial`) | PLC (`/plc`) |
| --- | --- | --- |
| 主页面 | `DeviceView`（设备+点位+采集+传输一体） | 拆分为 Device/Address/RW/Status 四页 |
| 设备选择 | 表单可选 `deviceType`(CNC/PLC/Robot) | 固定 `type:"PLC"` |
| 设备列表过滤 | `deviceType==='CNC'`（`:1354`） | `list("PLC")` |
| 协议来源 | 表单内置品牌/协议/端口映射 | `GET /api/plc/capabilities` 能力矩阵 |
| Inovance/串口 | 串口仅用于**程序传输**通道 | Inline 串口配置 + Inovance `Series` |
| 程序传输 | 有（CNC-only 配置节、FTP/SMB/NFS/FOCAS/GSK/Serial） | 无（不使用 program-transfer / verification 客户端） |
| 采集方式 | 页面内**客户端定时器** + 写 Influx | 探测→加入采集配置，跳 `/collection/manage` |
| 地址读写 | 点位弹窗内单/批量 | 独立 Address/RW 页 + 候选探测 |
| 质量处理 | 不分支（行状态看 `errorMessage`） | 仅 `"Good"` 有效，其余 bad |
| 错误文案 | 显式 502/404/500 指引 | 通用 `getErr` |
| 实时推送 | 不用 SignalR | 不用 SignalR |
| 状态监控 | 无独立页 | StatusMonitor 10s 轮询 test-connection |
| 导入 | 共享 `CollectionImportView`（隐藏 TIA） | 共享（含 TIA 符号表导入） |

---

## 5. 关键结论与注意点

1. **两模块共用一个后端引擎**，服务端无 CNC/PLC 分支；"检测"能力=连接测试 / 读写 / 地址浏览 / 采集 / （CNC）程序传输，均由网关代理到 Host 驱动执行。
2. **CNC 页面是"客户端采集"**：定时 `readTags` + `telemetryInfluxApi.writeBatch`，不启动 Host 采集任务、不用 SignalR；服务端采集/实时推送属于 `/collection/manage`（`CollectionManageView`）。
3. **质量语义不统一**：CNC 不按 `quality` 分支（只用 `errorMessage` 判成功/失败）；PLC 严格要求 `Good`，`Uncertain` 视为坏值。
4. **连接测试有回退**：`mode:'tcp'` 表示仅端口可达、协议未验证（CNC 会明确警告）。
5. **传输续传未接入 UI**：`program-transfer` 的 `resume/capabilities/status` 存在但 `DeviceView` 未调用。
6. **能力矩阵/探测是 PLC 独有**：`/api/plc/capabilities`（Host `PlcCapabilitiesController`）与 `utils/plcProbe.ts` 的候选地址探测。
7. 证据边界：本文为源码还原，未连真机；质量/类型/回退等行为以对应驱动实现为准（见 `PROTOCOL_ENCODING_20261009.md` 与 `PROTOCOL_REVIEW_20260926.md`）。
