"""Desktop UI for continuous multi-port ESP32 batch flashing."""

from __future__ import annotations

import json
import os
import queue
import threading
import tkinter as tk
from tkinter import messagebox, ttk

from batch_flash_core import BatchFlashController, load_profile
from t50pro_printer import T50ProClient, T50ProError, T50ProPrintQueue


BASE_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_PATH = os.path.join(BASE_DIR, "batch_flash_config.json")
COLORS = {
    "background": "#F3F6F8",
    "surface": "#FFFFFF",
    "line": "#DDE6EA",
    "text": "#173042",
    "muted": "#657887",
    "accent": "#087F73",
    "accent_dark": "#05675E",
    "success": "#16845B",
    "warning": "#B7791F",
    "danger": "#C43D4B",
    "idle": "#83929D",
}
STATUS_TEXT = {
    "waiting": "等待处理",
    "connecting": "识别设备",
    "identified": "检查记录",
    "erasing": "擦除中",
    "flashing": "烧录中",
    "verifying": "校验中",
    "restarting": "重启中",
    "completed": "烧录完成",
    "skipped": "已是当前版本",
    "failed": "处理失败",
    "retry_wait": "等待重试",
}


class DeviceRow:
    def __init__(self, parent: tk.Widget, port: str, description: str):
        self.frame = tk.Frame(parent, bg=COLORS["surface"], height=58)
        self.frame.pack(fill="x")
        self.frame.pack_propagate(False)
        self.frame.grid_columnconfigure(4, weight=1)
        self.port = tk.Label(
            self.frame, text=port, bg=COLORS["surface"], fg=COLORS["text"],
            font=("Microsoft YaHei UI", 10, "bold"), anchor="w",
        )
        self.port.grid(row=0, column=0, sticky="w", padx=(16, 8), pady=(8, 0))
        self.description = tk.Label(
            self.frame, text=description, bg=COLORS["surface"], fg=COLORS["muted"],
            font=("Microsoft YaHei UI", 8), anchor="w",
        )
        self.description.grid(row=1, column=0, sticky="w", padx=(16, 8), pady=(0, 7))
        self.mac = tk.Label(
            self.frame, text="待读取", bg=COLORS["surface"], fg=COLORS["muted"],
            font=("Consolas", 10), width=20, anchor="w",
        )
        self.mac.grid(row=0, column=1, rowspan=2, sticky="w", padx=8)
        self.status = tk.Label(
            self.frame, text=STATUS_TEXT["waiting"], bg="#EAF0F2", fg=COLORS["idle"],
            font=("Microsoft YaHei UI", 9, "bold"), width=12, pady=4,
        )
        self.status.grid(row=0, column=2, rowspan=2, padx=8)
        self.progress = ttk.Progressbar(
            self.frame, style="Flash.Horizontal.TProgressbar", maximum=100, length=180
        )
        self.progress.grid(row=0, column=3, padx=(8, 4), sticky="ew")
        self.progress_text = tk.Label(
            self.frame, text="0%", bg=COLORS["surface"], fg=COLORS["muted"],
            font=("Consolas", 9), width=5,
        )
        self.progress_text.grid(row=0, column=4, sticky="w")
        self.message = tk.Label(
            self.frame, text="串口已接入", bg=COLORS["surface"], fg=COLORS["muted"],
            font=("Microsoft YaHei UI", 9), anchor="w",
        )
        self.message.grid(row=1, column=3, columnspan=2, padx=(8, 12), sticky="ew")
        self.attempt = tk.Label(
            self.frame, text="第 0 次", bg=COLORS["surface"], fg=COLORS["muted"],
            font=("Microsoft YaHei UI", 9), width=9,
        )
        self.attempt.grid(row=0, column=5, rowspan=2, padx=(4, 16))
        self.separator = tk.Frame(parent, bg=COLORS["line"], height=1)
        self.separator.pack(fill="x")

    def update(self, event: dict) -> None:
        status = event.get("status", "waiting")
        progress = int(event.get("progress", 0))
        if event.get("mac"):
            self.mac.configure(text=event["mac"])
        self.progress["value"] = progress
        self.progress_text.configure(text=f"{progress}%")
        self.message.configure(text=event.get("message", ""))
        self.attempt.configure(text=f"第 {event.get('attempt', 0)} 次")
        palette = {
            "completed": ("#E5F5EE", COLORS["success"]),
            "skipped": ("#E7F2F1", COLORS["accent"]),
            "failed": ("#FBEAEC", COLORS["danger"]),
            "retry_wait": ("#FFF3DD", COLORS["warning"]),
        }
        background, foreground = palette.get(status, ("#E7F2F1", COLORS["accent"]))
        self.status.configure(
            text=STATUS_TEXT.get(status, status), bg=background, fg=foreground
        )

    def destroy(self) -> None:
        self.frame.destroy()
        self.separator.destroy()


