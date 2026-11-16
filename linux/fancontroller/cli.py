"""CLI stays usable on headless Debian systems without importing GTK."""

import argparse
import html
import json
from pathlib import Path
import shutil
import subprocess
import sys

from . import APP_NAME, __version__
from .backend import Backend


CONTROL_NAMES = ("profile", "conservation", "usb_charging", "fn_lock", "keyboard_backlight")
HELPER = "/usr/libexec/fancontroller-helper"


def change_control(name, value):
    if name not in CONTROL_NAMES:
        raise ValueError("不支持的设备设置")
    control = Backend().controls().get(name, {})
    if not control.get("available"):
        raise ValueError(control.get("reason") or "内核未提供此设备设置")
    if not isinstance(value, str) or value not in control.get("choices", []):
        raise ValueError("无效的设备设置值")
    if not Path(HELPER).is_file():
        raise RuntimeError("请先安装 .deb 软件包，设备设置需要已安装的授权助手")
    if not shutil.which("pkexec"):
        raise RuntimeError("未安装 pkexec；请运行 sudo apt install pkexec")
    result = subprocess.run(["/usr/bin/pkexec", HELPER, name, value], text=True,
                            capture_output=True, timeout=120, check=False)
    if result.returncode != 0:
        if result.returncode in (126, 127):
            raise RuntimeError("管理员授权未完成或已取消")
        try:
            error = json.loads(result.stdout).get("error")
        except (ValueError, AttributeError):
            error = None
        raise RuntimeError(error or result.stderr.strip() or "设备设置失败")
    try:
        response = json.loads(result.stdout)
    except ValueError as exc:
        raise RuntimeError("设备设置助手返回无效响应") from exc
    if not isinstance(response, dict) or "success" not in response:
        raise RuntimeError("设备设置助手返回无效响应")
    if response.get("success") is not True:
        raise RuntimeError(response.get("error", "设备设置失败"))
    if response.get("result") != value:
        raise RuntimeError("设备设置助手未返回有效的内核状态")
    return str(response["result"])


def export_report(snapshot, destination):
    path = Path(destination)
    report = json.dumps(snapshot, ensure_ascii=False, indent=2)
    if path.suffix.lower() in (".html", ".htm"):
        report = ('<!doctype html><html lang="zh-CN"><meta charset="utf-8">'
                  f'<meta name="viewport" content="width=device-width"><title>{APP_NAME} 硬件报告</title>'
                  '<style>body{font:16px system-ui;max-width:1100px;margin:40px auto;padding:20px}'
                  'pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#f3f4f6;padding:24px}</style>'
                  f'<h1>{APP_NAME} 硬件报告</h1><pre>'
                  + html.escape(report) + '</pre></html>')
    elif path.suffix.lower() != ".json":
        raise ValueError("报告文件扩展名应为 .json 或 .html")
    path.write_text(report + "\n", encoding="utf-8")


def main(argv=None):
    parser = argparse.ArgumentParser(description=f"{APP_NAME} — 小新 Air 14 Debian 硬件管理工具（Wayland / X11）")
    parser.add_argument("--version", action="version", version=APP_NAME + " " + __version__)
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--status", action="store_true", help="在终端查看设备状态")
    group.add_argument("--json", action="store_true", help="以 JSON 输出当前硬件报告")
    group.add_argument("--export", metavar="PATH", help="导出 JSON 或 HTML 硬件报告")
    group.add_argument("--set", nargs=2, metavar=("NAME", "VALUE"), help="设置 profile/conservation/usb_charging/fn_lock/keyboard_backlight；需要管理员授权")
    group.add_argument("--smoke-test", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args(argv)
    try:
        if args.set:
            print(change_control(*args.set))
            return 0
        if args.status or args.json or args.export:
            snapshot = Backend().snapshot()
            if args.export:
                export_report(snapshot, args.export)
                print("报告已导出：" + args.export)
            elif args.json:
                print(json.dumps(snapshot, ensure_ascii=False, indent=2))
            else:
                identity = snapshot["identity"]
                print(f"{APP_NAME} {__version__} · {identity.get('os', '')}")
                print(f"{identity.get('manufacturer', '')} {identity.get('model', '')} / {identity.get('bios', '')}")
                for name, control in snapshot["controls"].items():
                    print(f"{name}: {control['value'] if control['available'] else control['reason']}")
                for item in snapshot["health"]:
                    print(f"[{item['level']}] {item['name']}: {item['detail']}")
            return 0
        from .gui import run
        return run(smoke_test=args.smoke_test)
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError, ImportError) as exc:
        print(f"{APP_NAME}: {exc}", file=sys.stderr)
        return 1
