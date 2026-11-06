#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
if ! command -v pwsh >/dev/null; then
  printf '%s\n' 'Instala PowerShell 7 (pwsh) para usar el publicador multiplataforma.' >&2
  exit 1
fi
exec pwsh -NoProfile -File "$script_dir/publish-updates.ps1" "$@"
