"""ESP32 batch flashing engine shared by the desktop UI."""

from __future__ import annotations

import csv
import datetime as dt
import hashlib
import importlib.util
import json
import os
import queue
import re
import subprocess
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from typing import Callable

from serial.tools import list_ports


FIELDS = [
    "no", "time", "com", "mac", "chip", "features", "profile",
    "firmware", "status", "duration_s", "log", "firmware_sha256",
]
MAC_RE = re.compile(r"^\s*MAC:\s*((?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2})")
CHIP_RE = re.compile(r"^\s*Chip is\s+(.+?)(?:\s+\(|$)")
FEATURE_RE = re.compile(r"^\s*Features:\s*(.*)$")
WRITE_ADDRESS_RE = re.compile(r"Writing at 0x([0-9A-Fa-f]+)")
PERCENT_RE = re.compile(r"\((\d{1,3})\s*%\)")


def now_text() -> str:
    return dt.datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def resolve(base_dir: str, path: str) -> str:
    path = os.path.expandvars(os.path.expanduser(path))
    if os.path.isabs(path):
        return os.path.normpath(path)
    return os.path.normpath(os.path.join(base_dir, path))


def _esptool_command(config_dir: str, configured: str | None, python_exe: str) -> list[str]:
    if configured:
        path = resolve(config_dir, configured)
        return [python_exe, path] if path.lower().endswith(".py") else [path]
    platformio_esptool = os.path.expanduser(
        "~/.platformio/packages/tool-esptoolpy/esptool.py"
    )
    if os.path.exists(platformio_esptool):
        return [python_exe, platformio_esptool]
    if importlib.util.find_spec("esptool") is not None:
        return [python_exe, "-m", "esptool"]
    return ["esptool"]


def _firmware_signature(files: list[dict]) -> str:
    digest = hashlib.sha256()
    for item in files:
        digest.update(str(item["address"]).encode("ascii"))
        with open(item["resolved_path"], "rb") as firmware_file:
            for block in iter(lambda: firmware_file.read(1024 * 1024), b""):
                digest.update(block)
    return digest.hexdigest()


def load_profile(config_path: str, profile_name: str | None = None) -> dict:
    config_path = os.path.abspath(config_path)
    config_dir = os.path.dirname(config_path)
    with open(config_path, encoding="utf-8") as config_file:
        source = json.load(config_file)
    selected = profile_name or source.get("default_profile", "cabinet")
    if selected not in source.get("profiles", {}):
        raise ValueError(f"配置中不存在烧录类型: {selected}")
    profile = dict(source["profiles"][selected])
    runtime = dict(source)
    runtime.update(profile)
    runtime["_config_path"] = config_path
    runtime["_config_dir"] = config_dir
    runtime["_profile"] = selected
    runtime["python_exe"] = source.get("python_exe") or sys.executable
    runtime["esptool_command"] = _esptool_command(
        config_dir, source.get("esptool_path"), runtime["python_exe"]
    )
    runtime["files"] = [dict(item) for item in profile["files"]]
    for item in runtime["files"]:
        item["resolved_path"] = resolve(config_dir, item["path"])
        if not os.path.isfile(item["resolved_path"]):
            raise FileNotFoundError(f"固件文件不存在: {item['resolved_path']}")
        item["size"] = os.path.getsize(item["resolved_path"])
    runtime["_firmware"] = os.path.basename(runtime["files"][-1]["resolved_path"])
    runtime["_firmware_sha256"] = _firmware_signature(runtime["files"])
    runtime["records_csv"] = resolve(
        config_dir, source.get("records_csv", "flash_records.csv")
    )
    runtime["log_dir"] = resolve(config_dir, source.get("log_dir", "logs"))
    return runtime


