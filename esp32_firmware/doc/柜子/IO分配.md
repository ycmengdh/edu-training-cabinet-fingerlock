# 柜子节点 IO 分配（ESP32-S3）

更新时间：2026-07-28  
适用范围：`esp32_firmware/cabinet_node`  
板型：**ESP32-S3 N16R8**（16MB Flash + 8MB PSRAM；**主机维护口为 UART0，非 USB CDC**）

本文以**实际 PCB 接线**为准。固件引脚定义见 `cabinet_node/src/config.h`。  
系统级组网说明见仓库 `doc/系统架构与组网说明.md`。

芯片与根节点同为 **N16R8**：Flash/PSRAM、板级电源等同类；  
**外设 IO（指纹/按键/595）以本文为准，与根节点 TFT/SD 不同。**

通信（两种模式并存）：

- **Mesh 模式（默认，多柜）**：ESP-MESH 组网找 Root；**同时可常开 UART0 协议口**（与根节点 USB 同协议帧/波特率，便于旁路）
- **Debug / 单柜 UART 模式**：仅 UART0 直连上位机，**不组 Mesh**；长按任意键约 10s 切换并重启  
- 单柜控制协议与接 Root 相同，**不是** AP+TCP

---

## 1. 总览

| 模块 | 接口 | GPIO / 资源 | 方向 | 说明 |
| --- | --- | --- | --- | --- |
| 调试/上位机串口 TX | UART0 | **GPIO43** (U0TXD) | 输出 | 外接 USB-TTL RX；协议同根节点 |
| 调试/上位机串口 RX | UART0 | **GPIO44** (U0RXD) | 输入 | 外接 USB-TTL TX |
| 按键 K1 | 开锁 1 | GPIO47 | 输入（内部上拉） | 按下 = LOW |
| 按键 K2 | 开锁 2 | GPIO48 | 输入（内部上拉） | 按下 = LOW |
| 按键 K3 | 开锁 3 | GPIO45 | 输入（内部上拉） | 按下 = LOW；strapping 脚，见风险 |
| 按键 K4 | 开锁 4 | GPIO38 | 输入（内部上拉） | 按下 = LOW |
| 按键 K5 | 取消 | GPIO39 | 输入（内部上拉） | 按下 = LOW |
| 指纹通讯 TX | UART2 | GPIO17 | 输出 | ESP32 TX → AS608 RX |
| 指纹通讯 RX | UART2 | GPIO18 | 输入 | ESP32 RX ← AS608 TX |
| 指纹上电控制 | 电源开关 | GPIO42 | 输出 | 控制指纹模块供电 |
| 指纹上电状态 | 状态反馈 | GPIO21 | 输入 | 读指纹供电是否到位 |
| 595 数据 | DS | GPIO4 | 输出 | 74HC595 SER |
| 595 锁存 | STCP | GPIO15 | 输出 | 74HC595 RCLK |
| 595 时钟 | SHCP | GPIO16 | 输出 | 74HC595 SRCLK |
| 状态 LED | 板载指示 | GPIO2 | 输出 | Mesh/调试指示，非锁 LED |

---

## 2. 调试串口与上位机直连

| 项目 | 值 |
| --- | --- |
| 物理接口 | **UART0**（非板载 USB-Serial-JTAG / CDC） |
| 默认脚位 | TX=**GPIO43**, RX=**GPIO44**（ESP32-S3 默认 UART0） |
| 波特率 | **921600**（与根节点 `UPLINK_USB_BAUD` 相同） |
| 协议 | 与根节点上行一致：帧头 `0xA5 0x5A` + 长度 + JSON + CRC16/MODBUS |
| 工程开关 | `ARDUINO_USB_CDC_ON_BOOT=0`（`Serial` 走 UART0） |
| 工作模式 | `MODE_MESH`：Mesh + UART0 双开；`MODE_DEBUG`：仅 UART0 |
| 切换 | 任意键长按 10 s，Mesh ↔ Debug，重启生效 |
| 启动标记 | 明文 `[CABINET_BOOT] UART0-SERIAL ALIVE` / `PROTOCOL READY`（同根节点风格） |
| 注册 | 周期性 `REGISTER`（`is_root=false`, `uplink=uart0`, `role=cabinet`） |

联调要点：

