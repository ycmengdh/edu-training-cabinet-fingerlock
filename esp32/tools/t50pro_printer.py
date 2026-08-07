"""T50 Pro SDK bridge and serialized label-printing queue."""

from __future__ import annotations

import datetime as dt
import json
import os
import queue
import re
import shutil
import subprocess
import threading
import time
from dataclasses import dataclass
from typing import Callable


BASE_DIR = os.path.dirname(os.path.abspath(__file__))
BRIDGE_DIR = os.path.join(BASE_DIR, "t50pro_bridge")
BRIDGE_SOURCE = os.path.join(BRIDGE_DIR, "Program.cs")
BRIDGE_BIN_DIR = os.path.join(BRIDGE_DIR, "bin")
BRIDGE_EXE = os.path.join(BRIDGE_BIN_DIR, "CabinetT50ProBridge.exe")
SDK_DIR = os.path.join(
    BASE_DIR, "SUPVAN.T50PRO.DLL", "PackageSDK", "Supvan.T50PRO.SDK"
)
SDK_FILES = ("Supvan.T50PRO.SDK.dll", "SevenZip.dll", "zxing.dll")
MAC_HEX_RE = re.compile(r"^[0-9A-F]{12}$")
CREATE_NO_WINDOW = 0x08000000 if os.name == "nt" else 0


class T50ProError(RuntimeError):
    """Raised when the T50 Pro SDK bridge cannot complete an operation."""


def cabinet_label_text(mac: str) -> str:
    compact = re.sub(r"[^0-9A-Fa-f]", "", mac).upper()
    if not MAC_HEX_RE.fullmatch(compact):
        raise ValueError(f"MAC 地址格式无效: {mac}")
    return f"CAB_{compact}"


def _find_csc() -> str:
    configured = os.environ.get("T50PRO_CSC")
    candidates = [
        configured,
        os.path.join(
            os.environ.get("WINDIR", r"C:\Windows"),
            "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe",
        ),
        os.path.join(
            os.environ.get("WINDIR", r"C:\Windows"),
            "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe",
        ),
    ]
    for candidate in candidates:
        if candidate and os.path.isfile(candidate):
            return candidate
    raise T50ProError("未找到 .NET Framework 4 编译器，无法加载 T50 Pro 官方 SDK")


def _bridge_is_stale() -> bool:
    if not os.path.isfile(BRIDGE_EXE):
        return True
    executable_mtime = os.path.getmtime(BRIDGE_EXE)
    inputs = [BRIDGE_SOURCE, *(os.path.join(SDK_DIR, name) for name in SDK_FILES)]
    return any(os.path.getmtime(path) > executable_mtime for path in inputs)


def ensure_bridge() -> str:
    missing = [name for name in SDK_FILES if not os.path.isfile(os.path.join(SDK_DIR, name))]
    if missing:
        raise T50ProError(f"T50 Pro SDK 文件缺失: {', '.join(missing)}")
    if not os.path.isfile(BRIDGE_SOURCE):
        raise T50ProError(f"T50 Pro 桥接源码不存在: {BRIDGE_SOURCE}")
    if not _bridge_is_stale():
        return BRIDGE_EXE

    os.makedirs(BRIDGE_BIN_DIR, exist_ok=True)
    for name in SDK_FILES:
        shutil.copy2(os.path.join(SDK_DIR, name), os.path.join(BRIDGE_BIN_DIR, name))
    command = [
        _find_csc(),
        "/nologo",
        "/target:exe",
        "/optimize+",
        f"/out:{BRIDGE_EXE}",
        "/reference:System.dll",
        "/reference:System.Core.dll",
        "/reference:System.Drawing.dll",
        "/reference:System.Web.Extensions.dll",
        f"/reference:{os.path.join(BRIDGE_BIN_DIR, SDK_FILES[0])}",
        BRIDGE_SOURCE,
    ]
    result = subprocess.run(
        command,
        cwd=BRIDGE_BIN_DIR,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        creationflags=CREATE_NO_WINDOW,
        timeout=60,
        check=False,
    )
    if result.returncode != 0 or not os.path.isfile(BRIDGE_EXE):
        detail = (result.stdout + "\n" + result.stderr).strip()
        raise T50ProError(f"T50 Pro 桥接程序编译失败: {detail}")
    return BRIDGE_EXE


