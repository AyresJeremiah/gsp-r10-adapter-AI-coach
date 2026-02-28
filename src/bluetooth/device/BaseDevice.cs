using Google.Protobuf;
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using System.Linq;
using System.Text;
using LaunchMonitor.Proto;
using System.Threading.Tasks;
using System.Threading;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Tmds.DBus;

namespace gspro_r10.bluetooth
{
  // D-Bus interface for org.bluez.Agent1 — needed to register a NoInputNoOutput
  // pairing agent so that BlueZ uses Just Works pairing (no MITM flag).
  [DBusInterface("org.bluez.Agent1")]
  public interface IAgent1 : IDBusObject
  {
    Task ReleaseAsync();
    Task<string> RequestPinCodeAsync(ObjectPath device);
    Task DisplayPinCodeAsync(ObjectPath device, string pincode);
    Task<uint> RequestPasskeyAsync(ObjectPath device);
    Task DisplayPasskeyAsync(ObjectPath device, uint passkey, ushort entered);
    Task RequestConfirmationAsync(ObjectPath device, uint passkey);
    Task RequestAuthorizationAsync(ObjectPath device);
    Task AuthorizeServiceAsync(ObjectPath device, string uuid);
    Task CancelAsync();
  }

  // Minimal Just Works agent — accepts all pairing requests without user interaction.
  public class NoInputNoOutputAgent : IAgent1
  {
    public static readonly ObjectPath AgentPath = new ObjectPath("/gspro/agent");
    public ObjectPath ObjectPath => AgentPath;

    public Task ReleaseAsync() => Task.CompletedTask;
    public Task<string> RequestPinCodeAsync(ObjectPath device) => Task.FromResult("0000");
    public Task DisplayPinCodeAsync(ObjectPath device, string pincode) => Task.CompletedTask;
    public Task<uint> RequestPasskeyAsync(ObjectPath device) => Task.FromResult<uint>(0);
    public Task DisplayPasskeyAsync(ObjectPath device, uint passkey, ushort entered) => Task.CompletedTask;
    public Task RequestConfirmationAsync(ObjectPath device, uint passkey) => Task.CompletedTask;
    public Task RequestAuthorizationAsync(ObjectPath device) => Task.CompletedTask;
    public Task AuthorizeServiceAsync(ObjectPath device, string uuid) => Task.CompletedTask;
    public Task CancelAsync() => Task.CompletedTask;
  }
  public abstract class BaseDevice : IDisposable
  {
    internal static string BATTERY_SERVICE_UUID = "0000180f-0000-1000-8000-00805f9b34fb";
    internal static string BATTERY_CHARACTERISTIC_UUID = "00002a19-0000-1000-8000-00805f9b34fb";
    internal static string DEVICE_INFO_SERVICE_UUID = "0000180a-0000-1000-8000-00805f9b34fb";
    internal static string FIRMWARE_CHARACTERISTIC_UUID = "00002a28-0000-1000-8000-00805f9b34fb";
    internal static string MODEL_CHARACTERISTIC_UUID = "00002a24-0000-1000-8000-00805f9b34fb";
    internal static string SERIAL_NUMBER_CHARACTERISTIC_UUID = "00002a25-0000-1000-8000-00805f9b34fb";
    internal static string DEVICE_INTERFACE_SERVICE = "6a4e2800-667b-11e3-949a-0800200c9a66";
    internal static string DEVICE_INTERFACE_NOTIFIER = "6a4e2812-667b-11e3-949a-0800200c9a66";
    internal static string DEVICE_INTERFACE_WRITER = "6a4e2822-667b-11e3-949a-0800200c9a66";

    public Device Device { get; }
    public int Battery { get { return mBattery; }
      set {
        mBattery = value;
        BatteryLifeUpdated?.Invoke(this, new BatteryEventArgs() { Battery = value });
      }
    }

    public string? Model { get; private set; }
    public string? Firmware { get; private set; }
    public string? Serial { get; private set; }
    public event MessageEventHandler? MessageRecieved;
    public event MessageEventHandler? MessageSent;
    public delegate void MessageEventHandler(object sender, MessageEventArgs e);
    public class MessageEventArgs: EventArgs
    {
      public IMessage? Message { get; set; }
    }

