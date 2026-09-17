#!/bin/bash
# Цена наблюдения (пункт 8 критерия Э4.0): N загрузок с рендером под наблюдателем и N тех же загрузок с рендером
# скриптом лаборатории, без наблюдателя. Прогоны чередуются парами, и порядок в паре меняется через раз: прогрев
# кэшей программы и диска не должен доставаться одному способу.
#   SHOW_FILE="<файл шоу>" PROJECT_SOURCE='\\VBoxSvr\exchange\projects\<каталог>' lab/cycle-cost.sh [пар]   — по умолчанию 3
#
# Имена файла и каталога проекта передаются переменными и в скрипте не пишутся: имена проектов в открытый репозиторий
# не попадают. Проект — без диалогов при загрузке (пересохранённый текущей версией программы).
#
# Два числа на прогон. Загрузка: под наблюдателем — от начала шага launch (в него входит снимок служебных файлов,
# это часть цены) до факта main-window с именем файла, у скрипта — от Start-Process до того же заголовка. Рендер:
# от появления окна «Rendering Video» до диалога об окончании, одинаково у обоих. Окна оба опрашивают раз в
# полсекунды и кнопки нажимают одним и тем же Invoke UI Automation — разница только в наблюдении.
# Перед каждым прогоном проект заново кладётся на стенд, а прежний выходной файл удаляется.
# Переменные: LAB_HOST, LAB_KEY, LAB_EXCHANGE (~/Lab/exchange), PROJECT_SOURCE (обязательна), PROJECT_DIR (C:\lab\p),
# SHOW_FILE (обязательна), PSDOCTOR_OBSERVER_URL, PSDOCTOR_OBSERVER_KEY_FILE, OUT (каталог журналов).
set -euo pipefail
. "$(dirname "$0")/portable.sh"

pairs="${1:-3}"
host="${LAB_HOST:-192.168.56.5}"
lab_key="${LAB_KEY:-$HOME/.ssh/lab_ed25519}"
exchange="${LAB_EXCHANGE:-$HOME/Lab/exchange}"
source_dir="${PROJECT_SOURCE:?укажите PROJECT_SOURCE — каталог проекта в гостевой системе, откуда он кладётся заново}"
project_dir="${PROJECT_DIR:-C:\\lab\\p}"
show="${SHOW_FILE:?укажите SHOW_FILE — имя файла шоу в каталоге проекта}"
export PSDOCTOR_OBSERVER_URL="${PSDOCTOR_OBSERVER_URL:-http://$host:8100}"
export PSDOCTOR_OBSERVER_KEY_FILE="${PSDOCTOR_OBSERVER_KEY_FILE:-$HOME/Lab/secrets/observer.key}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
out="${OUT:-$repo/artifacts/lab/cycle-cost/$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$out"
ssh_opts=(-o BatchMode=yes -o IdentitiesOnly=yes -o ConnectTimeout=10 -i "$lab_key")

dotnet build "$repo/src/PsDoctor.Cli" --nologo -v q >/dev/null
psdoctor=(dotnet "$repo/src/PsDoctor.Cli/bin/Debug/net10.0/psdoctor.dll")
mkdir -p "$exchange/observer/lab"
cp -f "$repo/lab/guest/Invoke-InSession.ps1" "$repo/lab/guest/Measure-TitleLoad.ps1" "$repo/lab/guest/Win32.ps1" "$repo/lab/guest/Uia.ps1" "$exchange/observer/lab/"

# Имя выходного файла программа выбирает сама: каталог проекта, умолчальное имя.
output_name="${OUTPUT_NAME:-Новая презентация.mp4}"

# Скрипт PowerShell по SSH — закодированным: кириллица и кавычки через cmd не проходят.
guest_ps() {
  local encoded
  encoded="$(printf '%s' "[Console]::OutputEncoding=[Text.Encoding]::UTF8
$1" | iconv -f UTF-8 -t UTF-16LE | base64 -w0)"
  ssh "${ssh_opts[@]}" "user@$host" "pwsh -NoProfile -NonInteractive -EncodedCommand $encoded"
}

