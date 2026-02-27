using Google.Protobuf;
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using System.Linq;
using System.Text;
using LaunchMonitor.Proto;
using System.Threading.Tasks;
using System.Threading;
using System;
using System.Collections.Generic;

namespace gspro_r10.bluetooth
{
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
    private Queue<byte[]> mWriterQueue = new Queue<byte[]>();
    private EventWaitHandle mReaderSignal = new AutoResetEvent(false);
    private Queue<byte[]> mReaderQueue = new Queue<byte[]>();
    private EventWaitHandle mMsgProcessSignal = new AutoResetEvent(false);
    private Queue<byte[]> mMsgProcessQueue = new Queue<byte[]>();
    private ManualResetEventSlim mHandshakeCompleteResetEvent = new ManualResetEventSlim(false);
    private ManualResetEventSlim mProtoResponseResetEvent = new ManualResetEventSlim(false);
    private IMessage? mLastProtoReceived;
    private int mBattery;
    private bool mHandshakeComplete = false;
    private byte mHeader = 0x00;
    private int mProtoRequestCounter = 0;
    private CancellationTokenSource mCancellationToken;
    private Task mWriterTask;
    private Task mReaderTask;
    private Task mMsgProcessingTask;
    private GattCharacteristic? mGattWriter;
    private bool mDisposedValue;
    public bool DebugLogging { get; set; } = false;

    public BaseDevice(Device device)
    {
      Device = device;

      mCancellationToken = new CancellationTokenSource();
      mWriterTask = Task.Run(WriterThread, mCancellationToken.Token);
      mReaderTask = Task.Run(ReaderThread, mCancellationToken.Token);
      mMsgProcessingTask = Task.Run(MsgProcessingThread, mCancellationToken.Token);
    }

    // GetServiceAsync waits for a D-Bus InterfacesAdded signal that may never fire if services
    // were already resolved. Use GetServicesAsync (snapshot) + manual UUID match instead.
    // GetServicesAsync calls GetManagedObjects which can be slow on Pi; retry a few times.
    protected IGattService1 FindService(string serviceUUID)
    {
      List<string> lastUuids = new();
      for (int attempt = 0; attempt < 5; attempt++)
      {
        if (attempt > 0)
        {
          BaseLogger.LogDebug($"FindService retry {attempt}/4 for {serviceUUID}");
          Thread.Sleep(3000);
        }
        try
        {
          // Task.Run prevents sync-over-async deadlock: GetServicesAsync uses Tmds.DBus
          // which posts completions back to the ThreadPool. Blocking that same thread with
          // GetResult() would deadlock; Task.Run gives it a fresh context.
          IReadOnlyList<IGattService1>? services = Task.Run(async () =>
            await Device.GetServicesAsync()).GetAwaiter().GetResult();
          if (services == null || services.Count == 0) continue;

          lastUuids = new List<string>();
          foreach (var svc in services)
          {
            try
            {
              string uuid = Task.Run(async () => await svc.GetUUIDAsync()).GetAwaiter().GetResult();
              lastUuids.Add(uuid);
              if (string.Equals(uuid, serviceUUID, StringComparison.OrdinalIgnoreCase))
                return svc;
            }
            catch { lastUuids.Add("(error)"); }
          }
          // Services returned but UUID not found — log and retry
          BaseLogger.LogDebug($"Available services: [{string.Join(", ", lastUuids)}]");
        }
        catch (TimeoutException)
        {
          BaseLogger.LogDebug($"GetServicesAsync timed out on attempt {attempt + 1}/5");
        }
      }
      string available = lastUuids.Count > 0 ? string.Join(", ", lastUuids) : "(none retrieved)";
      throw new Exception($"Service '{serviceUUID}' not found after 5 attempts. Available: [{available}]");
    }

