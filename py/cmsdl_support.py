#!/usr/bin/env python3
"""cmsdl（HikariCalyx/cmsdl）支持模块 —— 由 wz_worker.py 以命令形式调用。

本模块**只做不联网的三件事**：
  1. 在本机定位 cmsdl 可执行文件（显式传入 → 环境变量 WZ_CMSDL → 默认目录 → PATH）；
  2. 跑 `cmsdl --version` / `cmsdl --help`，把**这个版本真实支持的开关面**解析出来；
  3. 按解析到的开关面拼 argv 并做安全判定（绝不在未显式确认时指向 WZ 数据源目录）。

为什么不硬编码参数：cmsdl 的开关逐版本在变（v0.2.5 的 `--download-wz-only`、
`--get-bit-torrent`、`--json` 在更早版本并不存在），硬编码会拼出它不认的命令行。
所以一律「先 --help，再照 surface 拼」。

真实的下载/执行与进度汇报由 host 半边负责（sibling 模块），本模块不 spawn 长任务。

v0.2.5 实测的用法（`./cmsdl --help` 原文）：
  Usage: cmsdl [OPTIONS] <--check|--download <PATH|URL>|--get-bit-torrent|
               --patch <VERSION|list>|--create-shortcut <PATH>> <REGION> [CLIENT_DIR]
  REGION: cms(国服 冒险岛Online) / tms(台服 新楓之谷) / manual(签名 CDN URL 单文件)
"""
from __future__ import annotations

import os
import re
import shutil
import subprocess
from typing import Any, Dict, List, Optional, Tuple

# ── 定位 cmsdl ────────────────────────────────────────────────────────
CMSDL_DIR = os.environ.get("WZ_CMSDL_DIR") or os.path.expanduser("~/.dsh/wz-assets/cmsdl")
_EXE = "cmsdl.exe" if os.name == "nt" else "cmsdl"

#: 默认下载存放目录（用户可写，**不是** WZ 数据源目录）
DEFAULT_TARGET_DIR = os.path.join(CMSDL_DIR, "client")

#: 探测超时：--version/--help 都是本地输出，2 秒足够
PROBE_TIMEOUT = 5.0

#: WZ 数据源目录（与 worker 的 DATA_DIR 同一环境变量，避免两处定义漂移）
DATA_DIR = os.environ.get("WZ_DATA_DIR") or "/Volumes/SSD/mxd/mxd/Data"


def _candidates(override: str = "") -> List[Tuple[str, str]]:
    """返回 [(绝对路径, 来源说明)]，按优先级排序。"""
    out: List[Tuple[str, str]] = []
    if override:
        out.append((os.path.expanduser(override), "调用方指定"))
    env = (os.environ.get("WZ_CMSDL") or "").strip()
    if env:
        out.append((os.path.expanduser(env), "环境变量 WZ_CMSDL"))
    out.append((os.path.join(CMSDL_DIR, _EXE), f"插件默认目录 {CMSDL_DIR}"))
    # 旧版本/别处落下的名字（gitee release 资产原名叫 cmsdl_darwin）
    out.append((os.path.join(CMSDL_DIR, "cmsdl_darwin"), f"{CMSDL_DIR}/cmsdl_darwin"))
    for name in ("/opt/homebrew/bin/cmsdl", "/usr/local/bin/cmsdl"):
        out.append((name, "常见安装路径"))
    which = shutil.which("cmsdl")
    if which:
        out.append((which, "PATH"))
    # 去重保序
    seen = set()
    uniq: List[Tuple[str, str]] = []
    for path, source in out:
        if path and path not in seen:
            seen.add(path)
            uniq.append((path, source))
    return uniq


def find_cmsdl(override: str = "") -> Tuple[Optional[str], str, List[Dict[str, str]]]:
    """找到第一个「存在且可执行」的 cmsdl。返回 (路径|None, 来源, 全部候选及状态)。"""
    checked: List[Dict[str, str]] = []
    for path, source in _candidates(override):
        exists = os.path.isfile(path)
        executable = exists and os.access(path, os.X_OK)
        checked.append({
            "path": path,
            "source": source,
            "exists": exists,
            "executable": executable,
        })
        if exists:
            if executable:
                return path, source, checked
            # 存在但没执行位：尝试补上（gitee 下载/解压后常见）
            try:
                mode = os.stat(path).st_mode
                os.chmod(path, mode | 0o755)
                if os.access(path, os.X_OK):
                    checked[-1]["executable"] = True
                    checked[-1]["note"] = "原本没有执行位，已 chmod +x"
                    return path, source, checked
            except OSError as exc:  # noqa: BLE001
                checked[-1]["note"] = f"chmod +x 失败：{exc}"
    return None, "", checked


