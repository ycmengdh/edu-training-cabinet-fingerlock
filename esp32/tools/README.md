# ESP32-S3 Batch Flash Tool

The batch tool flashes the ESP-IDF build outputs under `esp32/root_node/build`
and `esp32/cabinet_node/build`. It does not compile firmware, so build the
selected project before starting a production batch.

## Profiles

`cabinet` writes the complete first-installation image required for later Mesh
OTA updates:

| Address | File |
| --- | --- |
| `0x0` | `cabinet_node/build/bootloader/bootloader.bin` |
| `0x8000` | `cabinet_node/build/partition_table/partition-table.bin` |
| `0x10000` | `cabinet_node/build/cabinet_node_idf.bin` |
| `0x610000` | `cabinet_node/build/ota_data_initial.bin` |

`root` writes the Root factory image:

| Address | File |
| --- | --- |
| `0x0` | `root_node/build/bootloader/bootloader.bin` |
| `0x8000` | `root_node/build/partition_table/partition-table.bin` |
| `0x10000` | `root_node/build/cabinet_root_idf.bin` |

Do not add the old Arduino `boot_app0.bin` at `0xe000`; these ESP-IDF partition
tables do not use it.

## Operation

1. Build the Root or cabinet ESP-IDF project.
2. Run `启动批量烧录.bat`.
3. Select `root` or `cabinet` before starting monitoring.
4. Set the parallel device count and click the start button.
5. Connect devices. The tool detects approved USB serial adapters, flashes all
   required regions, verifies them, resets the device, and records its MAC.

Use `cabinet` only for the initial wired flash. Later cabinet application
updates use the `cabinet_node_idf.bin` file through Mesh OTA.