1. USB-TTL：模块 TX↔GPIO44，RX↔GPIO43，共地  
2. 上位机打开对应 COM，波特率 921600，按**根节点串口协议**收发  
3. 明文探测：发 `PING\n` 应回 `PONG`；正式业务用协议帧  
4. Debug 模式下设备会周期性发 `REGISTER`（`uplink=uart0`, `role=cabinet`）  
5. 日志在 Debug 模式下封装为 `cmd=LOG` 帧，避免打乱上位机解析  

与根节点差异仅在物理介质：

| | 根节点 | 柜子节点（调试） |
| --- | --- | --- |
| 物理口 | USB-Serial-JTAG (CDC, GPIO19/20) | **UART0 (GPIO43/44)** |
| 协议帧 / 波特率 / JSON 命令 | 相同 | 相同 |
| 业务角色 | Root + SD + 桥接 | 指纹/锁/按键本机业务 |

---

## 3. 按键

| 内部索引 | 硬件丝印 | GPIO | 界面名称 | 锁输出 | LED 输出 | 固件宏 |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | K1 | 47 | Lock1（系统锁） | OUT4 | OUT5 | `KEY0_PIN` |
| 1 | K2 | 48 | Lock2（实训柜1） | OUT3 | OUT6 | `KEY1_PIN` |
| 2 | K3 | 45 | Lock3（实训柜2） | OUT2 | OUT7 | `KEY2_PIN` |
| 3 | K4 | 38 | Lock4（实训柜3） | OUT1 | OUT8 | `KEY3_PIN` |
| 4 | K5 | 39 | 取消当前指纹流程 | - | - | `KEY4_PIN` / `KEY_CANCEL_INDEX=4` |

上位机数据库、权限数组和通信协议继续使用内部索引 `0..3`；只有用户可见名称使用 Lock1..Lock4。

电气约定：

- `INPUT_PULLUP`
- 按下接到 GND → 读到 LOW
- 消抖 20 ms
- 任意键长按 10 s：Mesh ↔ Debug(UART0) 切换并重启

风险：

- **GPIO45 为 ESP32-S3 strapping 脚**（VDD_SPI 相关）。  
  按键外部不要再加会在复位期间拉偏的强上下拉；若启动异常，优先检查 K3 外围。

---

## 4. 指纹模块（AS608）

| 信号 | GPIO | 方向 | 固件宏 | 接线 |
| --- | --- | --- | --- | --- |
| UART TX | 17 | 输出 | `FINGER_TX_PIN` | ESP32 GPIO17 → AS608 RX |
| UART RX | 18 | 输入 | `FINGER_RX_PIN` | ESP32 GPIO18 ← AS608 TX |
| 上电控制 | 21 | 输出 | `FINGER_PWR_PIN` | 控制模块电源通路（如 MOS/LDO EN）；**低有效**（`FINGER_PWR_ON_LEVEL=LOW`） |
| 上电状态 | 42 | 输入 | `FINGER_PWR_STATUS_PIN` | 读供电反馈；当前样机上电后读到 LOW，但模块握手正常，反馈极性/接线需实板确认 |

通讯参数：

- 外设：`HardwareSerial(2)` / UART2
- 波特率：`57600`（`FINGER_UART_BAUD`）
- 数据格式：8N1

上电时序（固件）：

1. 配置 GPIO42 为输出，拉到**上电有效电平**（当前硬件 **LOW** 有效，见 `FINGER_PWR_ON_LEVEL`）
2. 等待模块稳定（默认 300 ms）
3. 读 GPIO21 状态日志
4. 打开 UART2，执行 `verifyPassword()`

注意：

- 必须共地
- 供电电压以模块规格为准（多数 AS608 供电 3.3~5V，UART 常为 3.3V TTL）
- 当前板：控制脚**低电平上电**（`FINGER_PWR_ON_LEVEL=LOW`），状态反馈**高电平已上电**（`FINGER_PWR_STATUS_ON_LEVEL=HIGH`），两者独立配置

---

## 5. 74HC595 与锁 / 锁 LED

| 信号 | GPIO | 74HC595 引脚 | 固件宏 |
| --- | --- | --- | --- |
| DATA | 4 | SER / DS | `SHIFT_DS_PIN` |
| STCP | 15 | RCLK / ST_CP | `SHIFT_STCP_PIN` |
| SHCP | 16 | SRCLK / SH_CP | `SHIFT_SHCP_PIN` |

