#!/usr/bin/env python3
"""Оснастка стенда: детерминированная сборка переносного комплекта владельцем."""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import stat
import subprocess
import sys
import uuid
import xml.etree.ElementTree as ET


class TransferError(Exception):
    pass


def now() -> str:
    return dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat()


def emit(value: dict) -> None:
    print(json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True))


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def lstat_regular(path: Path, label: str) -> os.stat_result:
    try:
        info = path.lstat()
    except FileNotFoundError as error:
        raise TransferError(f"не найден {label}") from error
    attributes = getattr(info, "st_file_attributes", 0)
    if stat.S_ISLNK(info.st_mode) or attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0):
        raise TransferError(f"символическая ссылка или reparse point запрещены: {label}")
    if not stat.S_ISREG(info.st_mode):
        raise TransferError(f"ожидался обычный файл: {label}")
    return info


def lstat_directory(path: Path, label: str) -> os.stat_result:
    try:
        info = path.lstat()
    except FileNotFoundError as error:
        raise TransferError(f"не найден {label}") from error
    attributes = getattr(info, "st_file_attributes", 0)
    if stat.S_ISLNK(info.st_mode) or attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0):
        raise TransferError(f"символическая ссылка или reparse point запрещены: {label}")
    if not stat.S_ISDIR(info.st_mode):
        raise TransferError(f"ожидался каталог: {label}")
    return info


def safe_relative(raw: object, label: str) -> str:
    if not isinstance(raw, str) or not raw or "\\" in raw:
        raise TransferError(f"{label} должен быть непустым относительным путём с '/'")
    value = PurePosixPath(raw)
    if value.is_absolute() or any(part in ("", ".", "..") for part in value.parts):
        raise TransferError(f"{label} выходит за назначенный каталог")
    return value.as_posix()


def safe_child(root: Path, relative: str, label: str) -> Path:
    child = root.joinpath(*PurePosixPath(relative).parts)
    try:
        child.resolve(strict=False).relative_to(root.resolve())
    except ValueError as error:
        raise TransferError(f"{label} выходит за назначенный каталог") from error
    return child


def is_within(child: Path, parent: Path) -> bool:
    try:
        child.resolve(strict=False).relative_to(parent.resolve())
        return True
    except ValueError:
        return False


def same_snapshot(first: os.stat_result, second: os.stat_result) -> bool:
    return (first.st_dev, first.st_ino, first.st_size, first.st_mtime_ns) == (
        second.st_dev, second.st_ino, second.st_size, second.st_mtime_ns)


def stable_hash(path: Path, label: str) -> tuple[int, str]:
    before = lstat_regular(path, label)
    digest = sha256_file(path)
    after = lstat_regular(path, label)
    if not same_snapshot(before, after):
        raise TransferError(f"источник изменился при чтении: {label}")
    return before.st_size, digest


def copy_verified(source: Path, destination: Path, expected_size: int, expected_sha: str, label: str) -> None:
    before = lstat_regular(source, label)
    flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(source, flags)
    try:
        opened = os.fstat(descriptor)
        if not stat.S_ISREG(opened.st_mode) or not same_snapshot(before, opened):
            raise TransferError(f"источник изменился до копирования: {label}")
        digest = hashlib.sha256()
        written = 0
        with os.fdopen(descriptor, "rb", closefd=False) as reader, destination.open("xb") as writer:
            for block in iter(lambda: reader.read(1024 * 1024), b""):
                writer.write(block)
                digest.update(block)
                written += len(block)
            writer.flush()
            os.fsync(writer.fileno())
    finally:
        os.close(descriptor)
    after = lstat_regular(source, label)
    if not same_snapshot(before, after) or written != expected_size or digest.hexdigest() != expected_sha:
        raise TransferError(f"источник изменился при копировании: {label}")
    copied_size, copied_sha = stable_hash(destination, "скопированный файл")
    if copied_size != expected_size or copied_sha != expected_sha:
        raise TransferError("контрольная сумма копии не совпала")


def read_config(path: Path) -> dict:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise TransferError("не прочитан JSON-конфиг") from error
    if not isinstance(value, dict):
        raise TransferError("корень конфига должен быть объектом")
    return value


