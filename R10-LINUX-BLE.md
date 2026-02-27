# Garmin Approach R10 — Linux BLE Reference

This document captures everything learned while porting the GSP-R10 adapter from Windows (InTheHand.BluetoothLE) to Linux (BlueZ / Linux.Bluetooth) on a Raspberry Pi 4. It is intended as a reference for future development and AI-assisted sessions.

## Device Overview

- **Product**: Garmin Approach R10 golf launch monitor
- **Communication**: Bluetooth Low Energy (BLE) only — no Classic BT
- **BLE Address Type**: Static random (`DD:C1:97:75:9A:F6` in our case)
- **LE Features**: `0x01` — supports LE Encryption only (no Secure Connections)
- **Firmware tested**: 4.30
- **Protocol**: Custom GATT service using COBS-encoded, CRC16-validated, Protobuf messages
- **Advertising name**: Usually "Approach R10", sometimes "Approach R1" (firmware-dependent)

## GATT Service Map

| Service UUID | Name | Characteristics |
|---|---|---|
| `00001800` | Generic Access | Device Name, Appearance, Preferred Connection Parameters, Central Address Resolution |
| `00001801` | Generic Attribute | Service Changed (indicate) |
| `0000180a` | Device Information | Manufacturer, Model (`2a24`), Serial (`2a25`), Firmware (`2a28`) |
| `0000180f` | Battery Service | Battery Level (`2a19`, notify) |
| `6a4e2800-667b-11e3-949a-0800200c9a66` | Device Interface (Garmin proprietary) | See below |
| `6a4e3400-667b-11e3-949a-0800200c9a66` | Measurement (Garmin proprietary) | See below |

### Device Interface Service (`6a4e2800`)

This is the primary control channel for the R10 protocol.

| Characteristic UUID | Role | Properties | Auth Required |
|---|---|---|---|
| `6a4e2812` | **Notifier** — incoming data from R10 | read, write-without-response, write, notify | **YES** (CCCD requires authenticated/bonded link) |
| `6a4e2822` | **Writer** — outgoing commands to R10 | write-without-response, write | No |
| `6a4e2803` | Unknown | read, write | No |
| `6a4e2810` | Unknown | read, write, notify | No |
| `6a4e2811` | Unknown | read, write, notify | No |
| `6a4e2820` | Unknown | read, write | No |
| `6a4e2821` | Unknown | read, write | No |

### Measurement Service (`6a4e3400`)

| Characteristic UUID | Role | Properties |
|---|---|---|
| `6a4e3401` | Measurement data | notify |
| `6a4e3402` | Control point | read, write, notify |
| `6a4e3403` | Status | read, write, notify |

## BLE Security Model

### The Authentication Requirement

The device interface notifier characteristic (`6a4e2812`) has a CCCD (Client Characteristic Configuration Descriptor, UUID `2902`) at ATT handle `0x001d` that **requires an authenticated (bonded) BLE connection** to write. Without a bond, writing `0x0100` (enable notifications) returns:

```
ATT: Error Response
  Error: Insufficient Authentication (0x05)
```

All other characteristics (battery, measurement, control, status) work **without** authentication. Only the device interface notifier — the channel needed for the handshake and all protobuf communication — requires it.

### Pairing Behavior

The R10 has strict rules about when it accepts SMP (Security Manager Protocol) pairing:

- **Normal mode** (blinking blue LED): **Rejects ALL SMP pairing requests** by immediately disconnecting, regardless of IO capability, MITM, or SC flags
- **Pairing mode** (solid blue LED): **Accepts Just Works pairing** (NoInputNoOutput, no MITM, Legacy)

To enter pairing mode: with the R10 powered on, hold the power button. The LED sequence is: green → blue → off → green → **solid blue**. Solid blue = pairing mode.

### SMP Parameters That Work

```
IO capability: NoInputNoOutput (0x03)
Authentication requirement: Bonding, No MITM, Legacy, No Keypresses (0x01)
Max encryption key size: 16
```

### SMP Parameters That Fail

Any request with MITM or SC flags causes the R10 to disconnect, even in pairing mode on a BT 4.0 adapter:

