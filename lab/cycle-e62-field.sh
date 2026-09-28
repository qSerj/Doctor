#!/bin/bash
# Цикл опыта. Не часть продукта, см. lab/README.md.
# Критерии 2–4 Э6.2 «Полевой минимум» на стенде, поставленном с дежурством (lab/observer.sh --watch). Один шаг за
# запуск; ручные действия владельца — по подсказкам «>>>», время для них скрипт печатает сам.
#   lab/cycle-e62-field.sh watch [id пакета]     — критерий 2: ProShow открыт ярлыком — сеанс дежурства и environment;
#                                                  закрыт — сеанс кончается program-exited
#   lab/cycle-e62-field.sh incident [id пакета]  — критерий 3: «Решить проблему» при сеансе дежурства — одна метка,
#                                                  один факт incident, нового сеанса нет, ProShow тот же
#   lab/cycle-e62-field.sh hang [id пакета]      — критерий 4: ProShow приостановлен pssuspend — статус и окно ожидания
#                                                  Doctor, кнопка «Завершить ProShow» не раньше 3 мин, окно-призрак,
#                                                  завершение по кнопке
#   lab/cycle-e62-field.sh busy [id пакета]      — критерий 4, вторая часть: медленный проект держит ProShow «не отвечает»
#                                                  минутами, но ProShow работает — кнопки завершения нет
#
# Каждая проверка печатает «совпало» или «РАСХОЖДЕНИЕ»; код выхода — 0, если расхождений нет, иначе 1. Что видно
# только на экране — тексты Doctor и время появления кнопки, — владелец пишет в result.md пакета: машинного кода для
# этого нет. Время сравнивается по часам гостя: и факты наблюдателя, и процессы, и приостановка — оттуда; часы хоста
# после паузы ВМ расходятся с гостем на минуты. Факты и метки ложатся в results/: $LAB_EXCHANGE/checks/<id>/results.
# Переменные: LAB_HOST, LAB_KEY, LAB_EXCHANGE, PSDOCTOR_OBSERVER_URL, PSDOCTOR_OBSERVER_KEY_FILE (иначе OBSERVER_KEY).
set -uo pipefail
. "$(dirname "$0")/portable.sh"

step="${1:?укажите шаг: watch, incident, hang или busy}"
package="${2:-e62-field-001-$step}"
host="${LAB_HOST:-192.168.56.5}"
lab_key="${LAB_KEY:-$HOME/.ssh/lab_ed25519}"
exchange="${LAB_EXCHANGE:-$HOME/Lab/exchange}"
export PSDOCTOR_OBSERVER_URL="${PSDOCTOR_OBSERVER_URL:-http://$host:8100}"
export PSDOCTOR_OBSERVER_KEY_FILE="${PSDOCTOR_OBSERVER_KEY_FILE:-${OBSERVER_KEY:-$HOME/Lab/secrets/observer.key}}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
out="$exchange/checks/$package/results"
mkdir -p "$out"
# -n: ssh не читает консоль, где владелец жмёт клавиши.
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

is() { [ "$1" = "$2" ] && echo 1 || echo 0; }
has() { case "$1" in *"$2"*) echo 1 ;; *) echo 0 ;; esac; }
finish() { say "итог шага $step: расхождений $mismatches"; [ "$mismatches" = 0 ]; exit $?; }

health_json() { "${psdoctor[@]}" observe health 2>/dev/null; }

# Поле JSON по пути через точку: field "$json" activity.program. Логическое — true/false, нет поля — пусто.
field() {
  printf '%s' "$1" | "$PYTHON" -c "
import json,sys
text=sys.stdin.read().strip()
v=json.loads(text.splitlines()[0]) if text else None
for k in sys.argv[1].split('.'):
    v=v.get(k) if isinstance(v,dict) else None
print('' if v is None else str(v).lower() if isinstance(v,bool) else v)" "$2"
}

proshow_pids() { guest_ps "(@(Get-Process proshow -ErrorAction SilentlyContinue).Id | Sort-Object) -join ','"; }

alive() { [ "$(guest_ps "[bool](Get-Process -Id $1 -ErrorAction SilentlyContinue)")" = True ] && echo 1 || echo 0; }

# Время создания процесса по часам гостя, со смещением.
process_started() { guest_ps "(Get-Process -Id $1).StartTime.ToString('o')"; }

facts_of() { "${psdoctor[@]}" observe facts "$1" > "$out/facts-$1.jsonl" 2>/dev/null; }

incidents() { "${psdoctor[@]}" observe incidents > "$out/incidents.jsonl" 2>/dev/null; wc -l < "$out/incidents.jsonl" | tr -d ' '; }