    public virtual bool Setup()
    {
      if (DebugLogging)
        BaseLogger.LogDebug($"Getting device info service");
      IGattService1 deviceInfoService = FindService(DEVICE_INFO_SERVICE_UUID);
      if (DebugLogging)
        BaseLogger.LogDebug($"Reading serial number");
      IGattCharacteristic1 serialCharacteristic = deviceInfoService.GetCharacteristicAsync(SERIAL_NUMBER_CHARACTERISTIC_UUID).WaitAsync(TimeSpan.FromSeconds(30)).Result!;
      Serial = Encoding.ASCII.GetString(serialCharacteristic.ReadValueAsync(new Dictionary<string, object>()).WaitAsync(TimeSpan.FromSeconds(30)).Result);
      if (DebugLogging)
        BaseLogger.LogDebug($"Reading firmware version");
      IGattCharacteristic1 firmwareCharacteristic = deviceInfoService.GetCharacteristicAsync(FIRMWARE_CHARACTERISTIC_UUID).WaitAsync(TimeSpan.FromSeconds(30)).Result!;
      Firmware = Encoding.ASCII.GetString(firmwareCharacteristic.ReadValueAsync(new Dictionary<string, object>()).WaitAsync(TimeSpan.FromSeconds(30)).Result);
      if (DebugLogging)
        BaseLogger.LogDebug($"Reading model name");
      IGattCharacteristic1 modelCharacteristic = deviceInfoService.GetCharacteristicAsync(MODEL_CHARACTERISTIC_UUID).WaitAsync(TimeSpan.FromSeconds(30)).Result!;
      Model = Encoding.ASCII.GetString(modelCharacteristic.ReadValueAsync(new Dictionary<string, object>()).WaitAsync(TimeSpan.FromSeconds(30)).Result);
      if (DebugLogging)
        BaseLogger.LogDebug($"Reading battery life");
      IGattService1 batteryService = FindService(BATTERY_SERVICE_UUID);
      GattCharacteristic batteryCharacteristic = (GattCharacteristic)batteryService.GetCharacteristicAsync(BATTERY_CHARACTERISTIC_UUID).WaitAsync(TimeSpan.FromSeconds(30)).Result!;
      // Subscribe once via Value += (auto-subscribes internally, no explicit StartNotifyAsync needed)
      batteryCharacteristic.Value += (sender, args) => { Battery = args.Value[0]; return Task.CompletedTask; };
      if (DebugLogging)
        BaseLogger.LogDebug($"Setting up device interface service");
      IGattService1 deviceInterfaceService = FindService(DEVICE_INTERFACE_SERVICE);
      if (DebugLogging)
        BaseLogger.LogDebug($"Getting writer");
      mGattWriter = (GattCharacteristic)deviceInterfaceService.GetCharacteristicAsync(DEVICE_INTERFACE_WRITER).WaitAsync(TimeSpan.FromSeconds(30)).Result!;
      if (DebugLogging)
        BaseLogger.LogDebug($"Getting reader");
      GattCharacteristic deviceInterfaceNotifier = (GattCharacteristic)deviceInterfaceService.GetCharacteristicAsync(DEVICE_INTERFACE_NOTIFIER).WaitAsync(TimeSpan.FromSeconds(30)).Result!;
      // Subscribe once via Value += (auto-subscribes internally, no explicit StartNotifyAsync needed)
      deviceInterfaceNotifier.Value += (sender, args) => { ReadBytes(args.Value); return Task.CompletedTask; };
      bool handshakeSuccess = PerformHandShake();
      if (!handshakeSuccess)
        Console.WriteLine("Failed handshake. Something went wrong in setup");
      return handshakeSuccess;
    }

    private void ReaderThread()
    {
      List<byte> currentMessage = new List<byte>();

      while (!mCancellationToken.IsCancellationRequested)
      {
        if (mReaderQueue.Count > 0)
        {
          IEnumerable<byte> msg = mReaderQueue.Dequeue();

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
        else
        {
          mReaderSignal.WaitOne(5000);
        }
      }
    }

    private void WriterThread()
    {
      while (!mCancellationToken.IsCancellationRequested)
        if (mWriterQueue.Count > 0)
          mGattWriter?.WriteValueAsync(mWriterQueue.Dequeue(), new Dictionary<string, object>()).Wait();
        else
          mWriterSignal.WaitOne(5000);
    }

    private void MsgProcessingThread()
    {
      while (!mCancellationToken.IsCancellationRequested)
        if (mMsgProcessQueue.Count > 0)
          ProcessMessage(mMsgProcessQueue.Dequeue());
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
      if (BitConverter.ToUInt16(frame.SkipLast(2).Checksum()) != BitConverter.ToUInt16(frame.TakeLast(2).ToArray()))
      {
        Console.WriteLine("CRC ERROR");
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
        Console.WriteLine($"Failed to get response for proto {mProtoRequestCounter}");
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