#!/usr/bin/env python3
"""Оснастка разработки: перенос работы Doctor между машинами без участия ИИ.

У переноса одна машина-источник. export собирает на накопитель всё, без чего
работу не продолжить: Git обоих репозиториев, выжимку свежих чатов и их копии,
память агента, пакеты проверок, artifacts/ и локальный профиль для справки.
digest делает ту же выжимку отдельно — чтобы сверить её до export. receive сверяет пакет с этой машиной
и без --apply ничего не меняет; с --apply только перематывает Git вперёд и
докладывает файлы. Слияния нет: любое расхождение — остановка с объяснением.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import uuid


FORMAT_VERSION = 3
ACCEPTED_FORMATS = (2, 3)
JOURNAL = Path("memory") / "Ход работы.md"
BACKUP_DIR = ".handoff-backup"


class HandoffError(Exception):
    pass


def log(level: str, message: str) -> None:
    print(f"{level}: {message}")


def run(arguments: list[str], *, check: bool = True) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(arguments, text=True, encoding="utf-8", errors="replace", capture_output=True)
    if check and result.returncode:
        detail = result.stderr.strip() or result.stdout.strip() or "неизвестная ошибка"
        raise HandoffError(f"{' '.join(arguments[:4])}: код {result.returncode}: {detail}")
    return result


def git(repo: Path, *arguments: str, check: bool = True) -> str:
    return run(["git", "-C", str(repo), *arguments], check=check).stdout.strip()


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def files_under(root: Path) -> list[Path]:
    return sorted(path for path in root.rglob("*") if path.is_file() and not path.is_symlink())


def same_file(left: Path, right: Path) -> bool:
    return left.stat().st_size == right.stat().st_size and sha256(left) == sha256(right)


def repo_root() -> Path:
    return Path(git(Path(__file__).resolve().parent, "rev-parse", "--show-toplevel"))


def read_config(path: Path) -> dict:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise HandoffError(f"не прочитан локальный конфиг {path}; шаблон — handoff.local.example.json") from error
    for name in ("machine", "transit_root", "exchange_checks"):
        if not isinstance(value.get(name), str) or not value[name]:
            raise HandoffError(f"в {path.name} требуется непустой {name}")
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]*", value["machine"]):
        raise HandoffError("machine — только латиница, цифры, _ и -")
    return value


def claude_projects() -> Path:
    base = os.environ.get("CLAUDE_CONFIG_DIR")
    return (Path(base) if base else Path.home() / ".claude") / "projects"


def chats_name(root: Path) -> str:
    """Имя каталога чатов Claude Code: путь проекта, где всё, кроме букв и цифр, заменено на '-'."""
    return re.sub(r"[^A-Za-z0-9]", "-", str(root))


def repositories(root: Path) -> dict[str, Path]:
    found = {"doctor": root}
    if (root / "Memorex" / ".git").exists():
        found["memorex"] = root / "Memorex"
    return found


def journal_tail(memorex: Path | None) -> str:
    if memorex is None or not (memorex / JOURNAL).is_file():
        return ""
    text = (memorex / JOURNAL).read_text(encoding="utf-8")
    sections = re.split(r"(?m)^(?=## )", text)
    return sections[-1].strip() if len(sections) > 1 else ""


# ---------- выжимка чатов ----------

def parse_time(value: object) -> dt.datetime | None:
    if not isinstance(value, str) or not value:
        return None
    try:
        moment = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None
    return moment if moment.tzinfo else moment.astimezone()


def last_export_time(config: dict) -> dt.datetime:
    """Время последнего пакета этой машины в transit_root; пакетов нет — начало сегодняшнего дня."""
    latest = None
    transit = Path(config["transit_root"])
    for manifest_path in transit.glob("*/manifest.json") if transit.is_dir() else []:
        try:
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        created = parse_time(manifest.get("created_at"))
        if manifest.get("machine") == config["machine"] and created and (latest is None or created > latest):
            latest = created
    return latest or dt.datetime.combine(dt.date.today(), dt.time()).astimezone()


def fresh_sessions(chats: Path, since: dt.datetime) -> list[Path]:
    if not chats.is_dir():
        return []
    return sorted((path for path in chats.glob("*.jsonl")
                   if dt.datetime.fromtimestamp(path.stat().st_mtime).astimezone() > since),
                  key=lambda path: path.stat().st_mtime)


SERVICE_BLOCK = re.compile(r"<(system-reminder|local-command-stdout|local-command-stderr|local-command-caveat)>.*?</\1>", re.S)


def owner_text(content: object) -> str:
    if isinstance(content, list):
        content = "\n".join(block.get("text", "") for block in content
                            if isinstance(block, dict) and block.get("type") == "text")
    if not isinstance(content, str):
        return ""
    command = re.search(r"<command-name>(.*?)</command-name>", content, re.S)
    if command:
        arguments = re.search(r"<command-args>(.*?)</command-args>", content, re.S)
        return f"{command.group(1).strip()} {arguments.group(1).strip() if arguments else ''}".strip()
    text = SERVICE_BLOCK.sub("", content).strip()
    return "" if text.startswith("[Request interrupted") else text


def read_session(path: Path, since: dt.datetime) -> dict:
    """Реплики владельца и агента после since: без вывода инструментов, размышлений и служебных вставок."""
    session = {"id": path.stem, "title": "", "transfer": False, "turns": []}
    with path.open(encoding="utf-8", errors="replace") as stream:
        for line in stream:
            try:
                record = json.loads(line)
            except json.JSONDecodeError:
                continue
            kind = record.get("type")
            if kind == "custom-title":
                session["title"] = record.get("customTitle") or session["title"]
                continue
            if record.get("isSidechain") or record.get("isMeta"):
                continue
            message = record.get("message") or {}
            speaker, text = None, ""
            if kind == "user":
                speaker, text = "Владелец", owner_text(message.get("content"))
                if text.startswith("/handoff"):
                    session["transfer"] = True
            elif kind == "attachment":
                attachment = record.get("attachment") or {}
                if attachment.get("type") == "queued_command" and attachment.get("commandMode") == "prompt":
                    speaker, text = "Владелец", owner_text(attachment.get("prompt"))
            elif kind == "assistant" and isinstance(message.get("content"), list):
                for block in message["content"]:
                    if block.get("type") == "tool_use" and block.get("name") == "Skill" \
                            and (block.get("input") or {}).get("skill") == "handoff":
                        session["transfer"] = True
                speaker = "Агент"
                text = "\n".join(block.get("text", "") for block in message["content"]
                                 if block.get("type") == "text").strip()
            moment = parse_time(record.get("timestamp"))
            if not speaker or not text or (moment and moment <= since):
                continue
            session["turns"].append((speaker, moment, text))
    return session


def render_digest(machine: str, since: dt.datetime, sessions: list[dict]) -> str:
    now = dt.datetime.now().astimezone().replace(microsecond=0)
    lines = [f"# Выжимка чатов — {machine}, {since.replace(microsecond=0).isoformat()} … {now.isoformat()}", "",
             "Реплики владельца и агента из сессий этой машины после прошлого пакета, без вывода инструментов. "
             "Сверить с коммитами и Memorex: что сказано здесь и не записано нигде, — владельцу списком.", ""]
    for session in sessions:
        if not session["turns"]:
            continue
        first, last = session["turns"][0][1], session["turns"][-1][1]
        span = f"{first.astimezone():%d.%m %H:%M}–{last.astimezone():%H:%M}" if first and last else ""
        mark = ", чат переноса" if session["transfer"] else ""
        lines += [f"## {session['title'] or 'без заголовка'} — `{session['id'][:8]}`, {span}{mark}", ""]
        for speaker, moment, text in session["turns"]:
            stamp = f", {moment.astimezone():%H:%M}" if moment else ""
            lines += [f"**{speaker}{stamp}:**", "", text, ""]
    if len(lines) == 4:
        lines += ["Реплик нет.", ""]
    return "\n".join(lines)


def build_digest(machine: str, sessions: list[Path], since: dt.datetime) -> tuple[str, int]:
    read = [read_session(path, since) for path in sessions]
    return render_digest(machine, since, read), sum(1 for session in read if session["turns"])


def digest(config: dict, root: Path, source: Path | None, since_text: str | None, out: Path | None) -> Path:
    if since_text:
        since = parse_time(since_text)
        if since is None:
            raise HandoffError(f"--since: не разобрано время «{since_text}»; нужен ISO 8601, например 2026-09-28T09:00")
    else:
        since = last_export_time(config) if source is None else dt.datetime.fromtimestamp(0).astimezone()
    chats = source or claude_projects() / chats_name(root)
    sessions = fresh_sessions(chats, since)
    text, count = build_digest(config["machine"] if source is None else str(source), sessions, since)
    target = out or Path(config.get("digests", Path.home() / "Lab" / "digests")) / f"{dt.date.today().isoformat()}-{config['machine']}.md"
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(text, encoding="utf-8")
    log("OK", f"выжимка {target}: сессий с репликами {count}, с {since.replace(microsecond=0).isoformat()}, {len(text.encode('utf-8')) // 1024} КБ")
    return target


# ---------- export ----------

def check_source_repo(name: str, repo: Path, push: bool) -> dict:
    dirty = git(repo, "status", "--porcelain=v1").splitlines()
    if dirty:
        listing = "\n  ".join(dirty[:20]) + ("\n  …" if len(dirty) > 20 else "")
        raise HandoffError(f"{name}: незакоммиченные изменения — закоммитьте или отложите сами:\n  {listing}")
    branch = git(repo, "branch", "--show-current")
    if not branch:
        raise HandoffError(f"{name}: HEAD отсоединён; перенос делается с ветки")
    if run(["git", "-C", str(repo), "rev-parse", "--abbrev-ref", "@{u}"], check=False).returncode:
        raise HandoffError(f"{name}: у ветки {branch} нет upstream")
    if run(["git", "-C", str(repo), "fetch", "--quiet"], check=False).returncode:
        log("WARNING", f"{name}: fetch не прошёл (нет сети?); отправленность сверяется с последним известным origin")
    ahead = int(git(repo, "rev-list", "--count", "@{u}..HEAD"))
    behind = int(git(repo, "rev-list", "--count", "HEAD..@{u}"))
    if behind:
        raise HandoffError(f"{name}: в origin на {behind} коммит(ов) больше, чем здесь — источник устарел; сначала заберите их")
    if ahead:
        if not push:
            raise HandoffError(f"{name}: {ahead} неотправленных коммит(ов); отправьте сами или запустите export --push")
        run(["git", "-C", str(repo), "push", "--quiet"])
        log("OK", f"{name}: отправлено {ahead} коммит(ов)")
    return {"branch": branch, "head": git(repo, "rev-parse", "HEAD")}


def copy_tree(source: Path, target: Path, skip: tuple[str, ...] = ()) -> None:
    shutil.copytree(source, target, symlinks=False, ignore=shutil.ignore_patterns("__pycache__", *skip))


def readme(manifest: dict) -> str:
    repos = manifest["repositories"]
    lines = [f"# Перенос работы Doctor — {manifest['name']}", "",
             f"Собрано `scripts/handoff.py export` на машине {manifest['machine']}, {manifest['created_at']}. "
             + ", ".join(f"{name} `{info['branch']}` = `{info['head'][:7]}`" for name, info in repos.items())
             + "; всё закоммичено и отправлено в origin.", "", "## Где остановились", "",
             manifest["journal"] or "Раздела нет: страница `Memorex/memory/Ход работы.md` пуста или отсутствует.", "",
             "## Состав", "",
             "- `git/*.bundle` — все ветки репозиториев, если на приёме нет сети.",
             f"- `chats/digest.md` — выжимка сессий этой машины с {manifest['chats']['since']} ({manifest['chats']['sessions']} с репликами).",
             "- `chats/sessions/` — копии тех же сессий `.jsonl`: если выжимку не успели сверить на источнике, из них это делается на приёме.",
             "- `chats/memory/` — память агента.",
             "- `experiments/lab-exchange-checks/` — пакеты проверок.",
             "- `experiments/repo-artifacts/` — игнорируемый `artifacts/` репозитория.",
             "- `context/CLAUDE.local.md` — профиль машины-источника, только для справки.", "",
             "Не включены: ВМ, `inbound/`, хранилище проектов, ключи, `handoff.local.json`, `bin/obj`.", "",
             "## Приёмка", "", "```text", f"python3 scripts/handoff.py receive {manifest['name']}",
             f"python3 scripts/handoff.py receive {manifest['name']} --apply", "```", "",
             "Первая команда только сверяет и показывает план; вторая выполняет, если сверка не нашла препятствий.", ""]
    return "\n".join(lines)


def export(config: dict, root: Path, push: bool) -> Path:
    repos = repositories(root)
    heads = {name: check_source_repo(name, path, push) for name, path in repos.items()}
    transit = Path(config["transit_root"])
    transit.mkdir(parents=True, exist_ok=True)
    base = f"{dt.date.today().isoformat()}-{config['machine']}"
    name, number = base, 2
    while (transit / name).exists():
        name, number = f"{base}-{number}", number + 1
    building = transit / f".building-{name}-{uuid.uuid4().hex[:8]}"
    building.mkdir()
    try:
        (building / "git").mkdir()
        for repo_name, path in repos.items():
            bundle = building / "git" / f"{repo_name}.bundle"
            run(["git", "-C", str(path), "bundle", "create", str(bundle), "--branches", "--tags"])
            run(["git", "-C", str(path), "bundle", "verify", str(bundle)])
        chats = claude_projects() / chats_name(root)
        since = last_export_time(config)
        sessions = fresh_sessions(chats, since)
        (building / "chats" / "sessions").mkdir(parents=True)
        for session in sessions:
            shutil.copy2(session, building / "chats" / "sessions" / session.name)
        digest_text, digest_count = build_digest(config["machine"], sessions, since)
        (building / "chats" / "digest.md").write_text(digest_text, encoding="utf-8")
        if (chats / "memory").is_dir():
            copy_tree(chats / "memory", building / "chats" / "memory")
        elif not chats.is_dir():
            log("WARNING", f"каталог чатов не найден: {chats}")
        checks = Path(config["exchange_checks"])
        if checks.is_dir():
            copy_tree(checks, building / "experiments" / "lab-exchange-checks")
        else:
            log("WARNING", f"exchange_checks не найден: {checks}")
        if (root / "artifacts").is_dir():
            copy_tree(root / "artifacts", building / "experiments" / "repo-artifacts", (BACKUP_DIR,))
        if (root / "CLAUDE.local.md").is_file():
            (building / "context").mkdir()
            shutil.copy2(root / "CLAUDE.local.md", building / "context" / "CLAUDE.local.md")
        journal = journal_tail(repos.get("memorex"))
        if not journal:
            log("WARNING", "нет раздела в Memorex/memory/Ход работы.md — «где остановились» будет пустым")
        manifest = {"format": FORMAT_VERSION, "name": name, "machine": config["machine"],
                    "created_at": dt.datetime.now().astimezone().replace(microsecond=0).isoformat(),
                    "repositories": heads, "journal": journal,
                    "chats": {"since": since.replace(microsecond=0).isoformat(), "sessions": digest_count,
                              "copies": len(sessions)}}
        (building / "README.md").write_text(readme(manifest), encoding="utf-8")
        manifest["files"] = {path.relative_to(building).as_posix(): sha256(path) for path in files_under(building)}
        (building / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.replace(building, transit / name)
    except BaseException:
        log("ERROR", f"пакет не собран; неполный каталог оставлен для разбора: {building}")
        raise
    log("OK", f"пакет {transit / name}")
    log("OK", f"чаты: сессий с репликами {digest_count} с {manifest['chats']['since']}, копий .jsonl {len(sessions)}")
    return transit / name


# ---------- receive ----------

class Plan:
    def __init__(self) -> None:
        self.blockers: list[str] = []
        self.steps: list[tuple[str, object]] = []
        self.notes: list[str] = []

    def block(self, text: str) -> None:
        self.blockers.append(text)

    def step(self, text: str, action: object = None) -> None:
        self.steps.append((text, action))


def load_package(config: dict, argument: str) -> tuple[Path, dict]:
    package = Path(argument)
    if not package.is_absolute():
        package = Path(config["transit_root"]) / package
    try:
        manifest = json.loads((package / "manifest.json").read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise HandoffError(f"нет manifest.json в {package}; это не пакет export") from error
    if manifest.get("format") not in ACCEPTED_FORMATS:
        raise HandoffError(f"формат пакета {manifest.get('format')} не поддерживается (нужен один из {ACCEPTED_FORMATS})")
    broken = [name for name, digest in manifest["files"].items()
              if not (package / name).is_file() or sha256(package / name) != digest]
    if broken:
        raise HandoffError("пакет повреждён, не совпали контрольные суммы:\n  " + "\n  ".join(broken[:20]))
    return package, manifest


def plan_repo(plan: Plan, name: str, repo: Path | None, info: dict, bundle: Path) -> None:
    if repo is None:
        plan.block(f"{name}: репозитория здесь нет — склонируйте его, затем повторите")
        return
    dirty = git(repo, "status", "--porcelain=v1").splitlines()
    if dirty:
        plan.block(f"{name}: здесь есть незакоммиченное ({len(dirty)}), первое: {dirty[0].strip()} — закоммитьте и отправьте или отложите сами")
    branch = git(repo, "branch", "--show-current")
    if branch != info["branch"]:
        plan.block(f"{name}: здесь ветка «{branch or 'отсоединён'}», в пакете «{info['branch']}»")
        return
    head, target = git(repo, "rev-parse", "HEAD"), info["head"]
    if head == target:
        plan.notes.append(f"{name}: уже на {target[:7]}")
        return
    if run(["git", "-C", str(repo), "cat-file", "-e", f"{target}^{{commit}}"], check=False).returncode:
        run(["git", "-C", str(repo), "fetch", "--quiet"], check=False)
    if run(["git", "-C", str(repo), "cat-file", "-e", f"{target}^{{commit}}"], check=False).returncode:
        run(["git", "-C", str(repo), "fetch", "--quiet", str(bundle), f"+refs/heads/*:refs/handoff/{name}/*"])
    if run(["git", "-C", str(repo), "merge-base", "--is-ancestor", head, target], check=False).returncode == 0:
        count = git(repo, "rev-list", "--count", f"{head}..{target}")
        plan.step(f"{name}: перемотать {branch} вперёд на {count} коммит(ов) до {target[:7]}",
                  lambda: run(["git", "-C", str(repo), "merge", "--ff-only", "--quiet", target]))
    elif run(["git", "-C", str(repo), "merge-base", "--is-ancestor", target, head], check=False).returncode == 0:
        count = git(repo, "rev-list", "--count", f"{target}..{head}")
        plan.block(f"{name}: здесь {count} коммит(ов), которых нет в пакете — пакет старее этой машины или источник собран без них")
    else:
        plan.block(f"{name}: история здесь и в пакете разошлась — сливать скрипт не будет, разберитесь вручную")


def copy_file(source: Path, target: Path) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)


def plan_checks(plan: Plan, source: Path, target: Path) -> None:
    if not source.is_dir():
        return
    added = 0
    for item in sorted(source.iterdir()):
        local = target / item.name
        if not local.exists():
            added += 1
            plan.step(f"проверка {item.name}: добавить", lambda s=item, t=local: shutil.copytree(s, t))
            continue
        theirs = {p.relative_to(item) for p in files_under(item)}
        ours = {p.relative_to(local) for p in files_under(local)}
        if theirs != ours or any(not same_file(item / p, local / p) for p in theirs):
            plan.block(f"проверка {item.name}: здесь есть одноимённая с другим содержимым — результаты проверок не перезаписываются")
    if not added:
        plan.notes.append("пакеты проверок: новых нет")


def plan_artifacts(plan: Plan, source: Path, target: Path, package_name: str) -> None:
    if not source.is_dir():
        return
    added = replaced = 0
    backup = target / BACKUP_DIR / package_name
    for path in files_under(source):
        relative = path.relative_to(source)
        local = target / relative
        if not local.exists():
            added += 1
            plan.step("", lambda s=path, t=local: copy_file(s, t))
        elif not same_file(path, local):
            replaced += 1
            plan.step("", lambda s=path, t=local, b=backup / relative: (copy_file(t, b), copy_file(s, t)))
    if added or replaced:
        plan.step(f"artifacts/: добавить {added}, заменить {replaced}" + (f" (прежние — в artifacts/{BACKUP_DIR}/{package_name}/)" if replaced else ""))
    else:
        plan.notes.append("artifacts/: совпадает с пакетом")


def plan_digest(plan: Plan, source: Path, config: dict, manifest: dict) -> None:
    """Формат 3: выжимка и копии сессий кладутся для чтения; продолжать чужую сессию здесь нельзя."""
    target = Path(config.get("chats_readonly", Path.home() / "Lab" / "chats")) / manifest["name"]
    if not (source / "digest.md").is_file():
        return
    if (target / "digest.md").is_file() and same_file(source / "digest.md", target / "digest.md"):
        plan.notes.append(f"выжимка чатов: уже лежит в {target}")
        return
    for path in [source / "digest.md", *files_under(source / "sessions")]:
        plan.step("", lambda s=path, t=target / path.relative_to(source): copy_file(s, t))
    info = manifest["chats"]
    plan.step(f"выжимка чатов: {info['sessions']} сесс. с репликами с {info['since']}, копий .jsonl {info['copies']} → {target / 'digest.md'}")


def plan_memory(plan: Plan, memory_source: Path, root: Path) -> None:
    if not memory_source.is_dir():
        return
    memory_target = claude_projects() / chats_name(root) / "memory"
    for path in files_under(memory_source):
        relative = path.relative_to(memory_source)
        local = memory_target / relative
        if not local.exists():
            plan.step(f"память агента: добавить {relative}", lambda s=path, t=local: copy_file(s, t))
        elif not same_file(path, local):
            plan.notes.append(f"память агента: {relative} различается — оставлен здешний, сведите вручную или через skill")


def plan_chats(plan: Plan, source: Path, root: Path, config: dict, package_name: str) -> None:
    """Формат 2: полные копии каталога чатов."""
    if not source.is_dir():
        return
    local_chats = claude_projects() / chats_name(root)
    if source.name == local_chats.name:
        target, readonly = local_chats, False
    else:
        target, readonly = Path(config.get("chats_readonly", Path.home() / "Lab" / "chats")) / package_name, True
    added = replaced = 0
    for path in files_under(source):
        relative = path.relative_to(source)
        if relative.parts[0] == "memory":
            continue
        local = target / relative
        if not local.exists():
            added += 1
            plan.step("", lambda s=path, t=local: copy_file(s, t))
        elif not same_file(path, local):
            if path.stat().st_size > local.stat().st_size:
                replaced += 1
                plan.step("", lambda s=path, t=local: copy_file(s, t))
            else:
                plan.notes.append(f"чат {relative}: здесь не короче, чем в пакете — оставлен здешний")
    where = f"{target} — только для чтения: путь проекта на той машине другой, --resume их не найдёт" if readonly else str(target)
    if added or replaced:
        plan.step(f"чаты: добавить {added}, дополнить {replaced} → {where}")
    else:
        plan.notes.append("чаты: новых нет")
    plan_memory(plan, source / "memory", root)


def receive(config: dict, root: Path, argument: str, apply: bool) -> int:
    package, manifest = load_package(config, argument)
    log("OK", f"пакет {manifest['name']} с машины {manifest['machine']}, {manifest['created_at']}: контрольные суммы совпали")
    plan = Plan()
    local_repos = repositories(root)
    for name, info in manifest["repositories"].items():
        plan_repo(plan, name, local_repos.get(name), info, package / "git" / f"{name}.bundle")
    plan_checks(plan, package / "experiments" / "lab-exchange-checks", Path(config["exchange_checks"]))
    plan_artifacts(plan, package / "experiments" / "repo-artifacts", root / "artifacts", manifest["name"])
    if manifest["format"] == 2:
        plan_chats(plan, package / "chats" / manifest["chats"], root, config, manifest["name"])
    else:
        plan_digest(plan, package / "chats", config, manifest)
        plan_memory(plan, package / "chats" / "memory", root)
    plan.notes.append("CLAUDE.local.md не трогается: у этой машины свой профиль")

    for note in plan.notes:
        print(f"  = {note}")
    for text, _ in plan.steps:
        if text:
            print(f"  + {text}")
    for text in plan.blockers:
        print(f"  ! {text}")
    print("\nГде остановились:\n")
    print(manifest["journal"] or "(раздела нет)")
    print()
    if plan.blockers:
        log("STOP", f"препятствий: {len(plan.blockers)}; ничего не изменено")
        return 1
    if not apply:
        log("OK", "препятствий нет; выполнить — та же команда с --apply")
        return 0
    for _, action in plan.steps:
        if callable(action):
            action()
    log("OK", "принято")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Перенос работы Doctor между машинами")
    parser.add_argument("--config", type=Path, help="локальный handoff.local.json; по умолчанию в корне репозитория")
    sub = parser.add_subparsers(dest="command", required=True)
    export_parser = sub.add_parser("export", help="собрать пакет на накопитель")
    export_parser.add_argument("--push", action="store_true", help="отправить неотправленные коммиты вместо остановки")
    digest_parser = sub.add_parser("digest", help="выжимка реплик из сессий агента после прошлого пакета этой машины")
    digest_parser.add_argument("--since", help="с какого времени (ISO 8601); по умолчанию — время прошлого пакета этой машины")
    digest_parser.add_argument("--from", dest="source", type=Path, help="каталог с .jsonl, например принятые чаты; тогда по умолчанию — все сессии")
    digest_parser.add_argument("--out", type=Path, help="куда записать; по умолчанию ~/Lab/digests/<дата>-<машина>.md")
    receive_parser = sub.add_parser("receive", help="сверить пакет с этой машиной; с --apply — принять")
    receive_parser.add_argument("package", help="имя пакета в transit_root или путь к нему")
    receive_parser.add_argument("--apply", action="store_true", help="выполнить план, если препятствий нет")
    args = parser.parse_args()
    try:
        root = repo_root()
        config = read_config(args.config or root / "handoff.local.json")
        if args.command == "export":
            export(config, root, args.push)
            return 0
        if args.command == "digest":
            digest(config, root, args.source, args.since, args.out)
            return 0
        return receive(config, root, args.package, args.apply)
    except HandoffError as error:
        log("ERROR", str(error))
        return 2


if __name__ == "__main__":
    sys.exit(main())
