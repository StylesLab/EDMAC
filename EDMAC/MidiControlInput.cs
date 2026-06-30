using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Devices.Enumeration;
using Windows.Devices.Midi;
using Windows.Storage.Streams;

namespace EDMAC;

public sealed class MidiControlInput : IDisposable
{
    public const int ControlChangeBase = 0;
    private const int ControlCount = 24;
    private const int PlayPauseNote = 62;
    private const int PlayPauseDebounceMilliseconds = 75;
    private const int CallbackFunction = 0x00030000;
    private const int MidiData = 0x3C3;
    private static readonly int[] FunctionKeyNotes = [64, 65, 67, 69, 71, 72, 74, 76, 77, 79];

    private readonly Action<ConsoleKey> onControl;
    private readonly Action? onPlayPause;
    private readonly bool traceEnabled;
    private readonly List<OpenWinMmMidiInput> winMmInputs = [];
    private readonly List<MidiInPort> winRtInputs = [];
    private readonly List<string> deviceNames = [];
    private readonly List<string> failedDeviceMessages = [];
    private readonly object stateLock = new();
    private readonly int[][] controlValuesByDevice;
    private readonly object duplicateLock = new();
    private readonly object backendLock = new();
    private string? activeBackend;
    private long lastPlayPauseTicks;
    private bool disposed;