session_count() { "${psdoctor[@]}" observe sessions 2>/dev/null | grep -c '"id"'; }

# Сводка сеанса из /sessions: active и finished.
summary() {
  "${psdoctor[@]}" observe sessions 2>/dev/null | "$PYTHON" -c "
import json,sys
for line in sys.stdin:
    s=json.loads(line)
    if s.get('id')=='$1': print(str(s.get('active')).lower(), str(s.get('finished')).lower())"
}

wait_inactive() {
  local session="$1" limit="$2" state
  for _ in $(seq 1 "$limit"); do
    state="$(summary "$session")"
    [ "${state%% *}" = false ] && return 0
    sleep 1
  done
  return 1
}

# Последний факт сеанса: вид и причина.
last_fact() {
  facts_of "$1"
  tail -n 1 "$out/facts-$1.jsonl" | "$PYTHON" -c "
import json,sys
f=json.loads(sys.stdin.read()); print(f.get('kind',''), (f.get('data') or {}).get('reason',''))"
}

# Прошло секунд по часам хоста — только для подсказок; числа отчёта берутся по часам гостя.
clock() { printf '%d:%02d' $(( $1 / 60 )) $(( $1 % 60 )); }

# Живой сеанс дежурства над единственным ProShow: session, pid. Без него шаг не имеет смысла.
require_watch_session() {
  local json
  pid="$(proshow_pids)"
  say "ProShow в госте: pid ${pid:-нет}"
  case "$pid" in
    '' ) echo "шаг прерван: ProShow не запущен — откройте его ярлыком и повторите"; exit 3 ;;
    *,*) echo "шаг прерван: ProShow запущен не один раз ($pid)"; exit 3 ;;
  esac
  json="$(health_json)"
  echo "$json"
  session="$(field "$json" activity.session)"
  check "/health: attached над ProShow $pid" "$(is "$(field "$json" activity.program)/$(field "$json" activity.processId)" "attached/$pid")"
  [ -n "$session" ] || { echo "шаг прерван: живого сеанса нет — дежурство не подключилось"; finish; }
  facts_of "$session"
  check "сеанс $session начат дежурством (origin watch)" "$(is "$(head -n 1 "$out/facts-$session.jsonl" | "$PYTHON" -c "import json,sys; print((json.loads(sys.stdin.read()).get('data') or {}).get('origin'))")" watch)"
}

# Окна ProShow в сеансе по часам гостя от отметки: смена хэндла и признака «не отвечает».
window_timeline() {
  "$PYTHON" - "$out/facts-$1.jsonl" "$2" <<'PY'
import datetime as d, json, sys
facts = [json.loads(l) for l in open(sys.argv[1], encoding='utf-8') if l.strip()]
start = d.datetime.fromisoformat(next(f for f in facts if f['kind'] == 'session-started')['data']['startedAt'])
mark = d.datetime.fromisoformat(sys.argv[2])
def at(f):
    h, m, s = f['elapsed'].split(':')
    return start + d.timedelta(hours=int(h), minutes=int(m), seconds=float(s))
for f in facts:
    if f['kind'] == 'main-window':
        data = f['data']
        print(f"  {(at(f) - mark).total_seconds():8.1f} с  handle={data.get('handle')} hung={data.get('hung')}")
PY
}

# Метки после отметки по часам гостя: секунд от отметки, источник, сеанс, программа, пояснение.
incidents_since() {
  "$PYTHON" - "$out/incidents.jsonl" "$1" <<'PY'
import datetime as d, json, sys
mark = d.datetime.fromisoformat(sys.argv[2])
for line in open(sys.argv[1], encoding='utf-8'):
    if not line.strip():
        continue
    i = json.loads(line)
    at = d.datetime.fromisoformat(i['atUtc'].replace('Z', '+00:00'))
    if at >= mark:
        print(f"{(at - mark).total_seconds():.1f}\t{i['source']}\t{i.get('session') or '-'}\t{i['program']}\t{i.get('note') or '-'}")
PY
}

