#!/usr/bin/env python3
"""Оснастка стенда: запуск проверки владельцем с неизменяемым пакетом журнала."""

import argparse
import datetime as dt
import hashlib
import json
import os
import platform
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time
from pathlib import Path


WRAPPER_FAILURE = 125
ID_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}\Z")


def utc_now():
    return dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")


def write_json_atomic(path, value):
    data = json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
    with tempfile.NamedTemporaryFile("w", encoding="utf-8", dir=path.parent,
                                     prefix=".run-", delete=False) as temp:
        temp.write(data)
        temp_name = temp.name
    os.replace(temp_name, path)


def git(root, *args):
    try:
        return subprocess.run(["git", *args], cwd=root, check=True,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE).stdout
    except (OSError, subprocess.CalledProcessError) as error:
        raise RuntimeError("Не удалось прочитать метаданные Git.") from error


def nul_paths(data):
    return [Path(item.decode("utf-8", "surrogateescape"))
            for item in data.split(b"\0") if item]


def source_snapshot(root, excluded):
    tracked = nul_paths(git(root, "ls-files", "-z"))
    untracked = nul_paths(git(root, "ls-files", "--others", "--exclude-standard", "-z"))
    records = []
    digest = hashlib.sha256()
    for relative in sorted(set(tracked + untracked), key=lambda p: p.as_posix()):
        path = root / relative
        try:
            if path.resolve().is_relative_to(excluded):
                continue
        except FileNotFoundError:
            pass
        name = relative.as_posix()
        if path.is_symlink():
            kind, value = "symlink", os.readlink(path).encode("utf-8", "surrogateescape")
            record_hash = hashlib.sha256(value).hexdigest()
        elif path.is_file():
            kind = "file"
            file_hash = hashlib.sha256()
            with path.open("rb") as source:
                for block in iter(lambda: source.read(1024 * 1024), b""):
                    file_hash.update(block)
            value = file_hash.digest()
            record_hash = file_hash.hexdigest()
        elif path.exists():
            kind, value = "other", b""
            record_hash = None
        else:
            kind, value = "deleted", b""
            record_hash = None
        encoded_name = name.encode("utf-8", "surrogateescape")
        encoded_kind = kind.encode("ascii")
        for part in (encoded_name, encoded_kind, value):
            digest.update(len(part).to_bytes(8, "big"))
            digest.update(part)
        records.append({"path": name, "kind": kind, "sha256": record_hash})
    return {"sha256": digest.hexdigest(), "files": records}


def stop_process(process):
    """Возвращает подтверждённый код дочернего процесса либо None.

    На Windows CTRL_BREAK_EVENT посылается новой группе, но потомки могут его
    проигнорировать: там остановка остаётся best effort.
    """
    if process.poll() is not None:
        return process.returncode
    try:
        if os.name == "posix":
            os.killpg(process.pid, signal.SIGTERM)
        elif hasattr(signal, "CTRL_BREAK_EVENT"):
            process.send_signal(signal.CTRL_BREAK_EVENT)
        else:
            process.terminate()
    except (OSError, ProcessLookupError):
        pass
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        if process.poll() is not None:
            return process.returncode
        time.sleep(0.05)
    try:
        if os.name == "posix":
            os.killpg(process.pid, signal.SIGKILL)
        else:
            process.kill()
    except (OSError, ProcessLookupError):
        pass
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        if process.poll() is not None:
            return process.returncode
        time.sleep(0.05)
    return None


class CheckParser(argparse.ArgumentParser):
    def error(self, message):
        self.print_usage(sys.stderr)
        self.exit(WRAPPER_FAILURE, f"{self.prog}: ошибка: {message}\n")


def parser():
    result = CheckParser(
        description="Запускает одну проверку и сохраняет пакет в exchange/checks/<id>.",
        epilog=("Пример: python3 lab/run-check.py --exchange /path/to/exchange "
                "--id parse-20260921 --request lab/check-request.example.md -- "
                "dotnet test PsDoctor.slnx\n\nНе передавайте секреты в аргументах: argv сохраняется в run.json. "
                "В Windows отмена группы процессов выполняется по мере возможностей ОС."),
        formatter_class=argparse.RawDescriptionHelpFormatter)
    result.add_argument("--exchange", required=True, type=Path, help="корень папки обмена")
    result.add_argument("--id", required=True, help="новый идентификатор прогона")
    result.add_argument("--request", required=True, type=Path, help="подготовленное задание")
    result.add_argument("--cwd", type=Path, help="рабочий каталог команды; по умолчанию корень Git")
    result.add_argument("command", nargs=argparse.REMAINDER, help="после --: команда и её аргументы")
    return result


