# 网关配置与业务记录

`SeedData/App_Data` 保存 2026-09-29 导出的网关 JSON 业务数据：

- 81 台设备：本地 20 台与上游 81 台按 ID 合并，补入上游独有的 61 台。
- 2 类评价指标配置、1 条评价/知识库记录、7 条指标。
- 5 个验证任务及其结果、1 个报告模板、135 条操作日志、1 条用户/角色记录。
- 客户端/服务端通信配置和空的验证任务完成日志。

相同 ID 优先保留网关配置，并补齐上游独有设备。恢复设备设置 `restoredFromUpstream=true`，启动时只检查上游设备，不覆盖已恢复的 MySQL 配置。设备状态统一重置为 `Offline`。

密码、密码哈希、Token、凭据字典和日志中的凭据文本已清除；保留用户和角色记录。不存在密码哈希时，程序原有的管理员初始化逻辑会执行。客户需重新设置登录密码和设备密码。本包不携带服务连接配置、在线会话或机器证书。

定时任务保持关闭，两个任务的文件写入许可已重置，历史验证结果保留。通信服务监听和探测保持停止；本机 FTP 根目录 `F:\` 已清空，由客户重新选择。本包不包含实时或历史采样数据。

## 首次恢复

首次恢复前停止目标网关，将目标设置为实际启动的网关 DLL 同级 `App_Data`：

```powershell
.\deploy\gateway-data\Restore-GatewayData.ps1 -TargetAppData '<网关 DLL 所在目录>\App_Data'
```

脚本验证 SHA-256，在同级暂存目录准备文件，然后仅初始化不存在或完全为空的目标目录。目标包含任何文件或子目录时直接跳过，保留客户已有数据。

`SeedData/manifest.json` 记录所有文件的字节数和 SHA-256。`programs` 数组按 MySQL `NCPrograms.Id` 提供相对 `SeedData` 的文件路径，并以 `sourcePath` 保留原始路径。现有 4 个程序文件已随包保存（共 2,301,952 字节）；另有 128 个文件在原路径下已缺失，对应记录保留并标为 `available=false`。`RemotePath` 为设备端路径，本次不下载设备上的文件。

## 更新快照

保持来源网关/上游运行且无正在执行的验证任务，使用 `Start-MySql.ps1` 生成的 MySQL 客户端配置导出。输出必须为新目录或空目录：

```powershell
python .\deploy\gateway-data\Export-GatewayData.py --app-data '<实际运行的网关 DLL 所在目录>\App_Data' --client-config .\deploy\mysql\.runtime\client.cnf --output '<新的快照目录>'
```

导出器读取本机数据库连接凭据，但不会输出凭据。更新后检查清单、新附件及自由文本字段，再发布快照。检测到正在执行的验证任务，或程序文件达到 GitHub 的 100 MiB 限制时会停止导出。