def portable_context(config: dict) -> tuple[Path, str, str]:
    machine = config.get("machine")
    raw_root = config.get("portable_root")
    marker = config.get("portable_marker")
    if not isinstance(machine, str) or not machine:
        raise TransferError("в конфиге нужен machine")
    if not isinstance(raw_root, str) or not Path(raw_root).is_absolute():
        raise TransferError("portable_root должен быть явным абсолютным путём")
    if not isinstance(marker, dict):
        raise TransferError("в конфиге нужен portable_marker")
    marker_path = safe_relative(marker.get("path"), "portable_marker.path")
    marker_sha = marker.get("sha256")
    if not isinstance(marker_sha, str) or len(marker_sha) != 64 or any(c not in "0123456789abcdef" for c in marker_sha):
        raise TransferError("portable_marker.sha256 должен быть SHA-256 в нижнем регистре")
    root = Path(raw_root)
    lstat_directory(root, "корень переносного накопителя")
    marker_file = safe_child(root, marker_path, "portable_marker.path")
    lstat_regular(marker_file, "маркер переносного накопителя")
    if sha256_file(marker_file) != marker_sha:
        raise TransferError("маркер принадлежит другому переносному комплекту")
    return root, machine, marker_sha


def source_entries(config: dict, root: Path) -> tuple[list[dict], list[str]]:
    raw_sources = config.get("sources")
    if not isinstance(raw_sources, list) or not raw_sources:
        raise TransferError("sources должен быть непустым списком")
    prepared: list[dict] = []
    names: set[str] = set()
    for entry in raw_sources:
        if not isinstance(entry, dict):
            raise TransferError("каждый источник должен быть объектом")
        name, raw_path, required = entry.get("name"), entry.get("path"), entry.get("required")
        if not isinstance(name, str) or not name or name in names:
            raise TransferError("у источников нужны уникальные имена")
        if not isinstance(raw_path, str) or not Path(raw_path).is_absolute() or not isinstance(required, bool):
            raise TransferError(f"источник {name} должен иметь абсолютный path и required true/false")
        target = safe_relative(entry.get("target"), f"target источника {name}")
        names.add(name)
        path = Path(raw_path)
        if not path.exists():
            if required:
                raise TransferError(f"нет обязательного источника: {name}")
            prepared.append({"name": name, "target": target, "required": False, "missing": True})
            continue
        if is_within(path, root):
            raise TransferError(f"источник {name} лежит на переносном накопителе")
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0):
            raise TransferError(f"символическая ссылка запрещена у источника: {name}")
        if not (stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode)):
            raise TransferError(f"источник {name} должен быть каталогом или файлом")
        prepared.append({"name": name, "path": path, "target": target, "required": required, "file": stat.S_ISREG(info.st_mode)})
    active = [item for item in prepared if not item.get("missing")]
    for index, left in enumerate(active):
        for right in active[index + 1 :]:
            if is_within(left["path"], right["path"]) or is_within(right["path"], left["path"]):
                raise TransferError(f"источники пересекаются: {left['name']} и {right['name']}")
            left_target, right_target = PurePosixPath(left["target"]), PurePosixPath(right["target"])
            if left_target == right_target or left_target in right_target.parents or right_target in left_target.parents:
                raise TransferError(f"назначения пересекаются: {left['name']} и {right['name']}")
    return active, [item["name"] for item in prepared if item.get("missing")]