class RecordStore:
    def __init__(self, path: str):
        self.path = path
        self.pending_path = path + ".pending.csv"
        self._lock = threading.RLock()
        self._rows: list[dict] = []
        self._done: set[tuple[str, str, str]] = set()
        self._next_no = 1
        self._load()

    def _load(self) -> None:
        if not os.path.exists(self.path) and not os.path.exists(self.pending_path):
            return
        with self._lock:
            original_fields: list[str] = []
            pending_rows: list[dict] = []
            if os.path.exists(self.path):
                with open(self.path, encoding="utf-8-sig", newline="") as record_file:
                    reader = csv.DictReader(record_file)
                    original_fields = reader.fieldnames or []
                    self._rows = [dict(row) for row in reader]
            if os.path.exists(self.pending_path):
                with open(self.pending_path, encoding="utf-8-sig", newline="") as record_file:
                    pending_rows = [dict(row) for row in csv.DictReader(record_file)]
                self._rows.extend(pending_rows)
            for row in self._rows:
                try:
                    self._next_no = max(self._next_no, int(row.get("no") or 0) + 1)
                except ValueError:
                    pass
                signature = row.get("firmware_sha256") or ""
                if row.get("status") == "OK" and row.get("mac") and signature:
                    self._done.add(
                        (row["mac"].lower(), row.get("profile", ""), signature)
                    )
            if original_fields != FIELDS or pending_rows:
                try:
                    self._rewrite_locked()
                    if pending_rows:
                        os.remove(self.pending_path)
                except OSError:
                    pass

    def _rewrite_locked(self) -> None:
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        temp_path = self.path + ".tmp"
        with open(temp_path, "w", encoding="utf-8-sig", newline="") as record_file:
            writer = csv.DictWriter(record_file, fieldnames=FIELDS, extrasaction="ignore")
            writer.writeheader()
            for row in self._rows:
                writer.writerow({field: row.get(field, "") for field in FIELDS})
        try:
            os.replace(temp_path, self.path)
        except OSError:
            if os.path.exists(temp_path):
                os.remove(temp_path)
            raise

    def is_done(self, mac: str, profile: str, signature: str) -> bool:
        with self._lock:
            return (mac.lower(), profile, signature) in self._done

    def append(self, record: dict) -> dict:
        with self._lock:
            complete = {field: record.get(field, "") for field in FIELDS}
            complete["no"] = self._next_no
            self._next_no += 1
            os.makedirs(os.path.dirname(self.path), exist_ok=True)
            target_path = self.path
            try:
                self._append_file(target_path, complete)
            except OSError:
                target_path = self.pending_path
                self._append_file(target_path, complete)
            self._rows.append(complete)
            signature = complete.get("firmware_sha256") or ""
            if complete["status"] == "OK" and complete["mac"] and signature:
                self._done.add(
                    (complete["mac"].lower(), complete["profile"], signature)
                )
            complete["_record_path"] = target_path
            return complete

    @staticmethod
    def _append_file(path: str, record: dict) -> None:
        new_file = not os.path.exists(path)
        with open(path, "a", encoding="utf-8-sig", newline="") as record_file:
            writer = csv.DictWriter(record_file, fieldnames=FIELDS, extrasaction="ignore")
            if new_file:
                writer.writeheader()
            writer.writerow(record)

    def recent(self, limit: int = 100) -> list[dict]:
        with self._lock:
            return list(reversed(self._rows[-limit:]))


@dataclass
class FlashResult:
    port: str
    status: str
    mac: str = ""
    retry: bool = False


def _command_error(result: subprocess.CompletedProcess, fallback: str) -> str:
    lines = [line.strip() for line in (result.stdout or "").splitlines() if line.strip()]
    return lines[-1] if lines else fallback


def run_esptool(
    config: dict,
    port: str,
    after: str,
    command_args: list[str],
    timeout: float,
    on_line: Callable[[str], None] | None = None,
) -> subprocess.CompletedProcess:
    base = [
        "--chip", config["chip"], "--port", port,
        "--baud", str(config.get("baud", 921600)),
        "--before", config.get("before", "default_reset"),
        "--after", after,
        "--connect-attempts", str(config.get("connect_attempts", 5)),
    ]
    command = config["esptool_command"] + base + command_args
    creation_flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    process = subprocess.Popen(
        command,
        cwd=config["_config_dir"],
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
        bufsize=1,
        creationflags=creation_flags,
    )
    output_queue: queue.Queue[str | None] = queue.Queue()

    def read_output() -> None:
        assert process.stdout is not None
        for raw_line in process.stdout:
            output_queue.put(raw_line.rstrip("\r\n"))
        output_queue.put(None)

    threading.Thread(target=read_output, daemon=True).start()
    lines: list[str] = []
    deadline = time.monotonic() + timeout
    finished_output = False
    while not finished_output:
        if time.monotonic() >= deadline:
            process.kill()
            lines.append(f"命令执行超时（{timeout:.0f} 秒）")
            break
        try:
            line = output_queue.get(timeout=0.1)
        except queue.Empty:
            continue
        if line is None:
            finished_output = True
            continue
        lines.append(line)
        if on_line:
            on_line(line)
    try:
        return_code = process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        return_code = process.wait()
    return subprocess.CompletedProcess(command, return_code, "\n".join(lines), "")


