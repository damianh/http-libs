#!/usr/bin/env bash
# Example: ./run-conformance.sh --framework netstandard2.0 --file-system
set -euo pipefail
CONFORMANCE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec node "$CONFORMANCE_DIR/run-conformance.mjs" \
  --origin-port "${ORIGIN_PORT:-0}" --proxy-port "${PROXY_PORT:-0}" "$@"
