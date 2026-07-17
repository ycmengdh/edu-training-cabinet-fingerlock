# edu-training-cabinet-fingerlock

学校实训柜指纹智能锁系统。

## 目录结构

- `FingerprintLockManager/`：Windows 上位机（WPF）
- `esp32_firmware/root_node/`：ESP32 根节点固件，负责 Mesh 汇聚、上行通信、SD 卡和显示屏
- `esp32_firmware/cabinet_node/`：ESP32 柜子节点固件，负责指纹识别、按键和锁控制
- `esp32_firmware/common/`：两个固件工程共用的协议、Mesh 通信和调试代码
- `doc/`：系统功能与方案文档
- `esp32_firmware/doc/`：ESP32、根节点及硬件资料

ESP 固件统一使用 PlatformIO，两个节点是相互独立的工程：

```text
cd esp32_firmware/root_node
pio run

cd ../cabinet_node
pio run
```

根目录不再放置 `root_node`、`cabinet_node` 或 `common` 的副本。

## 系统职责

- 柜子节点：指纹录入、指纹匹配、权限判断和开锁全部在本地完成。柜子保存权限缓存和离线日志，断网不影响已同步权限的验证；联网只用于管理命令、配置同步和日志上报。
- 根节点：维护 ESP-MESH，所有柜子到上位机的消息都经过根节点；根节点也是唯一业务数据中心，用户、角色权限、设备状态、日志和指纹模板保存在 SD 卡。
- 上位机：通过根节点管理用户、权限、设备、指纹录入和日志展示，不创建或使用本地业务数据库。上位机本地只保存通信方式等运行配置。

数据流约束：`柜子本地验证 -> 根节点保存日志 -> 上位机查询/展示`；管理变更则是 `上位机 -> 根节点 SD -> 根节点广播 -> 柜子本地缓存`。