class BatchFlashApp:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.title("ESP32-S3 批量烧录台")
        self.root.geometry("1120x720")
        self.root.minsize(1000, 680)
        self.root.configure(bg=COLORS["background"])
        self.root.protocol("WM_DELETE_WINDOW", self._close)
        self.events: queue.Queue[dict] = queue.Queue()
        self.controller: BatchFlashController | None = None
        self.printer_client = T50ProClient()
        self.printer_queue: T50ProPrintQueue | None = None
        self.device_rows: dict[str, DeviceRow] = {}
        self.history_records: dict[str, dict] = {}
        self.monitoring = False
        self.active_count = 0
        self.printer_pending = 0
        self.printer_success = 0
        self.printer_failed = 0
        self._read_config()
        self._configure_styles()
        self._build_ui()
        self.root.after(80, self._poll_events)
        self.root.after(450, self.refresh_printers)
        if self.source_config.get("ui_auto_start", False):
            self.root.after(350, self.start_monitoring)

    def _read_config(self) -> None:
        with open(CONFIG_PATH, encoding="utf-8") as config_file:
            self.source_config = json.load(config_file)
        self.profiles = list(self.source_config.get("profiles", {}))
        self.printer_settings = dict(self.source_config.get("label_printer", {}))

    def _configure_styles(self) -> None:
        style = ttk.Style(self.root)
        style.theme_use("clam")
        style.configure(
            "Flash.Horizontal.TProgressbar",
            troughcolor="#E5ECEF",
            background=COLORS["accent"],
            bordercolor="#E5ECEF",
            lightcolor=COLORS["accent"],
            darkcolor=COLORS["accent"],
            thickness=9,
        )
        style.configure(
            "Primary.TButton",
            font=("Microsoft YaHei UI", 10, "bold"),
            foreground="white",
            background=COLORS["accent"],
            borderwidth=0,
            padding=(18, 10),
        )
        style.map("Primary.TButton", background=[("active", COLORS["accent_dark"])])
        style.configure(
            "Secondary.TButton",
            font=("Microsoft YaHei UI", 10),
            foreground=COLORS["text"],
            background="#EDF2F4",
            borderwidth=0,
            padding=(14, 10),
        )
        style.configure(
            "Tool.TCheckbutton",
            font=("Microsoft YaHei UI", 9),
            foreground=COLORS["text"],
            background=COLORS["surface"],
            padding=(2, 4),
        )
        style.map(
            "Tool.TCheckbutton",
            background=[("active", COLORS["surface"]), ("disabled", COLORS["surface"])],
        )
        style.configure(
            "Printer.TCheckbutton",
            font=("Microsoft YaHei UI", 9, "bold"),
            foreground=COLORS["text"],
            background="#F4F8F8",
            padding=(2, 4),
        )
        style.map(
            "Printer.TCheckbutton",
            background=[("active", "#F4F8F8"), ("disabled", "#F4F8F8")],
        )
        style.configure(
            "Records.Treeview",
            font=("Microsoft YaHei UI", 9),
            rowheight=31,
            background=COLORS["surface"],
            fieldbackground=COLORS["surface"],
            foreground=COLORS["text"],
            borderwidth=0,
        )
        style.configure(
            "Records.Treeview.Heading",
            font=("Microsoft YaHei UI", 9, "bold"),
            background="#EDF2F4",
            foreground=COLORS["muted"],
            relief="flat",
        )

    def _build_ui(self) -> None:
        header = tk.Frame(self.root, bg=COLORS["surface"], padx=24, pady=18)
        header.pack(fill="x")
        header.grid_columnconfigure(0, weight=1)
        title = tk.Frame(header, bg=COLORS["surface"])
        title.grid(row=0, column=0, sticky="w")
        tk.Label(
            title, text="ESP32-S3 批量烧录台", bg=COLORS["surface"],
            fg=COLORS["text"], font=("Microsoft YaHei UI", 18, "bold"),
        ).pack(anchor="w")
        self.summary_label = tk.Label(
            title, text="持续监听串口，自动识别、烧录、校验并重启",
            bg=COLORS["surface"], fg=COLORS["muted"],
            font=("Microsoft YaHei UI", 9),
        )
        self.summary_label.pack(anchor="w", pady=(4, 0))

        controls = tk.Frame(header, bg=COLORS["surface"])
        controls.grid(row=0, column=1, sticky="e")
        self.profile_var = tk.StringVar(
            value=self.source_config.get("default_profile", self.profiles[0])
        )
        self.parallel_var = tk.IntVar(value=int(self.source_config.get("max_parallel", 4)))
        self.force_var = tk.BooleanVar(value=False)
        self.print_label_var = tk.BooleanVar(
            value=bool(self.printer_settings.get("enabled", False))
        )
        self.printer_device_var = tk.StringVar(
            value=str(self.printer_settings.get("device_path", ""))
        )
        tk.Label(
            controls, text="烧录类型", bg=COLORS["surface"], fg=COLORS["muted"],
            font=("Microsoft YaHei UI", 9),
        ).grid(row=0, column=0, sticky="w")
        self.profile_combo = ttk.Combobox(
            controls, textvariable=self.profile_var, values=self.profiles,
            state="readonly", width=12,
        )
        self.profile_combo.grid(row=1, column=0, padx=(0, 10), pady=(4, 0))
        tk.Label(
            controls, text="并行数", bg=COLORS["surface"], fg=COLORS["muted"],
            font=("Microsoft YaHei UI", 9),
        ).grid(row=0, column=1, sticky="w")
        self.parallel_spin = ttk.Spinbox(
            controls, from_=1, to=16, textvariable=self.parallel_var, width=6
        )
        self.parallel_spin.grid(row=1, column=1, padx=(0, 10), pady=(4, 0))
        self.force_check = ttk.Checkbutton(
            controls, text="强制重刷", variable=self.force_var, style="Tool.TCheckbutton"
        )
        self.force_check.grid(row=1, column=2, padx=(0, 12), pady=(4, 0))
        self.start_button = ttk.Button(
            controls, text="开始监听", style="Primary.TButton", command=self.start_monitoring
        )
        self.start_button.grid(row=1, column=3, pady=(4, 0))
        self.stop_button = ttk.Button(
            controls, text="停止", style="Secondary.TButton", command=self.stop_monitoring,
            state="disabled",
        )
        self.stop_button.grid(row=1, column=4, padx=(8, 0), pady=(4, 0))

        printer_controls = tk.Frame(
            header,
            bg="#F4F8F8",
            padx=12,
            pady=9,
            highlightbackground=COLORS["line"],
            highlightthickness=1,
        )
        printer_controls.grid(row=1, column=0, columnspan=2, sticky="ew", pady=(16, 0))
        printer_controls.grid_columnconfigure(2, weight=1)
        self.print_label_check = ttk.Checkbutton(
            printer_controls,
            text="随烧录打印标签",
            variable=self.print_label_var,
            command=self._toggle_printer_controls,
            style="Printer.TCheckbutton",
        )
        self.print_label_check.grid(row=0, column=0, sticky="w")
        tk.Label(
            printer_controls, text="T50 Pro", bg="#F4F8F8",
            fg=COLORS["muted"], font=("Microsoft YaHei UI", 9, "bold"),
        ).grid(row=0, column=1, sticky="w", padx=(22, 8))
        self.printer_combo = ttk.Combobox(
            printer_controls,
            textvariable=self.printer_device_var,
            state="readonly",
            width=52,
        )
        self.printer_combo.grid(row=0, column=2, sticky="ew")
        self.printer_combo.bind("<<ComboboxSelected>>", self._printer_selected)
        self.printer_refresh_button = ttk.Button(
            printer_controls,
            text="重新检测",
            style="Secondary.TButton",
            command=self.refresh_printers,
        )
        self.printer_refresh_button.grid(row=0, column=3, padx=(8, 0))
        self.printer_status_label = tk.Label(
            printer_controls, text="等待检测", bg="#E5ECEF", fg=COLORS["idle"],
            font=("Microsoft YaHei UI", 9, "bold"), padx=10, pady=4,
        )
        self.printer_status_label.grid(row=0, column=4, padx=(8, 0))
        self._toggle_printer_controls()

        metrics_wrap = tk.Frame(self.root, bg=COLORS["background"])
        metrics_wrap.pack(fill="x", padx=24, pady=(12, 14))
        metrics = tk.Frame(
            metrics_wrap,
            bg=COLORS["surface"],
            highlightbackground=COLORS["line"],
            highlightthickness=1,
        )
        metrics.pack(fill="x")
        self.metric_values: dict[str, tk.Label] = {}
        labels = [
            ("connected", "连接设备", COLORS["text"]),
            ("active", "烧录中", COLORS["accent"]),
            ("success", "烧录成功", COLORS["success"]),
            ("failed", "烧录失败", COLORS["danger"]),
            ("skipped", "已跳过", COLORS["warning"]),
            ("printed", "标签成功", COLORS["success"]),
            ("print_failed", "标签失败", COLORS["danger"]),
        ]
        for column, (key, caption, color) in enumerate(labels):
            grid_column = column * 2
            cell = tk.Frame(metrics, bg=COLORS["surface"], padx=12, pady=10)
            cell.grid(row=0, column=grid_column, sticky="ew")
            metrics.grid_columnconfigure(grid_column, weight=1, uniform="metric")
            value = tk.Label(
                cell, text="0", bg=COLORS["surface"], fg=color,
                font=("Microsoft YaHei UI", 17, "bold"),
            )
            value.pack(anchor="center")
            tk.Label(
                cell, text=caption, bg=COLORS["surface"], fg=COLORS["muted"],
                font=("Microsoft YaHei UI", 9),
            ).pack(anchor="center", pady=(1, 0))
            self.metric_values[key] = value
            if column < len(labels) - 1:
                separator = tk.Frame(metrics, bg=COLORS["line"], width=1)
                separator.grid(row=0, column=grid_column + 1, sticky="ns", pady=10)

        content = tk.Frame(self.root, bg=COLORS["background"], padx=24)
        content.pack(fill="both", expand=True)
        content.grid_rowconfigure(2, weight=1)
        content.grid_columnconfigure(0, weight=1)
        section_title = tk.Frame(content, bg=COLORS["background"])
        section_title.grid(row=0, column=0, sticky="ew", pady=(0, 8))
        tk.Label(
            section_title, text="当前串口", bg=COLORS["background"], fg=COLORS["text"],
            font=("Microsoft YaHei UI", 12, "bold"),
        ).pack(side="left")
        self.monitor_badge = tk.Label(
            section_title, text="未监听", bg="#E5ECEF", fg=COLORS["idle"],
            font=("Microsoft YaHei UI", 9, "bold"), padx=10, pady=4,
        )
        self.monitor_badge.pack(side="right")

        device_panel = tk.Frame(
            content, bg=COLORS["surface"], highlightbackground=COLORS["line"],
            highlightthickness=1,
        )
        device_panel.grid(row=1, column=0, sticky="ew")
        self.device_container = tk.Frame(device_panel, bg=COLORS["surface"])
        self.device_container.pack(fill="x")
        self.empty_label = tk.Label(
            self.device_container, text="等待串口接入...",
            bg=COLORS["surface"], fg=COLORS["muted"],
            font=("Microsoft YaHei UI", 10), pady=22,
        )
        self.empty_label.pack(fill="x")

        lower = ttk.Panedwindow(content, orient="horizontal")
        lower.grid(row=2, column=0, sticky="nsew", pady=(14, 18))
        history_panel = tk.Frame(lower, bg=COLORS["surface"])
        log_panel = tk.Frame(lower, bg=COLORS["surface"])
        lower.add(history_panel, weight=3)
        lower.add(log_panel, weight=2)
        self._build_history(history_panel)
        self._build_log(log_panel)

    def _build_history(self, parent: tk.Frame) -> None:
        bar = tk.Frame(parent, bg=COLORS["surface"], padx=14, pady=10)
        bar.pack(fill="x")
        tk.Label(
            bar, text="烧录记录", bg=COLORS["surface"], fg=COLORS["text"],
            font=("Microsoft YaHei UI", 11, "bold"),
        ).pack(side="left")
        ttk.Button(
            bar, text="打开 CSV", style="Secondary.TButton", command=self._open_records
        ).pack(side="right")
        history_body = tk.Frame(parent, bg=COLORS["surface"])
        history_body.pack(fill="both", expand=True, padx=1, pady=(0, 1))
        columns = ("time", "com", "mac", "profile", "status", "duration")
        self.history = ttk.Treeview(
            history_body, columns=columns, show="headings", style="Records.Treeview"
        )
        headings = {
            "time": "时间", "com": "串口", "mac": "MAC", "profile": "类型",
            "status": "结果", "duration": "耗时",
        }
        widths = {"time": 145, "com": 62, "mac": 140, "profile": 72, "status": 78, "duration": 58}
        for column in columns:
            self.history.heading(column, text=headings[column])
            self.history.column(column, width=widths[column], minwidth=50, anchor="center")
        self.history.tag_configure("OK", foreground=COLORS["success"])
        self.history.tag_configure("FAIL", foreground=COLORS["danger"])
        self.history.tag_configure("SKIP", foreground=COLORS["warning"])
        scrollbar = ttk.Scrollbar(history_body, orient="vertical", command=self.history.yview)
        self.history.configure(yscrollcommand=scrollbar.set)
        scrollbar.pack(side="right", fill="y")
        self.history.pack(side="left", fill="both", expand=True)
        self.history.bind("<Button-3>", self._show_history_menu)
        self.history_menu = tk.Menu(
            self.root,
            tearoff=False,
            bg=COLORS["surface"],
            fg=COLORS["text"],
            activebackground=COLORS["accent"],
            activeforeground="white",
            relief="solid",
            borderwidth=1,
            font=("Microsoft YaHei UI", 9),
        )
        self.history_menu.add_command(
            label="重新打印标签", command=self._reprint_selected_label
        )

    def _build_log(self, parent: tk.Frame) -> None:
        bar = tk.Frame(parent, bg=COLORS["surface"], padx=14, pady=10)
        bar.pack(fill="x")
        tk.Label(
            bar, text="运行日志", bg=COLORS["surface"], fg=COLORS["text"],
            font=("Microsoft YaHei UI", 11, "bold"),
        ).pack(side="left")
        ttk.Button(
            bar, text="清空", style="Secondary.TButton", command=self._clear_log
        ).pack(side="right")
        self.log_text = tk.Text(
            parent, bg="#152630", fg="#D7E3E8", insertbackground="white",
            font=("Consolas", 9), relief="flat", padx=12, pady=10,
            width=46, height=10, wrap="word", state="disabled",
        )
        self.log_text.pack(fill="both", expand=True)

    def start_monitoring(self) -> None:
        if self.monitoring or (self.controller and self.controller.active_count):
            return
        if self.printer_pending or (
            self.printer_queue and self.printer_queue.pending_count
        ):
            messagebox.showinfo(
                "标签尚未打印完成",
                "请等待当前标签队列处理完成后再开始下一批。",
                parent=self.root,
            )
            return
        try:
            config = load_profile(CONFIG_PATH, self.profile_var.get())
            parallel = max(1, int(self.parallel_var.get()))
        except Exception as exc:
            messagebox.showerror("无法启动烧录", str(exc), parent=self.root)
            return

        mac_callback = None
        if self.printer_queue:
            self.printer_queue.close(wait=False)
            self.printer_queue = None
        if self.print_label_var.get():
            if config["_profile"] != "cabinet":
                messagebox.showerror(
                    "标签类型不匹配",
                    "CAB_ 标签仅适用于 cabinet 烧录类型。请切换类型或取消打印标签。",
                    parent=self.root,
                )
                return
            device_path = self.printer_device_var.get().strip()
            if not device_path:
                messagebox.showerror(
                    "未连接 T50 Pro",
                    "没有检测到可用的 T50 Pro。请先连接打印机并点击“重新检测”。",
                    parent=self.root,
                )
                return
            try:
                status = self.printer_client.get_status(device_path)
            except T50ProError as exc:
                messagebox.showerror("T50 Pro 未就绪", str(exc), parent=self.root)
                return
            state_name = status.get("state_name", "")
            if state_name != "Waiting":
                description = status.get("description") or state_name or "未知状态"
                messagebox.showerror(
                    "T50 Pro 未就绪", f"打印机当前状态：{description}", parent=self.root
                )
                return
            self.printer_queue = T50ProPrintQueue(
                self.printer_client, device_path, self.printer_settings, self.events.put
            )
            mac_callback = self.printer_queue.submit
            self.printer_pending = 0
            self.printer_success = 0
            self.printer_failed = 0
            self._update_printer_metrics()
            self._set_printer_status("打印已启用", "ready")
        self.controller = BatchFlashController(
            config,
            self.events.put,
            force=self.force_var.get(),
            max_parallel=parallel,
            mac_callback=mac_callback,
        )
        self._load_records()
        self.monitoring = True
        self._set_controls(True)
        digest = config["_firmware_sha256"][:10].upper()
        self.summary_label.configure(
            text=f"{config['_profile']} · {config['_firmware']} · 固件指纹 {digest}"
        )
        self.controller.start()

    def stop_monitoring(self) -> None:
        if self.controller:
            self.controller.stop()
        self.stop_button.configure(state="disabled")

    def _set_controls(self, running: bool) -> None:
        self.start_button.configure(state="disabled" if running else "normal")
        self.stop_button.configure(state="normal" if running else "disabled")
        self.profile_combo.configure(state="disabled" if running else "readonly")
        self.parallel_spin.configure(state="disabled" if running else "normal")
        self.force_check.configure(state="disabled" if running else "normal")
        self.print_label_check.configure(state="disabled" if running else "normal")
        self.printer_refresh_button.configure(state="disabled" if running else "normal")
        if running or not self.print_label_var.get():
            self.printer_combo.configure(state="disabled")
        else:
            self.printer_combo.configure(state="readonly")

    def _poll_events(self) -> None:
        try:
            while True:
                self._handle_event(self.events.get_nowait())
        except queue.Empty:
            pass
        self.root.after(80, self._poll_events)

    def _handle_event(self, event: dict) -> None:
        event_type = event["type"]
        if event_type == "monitor":
            self.monitoring = bool(event["running"])
            self.monitor_badge.configure(
                text="自动监听中" if self.monitoring else "已停止",
                bg="#E5F5EE" if self.monitoring else "#E5ECEF",
                fg=COLORS["success"] if self.monitoring else COLORS["idle"],
            )
            if not self.monitoring and self.active_count == 0:
                self._set_controls(False)
        elif event_type == "port_added":
            self._add_device(event["port"], event.get("description", "串口设备"))
        elif event_type == "port_removed":
            self._remove_device(event["port"])
        elif event_type == "device":
            row = self.device_rows.get(event["port"])
            if row is None:
                self._add_device(event["port"], "串口设备")
                row = self.device_rows[event["port"]]
            row.update(event)
        elif event_type == "record":
            self._insert_record(event["record"], at_top=True)
        elif event_type == "stats":
            self.active_count = event["active"]
            for key in ("connected", "active", "success", "failed", "skipped"):
                self.metric_values[key].configure(text=str(event.get(key, 0)))
            if not self.monitoring and self.active_count == 0:
                self._set_controls(False)
        elif event_type == "log":
            self._append_log(event)
        elif event_type == "printer_devices":
            self._handle_printer_devices(event)
        elif event_type == "printer":
            self._handle_printer_event(event)

    def _toggle_printer_controls(self) -> None:
        if not hasattr(self, "printer_combo"):
            return
        enabled = self.print_label_var.get() and not self.monitoring
        self.printer_combo.configure(state="readonly" if enabled else "disabled")

    def refresh_printers(self) -> None:
        if self.monitoring:
            return
        self.printer_refresh_button.configure(state="disabled")
        self._set_printer_status("正在检测...", "checking")

        def discover() -> None:
            try:
                devices = self.printer_client.list_devices()
                self.events.put({"type": "printer_devices", "devices": devices})
            except Exception as exc:
                self.events.put({"type": "printer_devices", "devices": [], "error": str(exc)})

        threading.Thread(target=discover, name="t50pro-discovery", daemon=True).start()

    def _handle_printer_devices(self, event: dict) -> None:
        devices = event.get("devices") or []
        self.printer_combo.configure(values=devices)
        current = self.printer_device_var.get()
        if current not in devices:
            self.printer_device_var.set(devices[0] if devices else "")
        if event.get("error"):
            self._set_printer_status("检测失败", "error")
            self._append_log({
                "time": event.get("time", ""),
                "message": f"T50 Pro 检测失败: {event['error']}",
            })
        elif devices:
            self._set_printer_status("已连接", "ready")
        else:
            self._set_printer_status("未检测到", "idle")
        if not self.monitoring:
            self.printer_refresh_button.configure(state="normal")
            self._toggle_printer_controls()

    def _printer_selected(self, _event=None) -> None:
        if self.printer_device_var.get().strip():
            self._set_printer_status("已连接", "ready")

    def _handle_printer_event(self, event: dict) -> None:
        status = event.get("status", "")
        label = event.get("label", "")
        if status == "queued":
            self.printer_pending += 1
            message = f"标签已入队: {label}"
        elif status == "printing":
            self._set_printer_status("打印中", "checking")
            message = f"正在打印标签: {label}"
        elif status == "retrying":
            self._set_printer_status("等待重试", "checking")
            message = (
                f"标签打印重试 {label}: {event.get('error', '打印机暂时未就绪')}"
            )
        elif status == "printed":
            self.printer_pending = max(0, self.printer_pending - 1)
            self.printer_success += 1
            self._set_printer_status("打印完成", "ready")
            message = f"标签打印完成: {label}"
        elif status == "failed":
            self.printer_pending = max(0, self.printer_pending - 1)
            self.printer_failed += 1
            self._set_printer_status("打印失败", "error")
            message = f"标签打印失败 {label}: {event.get('error', '未知错误')}"
        else:
            message = f"标签状态 {status}: {label}"
        self._update_printer_metrics()
        self._append_log({
            "time": event.get("time", ""),
            "port": event.get("port", ""),
            "message": message,
        })

    def _update_printer_metrics(self) -> None:
        if "printed" in self.metric_values:
            self.metric_values["printed"].configure(text=str(self.printer_success))
        if "print_failed" in self.metric_values:
            self.metric_values["print_failed"].configure(text=str(self.printer_failed))

    def _set_printer_status(self, text: str, tone: str) -> None:
        palette = {
            "ready": ("#E5F5EE", COLORS["success"]),
            "checking": ("#E7F2F1", COLORS["accent"]),
            "error": ("#FBEAEC", COLORS["danger"]),
            "idle": ("#E5ECEF", COLORS["idle"]),
        }
        background, foreground = palette.get(tone, palette["idle"])
        self.printer_status_label.configure(text=text, bg=background, fg=foreground)

    def _add_device(self, port: str, description: str) -> None:
        if port in self.device_rows:
            return
        self.empty_label.pack_forget()
        self.device_rows[port] = DeviceRow(self.device_container, port, description)

    def _remove_device(self, port: str) -> None:
        row = self.device_rows.pop(port, None)
        if row:
            row.destroy()
        if not self.device_rows:
            self.empty_label.pack(fill="x")

    def _load_records(self) -> None:
        for item in self.history.get_children():
            self.history.delete(item)
        self.history_records.clear()
        if self.controller:
            for record in self.controller.recent_records(100):
                self._insert_record(record)

    def _insert_record(self, record: dict, at_top: bool = False) -> None:
        status_map = {"OK": "成功", "FAIL": "失败", "SKIP": "已烧录"}
        values = (
            record.get("time", ""),
            record.get("com", ""),
            (record.get("mac") or "").upper(),
            record.get("profile", ""),
            status_map.get(record.get("status", ""), record.get("status", "")),
            f"{record.get('duration_s', '')}s",
        )
        item_id = self.history.insert(
            "", 0 if at_top else "end", values=values, tags=(record.get("status", ""),)
        )
        self.history_records[item_id] = dict(record)

    def _show_history_menu(self, event) -> None:
        item_id = self.history.identify_row(event.y)
        if not item_id:
            return
        self.history.selection_set(item_id)
        self.history.focus(item_id)
        record = self.history_records.get(item_id, {})
        can_print = bool(record.get("mac")) and record.get("profile") == "cabinet"
        self.history_menu.entryconfigure(
            "重新打印标签", state="normal" if can_print else "disabled"
        )
        try:
            self.history_menu.tk_popup(event.x_root, event.y_root)
        finally:
            self.history_menu.grab_release()

    def _reprint_selected_label(self) -> None:
        selection = self.history.selection()
        if not selection:
            return
        record = self.history_records.get(selection[0], {})
        mac = str(record.get("mac", "")).strip()
        if not mac or record.get("profile") != "cabinet":
            messagebox.showerror(
                "无法打印标签", "所选记录不是有效的 cabinet 烧录记录。", parent=self.root
            )
            return
        device_path = self.printer_device_var.get().strip()
        if not device_path:
            messagebox.showerror(
                "未连接 T50 Pro",
                "没有检测到可用的 T50 Pro。请先点击“重新检测”。",
                parent=self.root,
            )
            return
        try:
            status = self.printer_client.get_status(device_path)
            if status.get("state_name") != "Waiting":
                description = status.get("description") or status.get("state_name")
                raise T50ProError(f"打印机当前状态：{description or '未知'}")
            printer_queue = self._printer_queue_for(device_path)
            if not printer_queue.submit(
                str(record.get("com", "记录")), mac, allow_duplicate=True
            ):
                raise T50ProError("打印队列已经关闭，请重试")
        except (T50ProError, ValueError) as exc:
            messagebox.showerror("标签打印失败", str(exc), parent=self.root)
            return
        self._set_printer_status("已加入队列", "checking")

    def _printer_queue_for(self, device_path: str) -> T50ProPrintQueue:
        if self.printer_queue and not self.printer_queue.closed:
            if self.printer_queue.device_path == device_path:
                return self.printer_queue
            if self.printer_queue.pending_count:
                raise T50ProError("当前打印队列尚未完成，请稍后再更换打印机")
            self.printer_queue.close(wait=False)
        self.printer_queue = T50ProPrintQueue(
            self.printer_client,
            device_path,
            self.printer_settings,
            self.events.put,
        )
        return self.printer_queue

    def _append_log(self, event: dict) -> None:
        clock = event.get("time", "")[-8:]
        port = f" [{event['port']}]" if event.get("port") else ""
        line = f"{clock}{port}  {event.get('message', '')}\n"
        self.log_text.configure(state="normal")
        self.log_text.insert("end", line)
        self.log_text.see("end")
        self.log_text.configure(state="disabled")

    def _clear_log(self) -> None:
        self.log_text.configure(state="normal")
        self.log_text.delete("1.0", "end")
        self.log_text.configure(state="disabled")

    def _open_records(self) -> None:
        try:
            config = load_profile(CONFIG_PATH, self.profile_var.get())
            path = config["records_csv"]
            if not os.path.exists(path):
                messagebox.showinfo("烧录记录", "还没有生成烧录记录。", parent=self.root)
                return
            os.startfile(path)
        except Exception as exc:
            messagebox.showerror("无法打开记录", str(exc), parent=self.root)

    def _close(self) -> None:
        if self.controller and self.controller.active_count:
            messagebox.showwarning(
                "正在烧录",
                "请等待当前设备烧录完成后再关闭，避免中断固件写入。",
                parent=self.root,
            )
            return
        if self.printer_pending:
            messagebox.showwarning(
                "正在打印",
                "请等待标签打印完成后再关闭，避免漏打标签。",
                parent=self.root,
            )
            return
        if self.controller:
            self.controller.stop()
        if self.printer_queue:
            self.printer_queue.close(wait=False)
        self.root.destroy()


def main() -> None:
    root = tk.Tk()
    BatchFlashApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