def _run(argv: List[str], timeout: float = PROBE_TIMEOUT) -> Dict[str, Any]:
    """同步跑一条命令并抓取输出。**只用于 --version/--help 这类秒回命令。**"""
    try:
        proc = subprocess.run(
            argv,
            capture_output=True,
            text=True,
            timeout=timeout,
            stdin=subprocess.DEVNULL,
        )
        return {
            "argv": argv,
            "exit_code": proc.returncode,
            "stdout": proc.stdout or "",
            "stderr": proc.stderr or "",
            "ok": True,
        }
    except subprocess.TimeoutExpired:
        return {"argv": argv, "exit_code": None, "stdout": "", "stderr": f"超时（>{timeout}s）",
                "ok": False, "error": "timeout"}
    except OSError as exc:
        return {"argv": argv, "exit_code": None, "stdout": "", "stderr": str(exc),
                "ok": False, "error": type(exc).__name__}


# ── 解析 --help，得到「这个版本真实支持什么」──────────────────────────
_OPT_HEAD = re.compile(r"^\s{2,}(?:(-[A-Za-z]),\s+)?(--[a-z][a-z0-9-]*)\s*(.*)$")
_REGION_VAL = re.compile(r"^\s+-\s+([a-z0-9_-]+):\s*(.*)$")


def parse_help(text: str) -> Dict[str, Any]:
    """把 `cmsdl --help` 的输出解析成结构化开关面。

    不追求完美还原 clap：只抽出我们拼命令行真正需要的四样东西
    —— 长开关集合、哪些开关吃值、区服取值、互斥的模式开关。
    """
    flags: Dict[str, Dict[str, Any]] = {}
    in_options = False
    last: Optional[str] = None

    for raw in text.splitlines():
        line = raw.rstrip()
        if line.strip() == "Options:":
            in_options = True
            continue
        if not in_options:
            continue
        head = _OPT_HEAD.match(line)
        if head:
            _short, long_name, rest = head.group(1), head.group(2), (head.group(3) or "").strip()
            takes_value = bool(re.match(r"^(=)?<[^>]+>", rest))
            optional_value = bool(re.match(r"^\[=<?", rest))
            flags[long_name] = {
                "flag": long_name,
                "takes_value": takes_value or optional_value,
                "optional_value": optional_value,
                "value_spec": (re.match(r"^\[?=?<?([^>\]]*)>?\]?", rest).group(1) or "").strip("[]<>= "),
                "description": "",
            }
            last = long_name
            continue
        stripped = line.strip()
        if last and stripped and not stripped.startswith("-") and not line.startswith("  <"):
            # 描述首行（缩进比开关头更深）
            if not flags[last]["description"]:
                flags[last]["description"] = stripped

    # 区服取值：<REGION> 段里的 "Possible values:" 列表
    regions: List[Dict[str, str]] = []
    in_region_values = False
    for raw in text.splitlines():
        if "Possible values:" in raw:
            in_region_values = True
            continue
        if in_region_values:
            m = _REGION_VAL.match(raw.rstrip())
            if m:
                regions.append({"value": m.group(1), "description": m.group(2).strip()})
                continue
            if raw.strip() and not raw.startswith("          "):
                in_region_values = False

    # 互斥模式开关：从 Usage 行的 <--a|--b <X>|...> 组里取
    modes: List[str] = []
    usage_match = re.search(r"<((?:--[^<>]*(?:<[^<>]*>)?[^<>]*\|?)+)>", text)
    if usage_match:
        for part in usage_match.group(1).split("|"):
            m = re.search(r"--[a-z][a-z0-9-]*", part)
            if m and m.group(0) not in modes:
                modes.append(m.group(0))

    return {
        "flags": flags,
        "flag_names": sorted(flags),
        "regions": regions,
        "modes": modes,
    }


def _help_for(path: str) -> Dict[str, Any]:
    res = _run([path, "--help"])
    text = (res.get("stdout") or "") + (res.get("stderr") or "")
    surface = parse_help(text)
    surface["help_text"] = text
    surface["exit_code"] = res.get("exit_code")
    return surface


def _version_of(path: str) -> Dict[str, Any]:
    res = _run([path, "--version"])
    text = ((res.get("stdout") or "") + (res.get("stderr") or "")).strip()
    m = re.search(r"(\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?)", text)
    return {"raw": text, "version": m.group(1) if m else "", "exit_code": res.get("exit_code")}


