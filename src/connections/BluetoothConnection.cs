using gspro_r10.OpenConnect;
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using LaunchMonitor.Proto;
using gspro_r10.bluetooth;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Collections.Generic;
using System.Linq;

namespace gspro_r10
{
  public class BluetoothConnection : IDisposable
  {
    private static readonly double METERS_PER_S_TO_MILES_PER_HOUR = 2.2369;
    private static readonly float FEET_TO_METERS = 1 / 3.281f;
    private bool disposedValue;

    public ConnectionManager ConnectionManager { get; }
    public IConfigurationSection Configuration { get; }
    public int ReconnectInterval { get; }
    public LaunchMonitorDevice? LaunchMonitor { get; private set; }
    public Device? Device { get; private set; }

    public BluetoothConnection(ConnectionManager connectionManager, IConfigurationSection configuration)
    {
      ConnectionManager = connectionManager;
      Configuration = configuration;
      ReconnectInterval = int.Parse(configuration["reconnectInterval"] ?? "5");
      Task.Run(ConnectToDevice);

    }

    private async Task ConnectToDevice()
    {
      try
      {
        string deviceName = Configuration["bluetoothDeviceName"] ?? "Approach R10";
        string? deviceAddress = Configuration["bluetoothDeviceAddress"];
        Device = await FindDeviceAsync(deviceName, deviceAddress);
        if (Device == null)
        {
          BluetoothLogger.Error("Device must be paired through computer bluetooth settings before running");
          if (!string.IsNullOrWhiteSpace(deviceAddress))
            BluetoothLogger.Error($"If device is paired, make sure 'bluetoothDeviceAddress' in settings.json matches the device MAC exactly (e.g. 'DD:C1:97:75:9A:F6')");
          else
            BluetoothLogger.Error($"If device is paired, make sure 'bluetoothDeviceName' in settings.json matches exactly (currently: '{deviceName}')");
          return;
        }

        // Ensure device is paired before attempting connection — BlueZ requires explicit pairing
        // on Linux unlike Windows which handles it transparently on first GATT access.
        try
        {
          bool isPaired = await Task.Run(async () => await Device.GetPairedAsync());
          if (!isPaired)
          {
            BluetoothLogger.Info("Device not paired. Attempting to pair...");
            await Task.Run(async () => await Device.PairAsync());
            BluetoothLogger.Info("Pairing complete. Waiting 2s for BlueZ to stabilize...");
            await Task.Delay(2000);
          }
          else
          {
            BluetoothLogger.Info("Device is already paired");
          }
        }
        catch (Exception ex)
        {
          BluetoothLogger.Error($"Pairing attempt failed (will try to connect anyway): {ex.Message}");
        }

        int attemptNumber = 0;
        bool connected = false;
        do
        {
          attemptNumber++;
          BluetoothLogger.Info($"Connection attempt #{attemptNumber}");
          try
          {
            BluetoothLogger.Info($"Getting device name...");
            var dName = await Device.GetNameAsync();
            BluetoothLogger.Info($"Checking connected state...");
            // Check if already connected before calling ConnectAsync.
            // Calling ConnectAsync on an already-connected device leaks pending D-Bus calls
            // because WaitAsync abandons (but doesn't cancel) the underlying Task.
            bool isAlreadyConnected = await Task.Run(async () => await Device.GetConnectedAsync());
            if (!isAlreadyConnected)
            {
              BluetoothLogger.Info($"Connecting to {dName}: {Device.ObjectPath}");
              // Await directly without WaitAsync — BlueZ's own timeout (~30s) will cause
              // ConnectAsync to return an error if the connection fails, so no need to abandon the task.
              await Task.Run(async () => await Device.ConnectAsync());
              BluetoothLogger.Info($"ConnectAsync returned, polling ServicesResolved...");
            }
            else
            {
              BluetoothLogger.Info($"Already connected to {dName}: {Device.ObjectPath}");
            }

            // Poll for ServicesResolved rather than using WaitForPropertyValueAsync (which sets up
            // a D-Bus signal subscription that deadlocks subsequent GetManagedObjects calls in Tmds.DBus)
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
              bool resolved = await Task.Run(async () => await Device.GetServicesResolvedAsync());
              if (resolved) break;
              await Task.Delay(500);
            }
            BluetoothLogger.Info($"Verifying connected state...");
            await Task.Delay(2000); // let D-Bus object registration settle
            connected = await Task.Run(async () => await Device.GetConnectedAsync());
          }
          catch (Exception ex)
          {
            BluetoothLogger.Error($"Connection attempt failed: {ex.Message}");
            connected = false;
          }

          if (!connected)
          {
            // Force GC to reclaim any D-Bus/Tmds.DBus objects from the failed attempt
            GC.Collect(2, GCCollectionMode.Aggressive);
            GC.WaitForPendingFinalizers();
            BluetoothLogger.Info($"Could not connect to bluetooth device. Waiting {ReconnectInterval} seconds before trying again");
            await Task.Delay(TimeSpan.FromSeconds(ReconnectInterval));
          }
        }
        while (!connected);

        BluetoothLogger.Info($"Connected to Launch Monitor");
        LaunchMonitor = SetupLaunchMonitor(Device);
        Device.Disconnected += OnDeviceDisconnected;
      }
      catch (Exception e)
      {
        BluetoothLogger.Error($"Bluetooth connection error: {e}");
      }
    }

