#!/usr/bin/env bash
# disconnect.sh — Disconnect and unpair the Garmin Approach R10.

set -uo pipefail

R10_NAME="Approach R10"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "=== GSP R10 Disconnect ==="
echo ""

# ── 1. Discover available adapters ────────────────────────────────────────────
mapfile -t ADAPTER_LINES < <(bluetoothctl list 2>/dev/null | grep "^Controller")

if [[ ${#ADAPTER_LINES[@]} -eq 0 ]]; then
  echo "ERROR: No Bluetooth adapters found. Is bluetoothd running?" >&2
  exit 1
fi

ADAPTER_MACS=()
for line in "${ADAPTER_LINES[@]}"; do
  ADAPTER_MACS+=("$(awk '{print $2}' <<< "$line")")
done

CURRENT=$(grep -o '"bluetoothAdapterAddress"[[:space:]]*:[[:space:]]*"[^"]*"' \
  "$SCRIPT_DIR/settings.json" 2>/dev/null | grep -o '[A-F0-9:]\{17\}' || true)

# ── 2. Adapter selection ───────────────────────────────────────────────────────
DEFAULT_IDX=1
echo "Bluetooth adapters:"
for i in "${!ADAPTER_MACS[@]}"; do
  n=$((i + 1))
  if [[ "${ADAPTER_MACS[$i]}" == "$CURRENT" ]]; then
    printf "  > %d) %s  ← current\n" "$n" "${ADAPTER_MACS[$i]}"
    DEFAULT_IDX=$n
  else
    printf "    %d) %s\n" "$n" "${ADAPTER_MACS[$i]}"
  fi
done
echo ""
read -rp "Pick adapter [${DEFAULT_IDX}]: " CHOICE
CHOICE="${CHOICE:-$DEFAULT_IDX}"

if ! [[ "$CHOICE" =~ ^[0-9]+$ ]] || (( CHOICE < 1 || CHOICE > ${#ADAPTER_MACS[@]} )); then
  echo "Invalid choice." >&2
  exit 1
fi

ADAPTER_MAC="${ADAPTER_MACS[$((CHOICE - 1))]}"
echo "[BT] Using adapter: $ADAPTER_MAC"
echo ""

# ── 3. Find R10 by name ───────────────────────────────────────────────────────
R10_MAC=$(
  (
    echo "select $ADAPTER_MAC"
    echo "devices"
  ) | bluetoothctl 2>/dev/null \
    | grep "$R10_NAME" \
    | awk '{print $2}' \
    | head -1
)

if [[ -z "$R10_MAC" ]]; then
  echo "[BT] '$R10_NAME' not found in known devices — nothing to do."
  exit 0
fi

echo "[BT] Found '$R10_NAME' at $R10_MAC"

# ── 4. Disconnect and remove ──────────────────────────────────────────────────
bluetoothctl <<EOF
select $ADAPTER_MAC
disconnect $R10_MAC
remove $R10_MAC
EOF

echo "[BT] Done."
