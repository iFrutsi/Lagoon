using System.Runtime.InteropServices;

namespace NotchApp.Services;

/// <summary>Speaker volume/mute and system-wide microphone mute through Core Audio.</summary>
public sealed class AudioService
{
    const int eRender = 0, eCapture = 1;
    const int eConsole = 0, eCommunications = 2;
    const int CLSCTX_ALL = 23;

    static Guid _context = Guid.NewGuid();

    static IAudioEndpointVolume? Endpoint(int flow, int role)
    {
        try
        {
            // by class id, interface only (see BluetoothAudio: the enumerator is a shared COM object)
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
            if (enumerator.GetDefaultAudioEndpoint(flow, role, out var device) != 0 || device == null)
                return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj) != 0) return null;
            return obj as IAudioEndpointVolume;
        }
        catch
        {
            return null;
        }
    }

    public float GetVolume()
    {
        var ep = Endpoint(eRender, eConsole);
        if (ep == null) return 0;
        ep.GetMasterVolumeLevelScalar(out var level);
        return level;
    }

    public void SetVolume(float level) =>
        Endpoint(eRender, eConsole)?.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref _context);

    public bool GetSpeakerMute()
    {
        var ep = Endpoint(eRender, eConsole);
        if (ep == null) return false;
        ep.GetMute(out var muted);
        return muted;
    }

    public void SetSpeakerMute(bool muted) => Endpoint(eRender, eConsole)?.SetMute(muted, ref _context);

    public bool HasMicrophone => Endpoint(eCapture, eConsole) != null;

    public bool GetMicMute()
    {
        var ep = Endpoint(eCapture, eConsole);
        if (ep == null) return false;
        ep.GetMute(out var muted);
        return muted;
    }

    /// <summary>Mutes the default mic for both roles, so every app hears silence.</summary>
    public void SetMicMute(bool muted)
    {
        Endpoint(eCapture, eConsole)?.SetMute(muted, ref _context);
        Endpoint(eCapture, eCommunications)?.SetMute(muted, ref _context);
    }

    // ---- mic mute from anywhere (Windows, Teams, a keyboard key...): Windows calls us, no polling ----

    /// <summary>The default mic was muted or unmuted (raised on a background thread).</summary>
    public event Action<bool>? MicMuteChanged;

    /// <summary>An audio device came or went (headphones connecting...). Background thread.</summary>
    public event Action? DevicesChanged;

    /// <summary>Speakers: muted, level 0-1 (from anywhere: keys, Windows, apps). Background thread.</summary>
    public event Action<bool, float, bool>? SpeakerChanged; // muted, level, changed now (false: first read / new device)
    IAudioEndpointVolume? _watchedSpeaker;
    MuteCallback? _speakerCallback;

    IMMDeviceEnumerator? _devices;
    IAudioEndpointVolume? _watchedMic;
    MuteCallback? _muteCallback;
    DeviceCallback? _deviceCallback;
    readonly object _watchLock = new();

    public void WatchMicMute()
    {
        try
        {
            _devices ??= (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
            _muteCallback ??= new MuteCallback((m, _) => MicMuteChanged?.Invoke(m));
            _speakerCallback ??= new MuteCallback((m, v) => SpeakerChanged?.Invoke(m, v, true));
            if (_deviceCallback == null)
            {
                // earbuds connecting etc. change the default mic / speakers: watch the new ones
                _deviceCallback = new DeviceCallback(flow => { if (flow == eCapture) RewatchMic(); else RewatchSpeaker(); }, () => DevicesChanged?.Invoke());
                _devices.RegisterEndpointNotificationCallback(_deviceCallback);
            }
            RewatchMic();
            RewatchSpeaker();
        }
        catch { }
    }

    void RewatchMic()
    {
        lock (_watchLock)
        {
            try
            {
                if (_watchedMic != null) _watchedMic.UnregisterControlChangeNotify(_muteCallback!);
                _watchedMic = Endpoint(eCapture, eConsole);
                _watchedMic?.RegisterControlChangeNotify(_muteCallback!);
                if (_watchedMic != null && _watchedMic.GetMute(out var m) == 0) MicMuteChanged?.Invoke(m);
            }
            catch { }
        }
    }

    void RewatchSpeaker()
    {
        lock (_watchLock)
        {
            try
            {
                if (_watchedSpeaker != null) _watchedSpeaker.UnregisterControlChangeNotify(_speakerCallback!);
                _watchedSpeaker = Endpoint(eRender, eConsole);
                _watchedSpeaker?.RegisterControlChangeNotify(_speakerCallback!);
                if (_watchedSpeaker != null && _watchedSpeaker.GetMute(out var m) == 0 && _watchedSpeaker.GetMasterVolumeLevelScalar(out var v) == 0)
                    SpeakerChanged?.Invoke(m, v, false);
            }
            catch { }
        }
    }

    public void StopWatching()
    {
        lock (_watchLock)
        {
            try
            {
                if (_watchedSpeaker != null && _speakerCallback != null) _watchedSpeaker.UnregisterControlChangeNotify(_speakerCallback);
                if (_watchedMic != null && _muteCallback != null) _watchedMic.UnregisterControlChangeNotify(_muteCallback);
                if (_devices != null && _deviceCallback != null) _devices.UnregisterEndpointNotificationCallback(_deviceCallback);
            }
            catch { }
            _watchedMic = null;
            _watchedSpeaker = null;
            _deviceCallback = null;
        }
    }

    [ComVisible(true)]
    sealed class MuteCallback(Action<bool, float> changed) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr data)
        {
            // AUDIO_VOLUME_NOTIFICATION_DATA: GUID context (16 bytes), BOOL muted, float master volume
            changed(Marshal.ReadInt32(data, 16) != 0, BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20)));
            return 0;
        }
    }

    [ComVisible(true)]
    sealed class DeviceCallback(Action<int> defaultChanged, Action stateChanged) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string id, int state) { stateChanged(); return 0; }
        public int OnDeviceAdded(string id) => 0;
        public int OnDeviceRemoved(string id) => 0;
        public int OnDefaultDeviceChanged(int flow, int role, string? id) { if (role == eConsole) defaultChanged(flow); return 0; }
        public int OnPropertyValueChanged(string id, PROPERTYKEY key) => 0;
    }

    // ---- Core Audio COM interop ----

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [StructLayout(LayoutKind.Sequential)] public struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PROPERTYKEY key);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
