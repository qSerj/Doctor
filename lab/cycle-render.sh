#!/bin/bash
# Цикл рендеров через наблюдатель (пункт 12 критерия Э4.0, он же Л2): загрузка — рендер — окончание — закрытие,
# N раз подряд без человека, с проверкой длительности готового фильма по ffprobe.
#   SHOW_FILE="<файл шоу>" PROJECT_SOURCE='\\VBoxSvr\exchange\projects\<каталог>' lab/cycle-render.sh [прогонов]
#
# Имена файла и каталога проекта передаются переменными и в скрипте не пишутся: имена проектов в открытый
# репозиторий не попадают. Проект — без диалогов при загрузке (пересохранённый текущей версией программы).
#
# Перед каждым прогоном проект заново кладётся на стенд, а прежний выходной файл удаляется: иначе окно сохранения
# спросит о перезаписи, и сценарий остановится на неожиданном диалоге. Длительность шоу берётся из отчёта самого
# доктора по копии проекта на хосте, длительность фильма — ffprobe.
#
# Команды в гостевую систему идут pwsh -EncodedCommand: имя выходного файла кириллическое, а через cmd по SSH
# кириллица не проходит.
# Переменные: LAB_HOST, LAB_KEY, LAB_EXCHANGE, PROJECT_SOURCE (обязательна), PROJECT_DIR (C:\lab\p8),
# SHOW_FILE (обязательна), HOST_PROJECT (каталог той же копии на хосте), FFPROBE, OUT.
set -euo pipefail
. "$(dirname "$0")/portable.sh"

runs="${1:-5}"
host="${LAB_HOST:-192.168.56.5}"
lab_key="${LAB_KEY:-$HOME/.ssh/lab_ed25519}"
exchange="${LAB_EXCHANGE:-$HOME/Lab/exchange}"
source_dir="${PROJECT_SOURCE:?укажите PROJECT_SOURCE — каталог проекта в гостевой системе, откуда он кладётся заново}"
project_dir="${PROJECT_DIR:-C:\\lab\\p8}"
show="${SHOW_FILE:?укажите SHOW_FILE — имя файла шоу в каталоге проекта}"
host_project="${HOST_PROJECT:-$exchange/projects/p8new}"
ffprobe="${FFPROBE:-/c/ffmpeg/bin/ffprobe}"
export PSDOCTOR_OBSERVER_URL="${PSDOCTOR_OBSERVER_URL:-http://$host:8100}"
export PSDOCTOR_OBSERVER_KEY_FILE="${PSDOCTOR_OBSERVER_KEY_FILE:-$HOME/Lab/secrets/observer.key}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
out="${OUT:-$repo/artifacts/lab/cycle-render/$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$out"
ssh_opts=(-o BatchMode=yes -o IdentitiesOnly=yes -o ConnectTimeout=10 -i "$lab_key")

# PowerShell в гостевой системе: скрипт кодируется в base64 от UTF-16LE, иначе кириллица не доедет.
guest_ps() {
  local encoded
  encoded="$(printf '%s' "[Console]::OutputEncoding=[Text.Encoding]::UTF8
$1" | iconv -f UTF-8 -t UTF-16LE | base64 -w0)"
  ssh "${ssh_opts[@]}" "user@$host" "pwsh -NoProfile -NonInteractive -EncodedCommand $encoded"
}

dotnet build "$repo/src/PsDoctor.Cli" --nologo -v q >/dev/null
psdoctor=(dotnet "$repo/src/PsDoctor.Cli/bin/Debug/net10.0/psdoctor.dll")

# Длительность шоу по отчёту доктора — одна на все прогоны: проект между ними не меняется.
# Длительность шоу — время слайдов плюс время переходов: на пробном рендере 17.09.2026 их сумма совпала с
# длительностью фильма по ffprobe до миллисекунды (168 912 + 72 000 = 240 912). Поле showDurationMs раздела
# звука переходы не считает и для сверки не годится.
show_ms="$("${psdoctor[@]}" "$host_project/$show" | "$PYTHON" -c 'import json,sys
data = json.loads(sys.stdin.readline())["inventory"]["slides"]
print(data["totalTimeMs"] + data["totalTransTimeMs"])')"
echo "длительность шоу по отчёту: $show_ms мс" >&2

# Выходной файл программа называет сама: каталог проекта, имя по умолчанию.
output_name="${OUTPUT_NAME:-Новая презентация.mp4}"

