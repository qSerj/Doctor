# Общее для скриптов лаборатории: они запускаются и на Linux-машине разработки, и на Windows-хосте в Git Bash.
# Подключается точкой в начале скрипта: . "$(dirname "$0")/portable.sh"
#
# Чего не хватает в Git Bash: rsync (совсем) и python3 (есть python). Поэтому копирование каталога идёт через
# sync_dir, а разбор JSON — через "$PYTHON". Больше различий между машинами нет: ssh, scp, tar, curl и openssl
# в Git Bash есть.

if command -v python3 >/dev/null 2>&1; then
  PYTHON="$(command -v python3)"
elif command -v python >/dev/null 2>&1; then
  PYTHON="$(command -v python)"
else
  echo "нет python3 и python: без них не разобрать вывод наблюдателя" >&2
  exit 3
fi
export PYTHON

# Содержимое каталога-источника в каталог-приёмник, как rsync -a --delete: лишнее в приёмнике удаляется.
sync_dir() {
  local from="$1" to="$2"
  if command -v rsync >/dev/null 2>&1; then
    rsync -a --delete "$from/" "$to/"
  else
    rm -rf "$to"
    mkdir -p "$to"
    cp -r "$from/." "$to/"
  fi
}