    private Task OnDeviceDisconnected(Device sender, BlueZEventArgs args)
    {
      BluetoothLogger.Error("Lost bluetooth connection");
      if (Device != null)
        Device.Disconnected -= OnDeviceDisconnected;
      LaunchMonitor?.Dispose();
      _ = Task.Run(ConnectToDevice);
      return Task.CompletedTask;
    }

    private LaunchMonitorDevice? SetupLaunchMonitor(Device device)
    {
      LaunchMonitorDevice lm = new LaunchMonitorDevice(device);
      lm.AutoWake = bool.Parse(Configuration["autoWake"] ?? "false");
      lm.CalibrateTiltOnConnect = bool.Parse(Configuration["calibrateTiltOnConnect"] ?? "false");

      lm.DebugLogging = bool.Parse(Configuration["debugLogging"] ?? "false");

      lm.MessageRecieved += (o, e) => BluetoothLogger.Incoming(e.Message?.ToString() ?? string.Empty);
      lm.MessageSent += (o, e) => BluetoothLogger.Outgoing(e.Message?.ToString() ?? string.Empty);
      lm.BatteryLifeUpdated += (o, e) => BluetoothLogger.Info($"Battery Life Updated: {e.Battery}%");
      lm.Error += (o, e) => BluetoothLogger.Error($"{e.Severity}: {e.Message}");

      if (bool.Parse(Configuration["sendStatusChangesToGSP"] ?? "false"))
      {
        lm.ReadinessChanged += (o, e) =>
        {
          ConnectionManager.SendLaunchMonitorReadyUpdate(e.Ready);
        };
      }

      lm.ShotMetrics += (o, e) =>
      {
        LogMetrics(e.Metrics);
        ConnectionManager.SendShot(
          BallDataFromLaunchMonitorMetrics(e.Metrics?.BallMetrics),
          ClubDataFromLaunchMonitorMetrics(e.Metrics?.ClubMetrics)
        );
      };

      if (!lm.Setup())
      {
        BluetoothLogger.Error("Failed Device Setup");
        return null;
      }

      float temperature = float.Parse(Configuration["temperature"] ?? "60");
      float humidity = float.Parse(Configuration["humidity"] ?? "1");
      float altitude = float.Parse(Configuration["altitude"] ?? "0");
      float airDensity = float.Parse(Configuration["airDensity"] ?? "1");
      float teeDistanceInFeet = float.Parse(Configuration["teeDistanceInFeet"] ?? "7");
      float teeRange = teeDistanceInFeet * FEET_TO_METERS;

      lm.ShotConfig(temperature, humidity, altitude, airDensity, teeRange);

      BluetoothLogger.Info($"Device Setup Complete: ");
      BluetoothLogger.Info($"   Model: {lm.Model}");
      BluetoothLogger.Info($"   Firmware: {lm.Firmware}");
      BluetoothLogger.Info($"   Bluetooth ID: {lm.Device.ObjectPath}");
      BluetoothLogger.Info($"   Battery: {lm.Battery}%");
      BluetoothLogger.Info($"   Current State: {lm.CurrentState}");
      BluetoothLogger.Info($"   Tilt: {lm.DeviceTilt}");

      return lm;
    }

