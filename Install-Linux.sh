#!/usr/bin/env bash
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
command -v python3 >/dev/null || { echo 'Python 3 is required (install python3 using your package manager).' >&2; exit 1; }
exec python3 "$root/scripts/install_linux.py" "$@"
