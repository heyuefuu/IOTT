# 七项验证指标的"代码写死、配置改不了"逻辑问题清单

- 日期：2026-10-15
- 代码基线：`feature/compliance-fixes-20261010`（rebase 后，含 `VerifyAutomationService.*` 拆分：Communication / Files / FilesCoverage / HttpMeasurementAdapter / MeasurementAdapter）
- 口径：只列**写死在代码里、用户改配置也改不动**的验证逻辑问题；阈值/时长/档位这类配置项不作为问题（可按需调）。
- 方法：只读源码逐条核对，标注 `文件:行`。

## 一、死参数/未实现（4 条）

1. **`CommunicationRounds` 死参数** — 仅在 `VerifyTaskRunner.cs:456` 做区间校验（1–100），**任何测量算法都不读取**；稳定性用的是 `test.sampleIntervalSeconds` + `test.durationMinutes`。前端 .vue 也未渲染该字段（只在 `code20260423/code/src/api/machineConnectionVerify.ts:23` 有类型声明）。
2. **`MaxParallelTargets` 死参数** — 仅在 `VerifyTaskRunner.cs:457` 校验；并发读全部 `Task.WhenAll` 一起发出，**没有任何并行上限**（`VerifyAutomationService.Communication.cs:66,144`）。
3. **`RequiredMinConcurrentSuccess` 死参数** — 仅在 `VerifyTaskRunner.cs:457` 校验；从不参与判定。
4. **`max-connections` 只实现 `devices` 模式** — `Communication.cs:109-113`：`ConcurrencyMode != "devices"` 直接"未评分"。规则里可选 `connections`/`sessions`（`EvaluationAutomationRules.cs:82`），但实现不存在。`limit = min(设备数, MaxConnections)`（`Communication.cs:131-132`）是该模式的必然结果：量的是**设备数**，不是连接数。
5. **验证目录接口已成死路径** — `IVerifyMeasurementAdapter.ReadTransferDirectoryAsync`（`VerifyAutomationService.MeasurementAdapter.cs:16`）**已无任何指标调用**（transfer-protocol 改为走上传接口），Host 的 `POST api/data/{deviceId}/verification-directory`（`DataReadWriteController.Verification.cs:38`）随之成为死接口。

## 一之补、七项指标的调用链（本次核实）

| 指标 | 测量方式 | 调用的 Host 接口 | 底层能力 |
|---|---|---|---|
| industrial-protocol | 逐设备应用读 1 次 | `POST api/data/{id}/read`（地址为空先 `GET api/collection-config/{id}`） | `IProtocolDriver.ReadTagsAsync`（真实驱动） |
| communication-stability | 同一读接口按 `sampleIntervalSeconds` **重复采样**、持续 `durationMinutes` | `POST api/data/{id}/read` | 同上 |
| max-connections | 同一读接口 `Task.WhenAll` **并发**发起，档位 1..min(设备数,MaxConnections) | `POST api/data/{id}/read`（每档对**不同设备**） | 同上 |
| transfer-protocol | 0.1MiB 上传 + 回读 | `GET api/program-transfer/{id}/verification/snapshot` + `POST .../verification/round-trip` | 驱动 `IVerificationFileTransfer`/`INCProgramTransfer`（原子独占+回读 SHA256） |
| file-integrity | 0.1/1/10/20MiB 往返 | 同上 | 同上 |
| transfer-speed | 10MiB 往返，用返回的 `UploadSeconds` 计 MiB/s | 同上 | 同上 |
| file-size | 1/10/200MiB 逐级往返，取最大成功 | 同上 | 同上 |

