#!/usr/bin/env bash
# Оснастка стенда. Не часть продукта, см. lab/README.md.
# Отдать Codex заголовок авторизации Windows-MCP, не сохраняя ключ в config.toml.

set -euo pipefail

key_file=${PSDOCTOR_WINDOWS_MCP_KEY_FILE:-"$HOME/Lab/secrets/windows-mcp.key"}

if [[ ! -r $key_file ]]; then
    echo "Не читается ключ Windows-MCP: $key_file" >&2
    exit 1
fi

key=$(tr -d '[:space:]' < "$key_file")
if [[ ! $key =~ ^[[:xdigit:]]{64}$ ]]; then
    echo "Ключ Windows-MCP должен состоять из 64 шестнадцатеричных знаков." >&2
    exit 1
fi

printf '{"Authorization":"Bearer %s"}\n' "$key"