    private async Task<Device?> FindDeviceAsync(string deviceName, string? deviceAddress)
    {
      var adapters = await BlueZManager.GetAdaptersAsync();
      if (adapters.Count == 0)
      {
        BluetoothLogger.Error("No bluetooth adapters found");
        return null;
      }

      // Allow selecting a specific adapter by MAC address (e.g. a USB dongle that avoids WiFi coexistence)
      string? adapterAddress = Configuration["bluetoothAdapterAddress"];
      Adapter adapter;
      if (!string.IsNullOrWhiteSpace(adapterAddress))
      {
        Adapter? matched = null;
        foreach (var a in adapters)
        {
          try
          {
            string addr = await a.GetAddressAsync();
            if (string.Equals(addr, adapterAddress, StringComparison.OrdinalIgnoreCase))
            { matched = a; break; }
          }
          catch { }
        }
        if (matched == null)
        {
          BluetoothLogger.Error($"Adapter with address '{adapterAddress}' not found. Available adapters:");
          foreach (var a in adapters)
          {
            try { BluetoothLogger.Error($"  {await a.GetAddressAsync()}"); } catch { }
          }
          return null;
        }
        adapter = matched;
        BluetoothLogger.Info($"Using adapter {adapterAddress}");
      }
      else
      {
        adapter = adapters[0];
      }

      bool useAddress = !string.IsNullOrWhiteSpace(deviceAddress);

      // First pass: check already-known devices (no scan needed)
      Device? found = await SearchKnownDevicesAsync(adapter, deviceName, deviceAddress, useAddress);
      if (found != null) return found;

      // Second pass: run a discovery scan so the device can be found even if not previously paired
      BluetoothLogger.Info("Device not in known list. Starting 15s BLE discovery scan...");
      try { await adapter.StartDiscoveryAsync(); } catch (Exception ex) { BluetoothLogger.Error($"StartDiscovery failed: {ex.Message}"); }
      await Task.Delay(TimeSpan.FromSeconds(15));
      try { await adapter.StopDiscoveryAsync(); } catch { }

      found = await SearchKnownDevicesAsync(adapter, deviceName, deviceAddress, useAddress);

      if (found == null)
      {
        if (useAddress)
          BluetoothLogger.Error($"Could not find device with address '{deviceAddress}' after discovery scan. Make sure the device is powered on.");
        else
          BluetoothLogger.Error($"Could not find '{deviceName}' after discovery scan. Make sure the device is powered on.");
      }

      return found;
    }

    private async Task<Device?> SearchKnownDevicesAsync(Adapter adapter, string deviceName, string? deviceAddress, bool useAddress)
    {
      var devices = await adapter.GetDevicesAsync();
      if (devices.Count == 0) return null;

      var deviceDescriptions = new List<string>();
      Device? found = null;

      foreach (var device in devices)
      {
        try
        {
          string lastSegment = device.ObjectPath.ToString().Split('/').Last();
          // BlueZ path format: /org/bluez/hci0/dev_DD_C1_97_75_9A_F6
          string address = lastSegment.StartsWith("dev_")
            ? lastSegment.Substring(4).Replace('_', ':')
            : lastSegment.Replace('_', ':');
          string? name = await device.GetNameAsync();
          deviceDescriptions.Add($"'{name ?? "(unknown)"}' ({address})");

          if (useAddress)
          {
            if (string.Equals(address, deviceAddress, StringComparison.OrdinalIgnoreCase))
              found = device;
          }
          else
          {
            if (name == deviceName)
              found = device;
          }
        }
        catch
        {
          deviceDescriptions.Add("(unknown)");
        }
      }

      BluetoothLogger.Info($"Found {devices.Count} bluetooth device(s): {string.Join(", ", deviceDescriptions)}");
      return found;
    }

    public static BallData? BallDataFromLaunchMonitorMetrics(BallMetrics? ballMetrics)
    {
      if (ballMetrics == null) return null;
      return new BallData()
      {
        HLA = ballMetrics.LaunchDirection,
        VLA = ballMetrics.LaunchAngle,
        Speed = ballMetrics.BallSpeed * METERS_PER_S_TO_MILES_PER_HOUR,
        SpinAxis = ballMetrics.SpinAxis * -1,
        TotalSpin = ballMetrics.TotalSpin,
        SideSpin = ballMetrics.TotalSpin * Math.Sin(-1 * ballMetrics.SpinAxis * Math.PI / 180),
        BackSpin = ballMetrics.TotalSpin * Math.Cos(-1 * ballMetrics.SpinAxis * Math.PI / 180)
      };
    }

