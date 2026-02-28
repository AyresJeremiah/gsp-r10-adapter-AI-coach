#!/usr/bin/env bash
# publish.sh — Build a self-contained single-file executable for Raspberry Pi (linux-arm64).
# Usage: ./publish.sh /path/to/output/dir

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOTNET="${DOTNET_ROOT:-/home/jeremiah/dotnet}/dotnet"
RID="linux-arm64"
PROJECT="$SCRIPT_DIR/gspro-r10.csproj"

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <output-directory>" >&2
  exit 1
fi

OUT_DIR="$1"
mkdir -p "$OUT_DIR"

echo "Building self-contained single-file executable..."
echo "  RID:    $RID"
echo "  Output: $OUT_DIR"
echo ""

"$DOTNET" publish "$PROJECT" \
  --configuration Release \
  --runtime "$RID" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  --output "$OUT_DIR"

# Copy runtime files the app needs
cp "$SCRIPT_DIR/settings.json" "$OUT_DIR/settings.json" 2>/dev/null || true

echo ""
echo "=== Done ==="
echo "Executable: $OUT_DIR/gspro-r10"
echo "Run with:   $OUT_DIR/gspro-r10"
