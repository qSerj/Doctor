#!/bin/bash
# Цикл опыта. Не часть продукта, см. lab/README.md.
# Пункты 1, 3 и 4 критерия Э4.3: пассивное подключение к ProShow, запущенному вручную, отказы и живучесть программы.
# Один шаг за запуск; ручное действие владельца — перед шагом или по подсказке во время него.
#   lab/cycle-e43-attach.sh passive   — подключение, повторное подключение, команды программе, stop
#   lab/cycle-e43-attach.sh restart   — перезапуск наблюдателя посреди сеанса: обрыв журнала
#   lab/cycle-e43-attach.sh close     — владелец закрывает ProShow штатно во время сеанса
#   lab/cycle-e43-attach.sh kill      — скрипт снимает proshow.exe во время сеанса
#   lab/cycle-e43-attach.sh etw       — ETW-помощник остановлен: отказ подключения, затем восстановление
#   lab/cycle-e43-attach.sh two       — владелец запустил второй экземпляр: выбор не делается молча
#
# Каждая проверка печатает строку «совпало» или «РАСХОЖДЕНИЕ»; код выхода — 0, если расхождений нет, иначе 1.
# Факты сеансов шага ложатся в OUT (по умолчанию $LAB_EXCHANGE/checks/e43-attach-001-<шаг>/results).
# Переменные: LAB_HOST, LAB_KEY, LAB_EXCHANGE, PSDOCTOR_OBSERVER_URL, PSDOCTOR_OBSERVER_KEY_FILE, OUT.
set -uo pipefail
. "$(dirname "$0")/portable.sh"

step="${1:?укажите шаг: passive, restart, close, kill, etw или two}"
host="${LAB_HOST:-192.168.56.5}"
lab_key="${LAB_KEY:-$HOME/.ssh/lab_ed25519}"
exchange="${LAB_EXCHANGE:-$HOME/Lab/exchange}"
export PSDOCTOR_OBSERVER_URL="${PSDOCTOR_OBSERVER_URL:-http://$host:8100}"
export PSDOCTOR_OBSERVER_KEY_FILE="${PSDOCTOR_OBSERVER_KEY_FILE:-$HOME/Lab/secrets/observer.key}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
out="${OUT:-$exchange/checks/e43-attach-001-$step/results}"
mkdir -p "$out"
ssh_opts=(-o BatchMode=yes -o IdentitiesOnly=yes -o ConnectTimeout=10 -i "$lab_key")
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

# observe <аргументы> — печатает вывод и оставляет его в $reply, код — в $code.
observe() {
  reply="$("${psdoctor[@]}" observe "$@" 2>&1)"
  code=$?
  printf '$ observe %s\n%s\n(код %s)\n' "$*" "$reply" "$code"
}

check() {
  local what="$1" ok="$2"
  if [ "$ok" = 1 ]; then
    echo "совпало: $what"
  else
    echo "РАСХОЖДЕНИЕ: $what"
    mismatches=$((mismatches + 1))
  fi
}

is() { [ "$1" = "$2" ] && echo 1 || echo 0; }
has() { case "$1" in *"$2"*) echo 1 ;; *) echo 0 ;; esac; }

json() { printf '%s' "$1" | "$PYTHON" -c "import json,sys; print(json.loads(sys.stdin.read().strip().splitlines()[0]).get('$2',''))"; }

proshow_pids() { guest_ps "(@(Get-Process proshow -ErrorAction SilentlyContinue).Id | Sort-Object) -join ','"; }

alive() { [ "$(guest_ps "[bool](Get-Process -Id $1 -ErrorAction SilentlyContinue)")" = True ] && echo 1 || echo 0; }

now_utc() { date -u +%Y-%m-%dT%H:%M:%SZ; }

# Сводка сеанса из /sessions: active и finished.
summary() {
  "${psdoctor[@]}" observe sessions 2>/dev/null | "$PYTHON" -c "
import json,sys
for line in sys.stdin:
    s=json.loads(line)
    if s.get('id')=='$1': print(str(s.get('active')).lower(), str(s.get('finished')).lower())"
}

# Последний факт сеанса: вид и причина.
last_fact() {
  "${psdoctor[@]}" observe facts "$1" > "$out/facts-$1.jsonl" 2>/dev/null
  tail -n 1 "$out/facts-$1.jsonl" | "$PYTHON" -c "
import json,sys
f=json.loads(sys.stdin.read()); print(f.get('kind',''), (f.get('data') or {}).get('reason',''))"
}

