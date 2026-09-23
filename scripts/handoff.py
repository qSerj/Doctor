#!/usr/bin/env python3
"""Оснастка разработки: офлайн-передача рабочего состояния между машинами.

Скрипт не обращается к сети и не изменяет репозитории. Он сохраняет Git-историю
в bundle и незакоммиченное состояние отдельно, а inspect только сравнивает его.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import tarfile
import uuid


FORMAT_VERSION = 1
EXCLUDED_MATERIAL_WORDS = ("secret", "credential", "password", "private-key", "private_key")
SECRET_SUFFIXES = (".key", ".pem", ".pfx", ".p12")


class HandoffError(Exception):
    pass


def log(level: str, message: str) -> None:
    print(f"{level}: {message}")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def write_json(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def repo_root() -> Path:
    script_root = Path(__file__).resolve().parents[1]
    result = run(["git", "-C", str(script_root), "rev-parse", "--show-toplevel"], check=False)
    if result.returncode == 0:
        return Path(result.stdout.strip())
    return script_root


def run(arguments: list[str], *, cwd: Path | None = None, check: bool = True) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(arguments, cwd=cwd, text=True, encoding="utf-8", errors="replace", capture_output=True)
    if check and result.returncode:
        detail = result.stderr.strip() or result.stdout.strip() or "неизвестная ошибка"
        raise HandoffError(f"команда {' '.join(arguments[:3])} завершилась с кодом {result.returncode}: {detail}")
    return result


def read_config(path: Path) -> dict:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise HandoffError(f"не прочитан локальный конфиг {path}") from error
    if not isinstance(value, dict):
        raise HandoffError("корень handoff.local.json должен быть объектом")
    for name in ("machine", "transit_root"):
        if not isinstance(value.get(name), str) or not value[name]:
            raise HandoffError(f"в конфиге требуется непустой {name}")
    if not Path(value["transit_root"]).is_absolute():
        raise HandoffError("transit_root должен быть абсолютным путём локальной машины")
    return value


def safe_name(value: str, label: str) -> str:
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", value):
        raise HandoffError(f"{label} содержит недопустимые символы")
    return value


def relative(path: Path, root: Path) -> str:
    return path.relative_to(root).as_posix()


def git(repo: Path, arguments: list[str], *, check: bool = True) -> str:
    return run(["git", "-C", str(repo), *arguments], check=check).stdout


def git_bytes(repo: Path, arguments: list[str]) -> bytes:
    result = subprocess.run(["git", "-C", str(repo), *arguments], cwd=repo, capture_output=True)
    if result.returncode:
        raise HandoffError(f"git diff завершился с кодом {result.returncode}")
    return result.stdout


def repository_entries(root: Path, config: dict) -> list[tuple[str, Path]]:
    entries: list[tuple[str, Path]] = [("Doctor", root)]
    memorex = root / "Memorex"
    if (memorex / ".git").exists():
        entries.append(("Memorex", memorex))
    for item in config.get("additional_repositories", []):
        if not isinstance(item, dict):
            raise HandoffError("additional_repositories содержит не объект")
        name = safe_name(str(item.get("name", "")), "имя дополнительного репозитория")
        raw_path = item.get("path")
        if not isinstance(raw_path, str) or not Path(raw_path).is_absolute():
            raise HandoffError(f"репозиторий {name} требует абсолютный локальный path")
        path = Path(raw_path)
        if not (path / ".git").exists():
            raise HandoffError(f"не найден Git-репозиторий {name}")
        entries.append((name, path))
    names = [name for name, _ in entries]
    if len(set(names)) != len(names):
        raise HandoffError("имена репозиториев должны быть уникальны")
    return entries


def tracked_counts(repo: Path) -> tuple[int, int, bool]:
    status = git(repo, ["status", "--porcelain=v1", "-z"])
    entries = [item for item in status.split("\0") if item]
    modified = sum(1 for item in entries if not item.startswith("?? "))
    untracked = sum(1 for item in entries if item.startswith("?? "))
    return modified, untracked, bool(entries)


def add_untracked_archive(repo: Path, destination: Path) -> tuple[int, list[str]]:
    raw = run(["git", "-C", str(repo), "ls-files", "--others", "--exclude-standard", "-z"]).stdout
    names = [name for name in raw.split("\0") if name]
    if not names:
        return 0, []
    excluded = [name for name in names if name in {"handoff.local.json", "CLAUDE.local.md"}]
    names = [name for name in names if name not in set(excluded)]
    if not names:
        return 0, excluded
    with tarfile.open(destination, "w:gz", format=tarfile.PAX_FORMAT, dereference=False) as archive:
        for name in names:
            pure = PurePosixPath(name)
            if pure.is_absolute() or ".." in pure.parts:
                raise HandoffError("Git вернул небезопасный путь untracked-файла")
            source = repo.joinpath(*pure.parts)
            if not source.exists() and not source.is_symlink():
                raise HandoffError(f"untracked-файл исчез при упаковке: {name}")
            archive.add(source, arcname=name, recursive=False)
    with tarfile.open(destination, "r:gz") as archive:
        for member in archive.getmembers():
            pure = PurePosixPath(member.name)
            if pure.is_absolute() or ".." in pure.parts:
                raise HandoffError("архив untracked содержит небезопасный путь")
    return len(names), excluded


def create_repo_package(name: str, repo: Path, package: Path) -> dict:
    package.mkdir(parents=True)
    branch = git(repo, ["branch", "--show-current"]).strip() or "(detached)"
    head = git(repo, ["rev-parse", "HEAD"]).strip()
    modified, untracked, dirty = tracked_counts(repo)
    run(["git", "-C", str(repo), "bundle", "create", str(package / "repo.bundle"), "--all"])
    run(["git", "bundle", "verify", str(package / "repo.bundle")])
    (package / "status.txt").write_text(git(repo, ["status", "--short", "--branch"]), encoding="utf-8")
    (package / "log.txt").write_text(git(repo, ["log", "--oneline", "--decorate", "-30"]), encoding="utf-8")
    (package / "remotes.txt").write_text(git(repo, ["remote", "-v"]), encoding="utf-8")
    (package / "dirty.patch").write_bytes(git_bytes(repo, ["diff", "--binary", "HEAD"]))
    archived_untracked, excluded_untracked = add_untracked_archive(repo, package / "untracked.tar.gz") if untracked else (0, [])
    local_commits = int(git(repo, ["rev-list", "--count", "HEAD", "--not", "--remotes"]).strip() or "0")
    return {"name": name, "branch": branch, "head": head, "clean": not dirty, "modified": modified,
            "untracked": untracked, "untracked_archived": archived_untracked, "excluded_untracked": excluded_untracked,
            "local_commits": local_commits,
            "bundle": f"repositories/{name}/repo.bundle"}


def copy_materials(config: dict, destination: Path) -> tuple[list[dict], list[str]]:
    copied: list[dict] = []
    warnings: list[str] = []
    destination.mkdir(parents=True, exist_ok=True)
    entries = config.get("additional_materials", [])
    if not isinstance(entries, list):
        raise HandoffError("additional_materials должен быть списком")
    for item in entries:
        if not isinstance(item, dict):
            raise HandoffError("additional_materials содержит не объект")
        name = safe_name(str(item.get("name", "")), "имя дополнительного материала")
        sensitivity = item.get("sensitivity", "ordinary")
        if sensitivity != "ordinary":
            warnings.append(f"материал {name} не включён: sensitivity={sensitivity}; переносите его вручную")
            continue
        if any(word in name.lower() for word in EXCLUDED_MATERIAL_WORDS):
            warnings.append(f"материал {name} не включён: имя похоже на секрет")
            continue
        raw_path = item.get("path")
        if not isinstance(raw_path, str) or not Path(raw_path).is_absolute():
            raise HandoffError(f"материал {name} требует абсолютный локальный path")
        source = Path(raw_path)
        if not source.exists():
            if item.get("required", False):
                raise HandoffError(f"не найден обязательный материал {name}")
            warnings.append(f"необязательный материал {name} не найден")
            continue
        if source.is_symlink():
            raise HandoffError(f"материал {name} является символической ссылкой")
        candidates = [source] if source.is_file() else list(source.rglob("*"))
        for candidate in candidates:
            lowered_parts = {part.lower() for part in candidate.relative_to(source).parts}
            lowered_name = candidate.name.lower()
            if lowered_parts & {"secrets", ".ssh", "credentials"} or any(word in lowered_name for word in EXCLUDED_MATERIAL_WORDS) or lowered_name.endswith(SECRET_SUFFIXES):
                raise HandoffError(f"материал {name} содержит похожий на секрет файл/каталог ({candidate.name}); укажите узкий безопасный источник")
        target = destination / name
        if source.is_file():
            shutil.copy2(source, target)
            kind = "file"
        elif source.is_dir():
            shutil.copytree(source, target, symlinks=True)
            kind = "directory"
        else:
            raise HandoffError(f"материал {name} не является файлом или каталогом")
        copied.append({"name": name, "kind": kind, "path": f"materials/{name}"})
    return copied, warnings


def checksum_file(package: Path) -> None:
    paths = sorted(path for path in package.rglob("*") if path.is_file() and path.name != "checksums.sha256")
    (package / "checksums.sha256").write_text("".join(f"{sha256(path)}  {relative(path, package)}\n" for path in paths), encoding="utf-8")


def verify_package(package: Path) -> None:
    required = [package / "HANDOFF.md", package / "manifest.json", package / "checksums.sha256"]
    if any(not path.is_file() for path in required):
        raise HandoffError("не созданы обязательные файлы hand-off")
    manifest = json.loads((package / "manifest.json").read_text(encoding="utf-8"))
    for repo in manifest["repositories"]:
        run(["git", "bundle", "verify", str(package / repo["bundle"])])
    for line in (package / "checksums.sha256").read_text(encoding="utf-8").splitlines():
        digest, name = line.split("  ", 1)
        pure = PurePosixPath(name)
        if pure.is_absolute() or ".." in pure.parts or sha256(package.joinpath(*pure.parts)) != digest:
            raise HandoffError("контрольная сумма пакета не совпала")
    for archive in package.rglob("*.tar.gz"):
        with tarfile.open(archive, "r:gz") as value:
            value.getmembers()


def handoff_markdown(manifest: dict) -> str:
    rows = ["# Офлайн hand-off", "", f"Создан: {manifest['created_at']}", f"Машина: {manifest['machine']}", "",
            "| Репозиторий | Branch | HEAD | Состояние | Локальных коммитов | Modified | Untracked |",
            "| --- | --- | --- | --- | ---: | ---: | ---: |"]
    for repo in manifest["repositories"]:
        rows.append(f"| {repo['name']} | `{repo['branch']}` | `{repo['head'][:12]}` | {'clean' if repo['clean'] else 'dirty'} | {repo['local_commits']} | {repo['modified']} | {repo['untracked']} |")
    rows += ["", "Проверка целостности: OK.", "", "Пакет автономен: Git-история хранится в `repo.bundle`, а незакоммиченные tracked-изменения — в `dirty.patch`; untracked-файлы — в `untracked.tar.gz`."]
    if manifest["additional_materials"]:
        rows += ["", "Дополнительные материалы: " + ", ".join(item["name"] for item in manifest["additional_materials"]) + "."]
    if manifest["warnings"]:
        rows += ["", "Предупреждения:"] + [f"- {warning}" for warning in manifest["warnings"]]
    return "\n".join(rows) + "\n"


def export(config: dict, root: Path) -> Path:
    transit = Path(config["transit_root"])
    transit.mkdir(parents=True, exist_ok=True)
    machine = safe_name(config["machine"], "machine")
    stamp = dt.datetime.now().astimezone().strftime("%Y-%m-%d_%H-%M-%S")
    final = transit / f"{stamp}_{machine}"
    building = transit / f".building-{stamp}_{machine}-{uuid.uuid4().hex[:8]}"
    building.mkdir()
    try:
        repositories = [create_repo_package(name, path, building / "repositories" / name) for name, path in repository_entries(root, config)]
        materials, warnings = copy_materials(config, building / "materials")
        for repository in repositories:
            for excluded in repository.get("excluded_untracked", []):
                warnings.append(f"{repository['name']}/{excluded} не включён: это локальная конфигурация машины")
        created_at = dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat()
        manifest = {"format_version": FORMAT_VERSION, "created_at": created_at, "machine": machine,
                    "repositories": repositories, "additional_materials": materials, "warnings": warnings,
                    "integrity": {"status": "OK", "algorithm": "SHA-256"}}
        for warning in warnings:
            log("WARNING", warning)
        write_json(building / "manifest.json", manifest)
        (building / "HANDOFF.md").write_text(handoff_markdown(manifest), encoding="utf-8")
        checksum_file(building)
        verify_package(building)
        if final.exists():
            final = transit / f"{stamp}_{machine}-{uuid.uuid4().hex[:8]}"
        os.replace(building, final)
        latest_temp = transit / f".latest-{uuid.uuid4().hex}.tmp"
        latest_temp.write_text(final.name + "\n", encoding="utf-8")
        os.replace(latest_temp, transit / "latest.txt")
        with (transit / "handoff.log").open("a", encoding="utf-8") as stream:
            stream.write(f"{created_at} OK export {final.name}\n")
        log("OK", f"создан пакет {final}")
        return final
    except Exception as error:
        (building / "ERROR.log").write_text(f"ERROR: {error}\n", encoding="utf-8")
        log("ERROR", f"пакет не завершён; журнал: {building / 'ERROR.log'}")
        raise


def package_from_argument(transit: Path, argument: str | None) -> Path:
    if argument:
        path = Path(argument)
        return path if path.is_absolute() else transit / path
    latest = transit / "latest.txt"
    if not latest.is_file():
        raise HandoffError("latest.txt отсутствует; укажите пакет явно")
    return transit / latest.read_text(encoding="utf-8").strip()


def relation(local: Path, handoff_head: str) -> str:
    if run(["git", "-C", str(local), "merge-base", "--is-ancestor", handoff_head, "HEAD"], check=False).returncode == 0:
        count = git(local, ["rev-list", "--count", f"{handoff_head}..HEAD"]).strip()
        return "одинаковые истории" if count == "0" else f"local ahead by {count} commits"
    if run(["git", "-C", str(local), "merge-base", "--is-ancestor", "HEAD", handoff_head], check=False).returncode == 0:
        count = git(local, ["rev-list", "--count", f"HEAD..{handoff_head}"]).strip()
        return f"handoff ahead by {count} commits"
    return "histories diverged"


def inspect(config: dict, root: Path, argument: str | None) -> None:
    package = package_from_argument(Path(config["transit_root"]), argument)
    verify_package(package)
    manifest = json.loads((package / "manifest.json").read_text(encoding="utf-8"))
    local_repos = dict(repository_entries(root, config))
    log("OK", f"пакет {package.name} проверен")
    with (Path(config["transit_root"]) / "handoff.log").open("a", encoding="utf-8") as stream:
        stream.write(f"{dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat()} OK inspect {package.name}\n")
    for item in manifest["repositories"]:
        print(f"\n{item['name']}")
        local = local_repos.get(item["name"])
        print(f"handoff HEAD: {item['head']}")
        print(f"handoff branch: {item['branch']}")
        print(f"handoff worktree: {'clean' if item['clean'] else 'dirty'}")
        print(f"handoff modified: {item['modified']}")
        print(f"handoff untracked: {item['untracked']}")
        if local is None:
            print("local: репозиторий отсутствует")
            continue
        local_head = git(local, ["rev-parse", "HEAD"]).strip()
        modified, untracked, dirty = tracked_counts(local)
        print(f"local HEAD:   {local_head}")
        print(f"local worktree: {'dirty' if dirty else 'clean'} (modified {modified}, untracked {untracked})")
        print(relation(local, item["head"]))


def main() -> int:
    parser = argparse.ArgumentParser(description="Офлайн hand-off рабочего состояния Doctor")
    parser.add_argument("--config", type=Path, help="локальный handoff.local.json")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("export", help="создать новый автономный пакет")
    inspect_parser = sub.add_parser("inspect", help="сверить пакет без изменений локального дерева")
    inspect_parser.add_argument("package", nargs="?", help="имя или абсолютный путь пакета; по умолчанию latest")
    args = parser.parse_args()
    root = repo_root()
    config = read_config(args.config or root / "handoff.local.json")
    try:
        if args.command == "export":
            export(config, root)
        else:
            inspect(config, root, args.package)
        return 0
    except HandoffError as error:
        log("ERROR", str(error))
        return 2


if __name__ == "__main__":
    sys.exit(main())
