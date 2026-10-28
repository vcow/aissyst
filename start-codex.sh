#!/usr/bin/env bash
set -euo pipefail

project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
project_codex_home="$project_root/.codex-account"

if ! command -v codex >/dev/null 2>&1; then
    printf '%s\n' 'Codex CLI is not installed or is not available on PATH.' >&2
    exit 127
fi

umask 077
mkdir -p "$project_codex_home"
cd "$project_root"

# Apply account and state isolation only to the new process.
# Do not reuse a shared daemon or inherited API credentials.
exec env -u OPENAI_API_KEY -u CODEX_API_KEY -u CODEX_ACCESS_TOKEN \
    CODEX_HOME="$project_codex_home" \
    CODEX_SQLITE_HOME="$project_codex_home" \
    codex --no-daemon -c 'cli_auth_credentials_store="file"' \
    -c 'forced_login_method="chatgpt"' "$@"