class T50ProClient:
    def __init__(self, command_timeout: float = 45.0):
        self.command_timeout = command_timeout

    def _call(self, payload: dict, timeout: float | None = None) -> dict:
        executable = ensure_bridge()
        try:
            result = subprocess.run(
                [executable],
                input=json.dumps(payload, ensure_ascii=False),
                cwd=BRIDGE_BIN_DIR,
                capture_output=True,
                text=True,
                encoding="utf-8-sig",
                errors="replace",
                creationflags=CREATE_NO_WINDOW,
                timeout=timeout or self.command_timeout,
                check=False,
            )
        except subprocess.TimeoutExpired as exc:
            raise T50ProError("T50 Pro 响应超时，请检查连接和电源") from exc
        except OSError as exc:
            raise T50ProError(f"无法启动 T50 Pro SDK: {exc}") from exc

        output_lines = [line for line in result.stdout.splitlines() if line.strip()]
        if not output_lines:
            detail = result.stderr.strip() or f"进程退出码 {result.returncode}"
            raise T50ProError(f"T50 Pro SDK 没有返回结果: {detail}")
        try:
            response = json.loads(output_lines[-1])
        except json.JSONDecodeError as exc:
            raise T50ProError(f"无法解析 T50 Pro SDK 返回值: {output_lines[-1]}") from exc
        if not response.get("ok"):
            raise T50ProError(response.get("error") or "T50 Pro 操作失败")
        return response

    def list_devices(self) -> list[str]:
        response = self._call({"command": "devices"}, timeout=15)
        return [str(path) for path in response.get("devices") or [] if path]

    def get_status(self, device_path: str) -> dict:
        return self._call({"command": "status", "device_path": device_path}, timeout=15)

    def print_label(self, device_path: str, mac: str, settings: dict) -> dict:
        timeout_seconds = int(settings.get("timeout_seconds", 30))
        payload = {
            "command": "print",
            "device_path": device_path,
            "label_text": cabinet_label_text(mac),
            "width_mm": int(settings.get("width_mm", 50)),
            "height_mm": int(settings.get("height_mm", 30)),
            "direction": int(settings.get("direction", 3)),
            "margin_left_mm": int(settings.get("margin_left_mm", 5)),
            "margin_top_mm": int(settings.get("margin_top_mm", -5)),
            "gap_mm": int(settings.get("gap_mm", 3)),
            "speed": int(settings.get("speed", 40)),
            "deepness": int(settings.get("deepness", 4)),
            "font_name": str(settings.get("font_name", "Microsoft YaHei")),
            "font_size_mm": str(settings.get("font_size_mm", "3")),
            "timeout_seconds": timeout_seconds,
        }
        return self._call(payload, timeout=timeout_seconds + 10)


@dataclass(frozen=True)
class LabelJob:
    port: str
    mac: str
    text: str


class T50ProPrintQueue:
    """Serializes jobs for one printer and deduplicates MACs per batch session."""

    def __init__(
        self,
        client: T50ProClient,
        device_path: str,
        settings: dict,
        event_callback: Callable[[dict], None],
    ):
        self.client = client
        self.device_path = device_path
        self.settings = dict(settings)
        self.event_callback = event_callback
        self._jobs: queue.Queue[LabelJob | None] = queue.Queue()
        self._seen: set[str] = set()
        self._lock = threading.Lock()
        self._closed = False
        self._thread = threading.Thread(
            target=self._worker, name="t50pro-label-printer", daemon=True
        )
        self._thread.start()

    @property
    def pending_count(self) -> int:
        return self._jobs.unfinished_tasks

    @property
    def closed(self) -> bool:
        with self._lock:
            return self._closed

    def submit(self, port: str, mac: str, allow_duplicate: bool = False) -> bool:
        compact = re.sub(r"[^0-9A-Fa-f]", "", mac).upper()
        text = cabinet_label_text(compact)
        with self._lock:
            if self._closed or (not allow_duplicate and compact in self._seen):
                return False
            if not allow_duplicate:
                self._seen.add(compact)
        job = LabelJob(port=port, mac=compact, text=text)
        self._jobs.put(job)
        self._emit("queued", job)
        return True

    def close(self, wait: bool = False) -> None:
        with self._lock:
            if self._closed:
                return
            self._closed = True
        self._jobs.put(None)
        if wait:
            self._thread.join(timeout=45)

    def _worker(self) -> None:
        while True:
            job = self._jobs.get()
            try:
                if job is None:
                    return
                retry_count = max(0, int(self.settings.get("retry_count", 0)))
                retry_delay = max(
                    0.0, float(self.settings.get("retry_delay_seconds", 2))
                )
                for attempt in range(retry_count + 1):
                    self._emit("printing", job, attempt=attempt + 1)
                    try:
                        response = self.client.print_label(
                            self.device_path, job.mac, self.settings
                        )
                    except Exception as exc:
                        if attempt < retry_count:
                            self._emit(
                                "retrying", job, attempt=attempt + 1, error=str(exc)
                            )
                            if retry_delay:
                                time.sleep(retry_delay)
                            continue
                        self._emit("failed", job, error=str(exc))
                    else:
                        self._emit(
                            "printed", job, description=response.get("description", "")
                        )
                    break
            finally:
                self._jobs.task_done()

    def _emit(self, status: str, job: LabelJob, **payload) -> None:
        event = {
            "type": "printer",
            "status": status,
            "port": job.port,
            "mac": job.mac,
            "label": job.text,
            "time": dt.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        }
        event.update(payload)
        self.event_callback(event)
