#!/usr/bin/env bash
# Проверка перед коммитом в открытый репозиторий: нет ли в нём того, чему там не место —
# имён людей, названий клиентских работ, чужих путей.
#
# Список слов намеренно лежит НЕ здесь, а в закрытом хранилище памяти: сам список
# состоит ровно из того, что нельзя публиковать, и в открытом репозитории был бы утечкой.
#
#   Memorex/private-words.txt — по одному расширенному регулярному выражению в строке,
#                               строки с # и пустые игнорируются.
#
# Использование:
#   scripts/check-private.sh            проверить рабочее дерево
#   scripts/check-private.sh --staged   проверить только то, что заиндексировано
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
words="$root/Memorex/private-words.txt"

if [[ ! -f "$words" ]]; then
    echo "check-private: нет списка слов ($words)." >&2
    echo "Без него проверка бессмысленна. Проверка не пройдена." >&2
    exit 2
fi

pattern="$(grep -vE '^\s*(#|$)' "$words" | paste -sd'|' -)"
if [[ -z "$pattern" ]]; then
    echo "check-private: список слов пуст. Проверка не пройдена." >&2
    exit 2
fi

if [[ "${1-}" == "--staged" ]]; then
    mapfile -t files < <(git -C "$root" diff --cached --name-only --diff-filter=ACM)
else
    mapfile -t files < <(git -C "$root" ls-files --cached --others --exclude-standard)
fi

[[ ${#files[@]} -eq 0 ]] && { echo "check-private: нечего проверять."; exit 0; }

found=0
for f in "${files[@]}"; do
    [[ -f "$root/$f" ]] || continue
    if grep -nEi -- "$pattern" "$root/$f" >/dev/null 2>&1; then
        echo "УТЕЧКА в $f:" >&2
        grep -nEi -- "$pattern" "$root/$f" | head -5 >&2
        found=1
    fi
done

if [[ $found -eq 1 ]]; then
    echo >&2
    echo "Проверка не пройдена: в открытый репозиторий попало то, чему там не место." >&2
    exit 1
fi

echo "check-private: чисто, проверено файлов: ${#files[@]}"