def cmd_cmsdl_probe(args: Dict[str, Any]) -> Dict[str, Any]:
    """命令 cmsdl_probe：本机有没有 cmsdl、是哪个版本、支持哪些开关。

    不联网。找不到就如实返回 found=false 与全部候选（面板据此给「下载 cmsdl」按钮）。
    """
    override = str(args.get("path") or "").strip()
    path, source, checked = find_cmsdl(override)
    base = {
        "found": False,
        "path": None,
        "source": "",
        "candidates": checked,
        "cmsdl_dir": CMSDL_DIR,
        "default_target_dir": DEFAULT_TARGET_DIR,
        "data_dir": DATA_DIR,
        "exe_name": _EXE,
        "platform": os.name,
        "note": "",
    }
    if not path:
        base["note"] = (
            f"本机没有 cmsdl。默认会找 {os.path.join(CMSDL_DIR, _EXE)}，"
            "也可以在面板里下载 cmsdl_darwin，或指定已有路径。"
        )
        return base

    version = _version_of(path)
    surface = _help_for(path)
    base.update({
        "found": True,
        "path": path,
        "source": source,
        "size": os.path.getsize(path) if os.path.isfile(path) else 0,
        "executable": os.access(path, os.X_OK),
        "version": version.get("version") or "",
        "version_raw": version.get("raw") or "",
        "flags": surface["flag_names"],
        "flag_detail": surface["flags"],
        "regions": surface["regions"],
        "modes": surface["modes"],
        "help_text": surface["help_text"],
    })
    if not surface["flags"]:
        base["note"] = "cmsdl 能执行但 --help 没解析出任何开关，面板会退化成「只让用户填参数」。"
    elif not surface["regions"]:
        base["note"] = "cmsdl --help 里没解析到区服取值，区服列表会由 host 侧的兜底值给出。"
    return base


# ── argv 拼装 + 安全判定 ──────────────────────────────────────────────
def _path_relation(target: str, data_dir: str) -> str:
    """target 与 WZ 数据源目录的关系：same / inside / parent / other。"""
    t = os.path.realpath(target)
    d = os.path.realpath(data_dir)
    if t == d:
        return "same"
    if t.startswith(d.rstrip(os.sep) + os.sep):
        return "inside"
    if d.startswith(t.rstrip(os.sep) + os.sep):
        return "parent"
    return "other"


_RELATION_TEXT = {
    "same": "就是 WZ 数据源目录本身",
    "inside": "在 WZ 数据源目录里面",
    "parent": "是 WZ 数据源目录的上级目录",
}