    public static ClubData? ClubDataFromLaunchMonitorMetrics(ClubMetrics? clubMetrics)
    {
      if (clubMetrics == null) return null;
      return new ClubData()
      {
        Speed = clubMetrics.ClubHeadSpeed * METERS_PER_S_TO_MILES_PER_HOUR,
        SpeedAtImpact = clubMetrics.ClubHeadSpeed * METERS_PER_S_TO_MILES_PER_HOUR,
        AngleOfAttack = clubMetrics.AttackAngle,
        FaceToTarget = clubMetrics.ClubAngleFace,
        Path = clubMetrics.ClubAnglePath
      };
    }

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        if (disposing)
        {
          if (Device != null)
            Device.Disconnected -= OnDeviceDisconnected;
          LaunchMonitor?.Dispose();
        }

        disposedValue = true;
      }
    }

    public void LogMetrics(Metrics? metrics)
    {
      if (metrics == null)
      {
        return;
      }
      try
      {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"===== Shot {metrics.ShotId} =====");
        sb.AppendLine($"{"Ball Metrics",-40}│ {"Club Metrics",-40}│ {"Swing Metrics",-40}");
        sb.AppendLine($"{new string('─', 40)}┼─{new string('─', 40)}┼─{new string('─', 40)}");
        sb.Append($" {"BallSpeed:",-15} {metrics.BallMetrics?.BallSpeed * METERS_PER_S_TO_MILES_PER_HOUR,-22} │");
        sb.Append($" {"Club Speed:",-20} {metrics.ClubMetrics?.ClubHeadSpeed * METERS_PER_S_TO_MILES_PER_HOUR,-18} │");
        sb.AppendLine($" {"Backswing Start:",-20} {metrics.SwingMetrics?.BackSwingStartTime,-17}");

        sb.Append($" {"VLA:",-15} {metrics.BallMetrics?.LaunchAngle,-22} │");
        sb.Append($" {"Club Path:",-20} {metrics.ClubMetrics?.ClubAnglePath,-18} │");
        sb.AppendLine($" {"Downswing Start:",-20} {metrics.SwingMetrics?.DownSwingStartTime,-17}");

        sb.Append($" {"HLA:",-15} {metrics.BallMetrics?.LaunchDirection,-22} │");
        sb.Append($" {"Club Face:",-20} {metrics.ClubMetrics?.ClubAngleFace,-18} │");
        sb.AppendLine($" {"Impact time:",-20} {metrics.SwingMetrics?.ImpactTime,-17}");

        uint? backswingDuration = metrics.SwingMetrics?.DownSwingStartTime - metrics.SwingMetrics?.BackSwingStartTime;
        sb.Append($" {"Spin Axis:",-15} {metrics.BallMetrics?.SpinAxis * -1,-22} │");
        sb.Append($" {"Attack Angle:",-20} {metrics.ClubMetrics?.AttackAngle,-18} │");
        sb.AppendLine($" {"Backswing duration:",-20} {backswingDuration,-17}");

        uint? downswingDuration = metrics.SwingMetrics?.ImpactTime - metrics.SwingMetrics?.DownSwingStartTime;
        sb.Append($" {"Total Spin:",-15} {metrics.BallMetrics?.TotalSpin,-22} │");
        sb.Append($" {"",-20} {"",-18} │");
        sb.AppendLine($" {"Downswing duration:",-20} {downswingDuration,-17}");

        sb.Append($" {"Ball Type:",-15} {metrics.BallMetrics?.GolfBallType,-22} │");
        sb.Append($" {"",-20} {"",-18} │");
        sb.AppendLine($" {"Tempo:",-20} {(float)(backswingDuration ?? 0) / downswingDuration,-17}");

        sb.Append($" {"Spin Calc:",-15} {metrics.BallMetrics?.SpinCalculationType,-22} │");
        sb.Append($" {"",-20} {"",-18} │");
        sb.AppendLine($" {"Normal/Practice:",-20} {metrics.ShotType,-17}");
        BluetoothLogger.Info(sb.ToString());

      }
      catch (Exception e)
      {
        Console.WriteLine(e);
      }

    }

    public void Dispose()
    {
      Dispose(disposing: true);
      GC.SuppressFinalize(this);
    }
  }

  public static class BluetoothLogger
  {
    public static void Info(string message) => LogBluetoothMessage(message, LogMessageType.Informational);
    public static void Error(string message) => LogBluetoothMessage(message, LogMessageType.Error);
    public static void Outgoing(string message) => LogBluetoothMessage(message, LogMessageType.Outgoing);
    public static void Incoming(string message) => LogBluetoothMessage(message, LogMessageType.Incoming);
    public static void LogBluetoothMessage(string message, LogMessageType type) => BaseLogger.LogMessage(message, "R10-BT", type, ConsoleColor.Magenta);
  }
}
