# MySQL 本地部署与迁移

两套后端均使用 MySQL 8.4 / Pomelo EF Core 8，InfluxDB 配置不变。

## 启动

在仓库根目录执行：

```powershell
.\deploy\mysql\Start-Backends.ps1
# 已构建时可加 -NoBuild
```

脚本使用本机 MySQL 8.4 二进制创建独立实例，监听 `127.0.0.1:3307`；
不使用已有的 3306 实例。Host 地址为 `http://localhost:5173`，网关为 `http://localhost:5087`。
实例以后台进程运行；电脑重启后再次执行上述命令。此脚本未注册 Windows 服务或开机任务。
已运行的后端不会被脚本自动重启；更新代码后需先停止对应项目进程。

仅启动数据库或指定二进制路径：

```powershell
.\deploy\mysql\Start-MySql.ps1 -MySqlBin 'C:\Program Files\MySQL\MySQL Server 8.4\bin'
```

数据库文件、随机凭据、日志和备份保存在被 Git 忽略的 `.runtime/`。
脚本为两套项目生成被忽略的 `appsettings.Local.json`；环境变量和命令行优先于此文件。
应用账号 `iott` 仅能访问 `IndustrialIoT` 和 `MachineCollection`。
部署到其他机器时，使用 `ConnectionStrings__Default`、`ConnectionStrings__MachineCollection`
覆盖连接字符串，使用 `Database__ServerVersion` 指定 MySQL 版本。

## 随包业务数据（2026-09-29）

`SeedData/initial-data.sql` 包含两套 MySQL 库的表结构和 352 条业务记录：
Devices 81、CollectionProfiles 5、CollectionGroups 5、TagConfigs 8、
NCPrograms 221、RealtimeDataRecords 0、datacollection 32。

`Start-Backends.ps1` 在启动后端前自动导入快照，仅在两套库都没有表时执行；
任一库已有表即跳过，保留客户已有数据。导入中断会留下 `.runtime/initial-data.pending`，
再次启动会明确报错，需先核查并处理失败的导入。

设备以离线状态分发，设备连接配置中的密码、Token 等凭据已清空，客户需按现场配置填写。
快照保留设备 ID；网关首次启动时会从 Host 恢复设备列表。
NCPrograms 保留全部程序传输记录；现存的 4 个本地程序文件随网关快照分发，导入时自动重映射路径。
其余 128 个本地文件引用的源文件已缺失，记录保留，恢复后的本地路径置空；原路径可查网关快照清单。
网关 App_Data 由 `../gateway-data/Restore-GatewayData.ps1` 在首次启动时恢复。
不分发实时采样值或 InfluxDB 历史采样数据；历史库部署脚本仍保留，可用于新的采集。
MySQL 8.4 运行程序仍需预先安装。

## SQL Server 数据迁移

```powershell
.\deploy\mysql\Migrate-FromSqlServer.ps1 -Mode Inspect
# 停止两套后端后再迁移；目标表必须为空
.\deploy\mysql\Migrate-FromSqlServer.ps1 -Mode Apply
.\deploy\mysql\Migrate-FromSqlServer.ps1 -Mode Verify
```

默认源为本机 MSSQLLocalDB 的同名数据库；可通过 `SQLSERVER_HOST_CONNECTION` 和
`SQLSERVER_GATEWAY_CONNECTION` 覆盖。Apply 先生成 COPY_ONLY/CHECKSUM 备份并执行
RESTORE VERIFYONLY，再按外键顺序在一个 MySQL 跨库事务中复制，逐表核对记录数和 SHA-256。
源数据库保留，目标非空时拒绝覆盖。校验报告位于 `.runtime/migration-*.json`。
远程 SQL Server 的备份路径需由服务器可访问；当前自动备份脚本面向本机实例。

DateTimeOffset 转成 UTC `datetime(6)`；原始偏移量不单独保存，100ns 低位截到微秒后校验。
采集点 `date` 保留本地日历日期。JSON 使用 `longtext`，字符集为 `utf8mb4`。
新库由 EF EnsureCreated 建表；未来模型升级需显式 schema 升级，不能靠 EnsureCreated 修改已有表。

## 本次切换记录（2026-09-28）

7 表共 346 行：Devices 81、CollectionProfiles 5、CollectionGroups 5、TagConfigs 8、
NCPrograms 215、RealtimeDataRecords 0、datacollection 32。迁移数量与规范化内容校验通过。
已验证真实接口新增/修改/删除中文设备与采集点，并验证 MySQL 与两套后端重启后数据保留。
旧 SQL Server 运行文件副本及数据库备份位于 `.runtime/previous-*`、`.runtime/sqlserver-backup-*`。
回退需停止新后端、使用旧运行文件与 SQL Server 配置；切换后新增的 MySQL 数据需另行回迁。