kinds() { "$PYTHON" -c "
import json,sys
print(' '.join(sorted({json.loads(l).get('kind','') for l in open(sys.argv[1],encoding='utf-8') if l.strip()})))" "$out/facts-$1.jsonl"; }

wait_inactive() {
  local session="$1" limit="$2" state
  for _ in $(seq 1 "$limit"); do
    state="$(summary "$session")"
    [ "${state%% *}" = false ] && return 0
    sleep 1
  done
  return 1
}

raw_lines() {
  "${psdoctor[@]}" observe raw "$1" "$2" "$(now_utc)" > "$out/raw-$1.jsonl" 2>"$out/raw-$1.err"
  local rc=$?
  echo "$rc $(wc -l < "$out/raw-$1.jsonl")"
}

# Подключение, без которого шаг не имеет смысла: при отказе шаг прекращается.
attach_or_die() {
  observe attach
  check "attach принят (код 0)" "$(is "$code" 0)"
  [ "$code" = 0 ] || { echo "шаг прерван: подключения нет"; exit 1; }
  session="$(json "$reply" session)"
  pid="$(json "$reply" processId)"
  started="$(json "$reply" startedUtc)"
  since="$(now_utc)"
}

require_one_proshow() {
  local pids
  pids="$(proshow_pids)"
  say "ProShow в госте: pid ${pids:-нет}"
  case "$pids" in
    '' ) echo "шаг прерван: ProShow не запущен — запустите его вручную ярлыком и повторите"; exit 3 ;;
    *,*) echo "шаг прерван: ProShow запущен не один раз ($pids)"; exit 3 ;;
  esac
}

observe health

case "$step" in
passive)
  require_one_proshow
  say "подключение"
  attach_or_die
  check "attach вернул pid живого ProShow" "$(is "$pid" "$(proshow_pids)")"

  say "повторное подключение"
  observe attach
  check "повторный attach отвергнут (код 2)" "$(is "$code" 2)"
  check "  отказ program-running" "$(has "$reply" program-running)"

  say "команды программе в пассивном сеансе"
  for command in 'press "ОК"' 'close' 'render'; do
    observe run --step "$command"
    check "run --step $command отвергнут (код 2)" "$(is "$code" 2)"
    check "  отказ passive-session" "$(has "$reply" passive-session)"
  done
  # launch отвергается раньше проверки пассивности: программа уже жива, второй экземпляр не запускается.
  observe run --step 'launch "C:\lab\p3\FP.psh"'
  check "run --step launch отвергнут (код 2)" "$(is "$code" 2)"
  check "  отказ program-running" "$(has "$reply" program-running)"
  check "ProShow жив после отказов" "$(alive "$pid")"

  sleep 5
  say "stop"
  observe stop "$session"
  check "stop принят (код 0)" "$(is "$code" 0)"
  sleep 2
  check "ProShow жив после stop" "$(alive "$pid")"
  read -r kind reason <<< "$(last_fact "$session")"
  check "последний факт session-finished/stopped (есть $kind/$reason)" "$(is "$kind/$reason" session-finished/stopped)"
  all="$(kinds "$session")"
  check "в журнале program-attached" "$(has "$all" program-attached)"
  check "в журнале etw-state" "$(has "$all" etw-state)"
  read -r rc lines <<< "$(raw_lines "$session" "$since")"
  check "сырьё скачивается (код $rc, строк $lines)" "$(is "$rc" 0)"
  echo "виды фактов: $all"
  ;;

restart)
  require_one_proshow
  attach_or_die
  sleep 10
  say "перезапуск наблюдателя посреди сеанса (ETW-помощник не трогается)"
  guest_ps "Stop-ScheduledTask -TaskName psdoctor-observer
Get-CimInstance Win32_Process -Filter \"Name='PsDoctor.Observer.exe'\" | Where-Object { \$_.CommandLine -notmatch 'etw-helper' } | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force }
Start-Sleep -Seconds 1
Start-ScheduledTask -TaskName psdoctor-observer
'перезапущен'"
  for _ in $(seq 1 30); do "${psdoctor[@]}" observe health >/dev/null 2>&1 && break; sleep 1; done
  observe health
  check "наблюдатель отвечает после перезапуска" "$(is "$code" 0)"
  check "ProShow жив после перезапуска наблюдателя" "$(alive "$pid")"
  observe sessions
  check "сеанс не активен и не доведён до конца (обрыв): $(summary "$session")" "$(is "$(summary "$session")" 'false false')"
  read -r kind reason <<< "$(last_fact "$session")"
  echo "последний факт оборванного сеанса: $kind $reason"
  check "журнал оборванного сеанса читается" "$(is "$([ -s "$out/facts-$session.jsonl" ] && echo 1)" 1)"
  check "последняя строка не session-finished" "$(is "$([ "$kind" != session-finished ] && echo 1)" 1)"
  read -r rc lines <<< "$(raw_lines "$session" "$since")"
  check "сырьё оборванного сеанса скачивается (код $rc, строк $lines)" "$(is "$rc" 0)"
  say "новое подключение после перезапуска"
  observe attach
  check "attach после перезапуска принят (код 0)" "$(is "$code" 0)"
  [ "$code" = 0 ] && { observe stop "$(json "$reply" session)"; check "  stop принят" "$(is "$code" 0)"; }
  ;;