def main():
    args = parser().parse_args()
    if (not ID_RE.fullmatch(args.id) or not args.command
            or args.command[0] != "--" or len(args.command) == 1):
        parser().error("нужны безопасный id и команда после --")
    command = args.command[1:]
    if not args.request.is_file():
        parser().error("файл задания должен существовать")

    try:
        root = Path(git(Path.cwd(), "rev-parse", "--show-toplevel").decode().strip()).resolve()
        exchange = args.exchange.expanduser().resolve()
        if root.is_relative_to(exchange):
            raise RuntimeError("Папка exchange не может быть корнем репозитория или его предком.")
        check_dir = exchange / "checks" / args.id
        try:
            check_dir.mkdir(parents=True, exist_ok=False)
        except FileExistsError:
            print("Пакет с таким id уже существует; перезапись запрещена.", file=sys.stderr)
            return WRAPPER_FAILURE
        (check_dir / "results").mkdir()
        cwd = (args.cwd if args.cwd else root).expanduser()
        cwd = (root / cwd).resolve() if not cwd.is_absolute() else cwd.resolve()
        if not cwd.is_dir():
            raise RuntimeError("Рабочий каталог команды недоступен.")
        request = check_dir / "request.md"
        shutil.copyfile(args.request, request)
        before = source_snapshot(root, exchange)
        status = git(root, "status", "--porcelain=v1", "--untracked-files=all").decode("utf-8", "replace")
        run = {"started_at": utc_now(), "repository": str(root),
               "head": git(root, "rev-parse", "HEAD").decode().strip(), "git_status": status,
               "host": platform.node(), "platform": platform.platform(), "cwd": str(cwd),
               "argv": command, "source_before": before["sha256"]}
        write_json_atomic(check_dir / "source.json", {"before": before})
        write_json_atomic(check_dir / "run.json", run)
    except (OSError, RuntimeError) as error:
        print(f"Подготовить пакет проверки не удалось: {error}", file=sys.stderr)
        return WRAPPER_FAILURE

    print(f"Пакет проверки: {check_dir}", file=sys.stderr)
    interrupted = False
    child_returncode = None
    wrapper_error = None
    termination_confirmed = True
    process = None
    try:
        options = {"cwd": cwd, "stdout": subprocess.PIPE, "stderr": subprocess.STDOUT}
        if os.name == "posix":
            options["start_new_session"] = True
        elif hasattr(subprocess, "CREATE_NEW_PROCESS_GROUP"):
            options["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
        with (check_dir / "output.log").open("wb") as output:
            process = subprocess.Popen(command, **options)
            try:
                while chunk := process.stdout.read1(65536):
                    output.write(chunk)
                    output.flush()
                    sys.stdout.buffer.write(chunk)
                    sys.stdout.buffer.flush()
                child_returncode = process.wait()
            except KeyboardInterrupt:
                interrupted = True
                child_returncode = stop_process(process)
                termination_confirmed = child_returncode is not None
                if not termination_confirmed:
                    wrapper_error = "После отмены не подтверждено завершение дочернего процесса."
    except (OSError, subprocess.SubprocessError) as error:
        wrapper_error = f"{type(error).__name__}: {error}"
    finally:
        try:
            if process is not None and process.poll() is None:
                interrupted = True
                child_returncode = stop_process(process)
                termination_confirmed = child_returncode is not None
                if not termination_confirmed:
                    wrapper_error = "После отмены не подтверждено завершение дочернего процесса."
            try:
                after = source_snapshot(root, exchange)
                source_comparison = {
                    "status": "different" if before["sha256"] != after["sha256"] else "same",
                    "before": before["sha256"], "after": after["sha256"],
                    "note": "Сравнение снимков исходников; оно не подтверждает результат проверки."
                }
            except (OSError, RuntimeError) as error:
                after = None
                source_comparison = {"status": "unavailable",
                                     "note": "После запуска снимок исходников не получен."}
                wrapper_error = wrapper_error or f"{type(error).__name__}: {error}"
            with (check_dir / "source.json").open("r", encoding="utf-8") as source_file:
                sources = json.load(source_file)
            if after is not None:
                sources["after"] = after
            write_json_atomic(check_dir / "source.json", sources)
            runner_exit_code = (WRAPPER_FAILURE if wrapper_error else
                                130 if interrupted else child_returncode)
            run.update({"finished_at": utc_now(), "child_returncode": child_returncode,
                        "runner_exit_code": runner_exit_code, "interrupted": interrupted,
                        "termination_confirmed": termination_confirmed,
                        "source_comparison": source_comparison,
                        "wrapper_error": wrapper_error})
            write_json_atomic(check_dir / "run.json", run)
            if termination_confirmed:
                (check_dir / "exit.txt").write_text(f"{runner_exit_code}\n", encoding="ascii")
        except (OSError, RuntimeError, json.JSONDecodeError):
            return WRAPPER_FAILURE
    return runner_exit_code


if __name__ == "__main__":
    sys.exit(main())
