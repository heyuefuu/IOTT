# 历史采集数据库

设备页顶部的“历史库设置”支持连接本机或其他服务器上的 InfluxDB 3。
本地指运行 MachineConnectionApi 的电脑。配置入口需要 `config_manage` 权限；管理员可直接使用。

## 本机启动

启动 Docker Desktop，然后在仓库根目录执行：

```powershell
.\deploy\influxdb\Start-InfluxDb.ps1
```

脚本使用固定镜像版本 InfluxDB 3 Core 3.11.5，完成以下操作：

- 从后端 `appsettings.json` 的 `InfluxDB` 配置读取首次初始化用的 Token、数据库和表名。
- 创建容器及数据卷，初始化目录权限，等待数据库就绪。
- 初始化数据库和采集表，再执行查询验证；重复执行保留现有数据库和 Token。

默认设置：

| 配置 | 值 |
| --- | --- |
| 地址 | `http://127.0.0.1:8181` |
| 数据库名称 | `machine_collection` |
| 组织 | `default` |
| 数据表名称 | `datapoint` |
| 容器 | `iott-influxdb3` |
| 数据卷 | `iott-influxdb3-data` |

Token 初始化文件位于本目录 `.runtime/admin-token.json`，已加入 Git 忽略规则。
如果本地 Token 与后端配置不同，应将已有本地 Token 填入“历史库设置”。
备份时保留数据卷及 Token 文件。容器配置为随 Docker 恢复运行；Docker Desktop 仍需处于运行状态。

## 连接其他服务器

打开“历史库设置”，选择“其他服务器”，填写完整 HTTP(S) 地址、数据库名称、组织、表名和 Token，
点击“测试连接”后保存。测试会校验数据库和表的查询能力。
目标服务需支持 InfluxDB 3 的 SQL 查询接口和兼容的 v2 写入接口。
只读 Token 可以通过查询测试，采集保存还需要写入权限。

保存后的地址和凭据立即用于后续读写；已在途的写入仍使用启动该请求时的设置完成。
切换目标库后，新数据写入所选库，历史记录也从所选库查询，已有数据不会自动迁移。
Token 不会由接口回传；编辑时留空表示保留当前 Token。

也可在另一台 Windows 服务器上运行同一部署脚本，显式设置监听地址和端口：

```powershell
.\deploy\influxdb\Start-InfluxDb.ps1 -BindAddress '0.0.0.0' -Port 8181
```

使用自定义初始配置文件时传入 `-GatewaySettings <appsettings.json路径>`。
脚本的端口参数只设置数据库监听端口，应用中的连接地址需在“历史库设置”中同步填写。

## 配置位置与检查

页面保存的配置位于后端运行目录下 `App_Data/influx-settings.json`，优先于 `appsettings.json`。
重新部署后端时保留该目录。首次升级设置功能需重启后端一次，以后保存设置无需重启。

```powershell
docker compose -f .\deploy\influxdb\compose.yaml ps
docker compose -f .\deploy\influxdb\compose.yaml logs --tail 50 influxdb
```

在设备卡片中点击“开始采集”查看最新值，点击“历史采集记录”查看已保存数据。
写库失败时卡片显示“存储异常”；修复连接并成功写入后自动清除。
当前采集由设备页面驱动，关闭实时弹窗会继续采集，离开或刷新页面会停止。
数据库安装前未成功保存的采样不会自动补回。
