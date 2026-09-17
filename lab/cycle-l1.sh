#!/bin/bash
# Цикл Л1 через наблюдатель (пункт 2 критерия Э4.0): проект старого формата с недостающим шрифтом —
# запуск, оба диалога, заголовок, закрытие, выход — N раз подряд с Linux-хоста, без человека.
#   SHOW_FILE="<файл шоу>" lab/cycle-l1.sh [прогонов]    — по умолчанию 10
#
# Имя файла шоу передаётся переменной и в скрипте не пишется: имена проектов в открытый репозиторий не попадают.
#
# Перед каждым прогоном проект заново кладётся на стенд из папки обмена: программа переписывает .pxc
# при закрытии. Журналы прогонов — в $OUT (строка на факт), итог — строка JSON на прогон в stdout.
# Переменные: LAB_HOST, LAB_KEY, PROJECT_SOURCE (\\VBoxSvr\exchange\projects\p1), PROJECT_DIR (C:\lab\p1),
# SHOW_FILE (обязательна), PSDOCTOR_OBSERVER_URL, PSDOCTOR_OBSERVER_KEY_FILE, OUT (каталог журналов).
set -euo pipefail
. "$(dirname "$0")/portable.sh"

runs="${1:-10}"
host="${LAB_HOST:-192.168.56.5}"
lab_key="${LAB_KEY:-$HOME/.ssh/lab_ed25519}"
source_dir="${PROJECT_SOURCE:-\\\\VBoxSvr\\exchange\\projects\\p1}"
project_dir="${PROJECT_DIR:-C:\\lab\\p1}"
show="${SHOW_FILE:?укажите SHOW_FILE — имя файла шоу в каталоге проекта}"
export PSDOCTOR_OBSERVER_URL="${PSDOCTOR_OBSERVER_URL:-http://$host:8100}"
export PSDOCTOR_OBSERVER_KEY_FILE="${PSDOCTOR_OBSERVER_KEY_FILE:-$HOME/Lab/secrets/observer.key}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
out="${OUT:-$repo/artifacts/lab/cycle-l1/$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$out"
ssh_opts=(-o BatchMode=yes -o IdentitiesOnly=yes -o ConnectTimeout=10 -i "$lab_key")

dotnet build "$repo/src/PsDoctor.Cli" --nologo -v q >/dev/null
psdoctor=(dotnet "$repo/src/PsDoctor.Cli/bin/Debug/net10.0/psdoctor.dll")

scenario="launch \"$project_dir\\$show\"
wait dialog 120
press \"ОК\"
wait dialog 30
press \"Ok\"
wait title \"$show\" 600
close
wait exit 60"

passed=0
for run in $(seq 1 "$runs"); do
  ssh "${ssh_opts[@]}" "user@$host" "robocopy \"$source_dir\" \"$project_dir\" /MIR /R:3 /W:1 /NJH /NJS /NP /NFL /NDL >nul & exit /b 0"
  journal="$out/run-$run.jsonl"
  set +e
  printf '%s\n' "$scenario" | "${psdoctor[@]}" observe run - --follow > "$journal" 2> "$out/run-$run.err"
  code=$?
  set -e
  summary="$("$PYTHON" - "$journal" "$run" "$code" "$show" <<'EOF'
import json, sys
path, run, code, show = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), sys.argv[4]
facts = [json.loads(line) for line in open(path, encoding='utf-8') if line.strip()]
def seconds(f):
    h, m, s = f['elapsed'].split(':')
    return round(int(h) * 3600 + int(m) * 60 + float(s), 2)
finished = [f for f in facts if f['kind'] == 'scenario-finished']
presses = [f['data'] for f in facts if f['kind'] == 'dialog-pressed']
dialogs = [(seconds(f), f['data']['title'], f['data']['buttons']) for f in facts if f['kind'] == 'dialog-opened']
titles = [seconds(f) for f in facts if f['kind'] == 'main-window' and show in (f['data']['title'] or '')]
session_end = [f['data']['reason'] for f in facts if f['kind'] == 'session-finished']
result = {
    'run': run,
    'code': code,
    'status': finished[0]['data']['status'] if finished else None,
    'failed_line': finished[0]['data']['line'] if finished else None,
    'reason': finished[0]['data']['reason'] if finished else None,
    'dialogs': [{'t': t, 'title': title, 'buttons': buttons} for t, title, buttons in dialogs],
    'presses': [{'text': p['text'], 'result': p['result'], 'attempts': p['attempts'], 'cursor_moved': p['cursorMoved']} for p in presses],
    'title_with_file_t': titles[0] if titles else None,
    'session_end': session_end[0] if session_end else None,
    'end_t': seconds(facts[-1]) if facts else None,
}
result['ok'] = (result['status'] == 'completed' and len(presses) == 2
                and not any(p['cursorMoved'] for p in presses) and result['session_end'] == 'program-exited')
print(json.dumps(result, ensure_ascii=False))
EOF
)"
  echo "$summary"
  if printf '%s' "$summary" | grep -q '"ok": true'; then passed=$((passed + 1)); else
    # Сорвавшийся прогон не должен оставлять программу следующему: наблюдение прекращается, программа снимается.
    "${psdoctor[@]}" observe stop >/dev/null 2>&1 || true
    ssh "${ssh_opts[@]}" "user@$host" "taskkill /im proshow.exe /f >nul 2>&1 & exit /b 0"
  fi
  # Следующий запуск — когда сеанс закрыт: иначе отказ program-running.
  for _ in $(seq 1 60); do
    if ! "${psdoctor[@]}" observe sessions | grep -q '"active":true'; then break; fi
    sleep 1
  done
done
echo "{\"runs\": $runs, \"passed\": $passed, \"journals\": \"$out\"}"
[ "$passed" = "$runs" ]
