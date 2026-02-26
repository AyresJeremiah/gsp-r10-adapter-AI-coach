# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**GSP-R10 Adapter** is a Windows-only C# (.NET 7.0) bridge utility connecting the Garmin Approach R10 launch monitor to the GSPro golf simulator. It supports three integration modes:

1. **Direct Bluetooth** — BLE connection to R10 device
2. **E6 Server** — TCP server implementing E6 Connect protocol
3. **Putting Camera** — HTTP server for webcam-based putting data

All three modes funnel shot data to GSPro via the OpenConnect TCP protocol.

## Commands

```bash
# Build
dotnet build

# Run (requires settings.json in working directory)
dotnet run

# Test
dotnet test --verbosity normal

# Publish self-contained release (generates zip files in publish/)
./build/publish-win64-selfcontained.sh

# Publish runtime-dependent release
./build/publish-win64-dotnet.sh
```

Version is set via `<VersionPrefix>` in the `.csproj` file.

## Architecture

### Data Flow

```
R10 (Bluetooth)  →  BluetoothConnection → LaunchMonitorDevice → Protobuf decode ─┐
R10 (E6/TCP)     →  R10Connection (TCP server) → R10Session                       ├→ ConnectionManager → OpenConnectConnection → GSPro (127.0.0.1:921)
Putting Camera   →  PuttingConnection (HTTP server)                               ─┘
```

### Key Components

**`ConnectionManager.cs`** — Central orchestrator. Receives shot data from all three input sources, broadcasts `ClubChanged` events (e.g., to auto-start putting camera on putter selection), and coordinates lifecycle of all connections.

**`connections/`**
- `BluetoothConnection.cs` — BLE device discovery, GATT connection, environmental config (temp/humidity/altitude/tee distance)
- `OpenConnectConnection.cs` — TCP client to GSPro; sends heartbeats every 10s, handles club selection responses, auto-reconnects
- `R10Connection.cs` — TCP server implementing E6 Connect protocol (handshake, ping/pong, shot data)
- `PuttingConnection.cs` — HTTP server; manages external `ball_tracking.exe` process lifecycle

**`bluetooth/device/`**
- `LaunchMonitorDevice.cs` — R10-specific GATT protocol: device status, wake-up, shot capture, tilt calibration
- `BaseDevice.cs` — Low-level GATT with COBS encoding/decoding, CRC16 validation, queue-based multi-threaded I/O

**`api/`** — Data model definitions for each protocol:
- `OpenConnectApi.cs` — GSPro shot format (BallData, ClubData, Club enum with 27 types)
- `R10Api.cs` — E6 Connect protocol messages
- `PuttingApi.cs` — Putting camera data structures

**`util/`** — `Cobs.cs` (frame reliability encoding), `Crc16.cs`, `Bytes.cs` (hex conversion), `BaseLogger.cs` (color-coded console logging)

**`bluetooth/proto/LaunchMonitor.proto`** — Protobuf definitions for R10 BLE protocol.

### Shot Data Conversion

Three conversion functions in `ConnectionManager.cs` translate source-specific formats to OpenConnect:
- `BallDataFromLaunchMonitorMetrics()` — Bluetooth R10 → OpenConnect
- `BallDataFromR10BallData()` — E6 → OpenConnect
- `BallDataFromPuttingBallData()` — Putting camera → OpenConnect

Notable unit conversions: m/s × 2.2369 = mph; spin axis is negated from R10 value.

### Logging

Four color-coded loggers: Bluetooth (Magenta), OpenConnect/GSPro (Green), R10/E6 (Blue), Putting (Yellow). All are instances of `BaseLogger` with thread-safe output.

## Configuration (`settings.json`)

```json
{
  "openConnect": { "ip": "127.0.0.1", "port": 921 },
  "r10E6Server": { "enabled": true, "port": 2483 },
  "bluetooth": {
    "enabled": true,
    "bluetoothDeviceName": "Approach R10",
    "reconnectInterval": 10,
    "autoWake": true,
    "calibrateTiltOnConnect": true,
    "altitude": 0, "humidity": 0.5, "temperature": 60,
    "teeDistanceInFeet": 7
  },
  "putting": {
    "enabled": false, "port": 8888,
    "launchBallTracker": true, "onlyLaunchWhenPutting": true,
    "exePath": "./ball_tracking/ball_tracking.exe"
  }
}
```

## Key Dependencies

- `InTheHand.BluetoothLE` — Windows BLE/GATT
- `NetCoreServer` — TCP/HTTP server and client
- `Google.Protobuf` / `Grpc.Tools` — R10 BLE protocol serialization
- `Microsoft.Extensions.Configuration.Json` — Settings binding

## Platform Notes

- **Windows-only**: targets `net7.0-windows10.0.19041`; uses `User32.dll` P/Invoke for window management
- R10 must be pre-paired in Windows Bluetooth settings before connecting
- Putting integration requires external `ball_tracking.exe` from a separate Python project
- Shot deduplication via `ProcessedShotIDs` HashSet prevents duplicate submissions to GSPro
