#!/usr/bin/env bash
# setupR10.sh — Pair the Garmin Approach R10 on Linux via BlueZ.
#
# Prerequisites:
#   1. Put the R10 in PAIRING MODE first (hold power button → solid blue LED)
#   2. Run this script
#
# This handles: adapter selection, SC disable, scan, trust, pair, bond storage.
# Once paired, the bond persists across reboots — you only need to run this once.

set -uo pipefail

R10_NAME="Approach R10"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "=== Garmin R10 Pairing Setup ==="
echo ""
echo "IMPORTANT: Put the R10 in pairing mode FIRST!"
echo "  Hold the power button until the LED turns SOLID BLUE."
echo ""
read -rp "Is the R10 LED solid blue? [y/N]: " CONFIRM
if [[ "${CONFIRM,,}" != "y" ]]; then
  echo "Aborted. Put the R10 in pairing mode and try again."
  exit 1
fi

# ── 1. Discover available adapters ──────────────────────────────────────────
mapfile -t ADAPTER_LINES < <(bluetoothctl list 2>/dev/null | grep "^Controller")

if [[ ${#ADAPTER_LINES[@]} -eq 0 ]]; then
  echo "ERROR: No Bluetooth adapters found. Is bluetoothd running?" >&2
  exit 1
fi

ADAPTER_MACS=()
for line in "${ADAPTER_LINES[@]}"; do
  ADAPTER_MACS+=("$(awk '{print $2}' <<< "$line")")
done

# Read current adapter from settings.json
CURRENT=$(grep -o '"bluetoothAdapterAddress"[[:space:]]*:[[:space:]]*"[^"]*"' \
  "$SCRIPT_DIR/settings.json" 2>/dev/null | grep -o '[A-F0-9:]\{17\}' || true)

# ── 2. Adapter selection ────────────────────────────────────────────────────
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

# Persist the chosen adapter into settings.json
sed -i \
  "s|\"bluetoothAdapterAddress\"[[:space:]]*:[[:space:]]*\"[^\"]*\"|\"bluetoothAdapterAddress\": \"$ADAPTER_MAC\"|" \
  "$SCRIPT_DIR/settings.json"

# ── 3. Find hci index for adapter ──────────────────────────────────────────
HCI_IDX=""
for idx in $(ls -d /sys/class/bluetooth/hci* 2>/dev/null | grep -oP 'hci\K\d+'); do
  ADDR=$(cat /sys/class/bluetooth/hci${idx}/address 2>/dev/null | tr 'a-f' 'A-F')
  if [[ "$ADDR" == "$ADAPTER_MAC" ]]; then
    HCI_IDX=$idx
    break
  fi
done

if [[ -z "$HCI_IDX" ]]; then
  echo "ERROR: Could not find hci index for $ADAPTER_MAC" >&2
  exit 1
fi
echo "[BT] Adapter is hci${HCI_IDX}"

# ── 4. Disable Secure Connections (must power-cycle adapter) ────────────────
echo "[BT] Disabling Secure Connections on hci${HCI_IDX}..."
sudo systemctl stop bluetooth 2>/dev/null
sleep 1
sudo btmgmt --index "$HCI_IDX" power off 2>/dev/null
sudo btmgmt --index "$HCI_IDX" sc off 2>/dev/null
sudo btmgmt --index "$HCI_IDX" power on 2>/dev/null
sudo systemctl start bluetooth 2>/dev/null
sleep 2
echo "[BT] SC disabled, bluetooth restarted"

# ── 5. Remove old bond if present ──────────────────────────────────────────
bluetoothctl remove DD:C1:97:75:9A:F6 2>/dev/null
echo ""

# ── 6. Scan for R10 ────────────────────────────────────────────────────────
echo "[BT] Scanning for '$R10_NAME'..."

rm -f /tmp/bt_setup_fifo
mkfifo /tmp/bt_setup_fifo

(bluetoothctl --agent NoInputNoOutput < /tmp/bt_setup_fifo 2>&1 | tee /tmp/bt_setup.log) &
BT_PID=$!
sleep 1

exec 3>/tmp/bt_setup_fifo
echo "select $ADAPTER_MAC" >&3
sleep 0.5
echo "default-agent" >&3
sleep 0.5
echo "scan on" >&3

R10_MAC=""
for i in $(seq 1 30); do
  sleep 1
  if grep -q "DD:C1:97:75:9A:F6" /tmp/bt_setup.log 2>/dev/null; then
    R10_MAC="DD:C1:97:75:9A:F6"
    echo "[BT] Found R10 at $R10_MAC"
    break
  fi
  # Also check by name in case MAC changed
  MAC_BY_NAME=$(grep "$R10_NAME" /tmp/bt_setup.log 2>/dev/null | grep -oP '[0-9A-F]{2}(:[0-9A-F]{2}){5}' | head -1)
  if [[ -n "$MAC_BY_NAME" ]]; then
    R10_MAC="$MAC_BY_NAME"
    echo "[BT] Found '$R10_NAME' at $R10_MAC"
    break
  fi
  printf "\r  Scanning... (%ds)" "$i"
done
echo ""

if [[ -z "$R10_MAC" ]]; then
  echo "ERROR: R10 not found. Is it in pairing mode (solid blue LED)?" >&2
  echo "scan off" >&3
  echo "quit" >&3
  exec 3>&-
  wait $BT_PID 2>/dev/null
  rm -f /tmp/bt_setup_fifo /tmp/bt_setup.log
  exit 1
fi

# ── 7. Trust and Pair ──────────────────────────────────────────────────────
echo "[BT] Trusting and pairing..."
echo "scan off" >&3
sleep 0.5
echo "trust $R10_MAC" >&3
sleep 1
echo "pair $R10_MAC" >&3

# Wait for pairing result
PAIRED=false
for i in $(seq 1 15); do
  sleep 1
  if grep -q "Paired: yes\|Bonded: yes" /tmp/bt_setup.log 2>/dev/null; then
    PAIRED=true
    break
  fi
  if grep -q "Failed\|error\|AuthenticationCanceled" /tmp/bt_setup.log 2>/dev/null; then
    break
  fi
  printf "\r  Pairing... (%ds)" "$i"
done
echo ""

echo "quit" >&3
exec 3>&-
wait $BT_PID 2>/dev/null
rm -f /tmp/bt_setup_fifo

if $PAIRED; then
  echo ""
  echo "=== SUCCESS ==="
  echo "R10 is paired and bonded!"
  echo ""
  echo "Update settings.json if the MAC address changed:"
  echo "  Current R10 MAC: $R10_MAC"
  echo ""
  echo "You can now run the app with: dotnet run"

  # Update MAC in settings.json if needed
  sed -i \
    "s|\"bluetoothDeviceAddressTemp\"[[:space:]]*:[[:space:]]*\"[^\"]*\"|\"bluetoothDeviceAddressTemp\": \"$R10_MAC\"|" \
    "$SCRIPT_DIR/settings.json"
else
  echo ""
  echo "=== PAIRING FAILED ==="
  echo "Make sure:"
  echo "  1. The R10 LED is SOLID BLUE (pairing mode)"
  echo "  2. No other device (phone/tablet) is connected to the R10"
  echo "  3. Try power-cycling the R10 and putting it back in pairing mode"
  echo ""
  echo "Log: /tmp/bt_setup.log"
  exit 1
fi

rm -f /tmp/bt_setup.log
