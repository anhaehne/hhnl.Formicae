#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
API_PROJECT="$REPOSITORY_ROOT/src/hhnl.Formicae.Api/hhnl.Formicae.Api.csproj"
CLIENT_DIRECTORY="$REPOSITORY_ROOT/src/hhnl.Formicae.Api/ClientApp"
RESULT_DIRECTORY="$REPOSITORY_ROOT/test-results/dev"
STATE_DIRECTORY="${TMPDIR:-/tmp}/formicae-dev-$(id -u)"
API_PID_FILE="$STATE_DIRECTORY/api.pid"
UI_PID_FILE="$STATE_DIRECTORY/ui.pid"
API_LOG="$RESULT_DIRECTORY/api.log"
UI_LOG="$RESULT_DIRECTORY/ui.log"
API_URL="http://127.0.0.1:5000"
UI_URL="http://127.0.0.1:5173"

usage() {
  echo "Usage: $0 {prepare|start|status|logs|stop} [api|ui]" >&2
}

read_pid() {
  local pid_file="$1"
  if [[ -f "$pid_file" ]]; then
    local pid
    pid="$(<"$pid_file")"
    if [[ "$pid" =~ ^[0-9]+$ ]]; then
      printf '%s' "$pid"
    fi
  fi
}

is_running() {
  local pid_file="$1"
  local marker="$2"
  local pid
  pid="$(read_pid "$pid_file")"
  [[ -n "$pid" ]] || return 1
  kill -0 "$pid" 2>/dev/null || return 1
  [[ -r "/proc/$pid/cmdline" ]] || return 1
  tr '\0' ' ' < "/proc/$pid/cmdline" | grep -Fq -- "$marker"
}

wait_for_url() {
  local name="$1"
  local url="$2"
  local pid_file="$3"
  local marker="$4"
  local log_file="$5"

  # The background process can be observed before exec has applied its marker,
  # especially on busy CI hosts. Give that short transition a chance to finish
  # while still failing immediately if the process itself exits.
  local pid
  pid="$(read_pid "$pid_file")"
  for _ in $(seq 1 25); do
    if is_running "$pid_file" "$marker"; then
      break
    fi
    if [[ -z "$pid" ]] || ! kill -0 "$pid" 2>/dev/null; then
      echo "$name exited before becoming ready. Recent log output:" >&2
      tail -n 50 "$log_file" >&2 || true
      return 1
    fi
    sleep 0.2
  done

  for _ in $(seq 1 90); do
    if ! is_running "$pid_file" "$marker"; then
      echo "$name exited before becoming ready. Recent log output:" >&2
      tail -n 50 "$log_file" >&2 || true
      return 1
    fi
    if curl --fail --silent --show-error "$url" >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
  done

  echo "$name did not become ready at $url within 90 seconds. Recent log output:" >&2
  tail -n 50 "$log_file" >&2 || true
  return 1
}

stop_process() {
  local name="$1"
  local pid_file="$2"
  local marker="$3"
  local pid
  pid="$(read_pid "$pid_file")"

  if [[ -z "$pid" ]]; then
    rm -f -- "$pid_file"
    return 0
  fi

  if ! is_running "$pid_file" "$marker"; then
    echo "$name is not running; removing stale state."
    rm -f -- "$pid_file"
    return 0
  fi

  kill -- "-$pid" 2>/dev/null || kill "$pid" 2>/dev/null || true
  for _ in $(seq 1 10); do
    if ! kill -0 "$pid" 2>/dev/null; then
      rm -f -- "$pid_file"
      echo "$name stopped."
      return 0
    fi
    sleep 1
  done

  kill -KILL -- "-$pid" 2>/dev/null || kill -KILL "$pid" 2>/dev/null || true
  rm -f -- "$pid_file"
  echo "$name stopped after a forced shutdown."
}

prepare() {
  command -v dotnet >/dev/null || { echo "dotnet is required." >&2; exit 1; }
  command -v npm >/dev/null || { echo "npm is required." >&2; exit 1; }

  dotnet restore "$REPOSITORY_ROOT/hhnl.Formicae.slnx"
  dotnet build "$REPOSITORY_ROOT/hhnl.Formicae.slnx" --no-restore
  npm --prefix "$CLIENT_DIRECTORY" ci
  npm --prefix "$CLIENT_DIRECTORY" exec -- playwright install chromium
}