### 5.1 595 输出位映射

固件按 **MSB first** 移出 1 字节：bit0 → Q0 … bit7 → Q7。

| 595 输出 | 板卡输出 | 功能 | 逻辑 |
| --- | --- | --- | --- |
| Q0 | OUT1 | Lock4 继电器 | **高电平开锁**（1=开，0=关） |
| Q1 | OUT2 | Lock3 继电器 | 同上 |
| Q2 | OUT3 | Lock2 继电器 | 同上 |
| Q3 | OUT4 | Lock1 继电器 | 同上 |
| Q4 | OUT5 | Lock1 状态 LED | **高电平亮**（1=亮，0=灭） |
| Q5 | OUT6 | Lock2 状态 LED | 同上 |
| Q6 | OUT7 | Lock3 状态 LED | 同上 |
| Q7 | OUT8 | Lock4 状态 LED | 同上 |

对应关系：

- 开锁时：对应继电器 bit=1，对应 LED bit=1
- 关锁时：对应继电器 bit=0，对应 LED bit=0
- 开锁保持时间：`LOCK_OPEN_DURATION_MS` = 1000 ms，超时自动关锁
- 锁芯保护上限：`LOCK_FORCE_OFF_MS` = 2000 ms；若检测到任一锁连续开锁超过 2s，强制断开继电器

默认上电字节：`0x00`
（Q0~Q3=0 全关锁，Q4~Q7=0 全灭 LED）

---

## 6. 指纹验证窗口指示（V2.7）

### 6.1 锁状态 LED（595 Q4~Q7 / OUT5~OUT8）

验证成功进入 10s 窗口后，**有权限的锁**对应 LED 慢闪（800ms 周期），提示用户可按：

| 状态 | 锁 LED 表现 | 触发 |
| --- | --- | --- |
| 验证成功窗口 | 有权限的锁慢闪，无权限的锁灭 | `setPermissionHint(lock_perm)` |
| 按键开锁 | 该路常亮约 1s（跟随继电器），其它提示熄灭 | `openLock` + `clearPermissionHint` |
| 按无权限键 | 慢闪不停 | 窗口继续 |
| 取消 / 超时 | 全部熄灭 | `clearPermissionHint` |

交互流程：

1. 常态：锁 LED 全灭
2. 指纹匹配 + 本地权限有效 → 有权限锁 LED 慢闪
3. 10s 内按对应键 → 开锁 1s；按 K5 取消 / 超时 → 回常态
4. 按了无权限的键：提示失败，窗口不结束，可继续按其它有权限的锁

---

## 7. 建议联调顺序

1. UART0 接 USB-TTL，921600，看是否有：  
   `[CABINET_BOOT] UART0 ALIVE` / `PROTOCOL READY`
2. Debug 模式：上位机按根节点串口方式收 `REGISTER`，可下发本机 `device_id` 的命令  
3. 指纹：`[FINGER] power ...` -> `init success`
4. 按压已录入指纹：**有权限的锁 LED 慢闪**（进入 10s 窗口）；10s 内按 K1~K4 对应键则开锁约 1 s
5. 按压未录入指纹或无权限用户：锁 LED 仍灭
6. K5 取消：在窗口期内按下立即灭锁提示灯，回识别态；长按 10 s 切 Mesh/Debug

若指纹 init 失败：查 17/18 交叉、GPIO42 上电、GPIO21、共地、波特率。  
若按键无日志：查 47/48/45/38/39 与 active-LOW。
若锁不动：查 595 的 4/15/16、OUT1~OUT4 接线与高电平有效极性。
若上位机无帧：确认 **UART0 不是 USB 口**、交叉线、波特率、是否在 Debug 模式。

---

## 8. 固件文件索引

| 文件 | 内容 |
| --- | --- |
| `cabinet_node/platformio.ini` | N16R8；`USB_CDC_ON_BOOT=0` |
| `cabinet_node/src/config.h` | 全部 GPIO 宏 |
| `cabinet_node/src/key_handler.*` | 5 路按键 |
| `cabinet_node/src/fingerprint.*` | 指纹供电 + UART2 |
| `cabinet_node/src/lock_control.*` | 74HC595 锁与 LED |
| `common/mesh_comm.*` | Mesh + Debug(UART0 协议) |
| `cabinet_node/src/main.cpp` | 初始化顺序与主循环 |
