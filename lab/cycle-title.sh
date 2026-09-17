#!/bin/bash
# Цена наблюдения (пункт 8 критерия Э4.0): N загрузок одного проекта под наблюдателем и N загрузок скриптом,
# который только опрашивает заголовок, без наблюдателя. Прогоны чередуются парами, и порядок в паре меняется
# через раз: прогрев кэшей программы и диска не должен доставаться одному способу.
#   SHOW_FILE="<файл шоу>" PROJECT_SOURCE='\\VBoxSvr\exchange\projects\<каталог>' lab/cycle-title.sh [пар]   — по умолчанию 5
#
# Имена файла и каталога проекта передаются переменными и в скрипте не пишутся: имена проектов в открытый репозиторий
# не попадают. Проект — без диалогов при загрузке: скрипт без наблюдателя ничего не нажимает.
#
# Время под наблюдателем — от начала шага launch (в него входит снимок служебных файлов, это часть цены) до факта
# main-window с именем файла; рядом — от факта program-launched. Время скрипта — от Start-Process до того же заголовка.
# Опрос окон у обоих раз в полсекунды. Перед каждым прогоном проект заново кладётся на стенд.
# Переменные: LAB_HOST, LAB_KEY, LAB_EXCHANGE (~/Lab/exchange), PROJECT_SOURCE (обязательна), PROJECT_DIR (C:\lab\p),
# SHOW_FILE (обязательна), PSDOCTOR_OBSERVER_URL, PSDOCTOR_OBSERVER_KEY_FILE, OUT (каталог журналов).
set -euo pipefail
. "$(dirname "$0")/portable.sh"

pairs="${1:-5}"
host="${LAB_HOST:-192.168.56.5}"
lab_key="${LAB_KEY:-$HOME/.ssh/lab_ed25519}"
exchange="${LAB_EXCHANGE:-$HOME/Lab/exchange}"
source_dir="${PROJECT_SOURCE:?укажите PROJECT_SOURCE — каталог проекта в гостевой системе, откуда он кладётся заново}"
project_dir="${PROJECT_DIR:-C:\\lab\\p}"
show="${SHOW_FILE:?укажите SHOW_FILE — имя файла шоу в каталоге проекта}"
export PSDOCTOR_OBSERVER_URL="${PSDOCTOR_OBSERVER_URL:-http://$host:8100}"
export PSDOCTOR_OBSERVER_KEY_FILE="${PSDOCTOR_OBSERVER_KEY_FILE:-$HOME/Lab/secrets/observer.key}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
out="${OUT:-$repo/artifacts/lab/cycle-title/$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$out"
ssh_opts=(-o BatchMode=yes -o IdentitiesOnly=yes -o ConnectTimeout=10 -i "$lab_key")

dotnet build "$repo/src/PsDoctor.Cli" --nologo -v q >/dev/null
psdoctor=(dotnet "$repo/src/PsDoctor.Cli/bin/Debug/net10.0/psdoctor.dll")
mkdir -p "$exchange/observer/lab"
cp -f "$repo/lab/guest/Invoke-InSession.ps1" "$repo/lab/guest/Measure-TitleLoad.ps1" "$repo/lab/guest/Win32.ps1" "$exchange/observer/lab/"

# Скрипт PowerShell по SSH — закодированным: кириллица и кавычки через cmd не проходят.
guest_ps() {
  local encoded
  encoded="$(printf '%s' "[Console]::OutputEncoding=[Text.Encoding]::UTF8
$1" | iconv -t UTF-16LE | base64 -w0)"
  ssh "${ssh_opts[@]}" "user@$host" "pwsh -NoProfile -NonInteractive -EncodedCommand $encoded"
}

restore() {
  guest_ps "robocopy '$source_dir' '$project_dir' /MIR /R:3 /W:1 /NJH /NJS /NP /NFL /NDL | Out-Null; exit 0"
}

wait_closed() {
  for _ in $(seq 1 60); do
    if ! "${psdoctor[@]}" observe sessions | grep -q '"active":true'; then return; fi
    sleep 1
  done
}

scenario="launch \"$project_dir\\$show\"
wait title \"$show\" 600
close
wait exit 60"