class BatchFlashController:
    def __init__(
        self,
        config: dict,
        event_callback: Callable[[dict], None],
        force: bool = False,
        max_parallel: int | None = None,
        mac_callback: Callable[[str, str], None] | None = None,
    ):
        self.config = config
        self.event_callback = event_callback
        self.force = force
        self.max_parallel = max_parallel or int(config.get("max_parallel", 4))
        self.mac_callback = mac_callback
        self.records = RecordStore(config["records_csv"])
        self._stop = threading.Event()
        self._lock = threading.RLock()
        self._thread: threading.Thread | None = None
        self._executor: ThreadPoolExecutor | None = None
        self._active: set[str] = set()
        self._connected: dict[str, dict] = {}
        self._completed_ports: set[str] = set()
        self._next_retry: dict[str, float] = {}
        self._attempts: dict[str, int] = {}
        self._stats = {"success": 0, "failed": 0, "skipped": 0}

    @property
    def active_count(self) -> int:
        with self._lock:
            return len(self._active)

    def start(self) -> None:
        if self._thread and self._thread.is_alive():
            return
        self._stop.clear()
        self._executor = ThreadPoolExecutor(
            max_workers=self.max_parallel, thread_name_prefix="esp32-flash"
        )
        self._thread = threading.Thread(target=self._monitor, daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()

    def recent_records(self, limit: int = 100) -> list[dict]:
        return self.records.recent(limit)

    def _emit(self, event_type: str, **payload) -> None:
        payload["type"] = event_type
        payload.setdefault("time", now_text())
        self.event_callback(payload)

    def _log(self, message: str, port: str = "", level: str = "info") -> None:
        self._emit("log", message=message, port=port, level=level)

    def _emit_stats(self) -> None:
        with self._lock:
            self._emit(
                "stats",
                connected=len(self._connected),
                active=len(self._active),
                success=self._stats["success"],
                failed=self._stats["failed"],
                skipped=self._stats["skipped"],
            )

    def _discover_ports(self) -> dict[str, dict]:
        whitelist = self.config.get("vid_whitelist")
        ports: dict[str, dict] = {}
        for item in list_ports.comports():
            if whitelist and item.vid not in whitelist:
                continue
            ports[item.device] = {
                "port": item.device,
                "description": item.description or "串口设备",
                "vid": item.vid,
                "pid": item.pid,
                "serial_number": item.serial_number or "",
            }
        return ports

    def _monitor(self) -> None:
        self._emit("monitor", running=True)
        self._log(
            f"开始监听串口，最多 {self.max_parallel} 台并行，烧录类型 {self.config['_profile']}"
        )
        poll_interval = float(self.config.get("poll_interval", 0.6))
        retry_interval = float(self.config.get("retry_interval", 4.0))
        try:
            while not self._stop.is_set():
                current = self._discover_ports()
                now = time.monotonic()
                with self._lock:
                    removed = set(self._connected) - set(current)
                    added = set(current) - set(self._connected)
                    for port in removed:
                        self._completed_ports.discard(port)
                        self._next_retry.pop(port, None)
                        self._attempts.pop(port, None)
                        self._emit("port_removed", port=port)
                    for port in added:
                        self._emit("port_added", **current[port])
                    self._connected = current
                    candidates = [
                        port for port in sorted(current)
                        if port not in self._active
                        and port not in self._completed_ports
                        and now >= self._next_retry.get(port, 0)
                    ]
                    available = max(0, self.max_parallel - len(self._active))
                    for port in candidates[:available]:
                        self._active.add(port)
                        self._attempts[port] = self._attempts.get(port, 0) + 1
                        attempt = self._attempts[port]
                        assert self._executor is not None
                        self._executor.submit(self._worker, port, attempt, retry_interval)
                self._emit_stats()
                self._stop.wait(poll_interval)
        except Exception as exc:
            self._log(f"串口监听异常: {exc}", level="error")
        finally:
            if self._executor:
                self._executor.shutdown(wait=False, cancel_futures=False)
            self._emit("monitor", running=False)
            self._log("已停止监听；进行中的烧录会继续完成")

    def _worker(self, port: str, attempt: int, retry_interval: float) -> None:
        result = self._process_port(port, attempt)
        with self._lock:
            self._active.discard(port)
            if result.retry and port in self._connected and not self._stop.is_set():
                self._next_retry[port] = time.monotonic() + retry_interval
                self._emit(
                    "device",
                    port=port,
                    mac=result.mac,
                    status="retry_wait",
                    progress=0,
                    attempt=attempt,
                    message=f"{retry_interval:.0f} 秒后自动重试",
                )
            else:
                self._completed_ports.add(port)
            if result.status == "OK":
                self._stats["success"] += 1
            elif result.status == "SKIP":
                self._stats["skipped"] += 1
            elif result.status in {"FAIL", "CONNECT_FAIL"}:
                self._stats["failed"] += 1
        self._emit_stats()

    def _device_event(
        self,
        port: str,
        status: str,
        progress: int,
        message: str,
        attempt: int,
        mac: str = "",
    ) -> None:
        self._emit(
            "device",
            port=port,
            mac=mac,
            status=status,
            progress=max(0, min(100, int(progress))),
            message=message,
            attempt=attempt,
        )

    def _process_port(self, port: str, attempt: int) -> FlashResult:
        started = time.monotonic()
        output: list[str] = []
        mac = ""
        chip = ""
        features = ""
        self._device_event(port, "connecting", 3, "正在连接并读取 MAC", attempt)
        self._log(f"发现设备，开始第 {attempt} 次识别", port)
        time.sleep(float(self.config.get("settle_delay", 1.0)))
        try:
            info_result = run_esptool(
                self.config,
                port,
                "hard_reset",
                ["read_mac"],
                timeout=float(self.config.get("identify_timeout", 90)),
            )
            output.append(info_result.stdout or "")
            if info_result.returncode != 0:
                raise RuntimeError(_command_error(info_result, "未识别到 ESP32 芯片"))
            for line in (info_result.stdout or "").splitlines():
                match = MAC_RE.match(line)
                if match:
                    mac = match.group(1).lower()
                match = CHIP_RE.match(line)
                if match:
                    chip = match.group(1).strip()
                match = FEATURE_RE.match(line)
                if match:
                    features = match.group(1).strip()
            if not mac:
                raise RuntimeError("已连接串口，但没有读取到 MAC")
        except Exception as exc:
            self._device_event(port, "failed", 0, f"连接失败: {exc}", attempt)
            self._log(f"连接失败: {exc}", port, "error")
            return FlashResult(port, "CONNECT_FAIL", retry=True)

        display_mac = mac.upper()
        self._device_event(port, "identified", 10, "已读取 MAC，检查烧录记录", attempt, display_mac)
        signature = self.config["_firmware_sha256"]
        if not self.force and self.records.is_done(mac, self.config["_profile"], signature):
            record = self._save_record(
                port, mac, chip, features, "SKIP", started, "", output
            )
            self._device_event(
                port, "skipped", 100, "当前版本已烧录，设备已自动重启", attempt, display_mac
            )
            self._emit("record", record=record)
            self._log(f"当前固件已烧录，跳过 {display_mac}", port)
            return FlashResult(port, "SKIP", mac)

        flash_output: list[str] = []
        try:
            self._device_event(port, "flashing", 14, "开始写入固件", attempt, display_mac)
            if self.mac_callback:
                try:
                    self.mac_callback(port, display_mac)
                except Exception as exc:
                    self._log(f"标签任务提交失败: {exc}", port, "error")
            self._flash(port, attempt, display_mac, flash_output)
            output.extend(flash_output)
            delay = float(self.config.get("post_flash_delay", 1.0))
            if delay > 0:
                time.sleep(delay)
            record = self._save_record(
                port, mac, chip, features, "OK", started, "", output
            )
            self._device_event(
                port, "completed", 100, "烧录校验完成，设备已自动重启", attempt, display_mac
            )
            self._emit("record", record=record)
            self._log(f"烧录完成，MAC {display_mac}", port)
            return FlashResult(port, "OK", mac)
        except Exception as exc:
            detail = str(exc)
            output.extend(flash_output)
            record = self._save_record(
                port, mac, chip, features, "FAIL", started, detail, output
            )
            self._device_event(port, "failed", 0, detail, attempt, display_mac)
            self._emit("record", record=record)
            self._log(f"烧录失败: {detail}", port, "error")
            return FlashResult(port, "FAIL", mac, retry=True)

    def _flash(
        self,
        port: str,
        attempt: int,
        display_mac: str,
        outputs: list[str],
    ) -> None:
        for region in self.config.get("erase_first", []):
            self._device_event(port, "erasing", 12, "正在擦除指定区域", attempt, display_mac)
            result = run_esptool(
                self.config,
                port,
                "no_reset",
                ["erase_region", str(region["address"]), str(region["size"])],
                timeout=180,
            )
            outputs.append(result.stdout or "")
            if result.returncode != 0:
                raise RuntimeError(_command_error(result, "擦除失败"))

        write_args = [
            "write_flash",
            "--flash_mode", self.config.get("flash_mode", "qio"),
            "--flash_freq", self.config.get("flash_freq", "80m"),
            "--flash_size", self.config.get("flash_size", "16MB"),
        ]
        for item in self.config["files"]:
            write_args.extend([str(item["address"]), item["resolved_path"]])

        total_size = sum(item["size"] for item in self.config["files"])
        ordered_files = sorted(
            self.config["files"], key=lambda item: int(str(item["address"]), 0)
        )
        last_progress = 14

        def write_progress(line: str) -> None:
            nonlocal last_progress
            progress = last_progress
            address_match = WRITE_ADDRESS_RE.search(line)
            if address_match:
                address = int(address_match.group(1), 16)
                completed = 0
                for item in ordered_files:
                    start = int(str(item["address"]), 0)
                    if address >= start:
                        completed += min(item["size"], max(0, address - start))
                    else:
                        break
                progress = 14 + int(72 * min(1.0, completed / max(1, total_size)))
            else:
                percent_match = PERCENT_RE.search(line)
                if percent_match:
                    progress = 14 + int(int(percent_match.group(1)) * 0.72)
            last_progress = max(last_progress, progress)
            if last_progress > 14:
                self._device_event(
                    port, "flashing", last_progress, "正在写入固件", attempt, display_mac
                )

        verify_after = bool(self.config.get("verify_after_flash", True))
        write_result = run_esptool(
            self.config,
            port,
            "no_reset" if verify_after else "hard_reset",
            write_args,
            timeout=float(self.config.get("flash_timeout", 900)),
            on_line=write_progress,
        )
        outputs.append(write_result.stdout or "")
        if write_result.returncode != 0:
            raise RuntimeError(_command_error(write_result, "固件写入失败"))

        if verify_after:
            self._device_event(port, "verifying", 90, "正在校验写入内容", attempt, display_mac)
            verify_args = [
                "verify_flash",
                "--flash_mode", self.config.get("flash_mode", "qio"),
                "--flash_freq", self.config.get("flash_freq", "80m"),
                "--flash_size", self.config.get("flash_size", "16MB"),
            ]
            for item in self.config["files"]:
                verify_args.extend([str(item["address"]), item["resolved_path"]])
            verify_result = run_esptool(
                self.config,
                port,
                "hard_reset",
                verify_args,
                timeout=float(self.config.get("verify_timeout", 600)),
            )
            outputs.append(verify_result.stdout or "")
            if verify_result.returncode != 0:
                raise RuntimeError(_command_error(verify_result, "烧录后校验失败"))
        self._device_event(port, "restarting", 99, "校验通过，正在自动重启", attempt, display_mac)

    def _save_record(
        self,
        port: str,
        mac: str,
        chip: str,
        features: str,
        status: str,
        started: float,
        detail: str,
        output: list[str],
    ) -> dict:
        os.makedirs(self.config["log_dir"], exist_ok=True)
        safe_mac = mac.replace(":", "") or "UNKNOWN"
        stamp = dt.datetime.now().strftime("%Y%m%d_%H%M%S_%f")
        log_path = os.path.join(self.config["log_dir"], f"{safe_mac}_{stamp}_{port}.log")
        with open(log_path, "w", encoding="utf-8") as log_file:
            log_file.write("\n\n".join(part for part in output if part))
            if detail:
                log_file.write(f"\n\nERROR: {detail}\n")
        record = self.records.append({
            "time": now_text(),
            "com": port,
            "mac": mac,
            "chip": chip,
            "features": features,
            "profile": self.config["_profile"],
            "firmware": self.config["_firmware"],
            "status": status,
            "duration_s": f"{time.monotonic() - started:.1f}",
            "log": log_path,
            "firmware_sha256": self.config["_firmware_sha256"],
        })
        if record.get("_record_path") == self.records.pending_path:
            self._log("CSV 正在被占用，本条记录已暂存，关闭 CSV 后重启工具会自动合并", port, "warning")
        return record
