#!/bin/bash
# Цикл опыта. Не часть продукта, см. lab/README.md.
# Критерий 2 Э6.6 «Дневная сводка» на стенде, поставленном lab/observer.sh. Один шаг за запуск; ручные действия
# владельца — по подсказкам «>>>».
#   lab/cycle-e66-summary.sh days [id пакета]         — после установки: сводки за 30 прошлых дней и сегодняшняя, в
#                                                      каких днях Kernel-Power 41 прежних сбросов ВМ, сколько шло
#                                                      построение и сколько весят файлы
#   lab/cycle-e66-summary.sh crash [id пакета]        — пробник fvideo.exe падает (шаг crash цикла Э6.2) — падение
#                                                      воркера в сегодняшней сводке
#   lab/cycle-e66-summary.sh reset-before|reset-after — до жёсткого сброса ВМ и после входа: аварийное выключение в
#                                                      сегодняшней сводке; один пакет на оба шага
#
# Каждая проверка печатает «совпало» или «РАСХОЖДЕНИЕ»; код выхода — 0, если расхождений нет, иначе 1. Сводки ложатся
# в results/: $LAB_EXCHANGE/checks/<id>/results. Переменные — как у lab/cycle-e62-field.sh: LAB_HOST, LAB_KEY,
# LAB_EXCHANGE, PSDOCTOR_OBSERVER_URL, PSDOCTOR_OBSERVER_KEY_FILE (иначе OBSERVER_KEY).
set -uo pipefail
. "$(dirname "$0")/portable.sh"

step="${1:?укажите шаг: days, crash, reset-before или reset-after}"
case "$step" in
  reset-before|reset-after) package="${2:-e66-summary-001-reset}" ;;
  *) package="${2:-e66-summary-001-$step}" ;;
esac
host="${LAB_HOST:-192.168.56.5}"
lab_key="${LAB_KEY:-$HOME/.ssh/lab_ed25519}"
exchange="${LAB_EXCHANGE:-$HOME/Lab/exchange}"
export PSDOCTOR_OBSERVER_URL="${PSDOCTOR_OBSERVER_URL:-http://$host:8100}"
export PSDOCTOR_OBSERVER_KEY_FILE="${PSDOCTOR_OBSERVER_KEY_FILE:-${OBSERVER_KEY:-$HOME/Lab/secrets/observer.key}}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
out="$exchange/checks/$package/results"
mkdir -p "$out"
ssh_opts=(-n -o BatchMode=yes -o IdentitiesOnly=yes -o ConnectTimeout=10 -i "$lab_key")
mismatches=0

dotnet build "$repo/src/PsDoctor.Cli" --nologo -v q >/dev/null || { echo "сборка CLI не прошла" >&2; exit 3; }
psdoctor=(dotnet "$repo/src/PsDoctor.Cli/bin/Debug/net10.0/psdoctor.dll")

# PowerShell в гостевой системе: скрипт кодируется в base64 от UTF-16LE, иначе кириллица не доедет.
guest_ps() {
  local encoded
  encoded="$(printf '%s' "[Console]::OutputEncoding=[Text.Encoding]::UTF8
\$ProgressPreference='SilentlyContinue'
$1" | iconv -f UTF-8 -t UTF-16LE | base64 -w0)"
  ssh "${ssh_opts[@]}" "user@$host" "pwsh -NoProfile -NonInteractive -EncodedCommand $encoded" | tr -d '\r'
}

say() { printf '\n== %s\n' "$*"; }

ask() { printf '\n>>> %s\n' "$*"; }

check() {
  local what="$1" ok="$2"
  if [ "$ok" = 1 ]; then
    echo "совпало: $what"
  else
    echo "РАСХОЖДЕНИЕ: $what"
    mismatches=$((mismatches + 1))
  fi
}

finish() { say "итог шага $step: расхождений $mismatches"; [ "$mismatches" = 0 ]; exit $?; }

# Число из сегодняшней сводки: что считать — выражение Python над сводкой s.
today_number() {
  "${psdoctor[@]}" observe summary today --json 2>/dev/null | "$PYTHON" -c "
import json,sys
text=sys.stdin.read().strip()
s=json.loads(text) if text else None
print('' if s is None else $1)"
}

worker_crashes='s["workers"]["today"]'
shutdowns='len(s["machine"]["unexpectedShutdowns"])'

save_today() {
  "${psdoctor[@]}" observe summary today > "$out/today-$1.txt" 2>&1
  cat "$out/today-$1.txt"
}

case "$step" in
days)
  from="$("$PYTHON" -c 'import datetime; print((datetime.date.today() - datetime.timedelta(days=30)).isoformat())')"
  say "жду сводки с $from: первый проход хранителя идёт в фоне сразу после старта наблюдателя, предел 10 мин"
  started=$SECONDS
  count=0
  while [ $((SECONDS - started)) -le 600 ]; do
    "${psdoctor[@]}" observe summary --from "$from" --json > "$out/summaries.jsonl" 2> "$out/summaries.err"
    count="$(wc -l < "$out/summaries.jsonl" | tr -d ' ')"
    [ "$count" -ge 31 ] && break
    sleep 10
  done
  say "сводок: $count — через $((SECONDS - started)) с ожидания (часы хоста)"
  check "сводки за 30 прошлых дней и сегодняшняя (31)" "$([ "$count" -ge 31 ] && echo 1 || echo 0)"
  "${psdoctor[@]}" observe summary --from "$from" > "$out/summaries.txt" 2>&1
  "$PYTHON" - "$out/summaries.jsonl" > "$out/days.tsv" <<'PY'
