#!/usr/bin/env bash
# connect.sh — Pair the Garmin Approach R10 via BlueZ.
# Picks a Bluetooth adapter, finds the R10 by name, pairs on first run.

set -uo pipefail

R10_NAME="Approach R10"
DOTNET="${DOTNET_ROOT:-/home/jeremiah/dotnet}/dotnet"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "=== GSP R10 Adapter ==="
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

# Read current adapter from settings.json to mark the default
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

# Persist the chosen adapter into settings.json so the app uses it
sed -i \
  "s|\"bluetoothAdapterAddress\"[[:space:]]*:[[:space:]]*\"[^\"]*\"|\"bluetoothAdapterAddress\": \"$ADAPTER_MAC\"|" \
  "$SCRIPT_DIR/settings.json"

echo ""

# ── 3. Disable Secure Connections & power on ─────────────────────────────────
# BlueZ's main.conf SecureConnections=off is unreliable — must use btmgmt while
# adapter is powered off. Without this, auto-pairing sends the SC flag which the
# BT 4.0 USB adapter can't fulfill, causing the R10 to disconnect.
echo "[BT] Configuring adapter (SC off, power on)..."

# Find the hci index for the chosen adapter MAC
HCI_IDX=""
for idx in $(ls -d /sys/class/bluetooth/hci* 2>/dev/null | grep -oP 'hci\K\d+'); do
  ADDR=$(cat /sys/class/bluetooth/hci${idx}/address 2>/dev/null | tr 'a-f' 'A-F')
  if [[ "$ADDR" == "$ADAPTER_MAC" ]]; then
    HCI_IDX=$idx
    break
  fi
done

if [[ -n "$HCI_IDX" ]]; then
  echo "[BT] Adapter is hci${HCI_IDX}"
  # Must stop bluetoothd, power off, set SC off, power on, restart bluetoothd
  sudo systemctl stop bluetooth 2>/dev/null
  sleep 1
  sudo btmgmt --index "$HCI_IDX" power off 2>/dev/null
  sudo btmgmt --index "$HCI_IDX" sc off 2>/dev/null
  sudo btmgmt --index "$HCI_IDX" power on 2>/dev/null
  sudo systemctl start bluetooth 2>/dev/null
  sleep 2
  echo "[BT] SC disabled on hci${HCI_IDX}"
else
  echo "[BT] WARNING: Could not find hci index for $ADAPTER_MAC — skipping SC disable"
  bluetoothctl <<EOF >/dev/null 2>&1
select $ADAPTER_MAC
power on
EOF
fi

# ── 4. Find R10 by name ───────────────────────────────────────────────────────
# Look for "Approach R10" in the devices already known to this adapter.
find_r10_mac() {
  (
    echo "select $ADAPTER_MAC"
    echo "devices"
  ) | bluetoothctl 2>/dev/null \
    | grep "$R10_NAME" \
    | awk '{print $2}' \
    | head -1
}

R10_MAC=$(find_r10_mac)

if [[ -z "$R10_MAC" ]]; then
  echo "[BT] '$R10_NAME' not in known devices. Running 15s scan..."
  (
    echo "select $ADAPTER_MAC"
    echo "agent on"
    echo "default-agent"
    echo "scan on"
    sleep 15
    echo "scan off"
  ) | bluetoothctl

  R10_MAC=$(find_r10_mac)

  if [[ -z "$R10_MAC" ]]; then
    echo "ERROR: '$R10_NAME' not found. Make sure the device is powered on." >&2
    exit 1
  fi
fi

echo "[BT] Found '$R10_NAME' at $R10_MAC"

# ── 5. Pair if needed ─────────────────────────────────────────────────────────
if (
  echo "select $ADAPTER_MAC"
  echo "info $R10_MAC"
) | bluetoothctl 2>/dev/null | grep -q "Paired: yes"; then
  echo "[BT] Already paired — done."
else
  echo "[BT] Pairing..."
  bluetoothctl <<EOF
select $ADAPTER_MAC
pair $R10_MAC
trust $R10_MAC
EOF
  echo "[BT] Pairing done."
fi