    public event BatteryEventHandler? BatteryLifeUpdated;
    public delegate void BatteryEventHandler(object sender, BatteryEventArgs e);
    public class BatteryEventArgs: EventArgs
    {
      public int Battery { get; set; }
    }

    private EventWaitHandle mWriterSignal = new AutoResetEvent(false);
    private ConcurrentQueue<byte[]> mWriterQueue = new ConcurrentQueue<byte[]>();
    private EventWaitHandle mReaderSignal = new AutoResetEvent(false);
    private ConcurrentQueue<byte[]> mReaderQueue = new ConcurrentQueue<byte[]>();
    private EventWaitHandle mMsgProcessSignal = new AutoResetEvent(false);
    private ConcurrentQueue<byte[]> mMsgProcessQueue = new ConcurrentQueue<byte[]>();
    private ManualResetEventSlim mHandshakeCompleteResetEvent = new ManualResetEventSlim(false);
    private ManualResetEventSlim mProtoResponseResetEvent = new ManualResetEventSlim(false);
    private IMessage? mLastProtoReceived;
    private int mBattery;
    private bool mHandshakeComplete = false;
    private byte mHeader = 0x00;
    private int mProtoRequestCounter = 0;
    private readonly object mProtoRequestLock = new object();
    private CancellationTokenSource mCancellationToken;
    private Task mWriterTask;
    private Task mReaderTask;
    private Task mMsgProcessingTask;
    private GattCharacteristic? mGattWriter;
    private bool mDisposedValue;
    public bool DebugLogging { get; set; } = false;

    // Cached GATT discovery: serviceUUID → { charUUID → charPath }
    private Dictionary<string, Dictionary<string, string>>? mGattPaths;

    public BaseDevice(Device device)
    {
      Device = device;

      mCancellationToken = new CancellationTokenSource();
      mWriterTask = Task.Run(WriterThread, mCancellationToken.Token);
      mReaderTask = Task.Run(ReaderThread, mCancellationToken.Token);
      mMsgProcessingTask = Task.Run(MsgProcessingThread, mCancellationToken.Token);
    }

    // Linux.Bluetooth's GetServicesAsync and GetCharacteristicAsync both use
    // Tmds.DBus GetManagedObjects which permanently deadlocks after ConnectAsync
    // on the Pi. Bypass the library entirely: discover GATT paths via busctl
    // (which calls the same D-Bus method but on a fresh connection), then create
    // GattCharacteristic objects from the discovered paths.

    /// <summary>
    /// Discovers all GATT service and characteristic paths + UUIDs via busctl,
    /// bypassing the Linux.Bluetooth library's deadlocking GetManagedObjects.
    /// </summary>
    protected void DiscoverGattTree()
    {
      if (mGattPaths != null) return;

      string devicePath = Device.ObjectPath.ToString();
      if (DebugLogging)
        BaseLogger.LogDebug($"Discovering GATT tree via busctl for {devicePath}");

      string treeOutput = RunProcess("busctl", "tree org.bluez");

      // Extract all D-Bus object paths under our device
      var allPaths = treeOutput.Split('\n')
        .Select(line => {
          int idx = line.IndexOf('/');
          return idx >= 0 ? line.Substring(idx).Trim() : "";
        })
        .Where(p => p.StartsWith(devicePath + "/"))
        .ToList();

      mGattPaths = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

      // Find service paths (direct children like serviceXXXX)
      var servicePaths = allPaths
        .Where(p => IsDirectChild(devicePath, p, "service"))
        .ToList();

      foreach (var svcPath in servicePaths)
      {
        string svcUuid = ReadBusProperty(svcPath, "org.bluez.GattService1", "UUID");
        if (string.IsNullOrEmpty(svcUuid)) continue;

        var charMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Find characteristic paths under this service
        var charPaths = allPaths
          .Where(p => IsDirectChild(svcPath, p, "char"))
          .ToList();

        foreach (var charPath in charPaths)
        {
          string charUuid = ReadBusProperty(charPath, "org.bluez.GattCharacteristic1", "UUID");
          if (!string.IsNullOrEmpty(charUuid))
            charMap[charUuid] = charPath;
        }

        mGattPaths[svcUuid] = charMap;
        if (DebugLogging)
          BaseLogger.LogDebug($"  Service {svcUuid}: {charMap.Count} characteristics");
      }

      if (DebugLogging)
        BaseLogger.LogDebug($"Discovered {mGattPaths.Count} GATT services");
    }

