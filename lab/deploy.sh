#!/usr/bin/env bash
# Оснастка стенда. Не часть продукта, см. lab/README.md.
# Собрать приложения Doctor на хосте и положить готовый релиз в папку обмена.
# В ВМ релиз устанавливается командой:
#   pwsh -NoProfile -ExecutionPolicy Bypass -File \\VBoxSvr\exchange\psdoctor\Install-PsDoctor.ps1
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
exchange="${LAB_EXCHANGE:-$HOME/Lab/exchange}"
release_root="$exchange/psdoctor"
revision="$(git -C "$repo" rev-parse HEAD)"
if [ -n "$(git -C "$repo" status --porcelain)" ]; then revision="$revision-dirty"; fi
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
short_revision="${revision%%-*}"
release_id="$timestamp-$short_revision"
work="$release_root/.building-$release_id"
release="$release_root/releases/$release_id"
cleanup() { rm -rf "$work"; }
trap cleanup EXIT
mkdir -p "$work" "$release_root/releases"

projects=(
  'PsDoctor.Cli|src/PsDoctor.Cli/PsDoctor.Cli.csproj'
  'PsDoctor.App|src/PsDoctor.App/PsDoctor.App.csproj'
  'PsDoctor.Workbench|src/PsDoctor.Workbench/PsDoctor.Workbench.csproj'
  'PsDoctor.Observer|src/PsDoctor.Observer/PsDoctor.Observer.csproj'
)
printf 'Сборка релиза %s\n' "$revision" >&2
for item in "${projects[@]}"; do
  name="${item%%|*}"; project="${item#*|}"
  printf '  %s\n' "$name" >&2
  dotnet publish "$repo/$project" -c Release -r win-x64 --self-contained false \
    -p:SourceRevisionId="$revision" -o "$work/$name" --verbosity quiet --nologo
done

cp "$repo/lab/guest/Install-PsDoctor.ps1" "$work/Install-PsDoctor.ps1"
"${PYTHON:-python3}" - "$work/manifest.json" "$revision" "$timestamp" "${projects[@]}" <<'PY'
import json
import pathlib
import sys
manifest_path = pathlib.Path(sys.argv[1])
revision, timestamp = sys.argv[2:4]
projects = [item.split('|', 1)[0] for item in sys.argv[4:]]
manifest_path.write_text(json.dumps({
    "schema": 1, "revision": revision, "createdUtc": timestamp,
    "runtime": "win-x64", "projects": projects,
}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
PY
mv "$work" "$release"
cp "$release/Install-PsDoctor.ps1" "$release_root/Install-PsDoctor.ps1"
printf '%s\n' "releases/$release_id" > "$release_root/current.txt.new"
mv -f "$release_root/current.txt.new" "$release_root/current.txt"
printf 'Готово: %s\n' "$release_root/current.txt" >&2
printf 'На ВМ: pwsh -NoProfile -ExecutionPolicy Bypass -File \\\\VBoxSvr\\exchange\\psdoctor\\Install-PsDoctor.ps1\n' >&2
