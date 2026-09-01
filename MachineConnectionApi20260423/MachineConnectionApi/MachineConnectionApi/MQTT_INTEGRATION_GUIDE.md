# 第三方 MQTT 对接说明（MachineConnectionApi）

本文档用于第三方系统接入本项目的 MQTT 遥测数据。

## 1. 先理解调用方式

本服务对 MQTT 的角色是**发布者（Publisher）**，第三方系统通常作为**订阅者（Subscriber）**接收数据。

第三方若要拿到最新采集值，推荐按以下流程：

1. 调用 HTTP 采集接口触发一次采集。
2. 订阅 MQTT 主题接收采集结果 JSON。

## 2. MQTT 连接参数

当前默认配置（来自 `appsettings.json`）：

- Broker Host: `127.0.0.1`
- Broker Port: `1883`
- TLS: `false`
- Username: 空
- Password: 空
- QoS: `1`（AtLeastOnce）
- Retain: `false`

> 如部署环境不同，请以运行环境中的 `Mqtt` 配置为准。

## 3. Topic 规则

- Topic 前缀：`machines/telemetry`
- 最终 Topic：`machines/telemetry/{deviceId}`

说明：

- `{deviceId}` 来自采集请求中的 `deviceId`。
- 若 `deviceId` 包含 `/ \ + # 空格 Tab`，会被替换为 `_`。

建议第三方先使用通配订阅：

```bash
mosquitto_sub -h 127.0.0.1 -p 1883 -t "machines/telemetry/#" -v
```

## 4. 如何触发发送（HTTP 触发）

本服务在执行采集接口后发布 MQTT 消息。

- 方法：`GET`
- 地址：`/api/datacollection/collect?deviceId=<设备ID>`

示例：

```bash
curl "http://<api-host>:<api-port>/api/datacollection/collect?deviceId=test3"
```

调用成功后，会向对应 Topic 发布一条 JSON 消息。

## 5. 消息体格式（JSON）

消息体结构：

```json
{
  "deviceId": "test3",
  "collectedAt": "2026-04-21T06:10:30.123+00:00",
  "points": [
    {
      "name": "Temperature",
      "path": "ns=2;s=Channel1.Device1.Temp",
      "dataType": "Double",
      "value": 26.4,
      "quality": "Good",
      "timestamp": "2026-04-21T06:10:30.000Z",
      "status": "成功",
      "errorMessage": null
    }
  ]
}
```

字段说明：

- `deviceId`: 设备唯一标识
- `collectedAt`: 本批次采集时间（服务端时间）
- `points`: 点位数组
  - `name`: 点位名称
  - `path`: 点位路径
  - `dataType`: 数据类型
  - `value`: 点位值（可能为数字、布尔、字符串或 null）
  - `quality`: 质量
  - `timestamp`: 点位原始时间戳（若上游提供）
  - `status`: 采集状态（如 `成功`、`失败`）
  - `errorMessage`: 失败原因（成功时通常为 null）

## 6. 第三方最小联调步骤

1. 启动 MQTT Broker（例如 Mosquitto）。
2. 启动 MachineConnectionApi。
3. 在第三方侧订阅 `machines/telemetry/#`。
4. 调用采集接口：`GET /api/datacollection/collect?deviceId=test3`。
5. 验证第三方收到 `machines/telemetry/test3` 主题消息。

## 7. 常见问题

- 收不到消息：
  - 检查 Broker 地址和端口是否一致（`127.0.0.1:1883`）。
  - 检查是否订阅了正确主题（建议先用 `machines/telemetry/#`）。
  - 检查 `Mqtt:Enabled` 是否为 `true`。
  - 检查是否真的触发了采集接口。
- 能订阅到手工测试消息，但收不到业务消息：
  - 优先检查采集接口是否成功返回，以及设备是否有已配置采集点。

---

如需对接认证、TLS（8883）、多租户 Topic 规划，建议在生产环境单独扩展 `Mqtt` 配置并更新本文档。
