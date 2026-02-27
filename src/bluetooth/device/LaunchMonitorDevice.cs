using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using LaunchMonitor.Proto;
using static LaunchMonitor.Proto.State.Types;
using static LaunchMonitor.Proto.SubscribeResponse.Types;
using static LaunchMonitor.Proto.WakeUpResponse.Types;

namespace gspro_r10.bluetooth
{
  public class LaunchMonitorDevice : BaseDevice
  {
    internal static string MEASUREMENT_SERVICE_UUID = "6a4e3400-667b-11e3-949a-0800200c9a66";
    internal static string MEASUREMENT_CHARACTERISTIC_UUID = "6a4e3401-667b-11e3-949a-0800200c9a66";
    internal static string CONTROL_POINT_CHARACTERISTIC_UUID = "6a4e3402-667b-11e3-949a-0800200c9a66";
    internal static string STATUS_CHARACTERISTIC_UUID = "6a4e3403-667b-11e3-949a-0800200c9a66";

    private HashSet<uint> ProcessedShotIDs = new HashSet<uint>();

    private StateType _currentState;
    public StateType CurrentState { 
      get { return _currentState; } 
      private set {
        _currentState = value;
        Ready = value == StateType.Waiting;
      }
    }

    public Tilt? DeviceTilt { get; private set; }

    private bool _ready = false;
    public bool Ready { 
      get {return _ready; } 
      private set {
        bool changed = _ready != value;
        _ready = value;
        if (changed)
          ReadinessChanged?.Invoke(this, new ReadinessChangedEventArgs(){ Ready = value });
      }
    }

    public bool AutoWake { get; set; } = true;
    public bool CalibrateTiltOnConnect { get; set; } = true;

    public event ReadinessChangedEventHandler? ReadinessChanged;
    public delegate void ReadinessChangedEventHandler(object sender, ReadinessChangedEventArgs e);
    public class ReadinessChangedEventArgs: EventArgs
    {
      public bool Ready { get; set; }
    }

    public event ErrorEventHandler? Error;
    public delegate void ErrorEventHandler(object sender, ErrorEventArgs e);
    public class ErrorEventArgs: EventArgs
    {
      public string? Message { get; set; }
      public Error.Types.Severity Severity { get; set; }
    }

    public event MetricsEventHandler? ShotMetrics;
    public delegate void MetricsEventHandler(object sender, MetricsEventArgs e);
    public class MetricsEventArgs: EventArgs
    {
      public Metrics? Metrics { get; set; }
    }

    public LaunchMonitorDevice(Device device) : base(device)
    {

    }

    public override bool Setup()
    {
      // Enable device interface notifier FIRST — before any other GATT operations.
      // On the Pi, calling StartNotify on this characteristic after other
      // StartNotify/read operations causes BlueZ to hang.
      SetupDeviceInterfaceNotifier();

      if (DebugLogging)
        BaseLogger.LogDebug("Subscribing to measurement characteristic");
      GattCharacteristic measCharacteristic = FindCharacteristic(MEASUREMENT_SERVICE_UUID, MEASUREMENT_CHARACTERISTIC_UUID);
      // Subscribe once via StartNotifyAsync — no Value handler needed (data unused)
      Task.Run(async () => await measCharacteristic.StartNotifyAsync()).Wait(TimeSpan.FromSeconds(30));

      if (DebugLogging)
        BaseLogger.LogDebug("Subscribing to control characteristic");
      GattCharacteristic controlPoint = FindCharacteristic(MEASUREMENT_SERVICE_UUID, CONTROL_POINT_CHARACTERISTIC_UUID);
      // Subscribe once via StartNotifyAsync — no Value handler needed (unused for now)
      Task.Run(async () => await controlPoint.StartNotifyAsync()).Wait(TimeSpan.FromSeconds(30));

      if (DebugLogging)
        BaseLogger.LogDebug("Subscribing to status characteristic");
      GattCharacteristic statusCharacteristic = FindCharacteristic(MEASUREMENT_SERVICE_UUID, STATUS_CHARACTERISTIC_UUID);
      // Subscribe once via Value += (auto-subscribes internally)
      statusCharacteristic.Value += (sender, args) =>
      {
        bool isAwake = args.Value[1] == (byte)0;
        bool isReady = args.Value[2] == (byte)0;

        // the following is unused in favor of the status change notifications and wake control provided by the protobuf service
        // if (!isAwake)
        // {
        //   controlPoint.WriteValueAsync(new byte[] { 0x00 }, new Dictionary<string, object>()).Wait();
        // }
        return Task.CompletedTask;
      };


      bool baseSetupSuccess = base.Setup();
      if (!baseSetupSuccess)
      {
        BluetoothLogger.Error("Error during base device setup");
        return false;
      }


      WakeDevice();
      CurrentState = StatusRequest() ?? StateType.Error;
      DeviceTilt = GetDeviceTilt();
      SubscribeToAlerts().First();

      if (CalibrateTiltOnConnect)
        StartTiltCalibration();

      return true;
    }