import json,sys
print("день\tполон\tглавное\tKP41\tпробелы\tсеансов\tпадений воркеров")
for line in open(sys.argv[1], encoding="utf-8"):
    s=json.loads(line)
    print("\t".join([s["day"], str(s["complete"]).lower(),
        ",".join(f'{h["kind"]}×{h["count"]}' for h in s["highlights"]) or "-",
        str(len(s["machine"]["unexpectedShutdowns"])), ",".join(s["gaps"]) or "-",
        str(s["proShow"]["sessions"]), str(s["workers"]["today"])]))
PY
  cat "$out/days.tsv"
  kp41_days="$(awk -F'\t' 'NR > 1 && $2 == "true" && $4 > 0' "$out/days.tsv" | wc -l | tr -d ' ')"
  check "Kernel-Power 41 прежних сбросов ВМ — в прошлых днях (дней: $kp41_days)" "$([ "$kp41_days" -ge 1 ] && echo 1 || echo 0)"
  say "файлы сводок на стенде и время построения от старта наблюдателя"
  guest_ps "$(cat <<'PS'
$dir = Join-Path $env:LOCALAPPDATA 'PsDoctor\observer\sessions\summaries'
$files = @(Get-ChildItem -LiteralPath $dir -Filter '????-??-??.json' | Sort-Object LastWriteTime)
$observer = Get-CimInstance Win32_Process -Filter "Name='PsDoctor.Observer.exe'" |
    Where-Object { $_.CommandLine -notlike '*--etw-helper*' -and $_.CommandLine -notlike '*--watchdog*' } | Select-Object -First 1
$c = [Globalization.CultureInfo]::InvariantCulture
"файлов сводок: $($files.Count)"
if ($files.Count -gt 0) {
    "вес: всего $(($files | Measure-Object Length -Sum).Sum) байт, самый большой $(($files | Measure-Object Length -Maximum).Maximum) байт"
    "первый записан $($files[0].LastWriteTime.ToString('HH:mm:ss', $c)), последний $($files[-1].LastWriteTime.ToString('HH:mm:ss', $c)) — построение $([math]::Round(($files[-1].LastWriteTime - $files[0].LastWriteTime).TotalSeconds, 1).ToString($c)) с"
}
if ($observer) {
    "наблюдатель запущен $($observer.CreationDate.ToString('HH:mm:ss', $c))" + $(if ($files.Count -gt 0) { ", до последней сводки $([math]::Round(($files[-1].LastWriteTime - $observer.CreationDate).TotalSeconds, 1).ToString($c)) с" })
}
"кратких записей сеансов: $(@(Get-ChildItem -LiteralPath (Split-Path $dir) -Filter '*.brief.json').Count)"
"замеров: $(@(Get-Content -LiteralPath (Join-Path $dir 'readings.jsonl') -ErrorAction SilentlyContinue).Count)"
Get-Content -LiteralPath (Join-Path $dir 'coverage.json') -ErrorAction SilentlyContinue
PS
)" | tee "$out/files.txt"
  ;;

crash)
  before="$(today_number "$worker_crashes")"
  say "падений воркеров в сегодняшней сводке до пробника: ${before:-нет сводки}"
  bash "$repo/lab/cycle-e62-field.sh" crash "$package"
  say "шаг crash цикла Э6.2 кончился с кодом $? — его проверки выше; дальше — сводка"
  after="$before"
  started=$SECONDS
  while [ $((SECONDS - started)) -le 120 ]; do
    after="$(today_number "$worker_crashes")"
    [ -n "$before" ] && [ -n "$after" ] && [ "$after" -gt "$before" ] && break
    sleep 10
  done
  check "падение fvideo.exe в сегодняшней сводке (было ${before:-?}, стало ${after:-?})" \
    "$([ -n "$before" ] && [ -n "$after" ] && [ "$after" -gt "$before" ] && echo 1 || echo 0)"
  save_today after-crash
  ;;

reset-before)
  before="$(today_number "$shutdowns")"
  check "сегодняшняя сводка отдаётся" "$([ -n "$before" ] && echo 1 || echo 0)"
  echo "$before" > "$out/shutdowns-before.txt"
  say "аварийных выключений в сегодняшней сводке до сброса: $before"
  save_today before-reset >/dev/null
  ask "Сбросьте ВМ — в окне VirtualBox «Машина → Сброс» — войдите в Windows и запустите шаг reset-after тем же пакетом."
  ;;

reset-after)
  before="$(cat "$out/shutdowns-before.txt" 2>/dev/null)"
  [ -n "$before" ] || { echo "шаг прерван: нет shutdowns-before.txt — сначала reset-before тем же пакетом"; exit 3; }
  say "жду /health наблюдателя после входа, предел 5 мин"
  started=$SECONDS
  until "${psdoctor[@]}" observe health > /dev/null 2>&1; do
    [ $((SECONDS - started)) -le 300 ] || break
    sleep 10
  done
  say "жду аварийное выключение в сегодняшней сводке: опрос журнала раз в минуту, предел 4 мин"
  started=$SECONDS
  after="$before"
  while [ $((SECONDS - started)) -le 240 ]; do
    after="$(today_number "$shutdowns")"
    [ -n "$after" ] && [ "$after" -gt "$before" ] && break
    sleep 10
  done
  check "Kernel-Power 41 в сегодняшней сводке (было $before, стало ${after:-?})" \
    "$([ -n "$after" ] && [ "$after" -gt "$before" ] && echo 1 || echo 0)"
  save_today after-reset
  ;;

*)
  echo "незнакомый шаг: $step" >&2
  exit 3
  ;;
esac

finish