scenario="$out/сценарий.txt"
cat > "$scenario" <<SCN
launch "$project_dir\\$show"
wait title "$show" 600
render
wait render-done 3600
press "Ok"
close
wait exit 120
SCN

passed=0
for run in $(seq 1 "$runs"); do
  guest_out="$project_dir\\$output_name"
  # Заодно убирается след аварийного снятия программы: при чистом выходе она сама удаляет autosave.psh и
  # pshowtoken, а после taskkill они остаются, и следующий прогон встречает диалог «Recover Auto-saved Show?».
  guest_ps "robocopy '$source_dir' '$project_dir' /MIR /R:3 /W:1 /NJH /NJS /NP /NFL /NDL | Out-Null
\$след = \"\$env:LOCALAPPDATA\\VirtualStore\\Program Files (x86)\\Photodex\\ProShow Producer\"
Remove-Item '$guest_out', \"\$след\\autosave.psh\", \"\$след\\pshowtoken\" -ErrorAction SilentlyContinue
exit 0"
  journal="$out/run-$run.jsonl"
  set +e
  "${psdoctor[@]}" observe run "$scenario" --follow > "$journal" 2> "$out/run-$run.err"
  code=$?
  set -e
  # Фильм забирается на хост через папку обмена: ffprobe и сравнение — здесь.
  guest_ps "Copy-Item '$guest_out' '\\\\VBoxSvr\\exchange\\render-$run.mp4' -Force -ErrorAction SilentlyContinue; exit 0"
  film="$exchange/render-$run.mp4"
  film_ms=""
  if [ -f "$film" ]; then
    film_ms="$("$ffprobe" -v error -show_entries format=duration -of default=nk=1:nw=1 "$film" | "$PYTHON" -c 'import sys; print(round(float(sys.stdin.read().strip()) * 1000))')"
  fi
  summary="$("$PYTHON" - "$journal" "$run" "$code" "$show_ms" "${film_ms:-0}" "${film:-}" <<'EOF'
import json, os, sys
path, run, code, show_ms, film_ms, film = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4]), int(sys.argv[5]), sys.argv[6]
facts = [json.loads(line) for line in open(path, encoding='utf-8') if line.strip()]
def seconds(fact):
    h, m, s = fact['elapsed'].split(':')
    return round(int(h) * 3600 + int(m) * 60 + float(s), 2)
def first(pred):
    return next((f for f in facts if pred(f)), None)
finished = first(lambda f: f['kind'] == 'scenario-finished')
render = first(lambda f: f['kind'] == 'render-requested')
window = first(lambda f: f['kind'] == 'dialog-opened' and f['data'].get('title') == 'Rendering Video')
done = first(lambda f: f['kind'] == 'step-done' and f['data']['step'].startswith('wait render-done'))
title = first(lambda f: f['kind'] == 'step-done' and f['data']['step'].startswith('wait title'))
result = {
    'run': run, 'code': code,
    'status': finished['data']['status'] if finished else None,
    'reason': finished['data']['reason'] if finished else None,
    'title_s': seconds(title) if title else None,
    'render_start_s': seconds(window) if window else None,
    'render_s': round(seconds(done) - seconds(window), 2) if done and window else None,
    'film_bytes': os.path.getsize(film) if film and os.path.exists(film) else None,
    'film_ms': film_ms or None,
    'show_ms': show_ms,
    # Ровно длительность шоу фильм не даёт: кодек режет по кадрам. Полкадра при 30 к/с — 17 мс, берём секунду.
    'duration_close': abs(film_ms - show_ms) <= 1000 if film_ms else False,
}
result['ok'] = result['status'] == 'completed' and bool(render) and result['duration_close']
print(json.dumps(result, ensure_ascii=False))
EOF
)"
  echo "$summary"
  if printf '%s' "$summary" | grep -q '"ok": true'; then passed=$((passed + 1)); else
    "${psdoctor[@]}" observe stop >/dev/null 2>&1 || true
    ssh "${ssh_opts[@]}" "user@$host" "taskkill /im proshow.exe /f >nul 2>&1 & exit /b 0"
  fi
  for _ in $(seq 1 120); do
    if ! "${psdoctor[@]}" observe sessions | grep -q '"active":true'; then break; fi
    sleep 1
  done
done
echo "{\"runs\": $runs, \"passed\": $passed, \"journals\": \"$out\"}"
[ "$passed" = "$runs" ]