start() {
  command -v dotnet >/dev/null || { echo "dotnet is required." >&2; exit 1; }
  command -v node >/dev/null || { echo "node is required." >&2; exit 1; }
  command -v curl >/dev/null || { echo "curl is required." >&2; exit 1; }
  command -v setsid >/dev/null || { echo "setsid from util-linux is required." >&2; exit 1; }
  [[ -d "$CLIENT_DIRECTORY/node_modules" ]] || {
    echo "Frontend dependencies are missing. Run '$0 prepare' first." >&2
    exit 1
  }

  mkdir -p -- "$STATE_DIRECTORY" "$RESULT_DIRECTORY"

  if is_running "$API_PID_FILE" "formicae-dev-api" && is_running "$UI_PID_FILE" "formicae-dev-ui"; then
    echo "Formicae development services are already running."
    status
    return 0
  fi

  stop_process "API" "$API_PID_FILE" "formicae-dev-api"
  stop_process "UI" "$UI_PID_FILE" "formicae-dev-ui"

  setsid env \
    ASPNETCORE_ENVIRONMENT=Development \
    UseFakeAdapters=true \
    PersistenceMode=InMemory \
    WorkflowDiscovery__Enabled=false \
    bash -c 'exec -a formicae-dev-api dotnet run --project "$1" --no-launch-profile --urls http://127.0.0.1:5000' _ "$API_PROJECT" \
    >"$API_LOG" 2>&1 \
    </dev/null &
  local api_pid=$!
  printf '%s\n' "$api_pid" > "$API_PID_FILE"

  setsid bash -c 'cd -- "$1" && exec -a formicae-dev-ui node ./node_modules/vite/bin/vite.js --host 127.0.0.1 --port 5173 --strictPort' _ "$CLIENT_DIRECTORY" \
    >"$UI_LOG" 2>&1 \
    </dev/null &
  local ui_pid=$!
  printf '%s\n' "$ui_pid" > "$UI_PID_FILE"

  if ! wait_for_url "API" "$API_URL/healthz" "$API_PID_FILE" "formicae-dev-api" "$API_LOG"; then
    stop
    return 1
  fi

  if ! wait_for_url "UI" "$UI_URL" "$UI_PID_FILE" "formicae-dev-ui" "$UI_LOG"; then
    stop
    return 1
  fi

  echo "Formicae is ready:"
  echo "  API: $API_URL"
  echo "  UI:  $UI_URL"
  echo "  Logs: $RESULT_DIRECTORY"
}

status() {
  local failed=0

  if is_running "$API_PID_FILE" "formicae-dev-api" && curl --fail --silent "$API_URL/healthz" >/dev/null 2>&1; then
    echo "API: running and healthy at $API_URL"
  else
    echo "API: stopped or unhealthy"
    failed=1
  fi

  if is_running "$UI_PID_FILE" "formicae-dev-ui" && curl --fail --silent "$UI_URL" >/dev/null 2>&1; then
    echo "UI: running and responding at $UI_URL"
  else
    echo "UI: stopped or unhealthy"
    failed=1
  fi

  return "$failed"
}

logs() {
  local target="${1:-all}"
  case "$target" in
    api)
      tail -n 100 "$API_LOG" 2>/dev/null || echo "No API log available."
      ;;
    ui)
      tail -n 100 "$UI_LOG" 2>/dev/null || echo "No UI log available."
      ;;
    all)
      echo "== API =="
      tail -n 100 "$API_LOG" 2>/dev/null || echo "No API log available."
      echo "== UI =="
      tail -n 100 "$UI_LOG" 2>/dev/null || echo "No UI log available."
      ;;
    *)
      usage
      exit 2
      ;;
  esac
}

stop() {
  stop_process "UI" "$UI_PID_FILE" "formicae-dev-ui"
  stop_process "API" "$API_PID_FILE" "formicae-dev-api"
}

case "${1:-}" in
  prepare)
    [[ $# -eq 1 ]] || { usage; exit 2; }
    prepare
    ;;
  start)
    [[ $# -eq 1 ]] || { usage; exit 2; }
    start
    ;;
  status)
    [[ $# -eq 1 ]] || { usage; exit 2; }
    status
    ;;
  logs)
    [[ $# -le 2 ]] || { usage; exit 2; }
    logs "${2:-all}"
    ;;
  stop)
    [[ $# -eq 1 ]] || { usage; exit 2; }
    stop
    ;;
  *)
    usage
    exit 2
    ;;
esac