def inspect_vm(config: dict, sources: list[dict]) -> dict | None:
    vm = config.get("vm")
    if vm is None:
        return None
    if not isinstance(vm, dict):
        raise TransferError("vm должен быть объектом")
    source_name, name, vboxmanage = vm.get("source"), vm.get("name"), vm.get("vboxmanage")
    if not all(isinstance(value, str) and value for value in (source_name, name, vboxmanage)):
        raise TransferError("vm требует source, name и vboxmanage")
    source = next((item for item in sources if item["name"] == source_name), None)
    if source is None or source["file"]:
        raise TransferError("vm.source должен ссылаться на существующий каталог-источник")
    vbox_file = safe_relative(vm.get("vbox_file"), "vm.vbox_file")
    vbox_path = safe_child(source["path"], vbox_file, "vm.vbox_file")
    lstat_regular(vbox_path, "файл конфигурации VirtualBox")
    try:
        tree = ET.parse(vbox_path)
    except ET.ParseError as error:
        raise TransferError("не прочитан XML .vbox") from error
    disks: list[dict] = []
    for element in tree.iter():
        if element.tag.rsplit("}", 1)[-1] != "HardDisk":
            continue
        location = element.get("location")
        if not location:
            raise TransferError("в .vbox найден диск без location")
        disk = Path(location)
        if not disk.is_absolute():
            disk = vbox_path.parent / disk
        disk = disk.resolve(strict=False)
        if not disk.exists():
            raise TransferError(f"в .vbox указан отсутствующий диск: {disk.name}")
        covered = next((item["name"] for item in sources if is_within(disk, item["path"])), None)
        if covered is None:
            raise TransferError(f"внешний диск {disk.name} не включён отдельным источником")
        disks.append({"filename": disk.name, "source": covered})
    state = subprocess.run([vboxmanage, "showvminfo", name, "--machinereadable"], text=True, capture_output=True, timeout=30)
    if state.returncode != 0:
        raise TransferError("VBoxManage не подтвердил состояние указанной ВМ")
    vm_state = next((line.split("=", 1)[1].strip().strip('"') for line in state.stdout.splitlines() if line.startswith("VMState=")), None)
    if vm_state != "poweroff":
        raise TransferError("ВМ должна быть полностью выключена; saved и running запрещены")
    snapshots = sum(1 for item in tree.iter() if item.tag.rsplit("}", 1)[-1] == "Snapshot")
    return {"name": name, "state": vm_state, "vbox_file": vbox_file, "referenced_disks": disks, "snapshot_nodes": snapshots}


def scan_source(source: dict) -> tuple[list[str], list[dict]]:
    directories: list[str] = []
    files: list[dict] = []
    root, target, name = source["path"], source["target"], source["name"]
    if source["file"]:
        size, digest = stable_hash(root, f"источник {name}")
        return [], [{"source": name, "source_path": root, "path": target, "size": size, "sha256": digest}]
    lstat_directory(root, f"источник {name}")
    for directory, names, filenames in os.walk(root, topdown=True, followlinks=False):
        directory_path = Path(directory)
        relative = directory_path.relative_to(root).as_posix()
        destination_dir = target if relative == "." else f"{target}/{relative}"
        directories.append(destination_dir)
        for child in sorted(names):
            child_path = directory_path / child
            lstat_directory(child_path, f"источник {name}")
        names[:] = sorted(names)
        for filename in sorted(filenames):
            file_path = directory_path / filename
            size, digest = stable_hash(file_path, f"источник {name}")
            relative_file = file_path.relative_to(root).as_posix()
            files.append({"source": name, "source_path": file_path, "path": f"{target}/{relative_file}", "size": size, "sha256": digest})
    return directories, files


def git_metadata(config: dict) -> list[dict]:
    repositories = config.get("repositories", [])
    if not isinstance(repositories, list):
        raise TransferError("repositories должен быть списком")
    result: list[dict] = []
    for item in repositories:
        if not isinstance(item, dict) or not isinstance(item.get("name"), str) or not isinstance(item.get("path"), str):
            raise TransferError("репозиторий требует name и path")
        path, required = Path(item["path"]), item.get("required", False)
        if not path.is_absolute() or not isinstance(required, bool):
            raise TransferError(f"репозиторий {item['name']} задан неверно")
        if not path.is_dir():
            if required:
                raise TransferError(f"нет обязательного репозитория: {item['name']}")
            result.append({"name": item["name"], "status": "missing"})
            continue
        revision = subprocess.run(["git", "-C", str(path), "rev-parse", "HEAD"], text=True, capture_output=True, timeout=30)
        status = subprocess.run(["git", "-C", str(path), "status", "--porcelain=v1"], text=True, capture_output=True, timeout=30)
        if revision.returncode or status.returncode:
            if required:
                raise TransferError(f"не получены локальные сведения Git: {item['name']}")
            result.append({"name": item["name"], "status": "unavailable"})
            continue
        result.append({"name": item["name"], "status": "local", "revision": revision.stdout.strip(), "dirty_entries": len(status.stdout.splitlines()), "fetch": "не выполнялся"})
    return result