- `0x05` (Bonding + MITM) — rejected
- `0x2d` (Bonding + MITM + SC + CT2) — rejected
- `0x09` (Bonding + SC) — rejected (adapter doesn't support SC)

## BlueZ Configuration Requirements

### Secure Connections Must Be Disabled

The `SecureConnections = off` setting in `/etc/bluetooth/main.conf` is **unreliable** — it silently fails when the adapter is already powered. The only reliable method is `btmgmt` with a power cycle:

```bash
sudo systemctl stop bluetooth
sudo btmgmt --index $HCI_IDX power off
sudo btmgmt --index $HCI_IDX sc off
sudo btmgmt --index $HCI_IDX power on
sudo systemctl start bluetooth
```

Verify by checking that `secure-conn` does NOT appear in the settings output from `btmgmt power off` / `btmgmt power on`.

### IO Capability Must Be NoInputNoOutput

BlueZ's default agent uses `DisplayYesNo` IO capability, which sets the MITM flag in SMP pairing requests. The R10 rejects MITM. Two ways to fix this:

1. **D-Bus agent (programmatic)**: Register an `org.bluez.Agent1` object with `NoInputNoOutput` capability via `AgentManager1.RegisterAgentAsync`. The app does this in `BaseDevice.cs`.
2. **bluetoothctl**: Use `bluetoothctl --agent NoInputNoOutput` for manual pairing.

`btmgmt io-cap 3` does NOT work reliably — bluetoothd overrides it on restart.

## Pairing Procedure (One-Time)

Run `setupR10.sh` or manually:

```bash
# 1. Put R10 in pairing mode (hold power → solid blue LED)

# 2. Disable SC on adapter
sudo systemctl stop bluetooth
sudo btmgmt --index 1 power off
sudo btmgmt --index 1 sc off
sudo btmgmt --index 1 power on
sudo systemctl start bluetooth

# 3. Pair with NoInputNoOutput agent
bluetoothctl --agent NoInputNoOutput
  > scan on
  # Wait for "Approach R10" / DD:C1:97:75:9A:F6
  > scan off
  > trust DD:C1:97:75:9A:F6
  > pair DD:C1:97:75:9A:F6
  # Should see: Bonded: yes, Paired: yes
```

The bond (LTK) is stored at `/var/lib/bluetooth/<adapter-mac>/<device-mac>/info` and persists across reboots.

## Linux.Bluetooth / BlueZ Workarounds

### GetManagedObjectsAsync Deadlock

`Linux.Bluetooth` (v5.67.1) uses `Tmds.DBus` internally. After calling `ConnectAsync()` on a BLE device, any call to `GetManagedObjectsAsync()` (used by `GetServicesAsync`, `GetCharacteristicAsync`, etc.) **permanently deadlocks** on the Pi.

**Workaround**: Discover GATT services/characteristics via `busctl tree org.bluez` subprocess, then create `GattCharacteristic` objects using reflection:

```csharp
// 1. Get GATT paths via busctl
string treeOutput = RunProcess("busctl", "tree org.bluez");
// Parse paths like /org/bluez/hci1/dev_XX/serviceXXXX/charXXXX

// 2. Read UUIDs via busctl
string uuid = RunProcess("busctl",
    $"get-property org.bluez {charPath} org.bluez.GattCharacteristic1 UUID");

// 3. Create GattCharacteristic via fresh D-Bus connection + reflection
var conn = new Connection(Address.System);
await conn.ConnectAsync();
var proxy = conn.CreateProxy<IGattCharacteristic1>("org.bluez", new ObjectPath(path));
// Use internal CreateAsync(IGattCharacteristic1) via reflection
```

### StartNotify Hang on Unauthenticated Characteristics

When calling `StartNotify` on a characteristic that requires authentication, BlueZ writes to the CCCD, gets `Insufficient Authentication`, auto-initiates SMP pairing, and if pairing fails (R10 not in pairing mode), `StartNotify` hangs until the device disconnects.

**Workaround**: Ensure the device is already bonded before the app runs. The `SetupDeviceInterfaceNotifier()` method in `BaseDevice.cs` has a 30-second timeout on `StartNotify` and logs the result.

### BlueZ Auto-Subscribes During Service Resolution

BlueZ caches GATT data in `/var/lib/bluetooth/<adapter>/cache/<device>`. On reconnection, bluetoothd may auto-write to CCCDs to restore previous subscriptions — this happens during service resolution, before the app's code runs. If the device isn't bonded, this triggers `Insufficient Authentication` → auto-pairing → disconnect.

Clearing the cache (`sudo rm /var/lib/bluetooth/<adapter>/cache/<device>`) prevents this, but the real fix is ensuring the bond exists.

## Protocol Quick Reference

### Handshake

```
Central → R10:  00 00 00 00 00 00 00 00 00 01 00 00
R10 → Central:  00 01 00 00 00 00 00 00 00 00 01 00 00 01 00 00
Central → R10:  00
```

Byte 12 of the R10's response is the **header byte** used to prefix all subsequent BLE writes.

### Message Framing

1. Raw protobuf message
2. Prepend 2-byte length (includes length + CRC fields)
3. Append 2-byte CRC16
4. COBS encode
5. Prepend `0x00`, append `0x00` (frame delimiters)
6. Split into 19-byte BLE chunks, each prefixed with the header byte

### Key Protobuf Commands

| Direction | Message | Purpose |
|---|---|---|
| → | `WakeUpRequest` | Wake device from standby |
| ← | `WakeUpResponse` | status: SUCCESS |
| → | `StatusRequest` | Get current state |
| ← | `StatusResponse` | state: WAITING / STANDBY / ERROR |
| → | `TiltRequest` | Get device tilt |
| ← | `TiltResponse` | roll, pitch values |
| → | `SubscribeRequest` (LAUNCH_MONITOR) | Subscribe to shot/state alerts |
| → | `StartTiltCalibrationRequest` | Calibrate tilt sensor |
| → | `ShotConfigRequest` | Set temperature, humidity, altitude, air density, tee range |
| ← | `AlertNotification` (async) | Shot metrics, state changes, errors, tilt calibration results |

## Hardware Notes (Raspberry Pi 4)

- **hci0** (built-in, UART): BT 5.0, Cypress — supports SC but has WiFi coexistence issues (2.4GHz interference)
- **hci1** (USB dongle): BT 4.0, Broadcom — no SC support, but reliable for BLE
- The USB BT 4.0 dongle works fine for the R10 since the R10 itself only supports Legacy LE pairing
- WiFi coexistence on hci0 can prevent BLE scanning from finding the R10; use a USB adapter to avoid this

## Debugging Tools

- **btmon**: Essential for diagnosing BLE issues. `sudo btmon -w /tmp/capture.log` captures HCI traffic.
- **busctl**: `busctl tree org.bluez` shows the full D-Bus object tree including GATT services/characteristics.
- **busctl introspect**: `busctl introspect org.bluez /org/bluez/hci1/dev_XX/serviceXX/charXX` shows properties and methods.
- **busctl get-property**: Read individual properties like `Notifying`, `Connected`, `Paired`.