要点（对应"稳定性=多次连通 / 并发=多线程连通 / 文件=调上传下载接口"的说法）：
- **不是"连通"**：适配器明确拒绝以 TCP 握手/连通作证据（`HttpMeasurementAdapter.cs:37-38,110-111`），要求 Host 返回**结构化应用读证据**（`configurationFingerprint/revision` + `quality ∈ Good/Bad/Uncertain`）。
- **不是"多线程连通"**：是**并发 HTTP 应用读**（`Task.WhenAll`），且仅 `devices` 模式（`Communication.cs:109-113` 明确拒绝把复用连接当连接数）。
- **不是通用上传/下载**：走的是**验证专用** `/verification/snapshot` + `/verification/round-trip`；通用的是另一个 `ProgramTransferController`。
- **附件校验在网关本地**（读 `App_Data/evaluation-attachments/<id>.bin|.json`，`HttpMeasurementAdapter.cs:25-40`），不调 Host。

## 二、真实逻辑缺陷（13 条）

5. **`industrial-protocol` 只吃任务那 1 台设备** — `VerifyAutomationService.cs:104,116`（强制恰好 1 台）→ `:158` 只把 `selected` 传给 `MeasureProtocolsAsync`（对比 `:159/:161` 的 transfer/max-connections 传 `selected, all`）→ `Communication.cs:12-18` 候选 ≤ 1 → 实测上限被写死为 `1 / |标准集|`（部署项标准集 6 种 → 上限 16.7%）。
6. **稳定性：一次"无证据"采样即整项作废** — `Communication.cs:75-79`：`observations.Any(x => !x.Supported || !x.HasEvidence)` → `MarkUnrated` + `return`。**读超时/HTTP 异常**在 `HttpMeasurementAdapter.cs:109-111` 被转成 `Unknown`（不抛异常，只有用户"终止"才抛）→ 该项直接丢分；而 Host 返回 502 且带 `operation-failed` 时是"有证据失败"（`:87-91`）→ 计入失败继续。**同一物理事件两种结局**；且可在任意一次采样触发，丢掉已累计的整段观测。
7. **`max-connections`：`FailureLimit` 兼作档内重试 → 高估** — `Communication.cs:140-151`：每个档位最多重试 `FailureLimit` 次，**任一次全成功即记 `fullSuccess=true, best=level`**，与"完整成功"语义矛盾；而 `FailureLimit` 名义是"连续失败 N 次终止"（`:133,136`）。
8. **`max-connections`："仅下限即未评分"** — `Communication.cs:156-159`：档位没因失败中止且 `设备数 < MaxConnections` → 判"仅下限、未评分"。若选了 3 台而 `MaxConnections=4`，即使全部成功也**永远拿不到分数**。
9. **`transfer-protocol` 固定要求 0.1 MiB 附件** — `FilesCoverage.cs:31-34`：无论标准集如何，都必须有"恰好一个 0.1MiB"的已上传附件，否则整项未评分。
10. **`transfer-protocol`：标准集必须被显式传输目标全覆盖，否则整项未评分** — `FilesCoverage.cs:29-30`。部署项的 `protocols` 有 9 种（FTP/OPC UA/Modbus/Profibus/MTConnect/Profinet/MQTT/NC-Link/EtherNet/IP）→ 需 9 个目标齐备。
11. **`transfer-protocol`：任一候选无可靠证据 → 整项未评分** — `FilesCoverage.cs:61-64,73-74`（`hasUnknown`）。与 #10 叠加后，9 标准集下**几乎不可能产出分数**。
12. **`file-*`：每档附件"有且仅有一个"否则整项未评分** — `Files.cs:29-35`（全有或全无）。
13. **`file-size` 与 `file-integrity` 的失败策略不一致** — `Files.cs:79`：`file-size` 首个失败即 `break`（不再尝试更大文件），而 `file-integrity` 失败继续统计通过率。
14. **清理结果不进入评分** — Host 返回 `CleanupSucceeded`（`Host/src/IndustrialIoT.Host/Services/VerificationTransferService.cs:120-128,212`），但网关的 `TransferResult` **无该字段**（`VerifyAutomationService.HttpMeasurementAdapter.Transfers.cs:175-193`），且 Host 侧 `Success = IntegrityVerified = matches`（`:84`）→ **清理失败仍算通过**，残留的独占测试目录不影响评分（仅 Detail 提示）。
15. **`MarkUnrated` 不清 `Measurement/Value/MeasurementSource`** — `VerifyAutomationService.cs:193-197`。`max-connections` 先 `SetMeasurement` 再 `MarkUnrated`（`:154`→`:156-159`）→ 界面/导出显示 "4 count" 却"未评分"，且不计入"已评分"计数。
16. **`interruptions` 语义非直觉** — `Communication.cs:91`：只在"上一样本全成功"时累加，连续失败只算 1 次、开头即失败算 0 次（不参与评分）。
17. **代码默认值自相矛盾** — `EvaluationAutomationRules.DefaultFor("max-connections")` = 档位 (100→100),(0→0) 且 `PassRule.Threshold=100 count`（`EvaluationAutomationRules.cs:17-18`），而同指标描述"最大值为 4"、部署/种子配置为 (4/3/2) 且 `passRule:null`；`DefaultCriterionFor("industrial-protocol")` 又给 **100%**（`:28`）。默认值与指标定义/部署配置三方不一致。