    /// <summary>
    /// Finds a GATT characteristic by service and characteristic UUIDs,
    /// using the busctl-discovered paths. Returns a GattCharacteristic
    /// created from the D-Bus object path.
    /// </summary>
    protected GattCharacteristic FindCharacteristic(string serviceUUID, string charUUID)
    {
      DiscoverGattTree();

      if (!mGattPaths!.TryGetValue(serviceUUID, out var charMap))
      {
        string available = string.Join(", ", mGattPaths.Keys);
        throw new Exception($"Service '{serviceUUID}' not found. Available: [{available}]");
      }

      if (!charMap.TryGetValue(charUUID, out string? charPath))
      {
        string available = string.Join(", ", charMap.Keys);
        throw new Exception($"Characteristic '{charUUID}' not found in service '{serviceUUID}'. Available: [{available}]");
      }

      if (DebugLogging)
        BaseLogger.LogDebug($"Creating GattCharacteristic for {charPath}");

      return CreateGattCharacteristic(charPath);
    }

    private static Connection? sDbusConnection;

    private GattCharacteristic CreateGattCharacteristic(string objectPath)
    {
      // Create a fresh D-Bus system bus connection (once), bypassing the
      // library's internal connection which deadlocks on GetManagedObjects.
      if (sDbusConnection == null)
      {
        sDbusConnection = new Connection(Address.System);
        Task.Run(async () => await sDbusConnection.ConnectAsync()).GetAwaiter().GetResult();
        if (DebugLogging)
          BaseLogger.LogDebug("Created fresh D-Bus connection for GATT proxies");
      }

      // Create IGattCharacteristic1 proxy on our fresh connection
      var proxy = sDbusConnection.CreateProxy<IGattCharacteristic1>(
        "org.bluez", new ObjectPath(objectPath));

      // Call internal GattCharacteristic.CreateAsync(IGattCharacteristic1 proxy)
      var createAsync = typeof(GattCharacteristic).GetMethod("CreateAsync",
        BindingFlags.Static | BindingFlags.NonPublic)!;
      var task = (Task)createAsync.Invoke(null, new object[] { proxy })!;
      task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
      return (GattCharacteristic)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static FieldInfo? FindFieldOfType(Type type, Type fieldType)
    {
      return type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .FirstOrDefault(f => fieldType.IsAssignableFrom(f.FieldType));
    }

    private static bool IsDirectChild(string parent, string path, string prefix)
    {
      if (!path.StartsWith(parent + "/")) return false;
      string suffix = path.Substring(parent.Length + 1);
      return suffix.StartsWith(prefix) && !suffix.Contains('/');
    }

    private static string RunProcess(string fileName, string arguments, int timeoutMs = 10000)
    {
      var psi = new ProcessStartInfo(fileName, arguments)
      {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
      };
      using var proc = Process.Start(psi)!;
      if (!proc.WaitForExit(timeoutMs))
      {
        try { proc.Kill(); } catch { }
        return "";
      }
      return proc.StandardOutput.ReadToEnd();
    }

    private static string ReadBusProperty(string objectPath, string iface, string property)
    {
      string output = RunProcess("busctl", $"get-property org.bluez {objectPath} {iface} {property}");
      // Format: s "uuid-value"
      int start = output.IndexOf('"');
      int end = output.LastIndexOf('"');
      if (start >= 0 && end > start)
        return output.Substring(start + 1, end - start - 1);
      return "";
    }

    private static bool sAgentRegistered = false;

    /// <summary>
    /// Registers a NoInputNoOutput D-Bus agent with BlueZ so that auto-pairing
    /// triggered by "Insufficient Authentication" uses Just Works (no MITM flag).
    /// Without this, BlueZ defaults to DisplayYesNo which sets the MITM flag and
    /// causes the R10 to reject the pairing and disconnect.
    /// </summary>
    private void RegisterPairingAgent()
    {
      if (sAgentRegistered) return;

      try
      {
        if (sDbusConnection == null)
        {
          sDbusConnection = new Connection(Address.System);
          Task.Run(async () => await sDbusConnection.ConnectAsync()).GetAwaiter().GetResult();
          if (DebugLogging)
            BaseLogger.LogDebug("Created fresh D-Bus connection for GATT proxies");
        }

        var agent = new NoInputNoOutputAgent();

        // Export the agent object on D-Bus so BlueZ can call its methods
        Task.Run(async () => await sDbusConnection.RegisterObjectAsync(agent)).GetAwaiter().GetResult();
        if (DebugLogging)
          BaseLogger.LogDebug("Exported Agent1 object on D-Bus");

        // Register with BlueZ AgentManager1
        var agentManager = sDbusConnection.CreateProxy<IAgentManager1>(
          "org.bluez", new ObjectPath("/org/bluez"));
        Task.Run(async () => await agentManager.RegisterAgentAsync(NoInputNoOutputAgent.AgentPath, "NoInputNoOutput")).GetAwaiter().GetResult();
        if (DebugLogging)
          BaseLogger.LogDebug("Registered NoInputNoOutput agent with BlueZ");

        Task.Run(async () => await agentManager.RequestDefaultAgentAsync(NoInputNoOutputAgent.AgentPath)).GetAwaiter().GetResult();
        if (DebugLogging)
          BaseLogger.LogDebug("Set as default agent");

        sAgentRegistered = true;
      }
      catch (Exception ex)
      {
        BaseLogger.LogDebug($"Agent registration failed (non-fatal): {ex.Message}");
      }
    }

    /// <summary>
    /// Enables BLE notifications on the device interface notifier and sets up
    /// the D-Bus signal watcher. Must be called BEFORE any other StartNotify
    /// or GATT read operations to avoid BLE channel contention on the Pi.
    /// </summary>
    protected void SetupDeviceInterfaceNotifier()
    {
      DiscoverGattTree();

      string notifierPath = mGattPaths![DEVICE_INTERFACE_SERVICE][DEVICE_INTERFACE_NOTIFIER];

      // Ensure fresh D-Bus connection exists
      if (sDbusConnection == null)
      {
        sDbusConnection = new Connection(Address.System);
        Task.Run(async () => await sDbusConnection.ConnectAsync()).GetAwaiter().GetResult();
        if (DebugLogging)
          BaseLogger.LogDebug("Created fresh D-Bus connection for GATT proxies");
      }

      // Register a NoInputNoOutput pairing agent BEFORE StartNotify.
      // This characteristic's CCCD requires authentication; StartNotify triggers
      // auto-pairing. Without the right agent, BlueZ uses DisplayYesNo (MITM flag)
      // which the R10 rejects.
      RegisterPairingAgent();

      if (DebugLogging)
        BaseLogger.LogDebug($"Calling StartNotify on {notifierPath}");

      // Set up D-Bus signal watcher FIRST so we don't miss any notifications
      var notifierProxy = sDbusConnection.CreateProxy<IGattCharacteristic1>(
        "org.bluez", new ObjectPath(notifierPath));
      Task.Run(async () => await notifierProxy.WatchPropertiesAsync(changes =>
      {
        foreach (var kvp in changes.Changed)
        {
          if (kvp.Key == "Value")
          {
            ReadBytes((byte[])kvp.Value);
          }
        }
      })).Wait(TimeSpan.FromSeconds(10));
      if (DebugLogging)
        BaseLogger.LogDebug("D-Bus signal watcher ready");

      // Now call StartNotify — this writes to the CCCD, which triggers
      // Insufficient Authentication → auto-pairing (using our NoInputNoOutput agent)
      // → bond established → CCCD write retried → notifications enabled.
      try
      {
        Task.Run(async () => await notifierProxy.StartNotifyAsync())
          .WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        if (DebugLogging)
          BaseLogger.LogDebug("StartNotify succeeded on device interface notifier");
      }
      catch (TimeoutException)
      {
        BaseLogger.LogDebug("StartNotify timed out (30s) — pairing may have failed");
        // Check if Notifying got enabled despite the timeout
        string notifyingRaw = RunProcess("busctl",
          $"get-property org.bluez {notifierPath} org.bluez.GattCharacteristic1 Notifying").Trim();
        if (DebugLogging)
          BaseLogger.LogDebug($"Notifying status: {notifyingRaw}");
      }
      catch (Exception ex)
      {
        BaseLogger.LogDebug($"StartNotify failed: {ex.Message}");
      }
    }

    public virtual bool Setup()
    {
      DiscoverGattTree();

      if (DebugLogging)
        BaseLogger.LogDebug($"Reading serial number");
      GattCharacteristic serialCharacteristic = FindCharacteristic(DEVICE_INFO_SERVICE_UUID, SERIAL_NUMBER_CHARACTERISTIC_UUID);
      Serial = Encoding.ASCII.GetString(Task.Run(async () => await serialCharacteristic.ReadValueAsync(new Dictionary<string, object>())).WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult());
      if (DebugLogging)
        BaseLogger.LogDebug($"Reading firmware version");
      GattCharacteristic firmwareCharacteristic = FindCharacteristic(DEVICE_INFO_SERVICE_UUID, FIRMWARE_CHARACTERISTIC_UUID);
      Firmware = Encoding.ASCII.GetString(Task.Run(async () => await firmwareCharacteristic.ReadValueAsync(new Dictionary<string, object>())).WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult());
      if (DebugLogging)
        BaseLogger.LogDebug($"Reading model name");
      GattCharacteristic modelCharacteristic = FindCharacteristic(DEVICE_INFO_SERVICE_UUID, MODEL_CHARACTERISTIC_UUID);
      Model = Encoding.ASCII.GetString(Task.Run(async () => await modelCharacteristic.ReadValueAsync(new Dictionary<string, object>())).WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult());
      if (DebugLogging)
        BaseLogger.LogDebug($"Reading battery life");
      GattCharacteristic batteryCharacteristic = FindCharacteristic(BATTERY_SERVICE_UUID, BATTERY_CHARACTERISTIC_UUID);
      // Subscribe once via Value += (auto-subscribes internally, no explicit StartNotifyAsync needed)
      batteryCharacteristic.Value += (sender, args) => { Battery = args.Value[0]; return Task.CompletedTask; };
      if (DebugLogging)
        BaseLogger.LogDebug($"Getting writer");
      mGattWriter = FindCharacteristic(DEVICE_INTERFACE_SERVICE, DEVICE_INTERFACE_WRITER);

      bool handshakeSuccess = PerformHandShake();
      if (!handshakeSuccess)
        BluetoothLogger.Error("Failed handshake. Something went wrong in setup");
      return handshakeSuccess;
    }

    private void ReaderThread()
    {
      List<byte> currentMessage = new List<byte>();

      while (!mCancellationToken.IsCancellationRequested)
      {
        if (mReaderQueue.TryDequeue(out byte[]? rawMsg))
        {
          try
          {
            IEnumerable<byte> msg = rawMsg;

            byte header = msg.First();
            msg = msg.Skip(1);

            if (header == 0 || !mHandshakeComplete)
            {
              ContinueHandShake(msg);
              continue;
            }

            bool readComplete = false;

            if (msg.Last() == 0x00)
            {
              readComplete = true;
              msg = msg.SkipLast(1);
            }
            if (msg.Count() > 0 && msg.First() == 0x00)
            {
              currentMessage.Clear();
              msg = msg.Skip(1);
            }
            currentMessage.AddRange(msg);

            if (readComplete && currentMessage.Count > 0)
            {
              if (DebugLogging)
                BaseLogger.LogDebug($"  -> {currentMessage.ToHexString().PadRight(44)} (encoded)");
              byte[] decoded = COBS.Decode(currentMessage.ToArray()).ToArray();
              if (DebugLogging)
                BaseLogger.LogDebug($"-> {decoded.ToHexString().PadRight(46)} (decoded)");
              mMsgProcessQueue.Enqueue(decoded);
              mMsgProcessSignal.Set();
              currentMessage.Clear();
            }
          }
          catch (Exception ex)
          {
            BluetoothLogger.Error($"ReaderThread error: {ex.Message}");
          }
        }
        else
        {
          mReaderSignal.WaitOne(5000);
        }
      }
    }

    private void WriterThread()
    {
      var writeOptions = new Dictionary<string, object> { { "type", "command" } };
      while (!mCancellationToken.IsCancellationRequested)
        if (mWriterQueue.TryDequeue(out byte[]? data))
        {
          try
          {
            Task.Run(async () => await mGattWriter!.WriteValueAsync(data, writeOptions)).Wait();
          }
          catch (Exception ex)
          {
            BluetoothLogger.Error($"WriterThread error: {ex.Message}");
          }
        }
        else
          mWriterSignal.WaitOne(5000);
    }

    private void MsgProcessingThread()
    {
      while (!mCancellationToken.IsCancellationRequested)
        if (mMsgProcessQueue.TryDequeue(out byte[]? msg))
        {
          try
          {
            ProcessMessage(msg);
          }
          catch (Exception ex)
          {
            BluetoothLogger.Error($"MsgProcessingThread error: {ex.Message}");
          }
        }
        else
          mMsgProcessSignal.WaitOne(5000);
    }

    public bool PerformHandShake()
    {
      if (DebugLogging)
        BaseLogger.LogDebug($"Starting handshake");
      mHandshakeComplete = false;
      mHandshakeCompleteResetEvent.Reset();
      mHeader = 0x00;
      SendBytes("000000000000000000010000");
      return mHandshakeCompleteResetEvent.Wait(TimeSpan.FromSeconds(10));
    }

    private void ContinueHandShake(IEnumerable<byte> msg)
    {
      string msgHex = msg.ToHexString();

      if (msgHex.StartsWith("010000000000000000010000"))
      {
        mHeader = msg.ElementAt(12);
        SendBytes("00");
        mHandshakeComplete = true;
        mHandshakeCompleteResetEvent.Set();
        return;
      }
    }

    private void ProcessMessage(byte[] frame)
    {
      // Need at least 2-byte length header + 2-byte message type + 2-byte CRC = 6 bytes minimum
      if (frame.Length < 6)
      {
        BluetoothLogger.Error($"ProcessMessage: frame too short ({frame.Length} bytes), dropping: {frame.ToHexString()}");
        return;
      }

      if (BitConverter.ToUInt16(frame.SkipLast(2).Checksum()) != BitConverter.ToUInt16(frame.TakeLast(2).ToArray()))
      {
        BluetoothLogger.Error("CRC ERROR");
      }

      byte[] msg = frame.Skip(2).SkipLast(2).ToArray();
      string hex = msg.ToHexString();

      List<byte> ackBody = new List<byte>() { 0x00 };

      if (hex.StartsWith("A013"))
      {
        // device info
      }
      else if (hex.StartsWith("BA13"))
      {
        // config
      }
      else if (hex.StartsWith("B413")) // all protobuf responses
      {
        ushort counter = BitConverter.ToUInt16(msg[2..4]);
        ackBody.AddRange(msg[2..4]);
        ackBody.AddRange("00000000000000".ToByteArray());

        if (counter == mProtoRequestCounter)
        {
          mLastProtoReceived = WrapperProto.Parser.ParseFrom(msg.Skip(16).ToArray());
          MessageRecieved?.Invoke(this, new MessageEventArgs() { Message = mLastProtoReceived } );
          mProtoResponseResetEvent.Set();
        }
        else
        {
          BluetoothLogger.Error($"Proto response counter mismatch: got {counter}, expected {mProtoRequestCounter}");
        }
      }
      else if (hex.StartsWith("B313")) // all protobuf requests
      {
        ackBody.AddRange(msg[2..4]);
        ackBody.AddRange("00000000000000".ToByteArray());
        Task.Run(() => {
          var request = WrapperProto.Parser.ParseFrom(msg.Skip(16).ToArray());
          MessageRecieved?.Invoke(this, new MessageEventArgs() { Message = request} );
          HandleProtobufRequest(request);
        });
      }

      AcknowledgeMessage(msg, ackBody);
    }

    public abstract void HandleProtobufRequest(IMessage request);

    private void AcknowledgeMessage(IEnumerable<byte> msg, IEnumerable<byte> respBody)
    {
      WriteMessage("8813".ToByteArray().Concat(msg.Take(2)).Concat(respBody).ToArray());
    }

    public IMessage? SendProtobufRequest(IMessage proto)
    {
      lock (mProtoRequestLock)
      {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
          mProtoResponseResetEvent.Reset();

          byte[] bytes = proto.ToByteArray();
          int l = bytes.Length;
          byte[] fullMsg = "B313".ToByteArray()
            .Concat(BitConverter.GetBytes(mProtoRequestCounter))
            .Append<byte>(0x00)
            .Append<byte>(0x00)
            .Concat(BitConverter.GetBytes(l))
            .Concat(BitConverter.GetBytes(l))
            .Concat(bytes)
            .ToArray();

          WriteMessage(fullMsg);
          MessageSent?.Invoke(this, new MessageEventArgs(){ Message = proto });
          if (mProtoResponseResetEvent.Wait(5000))
          {
            mProtoRequestCounter++;
            return mLastProtoReceived;
          }
          else
          {
            // Always increment counter on timeout — the device has already
            // processed this request and moved its counter forward. Without
            // this, all subsequent responses arrive with a mismatched counter
            // and get silently dropped, permanently desyncing the protocol.
            mProtoRequestCounter++;
            BluetoothLogger.Error($"Timeout waiting for proto response (attempt {attempt}/3, counter now {mProtoRequestCounter})");
            if (attempt < 3)
              Thread.Sleep(500);
          }
        }
        return null;
      }
    }