close)
  require_one_proshow
  attach_or_die
  say ">>> Закройте ProShow штатно, крестиком. Если спросит о сохранении — «Нет». Жду до 5 минут."
  if wait_inactive "$session" 300; then
    check "сеанс закончился после закрытия программы" 1
  else
    check "сеанс закончился после закрытия программы (не дождался 5 минут)" 0
  fi
  read -r kind reason <<< "$(last_fact "$session")"
  check "последний факт session-finished/program-exited (есть $kind/$reason)" "$(is "$kind/$reason" session-finished/program-exited)"
  grep -E '"kind":"(program-exited|process-exited)"' "$out/facts-$session.jsonl" | cut -c1-300
  read -r rc lines <<< "$(raw_lines "$session" "$since")"
  check "сырьё после закрытия скачивается (код $rc, строк $lines)" "$(is "$rc" 0)"
  ;;

kill)
  require_one_proshow
  attach_or_die
  sleep 10
  say "снимаю proshow.exe $pid"
  guest_ps "Stop-Process -Id $pid -Force; 'снят'"
  if wait_inactive "$session" 60; then
    check "сеанс закончился после снятия программы" 1
  else
    check "сеанс закончился после снятия программы (не дождался минуты)" 0
  fi
  read -r kind reason <<< "$(last_fact "$session")"
  check "последний факт session-finished (есть $kind/$reason)" "$(is "$kind" session-finished)"
  grep -E '"kind":"(program-exited|process-exited)"' "$out/facts-$session.jsonl" | cut -c1-300
  read -r rc lines <<< "$(raw_lines "$session" "$since")"
  check "сырьё после снятия скачивается (код $rc, строк $lines)" "$(is "$rc" 0)"
  echo "Следующий запуск ProShow может спросить «Recover Auto-saved Show?» — ответить «Нет»."
  ;;

etw)
  require_one_proshow
  observe sessions
  before="$reply"
  say "останавливаю ETW-помощник"
  guest_ps "Stop-ScheduledTask -TaskName psdoctor-etw
Get-CimInstance Win32_Process -Filter \"Name='PsDoctor.Observer.exe'\" | Where-Object { \$_.CommandLine -match 'etw-helper' } | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force }
'остановлен'"
  observe attach
  check "attach без ETW отвергнут (код 2)" "$(is "$code" 2)"
  check "  отказ etw-unavailable" "$(has "$reply" etw-unavailable)"
  sleep 2
  observe sessions
  echo "сеансы, появившиеся из-за отказа (для сверки с пунктом 5 — «без пустого сеанса»):"
  diff <(printf '%s\n' "$before") <(printf '%s\n' "$reply") | grep '^>' || echo "  нет"
  check "ProShow жив после отказа" "$(alive "$(proshow_pids)")"
  say "запускаю ETW-помощник обратно"
  guest_ps "Start-ScheduledTask -TaskName psdoctor-etw
\$clock = [Diagnostics.Stopwatch]::StartNew()
while (-not (Get-CimInstance Win32_Process -Filter \"Name='PsDoctor.Observer.exe'\" | Where-Object { \$_.CommandLine -match 'etw-helper' }) -and \$clock.Elapsed.TotalSeconds -lt 20) { Start-Sleep -Milliseconds 200 }
'помощник: ' + [bool](Get-CimInstance Win32_Process -Filter \"Name='PsDoctor.Observer.exe'\" | Where-Object { \$_.CommandLine -match 'etw-helper' })"
  observe attach
  check "attach с восстановленным ETW принят (код 0)" "$(is "$code" 0)"
  [ "$code" = 0 ] && { observe stop "$(json "$reply" session)"; check "  stop принят" "$(is "$code" 0)"; }
  ;;

two)
  pids="$(proshow_pids)"
  say "ProShow в госте: pid ${pids:-нет}"
  case "$pids" in
    *,*)
      observe attach
      check "attach при двух экземплярах отвергнут (код 2)" "$(is "$code" 2)"
      check "  отказ ambiguous-program" "$(has "$reply" ambiguous-program)"
      ;;
    *)
      echo "Второго экземпляра нет — программа, похоже, не даёт запустить себя дважды. Проверка неприменима; запишите в чат, что было на экране."
      ;;
  esac
  ;;

*)
  echo "неизвестный шаг: $step" >&2
  exit 3
  ;;
esac

say "итог шага $step: расхождений $mismatches"
[ "$mismatches" = 0 ]
