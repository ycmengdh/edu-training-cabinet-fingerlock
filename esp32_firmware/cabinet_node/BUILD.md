# 编译 / 烧录 / 擦除 速查

> 环境：Windows + PowerShell + PlatformIO (独立安装，未入 PATH)
> 板型：ESP32-S3 N16R8（16MB Flash + 8MB Octal PSRAM）
> 固件目录：`d:\UGit\edu-training-cabinet-fingerlock\esp32_firmware\cabinet_node`

## 0. 路径与端口

| 项 | 值 |
|---|---|
| PIO 可执行 | `C:\Users\meng\.platformio\penv\Scripts\pio.exe` |
| 项目目录 | `d:\UGit\edu-training-cabinet-fingerlock\esp32_firmware\cabinet_node` |
| 串口端口 | `COM19`（若插拔后变化，到设备管理器查 ESP32-S3 的 COM 号） |
| 监视波特率 | `921600` |

PowerShell 调用 PIO 的统一前缀（避免 `pio` 不在 PATH 的报错）：

```powershell
$env:PLATFORMIO_CORE_DIR="$env:USERPROFILE\.platformio"
$pio = "$env:USERPROFILE\.platformio\penv\Scripts\pio.exe"
```

## 1. 仅编译（不烧录）

```powershell
& $pio run
```

## 2. 编译 + 烧录

```powershell
& $pio run -t upload --upload-port COM19
```

## 3. 擦除整个 Flash（清除 NVS / 权限 / 日志等全部数据）

```powershell
& $pio run -t erase --upload-port COM19
```

擦除后必须重新烧录固件：

```powershell
& $pio run -t upload --upload-port COM19
```

## 4. 只擦 NVS 分区（保留固件）

分区表 `common/partitions_16MB_log.csv` 中 NVS 位于 `0x9000`，大小 `0x5000`：

```powershell
$esptool = "$env:USERPROFILE\.platformio\packages\tool-esptoolpy\esptool.py"
python $esptool --port COM19 --baud 921600 erase_region 0x9000 0x5000
```

## 5. 串口监视器

```powershell
& $pio device monitor -p COM19 -b 921600
```

退出监视器：`Ctrl+A` 然后 `Q`（或直接关终端）。

## 6. 一键：擦除 + 烧录（彻底重置）

```powershell
$env:PLATFORMIO_CORE_DIR="$env:USERPROFILE\.platformio"
$pio = "$env:USERPROFILE\.platformio\penv\Scripts\pio.exe"
& $pio run -t erase --upload-port COM19
& $pio run -t upload --upload-port COM19
```

## 7. 分区表参考

| 分区 | 偏移 | 大小 | 说明 |
|---|---|---|---|
| nvs | 0x9000 | 0x5000 | 设备配置、权限缓存 |
| otadata | 0xe000 | 0x2000 | OTA 启动选择 |
| app0 | 0x10000 | 0x300000 | 固件 A 区 |
| app1 | 0x310000 | 0x300000 | 固件 B 区 |
| spiffs | 0x610000 | 0x1D0000 | SPIFFS |
| logstore | 0x7E0000 | 0x10000 | 离线日志环 |
| coredump | 0x7F0000 | 0x10000 | 崩溃转储 |

## 8. 备注

- PIO 不在系统 PATH，必须用完整路径 `& $pio ...` 调用。
- 端口号 `COM19` 为当前样机；换 USB 口或换电脑后会变，到设备管理器确认。
- 若烧录失败（端口被占用）：关掉其他串口工具（串口助手、监视器等）再试。
- 固件占用约：Flash 32.1%，RAM 22.5%（参考值，随代码变化）。