def verify_tree(generation: Path, manifest: dict, require_ready: bool) -> None:
    lstat_directory(generation, "поколение")
    manifest_path = generation / "manifest.json"
    lstat_regular(manifest_path, "manifest.json")
    expected = {"manifest.json", "READY"}
    for directory in manifest.get("directories", []):
        safe = safe_relative(directory, "каталог манифеста")
        lstat_directory(safe_child(generation, safe, "каталог манифеста"), "каталог поколения")
    for item in manifest.get("files", []):
        safe = safe_relative(item.get("path"), "файл манифеста")
        expected.add(safe)
        file_path = safe_child(generation, safe, "файл манифеста")
        size, digest = stable_hash(file_path, "файл поколения")
        if size != item.get("size") or digest != item.get("sha256"):
            raise TransferError("файл поколения не совпал с манифестом")
    for directory, names, filenames in os.walk(generation, topdown=True, followlinks=False):
        for name in sorted(names):
            lstat_directory(Path(directory) / name, "каталог поколения")
        for name in sorted(filenames):
            path = Path(directory) / name
            lstat_regular(path, "файл поколения")
            relative = path.relative_to(generation).as_posix()
            if relative not in expected:
                raise TransferError("в поколении есть неучтённый файл")
    if require_ready:
        ready_path = generation / "READY"
        lstat_regular(ready_path, "READY")
        try:
            ready = json.loads(ready_path.read_text(encoding="utf-8"))
        except json.JSONDecodeError as error:
            raise TransferError("READY повреждён") from error
        if ready.get("manifest_sha256") != sha256_file(manifest_path):
            raise TransferError("READY не подтверждает этот манифест")


def current_generation(root: Path) -> tuple[Path, dict] | None:
    current = root / "kit" / "current.json"
    if not current.exists():
        return None
    lstat_regular(current, "kit/current.json")
    try:
        pointer = json.loads(current.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise TransferError("current.json повреждён") from error
    generation = pointer.get("generation")
    if not isinstance(generation, str) or not generation or "/" in generation or "\\" in generation:
        raise TransferError("current.json содержит небезопасное поколение")
    directory = root / "kit" / "generations" / generation
    manifest_path = directory / "manifest.json"
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise TransferError("манифест текущего поколения не прочитан") from error
    if pointer.get("manifest_sha256") != sha256_file(manifest_path):
        raise TransferError("current.json не совпал с манифестом")
    verify_tree(directory, manifest, True)
    return directory, manifest


def prepare(config: dict) -> dict:
    root, machine, marker_sha = portable_context(config)
    sources, missing = source_entries(config, root)
    vm = inspect_vm(config, sources)
    directories: list[str] = []
    files: list[dict] = []
    for source in sources:
        source_dirs, source_files = scan_source(source)
        directories.extend(source_dirs)
        files.extend(source_files)
    files.sort(key=lambda item: item["path"])
    previous = current_generation(root)
    old_files = {} if previous is None else {item["path"]: item for item in previous[1].get("files", [])}
    changed = [item for item in files if old_files.get(item["path"], {}).get("sha256") != item["sha256"]]
    needed = sum(item["size"] for item in files) + max(64 * 1024 * 1024, sum(item["size"] for item in files) // 20)
    free = shutil.disk_usage(root).free
    return {"root": root, "machine": machine, "marker_sha": marker_sha, "sources": sources, "missing": missing, "directories": sorted(set(directories)), "files": files, "previous": previous, "vm": vm, "repositories": git_metadata(config), "changed": len(changed), "unchanged": len(files) - len(changed), "bytes": sum(item["size"] for item in files), "required_free": needed, "available_free": free}


def plan_report(prepared: dict) -> dict:
    return {"ok": True, "command": "plan", "machine": prepared["machine"], "current_generation": None if prepared["previous"] is None else prepared["previous"][1].get("generation"), "sources": [{"name": item["name"], "target": item["target"], "required": item["required"]} for item in prepared["sources"]], "missing_optional_sources": prepared["missing"], "files": len(prepared["files"]), "bytes": prepared["bytes"], "changed_files": prepared["changed"], "unchanged_files": prepared["unchanged"], "required_free_bytes": prepared["required_free"], "available_free_bytes": prepared["available_free"], "space_ok": prepared["available_free"] >= prepared["required_free"], "vm": prepared["vm"], "repositories": prepared["repositories"]}


def mkdir_checked(path: Path) -> None:
    if path.exists():
        lstat_directory(path, "служебный каталог комплекта")
    else:
        path.mkdir()


def write_json_new(path: Path, value: dict) -> None:
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2, sort_keys=True)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())