    private void ReadBytes(byte[] bytes)
    {
      if (DebugLogging)
        BaseLogger.LogDebug($"      -> {bytes.ToHexString().PadRight(40)} (ble read)");
      mReaderQueue.Enqueue(bytes);
      mReaderSignal.Set();
    }
    private void SendBytes(IEnumerable<byte> bytes)
    {
      if (DebugLogging)
        BaseLogger.LogDebug($"      <- {bytes.ToHexString().PadRight(40)} (ble write)");
      mWriterQueue.Enqueue(bytes.Prepend(mHeader).ToArray());
      mWriterSignal.Set();
    }

    public void SendBytes(string hexBytes) => SendBytes(hexBytes.ToByteArray());

    public void WriteMessage(byte[] bytes)
    {
      if (DebugLogging)
        BaseLogger.LogDebug($"<- {bytes.ToHexString().PadRight(46)} (raw)");

      // Length of message + 2 bytes for length field + 2 bytes for crc field
      ushort length = (ushort)(2 + bytes.Length + 2);
      IEnumerable<byte> bytesWithLength = BitConverter.GetBytes(length).Concat(bytes);
      IEnumerable<byte> fullFrame = bytesWithLength.Concat(bytesWithLength.Checksum());

      if (DebugLogging)
        BaseLogger.LogDebug($"  <- {fullFrame.ToHexString().PadRight(44)} (framed)");

      List<byte> encoded = COBS.Encode(fullFrame).Prepend<byte>(0x00).Append<byte>(0x00).ToList();
      if (DebugLogging)
        BaseLogger.LogDebug($"    <- {encoded.ToArray().ToHexString().PadRight(42)} (encoded)");

      while (encoded.Count > 19)
      {
        SendBytes(encoded.Take(19));
        encoded = encoded.Skip(19).ToList();
      }
      if (encoded.Count > 0)
        SendBytes(encoded);
    }

    protected virtual void Dispose(bool disposing)
    {
      if (!mDisposedValue)
      {
        if (disposing)
        {
          mCancellationToken.Cancel();
          mWriterTask.Wait();
          mReaderTask.Wait();
          mMsgProcessingTask.Wait();

          foreach (var d in MessageSent?.GetInvocationList() ?? Array.Empty<Delegate>())
            MessageSent -= (d as MessageEventHandler);

          foreach (var d in MessageRecieved?.GetInvocationList() ?? Array.Empty<Delegate>())
            MessageRecieved -= (d as MessageEventHandler);

          foreach (var d in BatteryLifeUpdated?.GetInvocationList() ?? Array.Empty<Delegate>())
            BatteryLifeUpdated -= (d as BatteryEventHandler);

          Device?.DisconnectAsync().Wait(TimeSpan.FromSeconds(30));
        }

        mDisposedValue = true;
      }
    }

    public void Dispose()
    {
      Dispose(disposing: true);
      GC.SuppressFinalize(this);
    }
  }
}
