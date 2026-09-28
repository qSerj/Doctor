#!/bin/bash
# Оснастка стенда. Не часть продукта, см. lab/README.md.
# Конвейер наблюдателя: собрать пакет Doctor (install/package.sh), поставить его на стенд продуктовым
# установщиком, спросить /health с хоста и прогнать тесты на стенде обоими путями. Отдельного
# лабораторного развёртывания нет: стенд стоит так же, как машина монтажёра, отличаются только настройки
# (lab/guest/stand-settings.json) и ключ, который берётся с хоста.
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
packages="$exchange/doctor"
guest_packages='\\VBoxSvr\exchange\doctor'
# Ключ и настройки установленного Doctor на стенде; установщик их не трогает.
guest_key='C:/Users/user/AppData/Local/PsDoctor/observer.key'
guest_settings='C:/ProgramData/PsDoctor/settings.json'
ssh_opts=(-o BatchMode=yes -o IdentitiesOnly=yes -o ConnectTimeout=10 -i "$lab_key")

step() { printf '\n== %s с: %s\n' "$SECONDS" "$*" >&2; }
guest_ps() { ssh "${ssh_opts[@]}" "user@$host" "pwsh -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $guest_stage\\lab\\$*"; }

cd "$repo"
# Коммит сборки. Правка, не попавшая в коммит, помечается: иначе /health врёт о том, что собрано.
revision="$(git rev-parse HEAD)"
[ -n "$(git status --porcelain)" ] && revision="$revision-dirty"

step "пакет Doctor $revision"
# Прежние пакеты стенду не нужны: ставится всегда последний.
mkdir -p "$packages"
rm -rf "$packages"/PsDoctor-*
package="$(bash install/package.sh "$packages" | tail -n 1)"
package_name="$(basename "$package")"

step "доставка в папку обмена"
mkdir -p "$stage"/{lab,packages}
cp -f lab/guest/Test-Stand.ps1 "$stage/lab/"
if [ "$tests" = 1 ]; then
  # Исходники — то, что видит git, без двора и памяти: они в .gitignore и на стенд не уходят.
  step "исходники для тестов на стенде"
  rm -rf "$stage/src.new"; mkdir -p "$stage/src.new"
  git ls-files -co --exclude-standard -z | tar -c --null -T - | tar -x -C "$stage/src.new"
  for project in "$stage"/src.new/tests/*/*.csproj; do
    step "пакеты для $(basename "$project")"
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
ssh "${ssh_opts[@]}" "user@$host" 'if not exist C:\Users\user\AppData\Local\PsDoctor mkdir C:\Users\user\AppData\Local\PsDoctor & if not exist C:\ProgramData\PsDoctor mkdir C:\ProgramData\PsDoctor'
# Сторож берёт ключ, который уже лежит в профиле, и не меняет его.
scp "${ssh_opts[@]}" -q "$secret" "user@$host:$guest_key"
step "настройки стенда"
scp "${ssh_opts[@]}" -q lab/guest/stand-settings.json "user@$host:$guest_settings"
# Ключ без пробельных символов: файл, приехавший с Windows-машины, кончается CR, и curl вставляет его
# в заголовок как есть — наблюдатель отвечает 400 без тела. Клиенты на .NET значение обрезают сами.
key="$(tr -d '[:space:]' < "$secret")"

step "установка на стенде"
ssh "${ssh_opts[@]}" "user@$host" "pwsh -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $guest_packages\\$package_name\\Install-Doctor.ps1 -User user -NoPause"

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
