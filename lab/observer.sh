#!/bin/bash
# Оснастка стенда. Не часть продукта, см. lab/README.md.
# Конвейер наблюдателя (Ш0 Э4.0): собрать на Linux, доставить на стенд, перезапустить задачей
# в сеансе пользователя, спросить /health с хоста и прогнать тесты на стенде обоими путями.
#   lab/observer.sh              — всё
#   lab/observer.sh --no-tests   — без тестов на стенде
#
# Стенд должен быть запущен. Сети у стенда нет, поэтому пакеты для тестов восстанавливаются здесь
# в локальный источник и уходят вместе с исходниками через папку обмена.
# Переменные: LAB_HOST (192.168.56.5), LAB_KEY (~/.ssh/lab_ed25519), LAB_EXCHANGE (~/Lab/exchange),
# OBSERVER_PORT (8100), OBSERVER_KEY (~/Lab/secrets/observer.key)
set -euo pipefail
. "$(dirname "$0")/portable.sh"

host="${LAB_HOST:-192.168.56.5}"
lab_key="${LAB_KEY:-$HOME/.ssh/lab_ed25519}"
exchange="${LAB_EXCHANGE:-$HOME/Lab/exchange}"
port="${OBSERVER_PORT:-8100}"
secret="${OBSERVER_KEY:-$HOME/Lab/secrets/observer.key}"
tests=1
[ "${1:-}" = "--no-tests" ] && tests=0

repo="$(cd "$(dirname "$0")/.." && pwd)"
stage="$exchange/observer"
guest_stage='\\VBoxSvr\exchange\observer'
ssh_opts=(-o BatchMode=yes -o IdentitiesOnly=yes -o ConnectTimeout=10 -i "$lab_key")

step() { printf '\n== %s с: %s\n' "$SECONDS" "$*" >&2; }
guest_ps() { ssh "${ssh_opts[@]}" "user@$host" "pwsh -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $guest_stage\\lab\\$*"; }

cd "$repo"
# Коммит сборки. Правка, не попавшая в коммит, помечается: иначе /health врёт о том, что собрано.
revision="$(git rev-parse HEAD)"
[ -n "$(git status --porcelain)" ] && revision="$revision-dirty"

step "сборка $revision"
dotnet publish src/PsDoctor.Observer -c Release -r win-x64 --self-contained false \
  -p:SourceRevisionId="$revision" -o artifacts/lab/observer --verbosity quiet --nologo

step "доставка в папку обмена"
mkdir -p "$stage"/{bin,lab,packages}
sync_dir artifacts/lab/observer "$stage/bin"
cp -f lab/guest/Deploy-Observer.ps1 lab/guest/Test-Stand.ps1 "$stage/lab/"
if [ "$tests" = 1 ]; then
  # Исходники — то, что видит git, без двора и памяти: они в .gitignore и на стенд не уходят.
  rm -rf "$stage/src.new"; mkdir -p "$stage/src.new"
  git ls-files -co --exclude-standard -z | tar -c --null -T - | tar -x -C "$stage/src.new"
  for project in "$stage"/src.new/tests/*/*.csproj; do
    dotnet restore "$project" --packages "$stage/packages" --verbosity quiet
  done
  rm -rf "$stage/src"; mv "$stage/src.new" "$stage/src"
fi

step "ключ наблюдателя"
if [ ! -s "$secret" ]; then
  mkdir -p "$(dirname "$secret")"
  (umask 077; openssl rand -hex 32 > "$secret")
  echo "создан $secret" >&2
fi
ssh "${ssh_opts[@]}" "user@$host" 'if not exist C:\lab\observer mkdir C:\lab\observer'
scp "${ssh_opts[@]}" -q "$secret" "user@$host:C:/lab/observer/observer.key"
# Ключ без пробельных символов: файл, приехавший с Windows-машины, кончается CR, и curl вставляет его
# в заголовок как есть — наблюдатель отвечает 400 без тела. Клиенты на .NET значение обрезают сами.
key="$(tr -d '[:space:]' < "$secret")"

step "перезапуск на стенде"
guest_ps Deploy-Observer.ps1 -Listen "$host:$port"

step "/health с хоста"
url="http://$host:$port/health"
body=""
for _ in $(seq 1 30); do
  if body="$(curl -sf -m 2 -H "Authorization: Bearer $key" "$url")"; then break; fi
  body=""; sleep 1
done
[ -n "$body" ] || { echo "нет ответа $url" >&2; exit 1; }
echo "$body"
commit="$(printf '%s' "$body" | "$PYTHON" -c 'import json,sys; print(json.load(sys.stdin)["commit"])')"
[ "$commit" = "$revision" ] || { echo "коммит на стенде $commit, собран $revision" >&2; exit 1; }
unauthorized="$(curl -s -o /dev/null -w '%{http_code}' -m 2 "$url")"
[ "$unauthorized" = 401 ] || { echo "без ключа ответ $unauthorized, ожидался 401" >&2; exit 1; }
echo "без ключа: $unauthorized" >&2

[ "$tests" = 1 ] || exit 0

step "тесты на стенде по SSH"
guest_ps Test-Stand.ps1

step "тесты на стенде в сеансе пользователя"
guest_ps Test-Stand.ps1 -Session

step "готово: $revision"
