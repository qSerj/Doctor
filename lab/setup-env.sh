#!/usr/bin/env bash
# Оснастка стенда. Настройка переменных окружения для лабораторных прогонов.
# Использование: source lab/setup-env.sh

# ============================================================================
# РЕДАКТИРУЕМЫЕ ЗНАЧЕНИЯ
# ============================================================================

export LAB_HOST='192.168.56.5'
export LAB_KEY="$HOME/.ssh/lab_ed25519"
export LAB_EXCHANGE="$HOME/Lab/exchange"

export OBSERVER_PORT='8100'
export OBSERVER_KEY="$HOME/Lab/secrets/observer.key"

# Путь проекта внутри Windows через общую папку VirtualBox.
export PROJECT_SOURCE='\\VBoxSvr\exchange\projects\slow-001'

# Рабочая копия проекта внутри гостевой Windows.
export PROJECT_DIR='C:\lab\slow-001'

# Только имя файла шоу, без каталога.
export SHOW_FILE='Юбилей папыpsh.psh'

# Каталог журналов текущей проверки.
export OUT="$PWD/artifacts/lab/slow-project"

# ============================================================================
# ПРОИЗВОДНЫЕ ЗНАЧЕНИЯ И ПРОВЕРКИ
# ============================================================================

export PSDOCTOR_OBSERVER_URL="http://$LAB_HOST:$OBSERVER_PORT"
export PSDOCTOR_OBSERVER_KEY_FILE="$OBSERVER_KEY"

mkdir -p "$LAB_EXCHANGE"

if [[ -r "$LAB_KEY" ]]; then
    ssh_status='найден'
else
    ssh_status="НЕ найден: $LAB_KEY"
fi

if [[ "$SHOW_FILE" == 'ИМЯ_ПРОЕКТА.psh' ]]; then
    show_status='нужно заменить SHOW_FILE'
else
    show_status='задан'
fi

printf 'Окружение psdoctor загружено.\n'
printf 'LAB_HOST=%s\n' "$LAB_HOST"
printf 'LAB_KEY=%s (%s)\n' "$LAB_KEY" "$ssh_status"
printf 'LAB_EXCHANGE=%s\n' "$LAB_EXCHANGE"
printf 'OBSERVER_PORT=%s\n' "$OBSERVER_PORT"
printf 'OBSERVER_KEY=%s\n' "$OBSERVER_KEY"
printf 'PSDOCTOR_OBSERVER_URL=%s\n' "$PSDOCTOR_OBSERVER_URL"
printf 'PSDOCTOR_OBSERVER_KEY_FILE=%s\n' "$PSDOCTOR_OBSERVER_KEY_FILE"
printf 'PROJECT_SOURCE=%s\n' "$PROJECT_SOURCE"
printf 'PROJECT_DIR=%s\n' "$PROJECT_DIR"
printf 'SHOW_FILE=%s (%s)\n' "$SHOW_FILE" "$show_status"
printf 'OUT=%s\n' "$OUT"

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    printf '\nОшибка: скрипт нужно подключать так:\n  source lab/setup-env.sh\n' >&2
    exit 2
fi