# Кроме проекта и прошлого фильма убирается след аварийного снятия программы: при чистом выходе она сама удаляет
# autosave.psh и pshowtoken, а после taskkill они остаются, и следующий прогон встречает диалог «Recover
# Auto-saved Show?» — для сценария это неожиданный диалог и остановка.
restore() {
  guest_ps "robocopy '$source_dir' '$project_dir' /MIR /R:3 /W:1 /NJH /NJS /NP /NFL /NDL | Out-Null
\$след = \"\$env:LOCALAPPDATA\\VirtualStore\\Program Files (x86)\\Photodex\\ProShow Producer\"
Remove-Item '$project_dir\\$output_name', \"\$след\\autosave.psh\", \"\$след\\pshowtoken\" -ErrorAction SilentlyContinue
exit 0"
}

wait_closed() {
  for _ in $(seq 1 60); do
    if ! "${psdoctor[@]}" observe sessions | grep -q '"active":true'; then return; fi
    sleep 1
  done
}

scenario="launch \"$project_dir\\$show\"
wait title \"$show\" 600
render
wait render-done 3600
press \"Ok\"
close
wait exit 120"

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
window = first(lambda f: f['kind'] == 'dialog-opened' and f['data'].get('title') == 'Rendering Video')
done = first(lambda f: f['kind'] == 'step-done' and f['data']['step'].startswith('wait render-done'))
result = {
    'mode': 'observer', 'run': run, 'code': code,
    'status': finished['data']['status'] if finished else None,
    'title_s': round(seconds(title) - seconds(step), 2) if title and step else None,
    'title_from_launched_s': round(seconds(title) - seconds(launched), 2) if title and launched else None,
    'render_s': round(seconds(done) - seconds(window), 2) if done and window else None,
    'snapshot_s': before['data']['seconds'] if before else None,
    'session_end': end['data']['reason'] if end else None,
}
result['ok'] = (result['status'] == 'completed' and result['title_s'] is not None
                and result['render_s'] is not None and result['session_end'] == 'program-exited')
print(json.dumps(result, ensure_ascii=False))
EOF
}

script_run() {
  local run="$1"
  restore
  set +e
  guest_ps "& '\\\\VBoxSvr\\exchange\\observer\\lab\\Invoke-InSession.ps1' -Script Measure-TitleLoad.ps1 -Arguments \"-ShowFile '$project_dir\\$show' -Render\" -TimeoutSec 5400; exit \$LASTEXITCODE" > "$out/script-$run.txt" 2>&1
  local code=$?
  set -e
  "$PYTHON" - "$out/script-$run.txt" "$run" "$code" <<'EOF'
import json, sys
path, run, code = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
lines = [l.strip().lstrip('\ufeff') for l in open(path, encoding='utf-8', errors='replace')]
data = next((json.loads(l) for l in lines if l.startswith('{')), {})
start, done = data.get('render_start_s'), data.get('render_done_s')
result = {
    'mode': 'script', 'run': run, 'code': code,
    'title_s': data.get('title_s'),
    'render_s': round(done - start, 2) if start and done else None,
    'exit_s': data.get('exit_s'),
    'failed': data.get('failed'),
}
result['ok'] = (code == 0 and result['title_s'] is not None and result['render_s'] is not None
                and data.get('code') is not None)
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
    good = [r for r in rows if r['mode'] == mode and r['ok']]
    summary[mode] = {'ok': len(good), 'of': sum(1 for r in rows if r['mode'] == mode)}
    # Медиана и разброс отдельно по загрузке и по рендеру: цена наблюдения у них разная.
    for number in ('title_s', 'render_s'):
        values = [r[number] for r in good if r.get(number) is not None]
        summary[mode][number] = {'median': round(statistics.median(values), 2) if values else None,
                                 'min': min(values, default=None), 'max': max(values, default=None)}
print(json.dumps(summary, ensure_ascii=False))
EOF