case "$step" in
watch)
  json="$(health_json)"
  echo "$json"
  check "дежурство включено (/health watch.enabled)" "$(is "$(field "$json" watch.enabled)" true)"
  [ "$(field "$json" watch.enabled)" = true ] || { echo "шаг прерван: стенд поставлен без --watch"; finish; }
  [ -z "$(proshow_pids)" ] || { echo "шаг прерван: ProShow уже запущен — закройте его и повторите"; exit 3; }
  sessions_before="$(session_count)"

  ask "Откройте ProShow ярлыком на рабочем столе стенда — как обычно. Жду до 5 минут."
  pid=""
  for _ in $(seq 1 300); do pid="$(proshow_pids)"; [ -n "$pid" ] && break; sleep 1; done
  [ -n "$pid" ] || { check "ProShow открыт" 0; finish; }
  case "$pid" in *,*) echo "шаг прерван: ProShow запущен не один раз ($pid)"; exit 3 ;; esac
  started="$(process_started "$pid")"
  say "ProShow $pid создан в $started по часам гостя"

  session=""
  for _ in $(seq 1 60); do
    json="$(health_json)"
    [ "$(field "$json" activity.program)" = attached ] && { session="$(field "$json" activity.session)"; break; }
    sleep 1
  done
  echo "$json"
  check "/health показывает attached" "$(is "$([ -n "$session" ] && echo 1)" 1)"
  [ -n "$session" ] || finish
  check "  под наблюдением ProShow $pid" "$(is "$(field "$json" activity.processId)" "$pid")"
  check "  новый сеанс один" "$(is "$(session_count)" "$((sessions_before + 1))")"

  facts_of "$session"
  read -r first origin second delay <<< "$("$PYTHON" - "$out/facts-$session.jsonl" "$started" <<'PY'
import datetime as d, json, sys
facts = [json.loads(l) for l in open(sys.argv[1], encoding='utf-8') if l.strip()]
opened = d.datetime.fromisoformat(facts[0]['data']['startedAt'])
print(facts[0]['kind'], facts[0]['data'].get('origin'), facts[1]['kind'] if len(facts) > 1 else '-',
      round((opened - d.datetime.fromisoformat(sys.argv[2])).total_seconds(), 1))
PY
)"
  check "первый факт session-started (есть $first)" "$(is "$first" session-started)"
  check "  origin watch (есть $origin)" "$(is "$origin" watch)"
  check "второй факт environment (есть $second)" "$(is "$second" environment)"
  check "сеанс открыт через $delay с после создания ProShow, не позже 15 с" "$("$PYTHON" -c "print(1 if 0 <= float('$delay') <= 15 else 0)")"
  say "окружение стенда"
  grep -m 1 '"kind":"environment"' "$out/facts-$session.jsonl" | "$PYTHON" -c "import json,sys; print(json.dumps(json.loads(sys.stdin.read())['data'], ensure_ascii=False, indent=2))"

  ask "Закройте ProShow крестиком. Если спросит о сохранении — «Нет». Жду до 5 минут."
  if wait_inactive "$session" 300; then
    check "сеанс закончился после закрытия ProShow" 1
  else
    check "сеанс закончился после закрытия ProShow (не дождался 5 минут)" 0
  fi
  read -r kind reason <<< "$(last_fact "$session")"
  check "последний факт session-finished/program-exited (есть $kind/$reason)" "$(is "$kind/$reason" session-finished/program-exited)"
  ask "Если за шаг появилось хоть одно окно Doctor — запишите в result.md пакета, какое. Окон быть не должно."
  ;;

incident)
  require_watch_session
  started="$(process_started "$pid")"
  sessions_before="$(session_count)"
  incidents_before="$(incidents)"
  mark="$(guest_ps "[DateTimeOffset]::Now.ToString('o')")"

  ask "Откройте окно Doctor (значок в трее) и один раз нажмите «Решить проблему». Жду новую метку до 5 минут."
  for _ in $(seq 1 600); do [ "$(incidents)" -gt "$incidents_before" ] && break; sleep 0.5; done
  # Лишний щелчок дал бы вторую строку: ждём ещё немного, прежде чем считать.
  sleep 5
  incidents_after="$(incidents)"
  check "ровно одна новая метка (было $incidents_before, стало $incidents_after)" "$(is "$incidents_after" "$((incidents_before + 1))")"
  incidents_since "$mark" | tee "$out/incidents-since.tsv"
  IFS=$'\t' read -r _ source marked program _ <<< "$(head -n 1 "$out/incidents-since.tsv")"
  check "  источник wizard (есть $source)" "$(is "$source" wizard)"
  check "  метка названа сеансом дежурства $session (есть $marked)" "$(is "$marked" "$session")"
  check "  программа attached (есть $program)" "$(is "$program" attached)"
  facts_of "$session"
  check "в сеансе один факт incident" "$(is "$(grep -c '"kind":"incident"' "$out/facts-$session.jsonl")" 1)"
  check "новых сеансов нет" "$(is "$(session_count)" "$sessions_before")"
  check "ProShow тот же: pid $pid" "$(is "$(proshow_pids)" "$pid")"
  check "  и время создания то же" "$(is "$(process_started "$pid")" "$started")"
  check "сеанс дежурства жив" "$(is "$(summary "$session")" 'true false')"
  ask "Запишите в result.md: как быстро после щелчка Doctor написал «Момент отмечен…» (по ощущению: сразу, секунда-две, дольше) и что мастер показал дальше. Окна мастера можно закрыть «Отменой»."
  ;;