def write_current(root: Path, value: dict) -> None:
    target = root / "kit" / "current.json"
    if target.exists():
        lstat_regular(target, "kit/current.json")
    temporary = target.with_name(f".current.{uuid.uuid4().hex}.tmp")
    write_json_new(temporary, value)
    os.replace(temporary, target)


def pack(prepared: dict) -> dict:
    if prepared["available_free"] < prepared["required_free"]:
        raise TransferError("на переносном накопителе недостаточно места для полного нового поколения")
    root = prepared["root"]
    kit = root / "kit"
    mkdir_checked(kit)
    generations = kit / "generations"
    mkdir_checked(generations)
    identifier = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
    stage = generations / f"{identifier}.incomplete"
    final = generations / identifier
    stage.mkdir()
    try:
        for directory in prepared["directories"]:
            safe_child(stage, directory, "назначение").mkdir(parents=True, exist_ok=True)
        old_directory, old_manifest = prepared["previous"] if prepared["previous"] else (None, {"files": []})
        old_files = {item["path"]: item for item in old_manifest.get("files", [])}
        for item in prepared["files"]:
            destination = safe_child(stage, item["path"], "назначение")
            destination.parent.mkdir(parents=True, exist_ok=True)
            old = old_files.get(item["path"])
            if old_directory is not None and old and old.get("sha256") == item["sha256"]:
                prior = safe_child(old_directory, item["path"], "предыдущее поколение")
                try:
                    os.link(prior, destination)
                    size, digest = stable_hash(destination, "жёсткая ссылка предыдущего поколения")
                    if size != item["size"] or digest != item["sha256"]:
                        raise TransferError("жёсткая ссылка не совпала с манифестом")
                except OSError:
                    copy_verified(prior, destination, item["size"], item["sha256"], "предыдущее поколение")
            else:
                copy_verified(item["source_path"], destination, item["size"], item["sha256"], f"источник {item['source']}")
        manifest = {"format": 1, "generation": identifier, "created_at": now(), "source_machine": prepared["machine"], "marker_sha256": prepared["marker_sha"], "directories": prepared["directories"], "files": [{key: item[key] for key in ("source", "path", "size", "sha256")} for item in prepared["files"]], "counts": {"files": len(prepared["files"]), "bytes": prepared["bytes"], "changed": prepared["changed"], "unchanged": prepared["unchanged"]}, "repositories": prepared["repositories"], "vm": prepared["vm"]}
        manifest_path = stage / "manifest.json"
        write_json_new(manifest_path, manifest)
        verify_tree(stage, manifest, False)
        manifest_sha = sha256_file(manifest_path)
        write_json_new(stage / "READY", {"generation": identifier, "manifest_sha256": manifest_sha, "created_at": now()})
        verify_tree(stage, manifest, True)
        os.replace(stage, final)
        write_current(root, {"generation": identifier, "manifest_sha256": manifest_sha, "updated_at": now()})
    except Exception:
        if stage.exists():
            try:
                write_json_new(stage / "FAILED.json", {"generation": identifier, "status": "incomplete", "failed_at": now()})
            except OSError:
                pass
        raise
    return {"ok": True, "command": "pack", "generation": identifier, "files": len(prepared["files"]), "bytes": prepared["bytes"], "current_updated": True}


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Детерминированный экспорт переносного комплекта")
    parser.add_argument("command", choices=("plan", "pack", "verify"))
    parser.add_argument("--config", required=True, type=Path)
    args = parser.parse_args(argv)
    try:
        config = read_config(args.config)
        if args.command == "verify":
            root, machine, _ = portable_context(config)
            current = current_generation(root)
            if current is None:
                raise TransferError("на накопителе нет текущего проверенного поколения")
            manifest = current[1]
            emit({"ok": True, "command": "verify", "machine": machine, "generation": manifest.get("generation"), "files": manifest.get("counts", {}).get("files"), "bytes": manifest.get("counts", {}).get("bytes")})
            return 0
        prepared = prepare(config)
        if args.command == "plan":
            emit(plan_report(prepared))
            return 0 if prepared["available_free"] >= prepared["required_free"] else 2
        emit(pack(prepared))
        return 0
    except (TransferError, OSError, subprocess.SubprocessError) as error:
        emit({"ok": False, "command": args.command, "error": str(error)})
        return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
