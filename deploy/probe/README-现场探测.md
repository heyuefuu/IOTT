IOTT 现场探测工具包（probe）
=========================================================
用途：目标机（192.168.1.200）连通性探测 + 真机（机床）网段发现/协议识别。
只做只读探测（TCP 连接、FTP banner、S7 COTP、OPC UA HEL、Modbus FC03、MTConnect /probe、
MQTT CONNECT、FOCAS 实连），不向设备写入任何数据。

目录
----
  ProbeKit.ps1        公共库：快速 TCP 扫描、UDP 触发 ARP、ping、协议指纹、建议配置
  probe-target.ps1    本机 → 目标机：ICMP/ARP/TCP/SSH/SMB/WinRM/HTTP/MySQL/Influx 多通道探测
  scan-machines.ps1   在目标机（或任意机床网段内的 PC）执行：自动发现网段 + ARP + 端口 + 协议 + 报告
  scan-remote.ps1     本机通过 SSH 让目标机执行 scan-machines 并自动拉回报告
  scout-devices.ps1   指定 IP 的深度指纹（13 种协议，含 FINS/Haas/Mewtocol）
  reverse-agent.ps1   目标机反向连回本机（白名单只读诊断，无任意命令执行）
  listener.ps1        本机反向通道接收端
  db-query\           MySQL 查询小工具（源码 + MySqlConnector.dll）

---------------------------------------------------------
0. 当前环境事实（2026-10-17 实测）
---------------------------------------------------------
- 本机 192.168.1.50；目标机 192.168.1.200（DESKTOP-V54DFAR），两块网卡：
  "以太网 2" 192.168.1.200/24（Up），"以太网" 192.168.1.201/24（网线已拔）。
- 目标机对**本机**开放：22 SSH（OpenSSH for Windows，密钥可登）、3306 MySQL 8.0.35（MySQL80）、
  8181 InfluxDB 3（需 token，401）。135/139/445 等从本机侧不可达（Windows 防火墙）。
- 目标机 IOTT 状态：Host 5173 正常（/api/Devices 94 台）、Web 8082 正常、网关 5087 正常
  （需业务 API Key，未带 Key 返回 401）。这三个端口只监听/放行在本机，外部不可达。
- 当前**没有真机**接入：目标机邻居表只有本机 192.168.1.50。

---------------------------------------------------------
1. 快速开始（推荐 3 条命令）
---------------------------------------------------------
# 1) 目标机能不能连、开了什么、SSH 能不能登
powershell -NoProfile -ExecutionPolicy Bypass -File .\probe-target.ps1 -Target 192.168.1.200

# 2) 让目标机扫描它所在的机床网段，并把报告拉回本机 out\
powershell -NoProfile -ExecutionPolicy Bypass -File .\scan-remote.ps1 -Target 192.168.1.200

# 3) 接上真机后持续观察（每 30 秒一轮，打印新出现的主机）
#    方式 A：本机（每轮都会重新 SSH + 扫描）
#      .\scan-remote.ps1 -Target 192.168.1.200
#    方式 B（更快）：在目标机上常驻
#      powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\ktb\iott-probe\scan-machines.ps1 -WatchSec 30

---------------------------------------------------------
2. 本机 → 目标机完全不通时
---------------------------------------------------------
方式 A：反向通道（目标机能 ping 通本机，说明反向可用）
  目标机：
    powershell -NoProfile -ExecutionPolicy Bypass -File .\reverse-agent.ps1 -Server 192.168.1.50 -Port 4444
  本机：
    powershell -NoProfile -ExecutionPolicy Bypass -File .\listener.ps1 -Port 4444
  可用命令（白名单）：info subnets ipconfig route arp neighbor listening netstat firewall
                      services disk hosts time scan quit
方式 B：I 盘 / U 盘（无需目标机联网）
  把 probe 目录拷到目标机，运行 scan-machines.ps1，把 out\ 带回本机。
方式 C：在目标机放行（管理员 PowerShell）
  netsh advfirewall firewall add rule name="IOTT-PROBE" dir=in action=allow protocol=TCP localport=22,445,5173,5087,8082 profile=any
  netsh advfirewall firewall add rule name="IOTT-ICMP" dir=in action=allow protocol=icmpv4 profile=any