observer_run() {
  local run="$1" journal="$out/observer-$1.jsonl"
  restore
  set +e
  printf '%s\n' "$scenario" | "${psdoctor[@]}" observe run - --follow > "$journal" 2> "$out/observer-$run.err"
  local code=$?
  set -e
  "$PYTHON" - "$journal" "$run" "$code" "$show" <<'EOF'
import json, sys
path, run, code, show = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), sys.argv[4]
facts = [json.loads(line) for line in open(path, encoding='utf-8') if line.strip()]
def seconds(f):
    h, m, s = f['elapsed'].split(':')
    return int(h) * 3600 + int(m) * 60 + float(s)
def first(pred):
    return next((f for f in facts if pred(f)), None)
step = first(lambda f: f['kind'] == 'step-started' and f['data']['line'] == 1)
launched = first(lambda f: f['kind'] == 'program-launched')
title = first(lambda f: f['kind'] == 'main-window' and show in (f['data']['title'] or ''))
before = first(lambda f: f['kind'] == 'service-files-before')
finished = first(lambda f: f['kind'] == 'scenario-finished')
end = first(lambda f: f['kind'] == 'session-finished')
result = {
    'mode': 'observer', 'run': run, 'code': code,
    'status': finished['data']['status'] if finished else None,
    'title_s': round(seconds(title) - seconds(step), 2) if title and step else None,
    'title_from_launched_s': round(seconds(title) - seconds(launched), 2) if title and launched else None,
    'snapshot_s': before['data']['seconds'] if before else None,
    'session_end': end['data']['reason'] if end else None,
}
result['ok'] = result['status'] == 'completed' and result['title_s'] is not None and result['session_end'] == 'program-exited'
print(json.dumps(result, ensure_ascii=False))
EOF
}

script_run() {
  local run="$1"
  restore
  set +e
  guest_ps "& '\\\\VBoxSvr\\exchange\\observer\\lab\\Invoke-InSession.ps1' -Script Measure-TitleLoad.ps1 -Arguments \"-ShowFile '$project_dir\\$show'\"; exit \$LASTEXITCODE" > "$out/script-$run.txt" 2>&1
  local code=$?
  set -e
  "$PYTHON" - "$out/script-$run.txt" "$run" "$code" <<'EOF'
import json, sys
path, run, code = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
lines = [l.strip().lstrip('\ufeff') for l in open(path, encoding='utf-8', errors='replace')]
data = next((json.loads(l) for l in lines if l.startswith('{')), {})
result = {'mode': 'script', 'run': run, 'code': code, 'title_s': data.get('title_s'), 'exit_s': data.get('exit_s')}
result['ok'] = code == 0 and result['title_s'] is not None and data.get('code') is not None
print(json.dumps(result, ensure_ascii=False))
EOF
}

results="$out/results.jsonl"
: > "$results"
for pair in $(seq 1 "$pairs"); do
  if [ $((pair % 2)) = 1 ]; then order="observer script"; else order="script observer"; fi
  for mode in $order; do
    line="$("${mode}_run" "$pair")"
    echo "$line" | tee -a "$results"
    if ! printf '%s' "$line" | grep -q '"ok": true'; then
      "${psdoctor[@]}" observe stop >/dev/null 2>&1 || true
      ssh "${ssh_opts[@]}" "user@$host" "taskkill /im proshow.exe /f >nul 2>&1 & exit /b 0"
    fi
    wait_closed
  done
done

"$PYTHON" - "$results" "$out" <<'EOF'
import json, statistics, sys
rows = [json.loads(l) for l in open(sys.argv[1], encoding='utf-8') if l.strip()]
summary = {'journals': sys.argv[2]}
for mode in ('observer', 'script'):
    values = [r['title_s'] for r in rows if r['mode'] == mode and r['ok']]
    summary[mode] = {'ok': len(values), 'of': sum(1 for r in rows if r['mode'] == mode),
                     'median_s': round(statistics.median(values), 2) if values else None,
                     'min_s': min(values, default=None), 'max_s': max(values, default=None)}
print(json.dumps(summary, ensure_ascii=False))
EOF
