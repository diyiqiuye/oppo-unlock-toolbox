
"""模拟设备：一个假的 adb / fastboot，用来在真机之外跑通整个工作流。

它把设备侧的文件系统映射到 MOCK_DEVICE_ROOT 指向的目录：
    /data/local/tmp/...        -> <root>/data/local/tmp/...
    /dev/block/by-name/abl_a   -> <root>/dev/block/by-name/abl_a
    /proc/modules              -> <root>/proc/modules
getprop 读 <root>/props.json；运行状态（是否已 root、是否已解锁、是否在 fastboot）
存在 <root>/state.json 里。

只覆盖工具箱真正会用到的那几条命令，目的是验证命令拼装、MD5/大小校验、
账本状态流转与失败分支 —— 不是完整的 adb 实现。
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import sys

ROOT = os.environ.get("MOCK_DEVICE_ROOT", "")
SERIAL = os.environ.get("MOCK_SERIAL", "MOCKFX5P0001")
STATE_FILE = os.path.join(ROOT, "state.json")
PROPS_FILE = os.path.join(ROOT, "props.json")
SIZES_FILE = os.path.join(ROOT, "sizes.json")

def load_json(path, default):
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return json.load(fh)
    except Exception:
        return dict(default)

def save_json(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(data, fh, ensure_ascii=False, indent=2)

def state():
    return load_json(STATE_FILE, {"rooted": False, "kernelsu": False,
                                  "fastboot": False, "unlocked": False})

def set_state(**kw):
    data = state()
    data.update(kw)
    save_json(STATE_FILE, data)

def dev_path(remote: str) -> str:
    """设备路径 -> 本机路径。"""
    remote = remote.strip().replace("\\", "/")
    return os.path.join(ROOT, remote.lstrip("/").replace("/", os.sep))

def ensure_parent(path):
    parent = os.path.dirname(path)
    if parent:
        os.makedirs(parent, exist_ok=True)

def md5_file(path):
    digest = hashlib.md5()
    with open(path, "rb") as fh:
        for block in iter(lambda: fh.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()

def props():
    data = load_json(PROPS_FILE, {})
    data["ro.boot.flash.locked"] = "0" if state().get("unlocked") else "1"
    return data

def is_block(remote_path: str) -> bool:
    return remote_path.startswith("/dev/block/")

def block_size(remote_path: str) -> int:
    """块设备（分区）的固定大小 —— 真分区不会因为 dd 覆盖几行就变大变小。"""
    data = load_json(SIZES_FILE, {})
    if remote_path not in data:
        path = dev_path(remote_path)
        if not os.path.isfile(path):
            return 0
        data[remote_path] = os.path.getsize(path)
        save_json(SIZES_FILE, data)
    return int(data[remote_path])

def keep_partition_size(remote_path: str, local_path: str):
    """dd 写块设备后，把模拟文件补齐/截断回分区固有大小。"""
    size = block_size(remote_path)
    if not size:
        return
    with open(local_path, "r+b") as fh:
        fh.seek(0, os.SEEK_END)
        current = fh.tell()
        if current < size:
            fh.write(b"\x00" * (size - current))
        elif current > size:
            fh.truncate(size)

def remote(cmd: str) -> int:
    cmd = cmd.strip()
    if not cmd:
        return 0

    match = re.match(r"^cd\s+(\S+)\s*&&\s*(.+)$", cmd, re.S)
    if match:
        return remote(match.group(2).strip())

    match = re.match(r"^echo\s+'(.+)'\s*\|\s*(\S*ksud)\s+debug\s+su$", cmd, re.S)
    if match:
        inner, ksud = match.group(1), match.group(2)
        if not os.path.isfile(dev_path(ksud)):
            print("%s: not found" % ksud)
            return 127
        if not state().get("rooted"):
            print("uid=2000(shell) gid=2000(shell)  ← 尚未 root")
            return 1
        return remote_root(inner)

    match = re.match(r"^head\s+-c\s+(\d+)\s+(\S+)\s*\|\s*md5sum$", cmd)
    if match:
        size, path = int(match.group(1)), dev_path(match.group(2))
        if not os.path.isfile(path):
            print("head: %s: No such file or directory" % match.group(2))
            return 1
        with open(path, "rb") as fh:
            data = fh.read(size)
        print("%s  %s" % (hashlib.md5(data).hexdigest(), match.group(2)))
        return 0

    if cmd.startswith("dmesg"):
        print("[   12.345678] capability: commoncap: allow root for: 10333")
        print("[   12.345679] capset: patched, ambient set")
        return 0

    while re.match(r"^[A-Za-z_][A-Za-z0-9_]*=\S*\s+", cmd):
        cmd = re.sub(r"^[A-Za-z_][A-Za-z0-9_]*=\S*\s+", "", cmd, count=1)

    if "exploit_guard" in cmd and "late-load" in cmd:
        if not os.path.isfile(dev_path("/data/local/tmp/exploit_guard")):
            print("sh: ./exploit_guard: not found")
            return 127
        print("[*] guard: patching .text signature … OK")
        print("[*] selinux: permissive, kptr_restrict=0")
        print("[*] capset: full caps for uid=0")
        print("[*] ksud late-load: queued (async insmod)")

        with open(dev_path("/proc/modules"), "w", encoding="utf-8") as fh:
            fh.write("kernelsu 262144 0 - Live 0x0000000000000000 (O)\n")
        set_state(rooted=True, kernelsu=True)
        return 0

    if "&&" in cmd:
        code = 0
        for part in [p.strip() for p in cmd.split("&&") if p.strip()]:
            code = remote(part)
            if code != 0:
                return code
        return code

    return remote_root(cmd) if state().get("rooted") else remote_shell(cmd)

def remote_root(cmd: str) -> int:
    """root 权限下执行的命令（id / dd / md5sum / dmesg…）。"""
    cmd = cmd.strip()
    if cmd == "id":
        print("uid=0(root) gid=0(root) groups=0(root) context=u:r:su:s0")
        return 0
    if "&&" in cmd:
        code = 0
        for part in [p.strip() for p in cmd.split("&&") if p.strip()]:
            code = remote_root(part)
            if code != 0:
                return code
        return code
    return remote_shell(cmd, root=True)

def remote_shell(cmd: str, root=False) -> int:
    cmd = cmd.strip()

    if cmd == "id":
        print("uid=2000(shell) gid=2000(shell) groups=2000(shell)")
        return 0

    if cmd == "sync":
        return 0

    if cmd == "uname -r":
        print(props().get("ro.kernel.version", "5.10.198-android12-9-gabcdef"))
        return 0

    match = re.match(r"^getprop\s+(\S+)$", cmd)
    if match:
        value = props().get(match.group(1), "")
        if value:
            print(value)
        return 0
    if cmd == "getprop":
        for key in sorted(props()):
            print("[%s]: [%s]" % (key, props()[key]))
        return 0

    match = re.match(r"^grep\s+-c\s+'?([^']+)'?\s+(\S+)$", cmd)
    if match:
        pattern, path = match.group(1).lstrip("^"), dev_path(match.group(2))
        text = ""
        if os.path.isfile(path):
            with open(path, "r", encoding="utf-8", errors="replace") as fh:
                text = fh.read()
        print(sum(1 for line in text.splitlines() if line.startswith(pattern)))
        return 0

    match = re.match(r"^grep\s+(\S+)\s+(\S+)$", cmd)
    if match:
        path = dev_path(match.group(2))
        if os.path.isfile(path):
            with open(path, "r", encoding="utf-8", errors="replace") as fh:
                for line in fh:
                    if match.group(1) in line:
                        print(line.rstrip())
        return 0

    match = re.match(r"^mkdir\s+-p\s+(\S+)$", cmd)
    if match:
        os.makedirs(dev_path(match.group(1)), exist_ok=True)
        return 0

    match = re.match(r"^chmod\s+(\S+)\s+(.+)$", cmd)
    if match:
        for target in match.group(2).split():
            if not os.path.isfile(dev_path(target)):
                print("chmod: %s: No such file or directory" % target)
                return 1
        return 0

    match = re.match(r"^dd\s+if=(\S+)\s+of=(\S+)(?:\s+bs=(\d+))?$", cmd)
    if match:
        src_remote, dst_remote = match.group(1), match.group(2)
        src, dst = dev_path(src_remote), dev_path(dst_remote)
        if not os.path.isfile(src):
            print("dd: %s: No such file or directory" % src_remote)
            return 1
        ensure_parent(dst)
        with open(src, "rb") as fh:
            data = fh.read()
        with open(dst, "wb") as fh:
            fh.write(data)
        if is_block(dst_remote):
            keep_partition_size(dst_remote, dst)
        print("%d bytes copied" % len(data))
        return 0

    match = re.match(r"^md5sum\s+(\S+)$", cmd)
    if match:
        path = dev_path(match.group(1))
        if not os.path.isfile(path):
            print("md5sum: %s: No such file or directory" % match.group(1))
            return 1
        print("%s  %s" % (md5_file(path), match.group(1)))
        return 0

    match = re.match(r"^wc\s+-c\s*<\s*(\S+)$", cmd)
    if match:
        remote_path = match.group(1)
        path = dev_path(remote_path)
        if not os.path.isfile(path):
            print("wc: %s: No such file or directory" % remote_path)
            return 1
        print(block_size(remote_path) if is_block(remote_path) else os.path.getsize(path))
        return 0

    match = re.match(r"^blockdev\s+--getsize64\s+(\S+)$", cmd)
    if match:
        remote_path = match.group(1)
        path = dev_path(remote_path)
        if not os.path.isfile(path):
            print("blockdev: %s: No such file or directory" % remote_path)
            return 1
        print(block_size(remote_path) if is_block(remote_path) else os.path.getsize(path))
        return 0

    print("sh: %s: not found" % cmd)
    return 127

def do_adb(args) -> int:
    if not args:
        print("adb: no command")
        return 1
    if args[0] == "version":
        print("Android Debug Bridge version 1.0.41")
        print("Version 37.0.0-14910828")
        return 0
    if args[0] == "start-server" or args[0] == "kill-server":
        return 0
    if args[0] == "wait-for-device":
        return 0
    if args[0] == "get-state":
        print("device" if not state().get("fastboot") else "unknown")
        return 0
    if args[0] == "devices":
        print("List of devices attached")
        if not state().get("fastboot"):
            print("%s\tdevice product:PFEM00 model:PFEM00 device:PFEM00 transport_id:1" % SERIAL)
        print("")
        return 0
    if args[0] == "push":
        local, remote_path = args[1], args[2]
        if not os.path.isfile(local):
            print("adb: error: cannot stat '%s'" % local)
            return 1
        target = dev_path(remote_path)
        if os.path.isdir(target):
            target = os.path.join(target, os.path.basename(local))
        ensure_parent(target)
        with open(local, "rb") as fh:
            data = fh.read()
        with open(target, "wb") as fh:
            fh.write(data)
        print("%s: 1 file pushed, 0 skipped." % local)
        return 0
    if args[0] == "pull":
        remote_path, local = args[1], args[2]
        source = dev_path(remote_path)
        if not os.path.isfile(source):
            print("adb: error: remote object '%s' does not exist" % remote_path)
            return 1
        if os.path.isdir(local):
            local = os.path.join(local, os.path.basename(remote_path))
        ensure_parent(local)
        with open(source, "rb") as fh:
            data = fh.read()
        with open(local, "wb") as fh:
            fh.write(data)
        print("%s: 1 file pulled, 0 skipped." % remote_path)
        return 0
    if args[0] == "reboot":
        target = args[1] if len(args) > 1 else ""

        if target in ("fastboot", "bootloader"):
            set_state(fastboot=True)
        return 0
    if args[0] == "shell":
        return remote(" ".join(args[1:]))
    print("adb: unknown command %s" % args[0])
    return 1

def do_fastboot(args) -> int:
    if not args:
        print("fastboot: no command")
        return 1
    if args[0] == "devices":
        if state().get("fastboot"):
            print("%s\tfastboot" % SERIAL)
        else:
            print("")
        return 0
    if args[0] == "getvar":
        if len(args) > 1 and args[1] == "unlocked":
            print("unlocked: %s" % ("yes" if state().get("unlocked") else "no"))
            print("Finished. Total time: 0.001s")
            return 0
        print("")
        return 0
    if args[:2] == ["flashing", "unlock"]:
        print("...")
        print("OKAY [  0.123s]")
        print("Finished. Total time: 0.124s")

        set_state(fastboot=False, unlocked=True, rooted=False, kernelsu=False)
        wipe_data()
        return 0
    if args[0] == "reboot":
        set_state(fastboot=False)
        return 0
    print("fastboot: unknown command %s" % args[0])
    return 1

def wipe_data():
    """解锁后清空 /data（含 /data/local/tmp），并清掉内核模块。"""
    data_dir = dev_path("/data")
    if os.path.isdir(data_dir):
        for base, dirs, files in os.walk(data_dir, topdown=False):
            for name in files:
                try:
                    os.remove(os.path.join(base, name))
                except OSError:
                    pass
            for name in dirs:
                try:
                    os.rmdir(os.path.join(base, name))
                except OSError:
                    pass
    with open(dev_path("/proc/modules"), "w", encoding="utf-8") as fh:
        fh.write("")

def main():
    if not ROOT:
        print("MOCK_DEVICE_ROOT 未设置")
        return 2
    name = os.path.basename(sys.argv[0]).lower()
    args = sys.argv[1:]

    if args and args[0] == "-s":
        args = args[2:]
    try:
        if name.startswith("fastboot"):
            return do_fastboot(args)
        return do_adb(args)
    except Exception as exc:
        print("mock: internal error: %r" % exc)
        return 3

if __name__ == "__main__":
    sys.exit(main())