hang)
  require_watch_session
  tool="$(guest_ps "foreach (\$p in 'C:\\SysinternalsSuite\\pssuspend64.exe', 'C:\\SysinternalsSuite\\pssuspend.exe') { if (Test-Path \$p) { \$p; break } }")"
  [ -n "$tool" ] || { echo "шаг прерван: в госте нет pssuspend в C:\\SysinternalsSuite"; exit 3; }
  incidents >/dev/null

  ask "Откройте окно Doctor (значок в трее) и держите его на виду. ProShow не трогайте, пока скрипт не попросит. Через 30 с приостановлю ProShow."
  sleep 30
  mark="$(guest_ps "\$at = [DateTimeOffset]::Now.ToString('o'); & '$tool' -accepteula -nobanner $pid *> \$null; \$at")"
  say "ProShow $pid приостановлен в $mark по часам гостя"
  ask "Смотрите на строку состояния Doctor. Когда она сменится на «ProShow не отвечает», запишите, сколько прошло — по таймеру ниже."
  begin=$SECONDS
  prompts=0
  shown=-15
  while [ "$(alive "$pid")" = 1 ]; do
    passed=$((SECONDS - begin))
    [ $((passed - shown)) -ge 15 ] && { echo "прошло $(clock "$passed")"; shown=$passed; }
    if [ "$prompts" -lt 1 ] && [ "$passed" -ge 45 ]; then
      prompts=1
      ask "Нажмите «Решить проблему». Ждём окно «ProShow не отвечает» без совета закрыть. Окно не закрывайте, следите за ним и таймером."
    fi
    if [ "$prompts" -lt 2 ] && [ "$passed" -ge 210 ]; then
      prompts=2
      ask "В окне ожидания уже должна быть кнопка «Завершить ProShow» — запишите, когда она появилась. Не нажимайте её. Щёлкните один раз по середине окна ProShow, не по крестику, и запишите, что стало с окном ProShow и с текстом в окне Doctor."
    fi
    if [ "$prompts" -lt 3 ] && [ "$passed" -ge 300 ]; then
      prompts=3
      ask "Нажмите «Завершить ProShow», затем в подтверждении — «Завершить ProShow». Если кнопки нет — запишите это и ничего не нажимайте."
    fi
    [ "$passed" -ge 480 ] && break
    sleep 5
  done

  if [ "$(alive "$pid")" = 1 ]; then
    guest_ps "& '$tool' -accepteula -nobanner -r $pid *> \$null; 'возобновлён'"
    check "ProShow завершён по кнопке за 8 минут (не завершён, возобновлён)" 0
  else
    check "ProShow завершён" 1
  fi

  facts_of "$session"
  say "главное окно ProShow в сеансе дежурства, секунд от приостановки"
  window_timeline "$session" "$mark" | tee "$out/window-timeline.txt"
  first_hung="$(awk '$4 == "hung=True" && $1 >= 0 { print $1; exit }' "$out/window-timeline.txt")"
  check "наблюдатель увидел «не отвечает» (через ${first_hung:-—} с)" "$(is "$([ -n "$first_hung" ] && echo 1)" 1)"
  incidents >/dev/null
  say "метки после приостановки: секунд, источник, сеанс, программа, пояснение"
  incidents_since "$mark" | tee "$out/incidents-since.tsv"
  wizard_at="$(awk -F'\t' '$2 == "wizard" && $5 == "-" { print $1; exit }' "$out/incidents-since.tsv")"
  check "метка «Решить проблему» при зависании (через ${wizard_at:-—} с)" "$(is "$([ -n "$wizard_at" ] && echo 1)" 1)"
  read -r terminate_at terminate_note <<< "$(awk -F'\t' '$5 ~ /^terminate/ { print $1, $5; exit }' "$out/incidents-since.tsv")"
  check "метка перед завершением: ${terminate_note:-нет}" "$(is "$([ -n "$terminate_at" ] && echo 1)" 1)"
  if [ -n "$terminate_at" ]; then
    check "  завершение не раньше 3 мин после приостановки (через $terminate_at с)" "$("$PYTHON" -c "print(1 if float('$terminate_at') >= 180 else 0)")"
    check "  завершён найденный процесс (pids=$pid)" "$(has "$terminate_note" "pids=$pid")"
  fi
  wait_inactive "$session" 30
  read -r kind reason <<< "$(last_fact "$session")"
  check "сеанс дежурства кончился program-exited (есть $kind/$reason)" "$(is "$kind/$reason" session-finished/program-exited)"
  ask "Запишите в result.md: когда появился статус «ProShow не отвечает», что было в окне ожидания в первые 60 с, когда появилась кнопка, что стало после щелчка по окну ProShow и что мастер показал после завершения. Следующий запуск ProShow может спросить «Recover Auto-saved Show?» — ответить «Нет»."
  ;;

