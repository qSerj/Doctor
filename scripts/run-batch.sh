#!/usr/bin/env bash
# Оснастка стенда — прогон доктора по пачке файлов шоу и сводка по ней.
#
# Каталог с материалом передаётся параметром: умолчания нет и не будет. Вшитый путь
# превратил бы разовую перепись чужой машины в требование к проекту.
#
# В просматриваемый каталог не пишется ничего: скрипт отказывается работать,
# если каталог результатов лежит внутри него.
#
#   scripts/run-batch.sh <каталог с .psh> <каталог результатов> [соль обезличивания]
#
# Соль делает псевдонимы одинаковыми между прогонами; без неё второй прогон
# несравним с первым. Соль в открытых документах не хранится — она в закрытой памяти.
#
# Опросчик видео назначается переменной FFPROBE (путь к ffprobe); без неё доктор ищет
# его рядом с собой и в PATH, а не найдя — оставляет видео неизмеренными с причиной.
#
# Идёт и на Linux, и в Git Bash на Windows.
#
# Имена переменных латиницей не по вкусу: имена в оболочке обязаны быть ASCII.

set -euo pipefail

if [[ $# -lt 2 || $# -gt 3 ]]; then
    echo "Использование: $0 <каталог с .psh> <каталог результатов> [соль обезличивания]" >&2
    exit 3
fi

source_dir=$(realpath -- "$1")
out_dir=$(realpath -m -- "$2")
salt=${3:-}

if [[ ! -d $source_dir ]]; then
    echo "Не каталог: $source_dir" >&2
    exit 3
fi

if [[ $out_dir == "$source_dir" ]]; then
    echo "Каталог результатов совпадает с просматриваемым каталогом — туда не пишется ничего." >&2
    exit 3
fi

case "$out_dir/" in
    "$source_dir"/*)
        echo "Каталог результатов внутри просматриваемого каталога — туда не пишется ничего." >&2
        exit 3
        ;;
esac

root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
bin=$root/src/PsDoctor.Cli/bin/Release/net10.0/psdoctor

# Собирается всегда: сборка инкрементальная, а прогон пачки старым бинарником
# молча дал бы числа прошлой версии доктора.
echo "Собираю CLI…" >&2
dotnet build "$root/src/PsDoctor.Cli" -c Release -v q --nologo >&2

if [[ -f $bin.exe ]]; then
    bin=$bin.exe
fi

probe_args=()
if [[ -n ${FFPROBE:-} ]]; then
    probe_args=(--ffprobe "$FFPROBE")
fi

to_windows=
if [[ $bin == *.exe ]] && command -v cygpath > /dev/null 2>&1; then
    to_windows=1
fi

python=python3
if ! command -v "$python" > /dev/null 2>&1; then
    python=python
fi

mkdir -p "$out_dir"
reports=$out_dir/reports.jsonl
index=$out_dir/index.tsv
errors=$out_dir/stderr.log
: > "$reports"
: > "$errors"
printf 'n\texit\tms\tbytes\tpath\n' > "$index"

anon=$out_dir/reports-anon.jsonl
if [[ -n $salt ]]; then
    : > "$anon"
fi

mapfile -t files < <(find "$source_dir" -type f -iname '*.psh' | sort)
echo "Файлов шоу: ${#files[@]}" >&2

started=$(date +%s)
n=0
for f in "${files[@]}"; do
    n=$((n + 1))
    rel=${f#"$source_dir"/}
    # В Git Bash путь для программы Windows переводится явно: сама оболочка
    # не переводит аргумент с апострофом, и файл объявляется ненайденным.
    arg=$f
    if [[ -n $to_windows ]]; then
        arg=$(cygpath -w -- "$f")
    fi
    t0=$(date +%s%N)
    line=$("$bin" "$arg" "${probe_args[@]}" 2>>"$errors") && code=0 || code=$?
    t1=$(date +%s%N)
    printf '%s\n' "$line" >> "$reports"
    printf '%d\t%d\t%d\t%d\t%s\n' "$n" "$code" "$(( (t1 - t0) / 1000000 ))" "$(stat -c %s -- "$f")" "$rel" >> "$index"

    if [[ -n $salt ]]; then
        "$bin" "$arg" "${probe_args[@]}" --anonymize --anonymize-salt "$salt" >> "$anon" 2>>"$errors" || true
    fi
done
finished=$(date +%s)
wall_seconds=$((finished - started))

printf '{"files":%d,"wallSeconds":%d}\n' "${#files[@]}" "$wall_seconds" > "$out_dir/run-meta.json"

echo "Прогон: ${#files[@]} файлов за $wall_seconds с" >&2

"$python" "$root/scripts/summarize.py" "$out_dir"