---------------------------------------------------------
3. 端口 / 协议对照（IOTT 支持）
---------------------------------------------------------
设备协议
  8193   FOCAS / FANUC CNC（FOCAS2，采集 + 程序传输）
  11520  GSK WebServer（广州数控 GSK988/980 等）
  5000   MTConnect Agent（HTTP /probe）
  19001  NC-Link API（华中数控）
  1883   NC-Link over MQTT（设备 Broker；需 DeviceGuid）
  502    ModbusTCP（也常见于 Estun/Huazhong 机器人）
  102    Siemens S7（需 Rack/Slot）
  4840   OPC UA（需 UseSecurity=false/端点 URL）
  9600   Omron FINS
  9999   Haas MDC
  5007   Mitsubishi MC / Panasonic Mewtocol（需厂家确认）
  60008  FANUC 机器人接口（非 CNC）
文件传输（挂在主设备上的 transfer）
  21     FTP（FANUC/GSK 程序上传下载 + 文件类验证）
  445    SMB（需共享/账号）
  2049   NFS
基础设施（不要建成设备）
  22 SSH / 80 443 HTTP(S) / 135 139 445 Windows / 3306 3307 MySQL / 3389 RDP /
  5173 IOTT Host / 5087 网关 / 5985 5986 WinRM / 8082 IOTT Web / 8090 模拟器 /
  8181 InfluxDB / 18083 EMQX Dashboard

---------------------------------------------------------
4. 输出与判读
---------------------------------------------------------
scan-machines.ps1 输出（目标机 out\ 与本机 out\remote-*）：
  scan-<时间>.csv / .json / .md，并覆盖 latest.*
字段：Host | MAC | Port | Protocol | Kind | Evidence | Hint | SuggestedConfig
- Kind=Device  → 可直接抄 SuggestedConfig 建设备
- Kind=Transfer→ 作为 transfer（FTP/SMB/NFS）挂到主设备
- “present at L2 but no scanned port open” = 主机存在但端口被过滤
  （可能只开 FOCAS 8193、或防火墙只放行特定端口）

---------------------------------------------------------
5. 真机接入流程（现场 10 分钟）
---------------------------------------------------------
1) 接上网线 → 本机跑 scan-remote.ps1（或目标机 -WatchSec 30），确认新主机 IP/MAC/端口
2) 按 SuggestedConfig 在 IOTT 建设备（protocol/host/port/extendedProperties）
3) 文件类验证（5.2.5/6/7）需要设备 FTP/SMB/NFS：把该主机加成 transfer={protocol=FTP,host=...,port=21}
4) FOCAS 深度确认（端口开放 ≠ FOCAS 可用）：
     I:\focas-probe\run.cmd <ip> 8193
   或在 scan-machines 上带 -FocasProbePath <FocasProbe.exe 路径>
   期望输出：cnc_allclibhndl3 -> rc=0 EW_OK / CONNECT OK / cnc_type='0' series='D4G3'
5) 采点位 → 建验证任务 → 看结果（读类点位需要采集 profile 或规则 readAddress）

---------------------------------------------------------
6. 已知坑（已内置规避）
---------------------------------------------------------
- SendARP 在网桥/NIC 团队网卡上不可靠 → 脚本改用“UDP poke 触发系统 ARP + 读邻居表”
  （实测 .201/.1 显示 Incomplete，.200 显示 Reachable + MAC）
- 只回 ARP、不回 ICMP 的机器：先 UDP poke 再读邻居表即可判活
- ICMP 被防火墙拦：以 TCP/ARP 为准（probe-target 会分别报告）
- 目标机 Windows 防火墙可能只放行 3306/8181：5173/5087/8082 需从目标机本机访问或加规则
- 目标机有两块网卡/两个网段时，scan-machines 会自动扫描所有非默认路由网卡；
  默认路由网卡与 WLAN 默认跳过（WLAN 需要 -IncludeWlan）

---------------------------------------------------------
7. 2026-10-17 本次实测记录
---------------------------------------------------------
- 目标机 .200 对：22/3306/8181 开放；SSH 密钥（id_ed25519_obugs）可直接登录 DESKTOP-V54DFAR
- 3306 是 MySQL80（远程 iott 账号无权限）；IOTT 自己的库在 127.0.0.1:3307（仅本机）
- InfluxDB 3 在 8181，/health 401（需 token）
- IOTT Host 5173 /api/Devices = 200（94 台）；Web 8082 = 200
- 网关 5087 曾 500：C:\iott-bundle\gateway\App_Data\verify-task-completions.json 为 2 个 NUL 字节，
  启动反序列化崩溃。已备份为 .bak-20261017234327 并改写为 []，重启 IOTT-API 后恢复正常（401 鉴权）。
- 目标机邻居表仅 192.168.1.50（本机），无真机