## 三、设计性（非缺陷，2 条）

18. **稳定性只测单台设备** — `VerifyAutomationService.cs:160` 传 `selected`；与"单台设备连续通讯 30min"的指标定义一致。
19. **证据与 Host 实现强耦合** — `HttpMeasurementAdapter.cs:73,76-84,95-99`：`ReadTypes` 只认 Host 的规范类型名、`configurationFingerprint/revision` 必须与 Host 一致；任一不匹配即整项未评分（fail-closed 是有意的，但耦合点是硬编码）。

## 四、已撤回（不作为问题）

- ~~`max-connections` 全成功时报 `best=maximum`~~ —— 阶梯逻辑本身正确；只证明 `≥上限` 而报成"恰好=上限"属**保守**且 Detail 已披露。
- ~~`Score` 低于最低档下限返回 null~~ —— 代码自带档位**都含 `min 0`**，有效测量不会得 null；只有用户把档位改成缺 0 下限才出现 → 属配置问题。
- 早期"`transfer-protocol` 用列目录冒充传输能力"在新基线上**已不存在**：现为"真实上传 + 回读 SHA256"（`FilesCoverage.cs:56-68`）。

## 五、相关：设备"文件传输协议不支持"的两层原因（本次核查）

- **Host 验证口径写死**：`VerificationTransferService.Snapshot.cs:29` `SupportsSafeProtocol` 只认 **FTP / SMB / NFS**；其余协议（GSK / NC-Link / NC-Link API / FOCAS / JingDiao / Serial 等，业务上 Host 有传输驱动）在做 5.2.4/5.2.5/5.2.6/5.2.7 验证时一律"不支持/无证据"。
- **本机模拟器只实现了 FTP**：`tools/DeviceSimGateway/Services/FtpServerService.cs`，**没有 SMB/NFS 服务端**。种子设备 `deploy/gateway-data/SeedData/App_Data/devices.json`（81 台）中 `transfer` 分布：`SMB=18`、`FTP=4`、无 transfer=59 → 那 18 台 SMB 设备在本机**无法做真实文件传输验证**（是"本机未实现模拟"，不是设备不支持，Host 支持 SMB 验证）。
- 另：本机 FTP 模拟实测中，0.1MiB 可完成往返，但 1/10/20MiB 出现"回读 0 字节 / FtpException"（大文件流式往返有缺陷，需单独修）。

## 六、修复优先级建议

1. #1/#2/#3/#4（死参数与缺实现）：要么实现，要么从 `VerifyRunOptions`/前端类型/校验中删除。
2. #6（超时整项作废）：把"传输层无证据"按"失败"计入，而不是作废整项。
3. #9/#10/#11 + #12（transfer-protocol 近乎不可评分）：改为允许部分覆盖分、附件与标准集解耦。
4. #5（industrial-protocol 单机上限）：按机群聚合或允许显式选设备集合。
5. #7/#8/#13/#14/#15/#16/#17：逐项做语义/聚合修正。