def cmd_cmsdl_plan(args: Dict[str, Any]) -> Dict[str, Any]:
    """命令 cmsdl_plan：拼出一条**将通过 --help 校验过**的 cmsdl 命令行，并做安全判定。

    只拼不跑。host 侧拿到 argv 后原样打印再 spawn，保证「看到的就是跑的」。

    安全规则（任何一条不满足就进 blocked，host 必须拒绝执行）：
      * 目标目录落在 WZ 数据源目录内/外/上级 —— 必须有 allow_data_dir=true；
      * `--purge-wz-files` 会删 Data 下不在清单里的文件 —— 必须 allow_data_dir=true 且 purge=true；
      * `--launch-after-patching` 会拉起游戏 —— 服务端场景必须显式 launch_after_patching=true；
      * extra_args 里每个开关都必须在 --help 解析出的开关面里。
    """
    region = str(args.get("region") or "cms").strip().lower()
    mode = str(args.get("mode") or "download").strip().lower()
    target_dir = str(args.get("target_dir") or DEFAULT_TARGET_DIR).strip()
    target_dir = os.path.expanduser(target_dir or DEFAULT_TARGET_DIR)
    # 游戏客户端目录（CLIENT_DIR 位置参数）。官方用法：
    #   cmsdl [OPTIONS] <--download <PATH|URL>|...> <REGION> [CLIENT_DIR]
    # 对 CMS，WZ 落在 <CLIENT_DIR>/mxd/Data；对 TMS 落在 <CLIENT_DIR>/Data。
    # 之前这里**没传** CLIENT_DIR，等于把 --download 的 PATH 当成了客户端目录，
    # 所以「下载到指定客户端」这条路是缺的（用户要求：能选游戏客户端位置并下到那里）。
    client_dir = str(args.get("client_dir") or "").strip()
    client_dir = os.path.expanduser(client_dir) if client_dir else ""
    wz_subdir = "mxd/Data" if region == "cms" else "Data"
    effective_target = os.path.join(client_dir, wz_subdir) if client_dir else target_dir
    allow_data_dir = bool(args.get("allow_data_dir"))
    purge = bool(args.get("purge"))
    launch_after_patching = bool(args.get("launch_after_patching"))
    wz_only = bool(args.get("wz_only"))
    patch_version = str(args.get("patch_version") or "latest").strip()
    torrent_out = str(args.get("torrent_out") or "").strip()
    build = args.get("build")
    build_since = args.get("build_since")
    filter_spec = str(args.get("filter") or "").strip()
    proxy = args.get("proxy")
    insecure = bool(args.get("insecure"))
    verbose = int(args.get("verbose") or 0)
    extra_args = args.get("extra_args") or []
    if isinstance(extra_args, str):
        extra_args = [t for t in extra_args.split() if t]

    blocked: List[str] = []
    warnings: List[str] = []

    # 1) 二进制必须可用，且必须拿到开关面（否则一律不拼，交给用户手填）
    path, source, _checked = find_cmsdl(str(args.get("path") or "").strip())
    if not path:
        return {
            "ok": False,
            "argv": [],
            "blocked": [f"本机没有可执行的 cmsdl（默认找 {os.path.join(CMSDL_DIR, _EXE)}）。"],
            "warnings": [],
            "mode": mode,
            "region": region,
        }
    surface = _help_for(path)
    flags = surface["flags"]
    if not flags:
        return {
            "ok": False,
            "argv": [],
            "blocked": ["cmsdl --help 没解析出任何开关，拒绝硬拼命令行。"],
            "warnings": [],
            "mode": mode,
            "region": region,
            "help_text": surface["help_text"],
        }

    unknown = [a for a in extra_args if isinstance(a, str) and a.startswith("--")
               and a.split("=")[0] not in flags]
    if unknown:
        blocked.append(f"这些开关不在本机 cmsdl 的 --help 里：{' '.join(unknown)}")

    # 2) 区服
    known_regions = [r["value"] for r in surface["regions"]]
    if known_regions and region not in known_regions:
        blocked.append(f"区服 {region!r} 不在 cmsdl 支持的取值里：{', '.join(known_regions)}")

    # 3) 目标目录安全判定。只有**会写目录**的模式才拦（check/patch_list 只读）。
    write_mode = mode in ("download", "patch")
    relation = _path_relation(effective_target, DATA_DIR)
    if write_mode:
        if relation != "other" and not allow_data_dir:
            blocked.append(
                f"目标目录 {effective_target} {_RELATION_TEXT[relation]}（{DATA_DIR}）。"
                "要写它必须显式打开「允许写入数据源目录」。"
            )
        if relation != "other":
            warnings.append(
                f"目标目录 {effective_target} {_RELATION_TEXT[relation]}；cmsdl 会在这里增删 WZ 文件，确认已备份再继续。"
            )

    # 4) 拼 argv。
    #    顺序严格照 README 里给过的例子，不依赖 clap 把选项和位置参数混着认：
    #      cmsdl cms --check
    #      cmsdl cms --download /path/to/cms/client
    #      cmsdl cms --patch list
    #      cmsdl cms --patch latest /path/to/cms/client
    #    即 [全局开关] <REGION> <模式参数…>；全局开关放最前，clap 到处都收。
    opts: List[str] = []
    mode_args: List[str] = []

    def add_flag(name: str, value: Optional[str] = None) -> bool:
        """只有本机 --help 里真有这个开关才加（加到全局开关区）。返回是否加上了。"""
        if name not in flags:
            return False
        if value is None:
            opts.append(name)
        elif flags[name].get("optional_value"):
            opts.append(f"{name}={value}")
        else:
            opts.append(name)
            opts.append(value)
        return True

    if proxy is not None and proxy is not False:
        proxy_value = None if proxy is True or proxy == "" else str(proxy)
        if not add_flag("--proxy", proxy_value):
            warnings.append("本机 cmsdl 不支持 --proxy，已忽略代理设置。")
    if insecure:
        if not add_flag("--allow-insecure"):
            warnings.append("本机 cmsdl 不支持 --allow-insecure，已忽略。")
    for _ in range(max(0, min(verbose, 3))):
        if "--verbose" in flags or "-v" in surface["help_text"]:
            opts.append("-v")

    # --no-gui：非 Windows 上 cmsdl 本来也不弹窗（--help 原文：
    # "On non-Windows platforms the GUI is never shown regardless of this flag."）
    if not add_flag("--no-gui") and os.name != "nt":
        warnings.append("本机 cmsdl 没有 --no-gui；非 Windows 平台本来就不弹窗，可忽略。")

    if mode == "check":
        mode_args.append("--check")
        if build is not None and str(build).strip():
            if not add_flag("--build", str(build).strip()):
                warnings.append("本机 cmsdl 不支持 --build，已忽略。")
        if not add_flag("--json"):
            warnings.append("本机 cmsdl 不支持 --json，host 侧会退化成解析人读输出。")

    elif mode == "download":
        if region == "manual":
            blocked.append("region=manual 时 --download 收的是签名 URL，不是目标目录；本面板不支持这条路径。")
        mode_args.extend(["--download", target_dir])
        if wz_only:
            if not add_flag("--download-wz-only"):
                warnings.append("本机 cmsdl 不支持 --download-wz-only，会下整个客户端（体积大得多）。")
        if filter_spec:
            if not add_flag("--filter", filter_spec):
                warnings.append("本机 cmsdl 不支持 --filter，已忽略。")
        if purge:
            if not allow_data_dir:
                blocked.append("--purge-wz-files 会删除数据源目录里不在清单中的文件，必须先打开「允许写入数据源目录」。")
            elif not add_flag("--purge-wz-files"):
                warnings.append("本机 cmsdl 不支持 --purge-wz-files，已忽略。")
            else:
                warnings.append("已启用 --purge-wz-files：数据源目录里不在官方清单中的文件会被删除。")
        if build is not None and str(build).strip():
            add_flag("--build", str(build).strip())

    elif mode == "patch_list":
        mode_args.extend(["--patch", "list"])
        if build_since is not None and str(build_since).strip():
            add_flag("--build-since", str(build_since).strip())
        if not add_flag("--json"):
            warnings.append("本机 cmsdl 不支持 --json，host 侧会退化成解析人读输出。")

    elif mode == "patch":
        # README：cmsdl cms --patch latest /path/to/cms/client —— 目录是 CLIENT_DIR 位置参数
        mode_args.extend(["--patch", patch_version or "latest", target_dir])
        if launch_after_patching:
            if not add_flag("--launch-after-patching"):
                warnings.append("本机 cmsdl 不支持 --launch-after-patching，已忽略。")
            else:
                warnings.append("已启用 --launch-after-patching：打完补丁会直接拉起游戏。")
        else:
            warnings.append("未启用 --launch-after-patching：只打补丁，不启动游戏。")

    elif mode == "torrent":
        mode_args.append("--get-bit-torrent")
        out_dir = torrent_out or CMSDL_DIR
        if not add_flag("--output", out_dir):
            warnings.append("本机 cmsdl 不支持 -o/--output，.torrent 会落在进程当前目录。")

    else:
        blocked.append(f"未知 mode {mode!r}（可用：check / download / patch_list / patch / torrent）。")

    # 顺序很关键（官方 usage）：cmsdl [OPTIONS] <--download …> <REGION> [CLIENT_DIR]
    # 所以是先 opts、再 mode_args（含 --download <PATH>）、最后才 REGION 与 CLIENT_DIR。
    positional: List[str] = [region]
    if client_dir and mode in ("download", "patch"):
        positional.append(client_dir)
    argv: List[str] = [path, *opts, *mode_args, *positional, *[str(a) for a in extra_args]]

    # 5) 存在的旧目录提示（不改任何文件，只看一眼）
    exists = os.path.isdir(effective_target)
    return {
        "ok": not blocked,
        "argv": argv,
        "argv_text": " ".join(_quote(a) for a in argv),
        "blocked": blocked,
        "warnings": warnings,
        "mode": mode,
        "region": region,
        "cmsdl": {"path": path, "source": source, "help_flags": surface["flag_names"], "regions": known_regions},
        "target": {
            "dir": target_dir,
            "client_dir": client_dir or None,
            "effective_dir": effective_target,
            "wz_subdir": wz_subdir,
            "exists": exists,
            "relation_to_data_dir": relation,
            "data_dir": DATA_DIR,
            "wz_only": wz_only,
        },
    }


def _quote(token: str) -> str:
    """给「将执行的完整命令」做人类可读的引号处理（不用于真的执行）。"""
    if token and re.fullmatch(r"[A-Za-z0-9_@%+=:,./-]+", token):
        return token
    return "'" + token.replace("'", "'\\''") + "'"


COMMAND_NAMES = ("cmsdl_probe", "cmsdl_plan")