busy)
  json="$(health_json)"
  check "дежурство включено (/health watch.enabled)" "$(is "$(field "$json" watch.enabled)" true)"
  [ -z "$(proshow_pids)" ] || { echo "шаг прерван: ProShow уже запущен — закройте его и повторите"; exit 3; }
  incidents >/dev/null
  ask "Откройте на стенде медленный проект из опыта slow-project-001 двойным щелчком по его файлу .psh. Окно Doctor держите открытым. Жду до 5 минут."
  pid=""
  for _ in $(seq 1 300); do pid="$(proshow_pids)"; [ -n "$pid" ] && break; sleep 1; done
  [ -n "$pid" ] || { check "ProShow открыт" 0; finish; }
  mark="$(process_started "$pid")"
  session=""
  for _ in $(seq 1 60); do
    json="$(health_json)"
    [ "$(field "$json" activity.program)" = attached ] && { session="$(field "$json" activity.session)"; break; }
    sleep 1
  done
  check "дежурство подключилось" "$(is "$([ -n "$session" ] && echo 1)" 1)"
  [ -n "$session" ] || finish

  say "жду, пока окно ProShow перестанет отвечать (до 10 минут)"
  hung_since=""
  for _ in $(seq 1 120); do
    facts_of "$session"
    hung_since="$(window_timeline "$session" "$mark" | awk '$4 == "hung=True" { print $1; exit }')"
    [ -n "$hung_since" ] && break
    sleep 5
  done
  [ -n "$hung_since" ] || { echo "окно ProShow так и не перестало отвечать — проверка неприменима; запишите это в result.md"; finish; }
  say "окно не отвечает с $hung_since с после создания ProShow"
  ask "Через 30 с нажмите «Решить проблему». Первую минуту окно ожидания просит не закрывать ProShow, потом должно сказать, что ProShow занят, но работает. Кнопки «Завершить ProShow» быть не должно. Окно не закрывайте, пока ProShow не оживёт."

  say "жду, пока ProShow снова ответит (до 15 минут)"
  for _ in $(seq 1 180); do
    facts_of "$session"
    window_timeline "$session" "$mark" > "$out/window-timeline.txt"
    [ "$(tail -n 1 "$out/window-timeline.txt" | awk '{ print $4 }')" = hung=False ] \
      && [ "$(grep -c 'hung=True' "$out/window-timeline.txt")" -gt 0 ] && break
    sleep 5
  done
  cat "$out/window-timeline.txt"
  longest="$("$PYTHON" - "$out/window-timeline.txt" <<'PY'
import sys
rows = [l.split() for l in open(sys.argv[1], encoding='utf-8') if l.strip()]
best, since = 0.0, None
for r in rows:
    t, hung = float(r[0]), r[3] == 'hung=True'
    if hung and since is None:
        since = t
    if not hung and since is not None:
        best, since = max(best, t - since), None
print(round(best, 1))
PY
)"
  say "самое долгое «не отвечает»: $longest с"
  # Короче 3 мин кнопка не появилась бы и без учёта работы — тогда проверка ничего не доказывает.
  check "зависание не короче 3 мин ($longest с)" "$("$PYTHON" -c "print(1 if float('$longest') >= 180 else 0)")"
  incidents >/dev/null
  incidents_since "$mark" | tee "$out/incidents-since.tsv"
  marks="$(awk -F'\t' '$2 == "wizard"' "$out/incidents-since.tsv" | wc -l | tr -d ' ')"
  check "метка «Решить проблему» при зависании (меток $marks)" "$([ "$marks" -ge 1 ] && echo 1 || echo 0)"
  check "завершения не было" "$(is "$(grep -c terminate "$out/incidents-since.tsv")" 0)"
  check "ProShow жив" "$(alive "$pid")"
  ask "Запишите в result.md, какие тексты показывало окно ожидания и была ли хоть на миг кнопка «Завершить ProShow». Потом закройте окно «Понятно» и ProShow — крестиком; при вопросе о сохранении — «Нет»."
  ;;

*)
  echo "неизвестный шаг: $step" >&2
  exit 3
  ;;
esac

finish
