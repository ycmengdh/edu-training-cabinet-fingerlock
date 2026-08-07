# ESP-IDF cabinet firmware

This directory contains the ESP-IDF 6 firmware. It is independent from the
legacy PlatformIO/Arduino build in `../esp32_firmware`.

## Source layout

- `common_components/`: components used by both firmware projects, including
  the application protocol, Mesh transport, serial framing, and NVS-backed
  device configuration
- `root_node/components/`: Root-only `controller`, ST7735 `display`, and SD
  `storage` components
- `cabinet_node/components/`: Cabinet-only `controller`, `fingerprint`, and
  lock/key `hardware` components
- `root_node/main/` and `cabinet_node/main/`: each firmware's startup and
  runtime orchestration

Each project discovers its own local `components/` directory automatically,
so local component names do not repeat the node role. Public C APIs retain
their `root_` or `cab_` namespace prefixes. Each project adds only
`common_components/` through `EXTRA_COMPONENT_DIRS`, keeping node-specific
code out of the other firmware's component graph.

## Internal network

- Transport: Espressif ESP-Mesh-Lite 1.0.2 `no_router` mode
- Channel: 6 (fixed)
- Mesh vendor ID: `43:42`
- Mesh ID: `31`
- Mesh key: `Mesh@2026`
- Maximum depth: 6 levels
- Maximum nodes: 100 total, including the fixed Root
- Maximum direct children per parent: 6
- Root: fixed at level 1, USB-Serial-JTAG uplink only
- Cabinet: cannot become root; it joins a matching parent without configured
  infrastructure Wi-Fi
- Wi-Fi power saving is disabled to reduce command latency and reconnect risk

No infrastructure router, Internet connection, DNS service, or cloud service
is required. Mesh-Lite creates private SoftAP and station interfaces only for
internal parent/child links. Root-to-cabinet application packets include a
destination MAC and are relayed down the tree; only the addressed cabinet
passes a command to its business handler.

Application reliability remains end-to-end. The host retries the same message
ID when a response is missing, and the cabinet response cache returns the
previous response without executing a repeated lock command again.

Cabinets send an application heartbeat every 5 seconds. Root replies with a
`HEARTBEAT_ACK` carrying the same message ID. If a cabinet receives no valid
Root downlink for 7 seconds after a heartbeat, it announces `REGISTER` again
to repair the application route. Mesh-Lite remains responsible for parent-link
recovery; an application timeout does not restart the Wi-Fi/Mesh stack.

Root and cabinets also publish `STATUS_REPORT` every 60 seconds. Cabinet
reports use the same compact 24-byte payload as `STATUS_RESPONSE`; the WPF
mapper decodes both commands identically. The first cabinet report is spread
over a deterministic 5-55 second window based on its MAC address, preventing
a synchronized burst when a large installation is powered at once.

## Cabinet Mesh OTA

The WPF cabinet management page can upload a `cabinet_node_idf` application
image to the Root SD card and start a Mesh-Lite LAN OTA distribution. Root
validates the ESP32-S3 image header, project name, version, size, and SHA-256
before it exposes the image to the Mesh. Root only distributes the cabinet
image and never writes it to its own OTA partition.

Cabinets write the image to the inactive OTA slot, reboot after validation,
then report the version from the ESP-IDF application descriptor. A newly
booted image is marked valid only after the cabinet has rejoined the Mesh and
received a Root heartbeat ACK. If that does not happen within 90 seconds, the
bootloader rolls back to the previous image.

The first installation of this firmware must use a complete serial flash so
the cabinet receives the new two-slot OTA partition table and bootloader
rollback configuration. Later cabinet releases can use Mesh OTA. Build the
OTA target with a version different from the running firmware, for example
change `PROJECT_VER` from `0.0.1-cab` to `0.0.2-cab`; a cabinet intentionally
rejects an image whose version is already running.

Mesh-Lite also rejects a distribution when the Root application's own version
equals the cabinet image version. Keep Root versions in the `x.y.z-root`
namespace and cabinet versions in the `x.y.z-idf` namespace. Root never installs
the cabinet image; the distinct version only prevents Mesh-Lite from mistaking
the distribution for a same-version update.

During the same Root runtime, the desktop reuses an already validated image
when its version, byte size, and SHA-256 match. Root startup never scans or
hashes an OTA image, so a damaged or slow SD card cannot delay display and
serial initialization.

Root copies Mesh ingress into a 64-entry PSRAM-backed queue before protocol
handling, ACK transmission, or USB forwarding. This keeps Mesh-Lite callbacks
short during multi-cabinet bursts. Its `device_id` to MAC route table is shared
with the host serial task through a mutex, and Root status counts only cabinet
routes, not Root itself.

## Runtime diagnostics

- Root `READ_STATUS` reports `child_count`, `route_count`, `sd_ready`, and
  `sd_error`. Root registration/status also reports `mesh_rx_drops`.
  `SD_QUERY_VERSION` reports FAT capacity and used bytes.
- Cabinet `READ_STATUS` sets `fingerprint_ready` from the live sensor state.
- Cabinet registration reports `mesh_root_responses`, `mesh_heartbeat_acks`,
  `mesh_heartbeat_timeouts`, and `mesh_queue_full`. The compact heartbeat and
  status payloads also carry send-failure, queue-drop, and recovery counters.
- Cabinet `READ_CONFIG` also reports `fingerprint_power` (the raw power
  feedback meaning of GPIO42: high is powered, low is not powered),
  `fingerprint_power_off_level`, `fingerprint_power_on_level`,
  `fingerprint_handshake`, `fingerprint_probe_result`, and
  `fingerprint_error` for wiring and power diagnosis.
- `fingerprint_count` is persisted for compatibility and does not by itself
  prove that the fingerprint module is currently reachable.
- The cabinet fingerprint UART matches the existing DM900 hardware at
  57600 baud, 8 data bits, no parity, and 2 stop bits.
- Root drives the existing 0.96-inch ST7735 display directly through ESP-IDF
  SPI3 at 27 MHz: MOSI GPIO11, SCLK GPIO10, CS GPIO12, DC GPIO13, and reset
  GPIO14. The 160x80 status console shows USB host state, Mesh/cabinet count,
  SD state, traffic failures, and uptime. Its 25 KB DMA framebuffer and panel
  initialization are isolated in `root_display`; a display failure is exposed
  as `display_ready=false` and never restarts or blocks Mesh/USB service.

## Build

Open an ESP-IDF 6.0.2 terminal, then run:

```powershell
cd esp32/root_node
idf.py set-target esp32s3
idf.py build

cd ../cabinet_node
idf.py set-target esp32s3
idf.py build
```

The first configure downloads the pinned `espressif/mesh_lite` 1.0.2 managed
component and its dependencies.

Flash the current hardware ports:

```powershell
cd esp32/root_node
idf.py -p COM16 flash

cd ../cabinet_node
idf.py -p COM12 flash
```

COM12 is the currently enumerated CH343 cabinet port; it was previously COM13.