    private MidiControlInput(Action<ConsoleKey> onControl, Action? onPlayPause)
    {
        this.onControl = onControl;
        this.onPlayPause = onPlayPause;
        traceEnabled = string.Equals(
            Environment.GetEnvironmentVariable("EDMAC_MIDI_TRACE"),
            "1",
            StringComparison.OrdinalIgnoreCase);
        controlValuesByDevice = Enumerable
            .Range(0, MidiInGetNumDevs())
            .Select(_ => new int[128])
            .ToArray();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void MidiInProc(
        IntPtr midiIn,
        int message,
        IntPtr instance,
        IntPtr parameter1,
        IntPtr parameter2);

    public static MidiControlInput Start(
        Action<ConsoleKey> onControl,
        Action? onPlayPause = null,
        MidiInputBackend backend = MidiInputBackend.All)
    {
        var input = new MidiControlInput(onControl, onPlayPause);
        input.StartDevices(backend);
        return input;
    }

    public IReadOnlyList<string> DeviceNames => deviceNames;

    public IReadOnlyList<string> FailedDeviceMessages => failedDeviceMessages;

    private void StartDevices(MidiInputBackend backend)
    {
        if (backend is MidiInputBackend.WindowsMidi or MidiInputBackend.All)
        {
            StartWinRtDevices();
        }

        if (backend is MidiInputBackend.WinMm or MidiInputBackend.All)
        {
            StartWinMmDevices();
        }
    }

    private void StartWinRtDevices()
    {
        DeviceInformationCollection devices;

        try
        {
            devices = DeviceInformation
                .FindAllAsync(MidiInPort.GetDeviceSelector())
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception)
        {
            failedDeviceMessages.Add($"Windows MIDI: {exception.Message}");
            return;
        }

        foreach (DeviceInformation device in devices)
        {
            try
            {
                MidiInPort? port = MidiInPort
                    .FromIdAsync(device.Id)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();

                if (port is null)
                {
                    failedDeviceMessages.Add($"{device.Name}: Windows MIDI did not open the input port.");
                    continue;
                }

                string deviceName = $"Windows MIDI: {device.Name}";
                port.MessageReceived += (_, args) =>
                    HandleRawBytes("Windows MIDI", deviceName, ReadRawBytes(args.Message.RawData));
                winRtInputs.Add(port);
                deviceNames.Add(deviceName);
            }
            catch (Exception exception)
            {
                failedDeviceMessages.Add($"{device.Name}: {exception.Message}");
            }
        }
    }

    private void StartWinMmDevices()
    {
        for (var deviceNumber = 0; deviceNumber < MidiInGetNumDevs(); deviceNumber++)
        {
            int capturedDeviceNumber = deviceNumber;
            string deviceName = GetDeviceName(deviceNumber);
            MidiInProc callback = (_, message, _, parameter1, _) =>
            {
                if (traceEnabled && message != MidiData)
                {
                    ConsoleUi.KeyValue(
                        "MidiCallback",
                        $"{deviceName} message=0x{message:X} parameter1=0x{parameter1.ToInt64():X}");
                }

                if (message == MidiData)
                {
                    HandleShortMessage(
                        "WinMM",
                        capturedDeviceNumber,
                        deviceName,
                        parameter1.ToInt64());
                }
            };

            int result = MidiInOpen(
                out IntPtr handle,
                deviceNumber,
                callback,
                IntPtr.Zero,
                CallbackFunction);
            if (result != 0)
            {
                failedDeviceMessages.Add($"{deviceName}: {FormatMidiError(result)}");
                continue;
            }

            result = MidiInStart(handle);
            if (result != 0)
            {
                MidiInClose(handle);
                failedDeviceMessages.Add($"{deviceName}: {FormatMidiError(result)}");
                continue;
            }

            winMmInputs.Add(new OpenWinMmMidiInput(handle, callback));
            deviceNames.Add($"WinMM: {deviceName}");
        }
    }

    private static byte[] ReadRawBytes(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(bytes);
        return bytes;
    }

    private void HandleRawBytes(string backend, string deviceName, byte[] message)
    {
        if (message.Length < 3)
        {
            return;
        }

        int status = message[0];
        int data1 = message[1];
        int data2 = message[2];
        HandleMidiMessage(backend, deviceName, status, data1, data2);
    }

    private void HandleShortMessage(
        string backend,
        int deviceNumber,
        string deviceName,
        long rawMessage)
    {
        int status = (int)(rawMessage & 0xFF);
        int data1 = (int)((rawMessage >> 8) & 0xFF);
        int data2 = (int)((rawMessage >> 16) & 0xFF);
        HandleMidiMessage(backend, deviceName, status, data1, data2, deviceNumber);
    }

    private void HandleMidiMessage(
        string backend,
        string deviceName,
        int status,
        int data1,
        int data2,
        int deviceNumber = 0)
    {
        int command = status & 0xF0;
        int channel = status & 0x0F;

        if (traceEnabled)
        {
            ConsoleUi.KeyValue(
                "MidiIn",
                $"{deviceName} status=0x{status:X2} channel={channel + 1} data1={data1} data2={data2}");
        }

        if (TryMapPlayPause(command, data1, data2))
        {
            if (!TryUseBackend(backend))
            {
                return;
            }

            if (TryConsumePlayPause())
            {
                InvokePlayPause();
            }

            return;
        }

        if (TryMapFunctionNote(command, data1, data2, out ConsoleKey noteControl) ||
            TryMapControlChange(deviceNumber, command, data1, data2, out noteControl))
        {
            if (!TryUseBackend(backend))
            {
                return;
            }

            InvokeControl(noteControl);
        }
    }

    private bool TryUseBackend(string backend)
    {
        lock (backendLock)
        {
            activeBackend ??= backend;
            return activeBackend == backend;
        }
    }

    private void InvokeControl(ConsoleKey control)
    {
        onControl(control);
    }

    private void InvokePlayPause()
    {
        if (onPlayPause is null)
        {
            return;
        }

        _ = Task.Run(onPlayPause);
    }

    private static bool TryMapPlayPause(int command, int noteNumber, int velocity)
    {
        return command == 0x90 &&
            velocity > 0 &&
            noteNumber == PlayPauseNote;
    }

    private bool TryConsumePlayPause()
    {
        long now = Environment.TickCount64;

        lock (duplicateLock)
        {
            if (now - lastPlayPauseTicks < PlayPauseDebounceMilliseconds)
            {
                return false;
            }

            lastPlayPauseTicks = now;
            return true;
        }
    }

    private static bool TryMapFunctionNote(
        int command,
        int noteNumber,
        int velocity,
        out ConsoleKey control)
    {
        control = default;

        if (command != 0x90 || velocity <= 0)
        {
            return false;
        }

        int index = Array.IndexOf(FunctionKeyNotes, noteNumber);
        return TryMapIndex(index, out control);
    }

    private bool TryMapControlChange(
        int deviceNumber,
        int command,
        int controller,
        int value,
        out ConsoleKey control)
    {
        control = default;

        if (command != 0xB0)
        {
            return false;
        }

        bool wasOff;

        lock (stateLock)
        {
            wasOff = controlValuesByDevice[deviceNumber][controller] == 0;
            controlValuesByDevice[deviceNumber][controller] = value;
        }

        if (!wasOff || value == 0)
        {
            return false;
        }

        return TryMapIndex(controller - ControlChangeBase, out control);
    }

    private static bool TryMapIndex(int index, out ConsoleKey control)
    {
        control = default;

        if (index is < 0 or >= ControlCount)
        {
            return false;
        }

        control = ConsoleKey.F1 + index;
        return true;
    }

    private static string GetDeviceName(int deviceNumber)
    {
        int result = MidiInGetDevCaps(
            deviceNumber,
            out MidiInCaps capabilities,
            Marshal.SizeOf<MidiInCaps>());

        return result == 0
            ? capabilities.ProductName
            : $"MIDI input {deviceNumber}";
    }

    private static string FormatMidiError(int errorCode)
    {
        var message = new StringBuilder(256);

        return MidiInGetErrorText(errorCode, message, message.Capacity) == 0
            ? message.ToString()
            : new Win32Exception(errorCode).Message;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        foreach (MidiInPort input in winRtInputs)
        {
            input.Dispose();
        }

        foreach (OpenWinMmMidiInput input in winMmInputs)
        {
            MidiInStop(input.Handle);
            MidiInReset(input.Handle);
            MidiInClose(input.Handle);
        }
    }

    [DllImport("winmm.dll", EntryPoint = "midiInGetNumDevs")]
    private static extern int MidiInGetNumDevs();

    [DllImport("winmm.dll", EntryPoint = "midiInGetDevCapsW", CharSet = CharSet.Unicode)]
    private static extern int MidiInGetDevCaps(
        int deviceId,
        out MidiInCaps capabilities,
        int capabilitiesSize);

    [DllImport("winmm.dll", EntryPoint = "midiInOpen")]
    private static extern int MidiInOpen(
        out IntPtr handle,
        int deviceId,
        MidiInProc callback,
        IntPtr instance,
        int flags);

    [DllImport("winmm.dll", EntryPoint = "midiInStart")]
    private static extern int MidiInStart(IntPtr handle);

    [DllImport("winmm.dll", EntryPoint = "midiInStop")]
    private static extern int MidiInStop(IntPtr handle);

    [DllImport("winmm.dll", EntryPoint = "midiInReset")]
    private static extern int MidiInReset(IntPtr handle);

    [DllImport("winmm.dll", EntryPoint = "midiInClose")]
    private static extern int MidiInClose(IntPtr handle);

    [DllImport("winmm.dll", EntryPoint = "midiInGetErrorTextW", CharSet = CharSet.Unicode)]
    private static extern int MidiInGetErrorText(
        int errorCode,
        StringBuilder message,
        int messageSize);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiInCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ProductName;

        public uint Support;
    }

    private sealed record OpenWinMmMidiInput(IntPtr Handle, MidiInProc Callback);
}

public enum MidiInputBackend
{
    WinMm,
    WindowsMidi,
    All
}