    public override void HandleProtobufRequest(IMessage request)
    {
      if (request is WrapperProto WrapperProtoRequest)
      {
        AlertDetails notification = WrapperProtoRequest.Event.Notification.AlertNotification_;
        if (notification.State != null)
        {
          CurrentState = notification.State.State_;
          if (notification.State.State_ == StateType.Standby)
          {
            if (AutoWake)
            {
              BluetoothLogger.Info("Device asleep. Sending wakeup call");
              WakeDevice();
            }
            else
            {
              BluetoothLogger.Error("Device asleep. Wake device using button (or enable autowake in settings)");
            }
          }
        }
        if (notification.Error != null && notification.Error.HasCode)
        {
          Error?.Invoke(this, new ErrorEventArgs() { Message = $"{notification.Error.Code.ToString()} {notification.Error.DeviceTilt}", Severity = notification.Error.Severity });
        }
        if (notification.Metrics != null)
        {
          if (ProcessedShotIDs.Contains(notification.Metrics.ShotId))
          {
            BluetoothLogger.Error($"Received duplicate shot data {notification.Metrics.ShotId}.  Ignoring");
          }
          else
          {
            ProcessedShotIDs.Add(notification.Metrics.ShotId);
            ShotMetrics?.Invoke(this, new MetricsEventArgs() { Metrics = notification.Metrics });
          }
        }
        if (notification.TiltCalibration != null)
        {
          DeviceTilt = GetDeviceTilt();
        }
      }
    }

    public Tilt? GetDeviceTilt()
    {
      IMessage? resp = SendProtobufRequest(
        new WrapperProto() { Service = new LaunchMonitorService() { TiltRequest = new TiltRequest() } }
      );

      if (resp is WrapperProto WrapperProtoResponse)
        return WrapperProtoResponse.Service.TiltResponse.Tilt;
      
      return null;
    }

    public ResponseStatus? WakeDevice()
    {
      IMessage? resp = SendProtobufRequest(
        new WrapperProto() { Service = new LaunchMonitorService() { WakeUpRequest = new WakeUpRequest() } }
      );

      if (resp is WrapperProto WrapperProtoResponse)
        return WrapperProtoResponse.Service.WakeUpResponse.Status;

      return null;
    }

    public StateType? StatusRequest()
    {
      IMessage? resp = SendProtobufRequest(
        new WrapperProto() { Service = new LaunchMonitorService() { StatusRequest = new StatusRequest() } }
      );

      if (resp is WrapperProto WrapperProtoResponse)
        return WrapperProtoResponse.Service.StatusResponse.State.State_;

      return null;
    }

    public List<AlertStatusMessage> SubscribeToAlerts()
    {
      IMessage? resp = SendProtobufRequest(
        new WrapperProto()
        {
          Event = new EventSharing()
          {
            SubscribeRequest = new SubscribeRequest()
            {
              Alerts = { new List<AlertMessage>() { new AlertMessage() { Type = AlertNotification.Types.AlertType.LaunchMonitor } } }
            }
          }
        }
      );

      if (resp is WrapperProto WrapperProtoResponse)
        return WrapperProtoResponse.Event.SubscribeRespose.AlertStatus.ToList();

      return new List<AlertStatusMessage>();

    }

    public bool ShotConfig(float temperature, float humidity, float altitude, float airDensity, float teeRange)
    {
      IMessage? resp = SendProtobufRequest(new WrapperProto()
      {
        Service = new LaunchMonitorService()
        {
          ShotConfigRequest = new ShotConfigRequest()
          {
            Temperature = temperature,
            Humidity = humidity,
            Altitude = altitude,
            AirDensity = airDensity,
            TeeRange = teeRange
          }
        }
      });

      if (resp is WrapperProto WrapperProtoResponse)
        return WrapperProtoResponse.Service.ShotConfigResponse.Success;

      return false;
    }

    public ResetTiltCalibrationResponse.Types.Status? ResetTiltCalibrartion(bool shouldReset = true)
    {
      IMessage? resp = SendProtobufRequest(
        new WrapperProto() { Service = new LaunchMonitorService() { ResetTiltCalRequest = new ResetTiltCalibrationRequest() { ShouldReset = shouldReset } } }
      );

      if (resp is WrapperProto WrapperProtoResponse)
        return WrapperProtoResponse.Service.ResetTiltCalResponse.Status;

      return null;
    }

    public StartTiltCalibrationResponse.Types.CalibrationStatus? StartTiltCalibration(bool shouldReset = true)
    {
      IMessage? resp = SendProtobufRequest(
        new WrapperProto() { Service = new LaunchMonitorService() { StartTiltCalRequest = new StartTiltCalibrationRequest() } }
      );

      if (resp is WrapperProto WrapperProtoResponse)
        return WrapperProtoResponse.Service.StartTiltCalResponse.Status;

      return null;
    }

    protected override void Dispose(bool disposing)
    {
      foreach (var d in ReadinessChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
        ReadinessChanged -= (d as ReadinessChangedEventHandler);

      foreach (var d in Error?.GetInvocationList() ?? Array.Empty<Delegate>())
        Error -= (d as ErrorEventHandler);

      foreach (var d in ShotMetrics?.GetInvocationList() ?? Array.Empty<Delegate>())
        ShotMetrics -= (d as MetricsEventHandler);

      base.Dispose(disposing);
    }
  }
}