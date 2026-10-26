# IottDevMock — 虚拟数据项目

在没有真实机床/PLC 的情况下，为 CNC/PLC 监控、采集与评价提供一个**独立**的虚拟数据项目。不加入 `IndustrialIoT.sln`，可单独构建运行。

## 子命令

```powershell
# 启动 Mock 上游 Host（实现 IndustrialIoT.Host 被用到的 HTTP 契约）
dotnet run --project tools/IottDevMock -- serve --port 5199 --scenario Normal --seed 42 --devices 30

# 生成网关 App_Data/devices.json（会备份原文件为 devices.json.bak）
dotnet run --project tools/IottDevMock -- seed --root .

# 仅预览将写入的文件
dotnet run --project tools/IottDevMock -- seed --root . --dry-run

# P4：额外生成 verify-tasks.json / 指标补丁(automation+files) / 测试附件 / cs-connectivity.json
dotnet run --project tools/IottDevMock -- seed --root . --verify
dotnet run --project tools/IottDevMock -- seed --root . --verify --large   # 额外生成 200MiB 附件(file-size)

# 删除生成的种子
dotnet run --project tools/IottDevMock -- clean --root .

# 协议端点模拟（方案 C）：Modbus TCP + FINS/TCP 从站，供真实 Host 驱动直连
dotnet run --project tools/IottDevMock -- simulate --modbus-port 502 --fins-port 9600 --seed 42
```

参数：`--port`(默认 5199)、`--scenario`、`--seed`、`--devices`(默认 30)、`--root`、`--dry-run`。
场景：`Normal | HighLatency | IntermittentFault | DataDrift | PartialBad`。

## 已实现端点

### 阶段 1：设备 / 地址 / 读 / 能力
| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/health` | 健康检查 |
| GET | `/api/Devices?type=` | 设备列表（按类型过滤） |
| GET | `/api/Devices/{id}` | 设备详情 |
| POST/PUT/DELETE | `/api/Devices[/{id}]` | 接受网关设备同步 |
| POST | `/api/Devices/{id}/test-connection` | 连接测试（延迟/故障注入） |
| GET | `/api/addressspace/{id}?parentPath=` | 地址空间浏览（懒加载层级） |
| GET | `/api/addressspace/{id}/export` | 地址导出（CSV） |
| POST | `/api/data/{id}/read` | 读取（含 verification 证据回显） |
| POST | `/api/data/{id}/verification-directory` | 目录读取（验证用） |
| POST | `/api/connection-verification/start` · GET `/{testId}` | 批量并行连接验证 |
| GET | `/api/plc/capabilities` | PLC 能力矩阵 |

### 阶段 2：写 / 程序传输 / 采集 / 采集配置 / 批量导入 / NC-Link
| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/api/data/{id}/write` | 写入 |
| GET | `/api/program-transfer/{id}/files?path=&recursive=` | 程序文件列表 |
| GET | `/api/program-transfer/{id}/browse?path=` | 目录浏览 |
| GET | `/api/program-transfer/{id}/capabilities` | 传输能力（含续传） |
| GET | `/api/program-transfer/{id}/history` | 传输历史 |
| GET | `/api/program-transfer/{transferId}/status` | 单条传输状态 |
| POST | `/api/program-transfer/{id}/upload` (multipart) | 上传 |
| POST | `/api/program-transfer/{id}/download` | 下载（返回文件流） |
| POST | `/api/program-transfer/{id}/resume` | 断点续传 |
| POST | `/api/program-transfer/{id}/download-batch` | 批量下载（生成 ZIP 制品） |
| POST | `/api/program-transfer/{id}/upload-batch` (multipart) | 批量上传 |
| GET | `/api/program-transfer/tasks/{taskId}` | 批量任务状态（立即 Completed） |
| GET | `/api/program-transfer/tasks/{taskId}/artifact` | 批量下载 ZIP |
| POST | `/api/collection/start` · `/stop/{taskId}` · GET `/status` | 采集任务 |
| GET/POST | `/api/collection-config/{deviceId}` | 采集配置列表/创建 |
| GET/PUT/DELETE | `/api/collection-config/profile/{profileId}` | 采集配置详情/更新/删除 |
| POST | `/api/batch-import/tags/{deviceId}` (multipart) | 批量导入（CSV/JSON 行数统计） |
| GET | `/api/nclink/{id}/probe` · `/dataitems` · `/sample-channels` | NC-Link 诊断 |

### 阶段 3：验证证据链（供网关 `/verify` 引擎）
| 方法 | 路径 | 说明 |
| --- | --- | --- |
| POST | `/api/data/{id}/read`（带 `verification`） | 应用读证据：`code=completed`、`simulated=false`、回显指纹/revision、`operation=application-read` |
| POST | `/api/data/{id}/verification-directory` | 目录读证据：协议取传输协议（FTP/SMB/…）、`operation=directory-read` |
| GET | `/api/program-transfer/{id}/verification/snapshot` | 验证安全配置快照（deviceId/token(64hex)/supported/protocol/primary+transfer） |
| POST | `/api/program-transfer/{id}/verification/round-trip` (multipart) | FTP 独占创建+回读：返回大小/SHA256/上传计时，`success+integrityVerified` |

### 方案 C：协议端点模拟（真驱动 E2E）
| 命令 | 端点 | 说明 |
| --- | --- | --- |
| `simulate --modbus-port 502` | Modbus TCP 从站 | 功能码 1/2/3/4/5/6/15/16；数据确定性初始化，写入更新内存镜像 |
| `simulate --fins-port 9600` | FINS/TCP 从站 | FINS/TCP 帧（命令 0/2）+ 内存区读写（0101/0102），DM/CIO/WR/HR/AR 字访问 |

把设备的 `host` 指向 `127.0.0.1`、`port` 指向从站端口，用**真实** `IndustrialIoT.Host` 的 `ModbusTCP` / `ModbusRTU(overTcp)` / `FINS` 驱动连接即可得到真实通讯（非伪造证据）。

非可浏览协议（`HaasMdc`/`MTConnect` 等）在 `addressspace` 返回 `422 ADDRESS_SPACE_BROWSING_NOT_SUPPORTED`，用于验证前端降级提示。

## 接入方式

1. 启动 Mock：`serve --port 5199`。
2. 把网关 `MachineConnectionApi/.../appsettings.Local.json` 的
   `IndustrialIoT:BaseUrl` 改为 `http://localhost:5199` 并重启网关（无需真实 Host/设备）。
3. 或运行 `seed` 写入设备台账后，直接以真实 `IndustrialIoT.Host + Simulator` 跑采集。

CNC/PLC 设备列表来自网关本地 `devices.json`；地址浏览/读写/连接测试/批量验证/程序传输/采集配置代理到 Mock Host。

## 注意

- `verification` 证据为**伪造**（回显网关下发的指纹/revision、`simulated=false`），**仅用于调试**，严禁用于验收。
- 数据确定性由 `--seed` 保证；故障/坏质量由 `--scenario` 注入。
- 阶段 3 的 Host 侧证据链已就绪；`/verify` **端到端跑分**还需阶段 4：网关侧 `verify-tasks.json`/`evaluation-indicators.json` 及 0.1/1/10/20/200MiB 测试附件（`App_Data/evaluation-attachments`）。
- 方案 C 目前提供 **Modbus TCP** 与 **FINS/TCP** 从站；OPC UA（需协议栈）/ S7 / MQTT 等端点待后续按需增加。
