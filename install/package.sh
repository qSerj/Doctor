#!/usr/bin/env bash
# Собрать пакет установки Doctor: папку, которую инженер несёт на машину монтажёра и запускает «Установить.cmd».
# Сборка самодостаточная (self-contained): на машине не нужен установленный .NET.
#   install/package.sh [каталог-вывода]    по умолчанию artifacts/doctor
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
out_root="${1:-$repo/artifacts/doctor}"
revision="$(git -C "$repo" rev-parse HEAD)"
if [ -n "$(git -C "$repo" status --porcelain)" ]; then revision="$revision-dirty"; fi
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
package_id="PsDoctor-$timestamp-${revision:0:7}"
work="$out_root/.building-$package_id"
package="$out_root/$package_id"
cleanup() { rm -rf "$work"; }
trap cleanup EXIT
mkdir -p "$work"

projects=(
  'App|src/PsDoctor.App/PsDoctor.App.csproj'
  'Observer|src/PsDoctor.Observer/PsDoctor.Observer.csproj'
)
printf 'Сборка пакета %s\n' "$revision" >&2
for item in "${projects[@]}"; do
  name="${item%%|*}"; project="${item#*|}"
  printf '  %s\n' "$name" >&2
  dotnet publish "$repo/$project" -c Release -r win-x64 --self-contained true \
    -p:SourceRevisionId="$revision" -o "$work/$name" --verbosity quiet --nologo
done

cp "$repo/install/Install-Doctor.ps1" "$repo/install/Установить.cmd" "$repo/install/Удалить.cmd" "$repo/install/README.md" "$work/"
"${PYTHON:-python3}" - "$work/manifest.json" "$revision" "$timestamp" <<'PY'
import json
import pathlib
import sys
path = pathlib.Path(sys.argv[1])
revision, timestamp = sys.argv[2:4]
path.write_text(json.dumps({
    "schema": 1, "product": "PsDoctor", "revision": revision, "createdUtc": timestamp,
    "runtime": "win-x64", "programs": ["App", "Observer"],
}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
PY
mv "$work" "$package"
printf 'Готово: %s\n' "$package" >&2
