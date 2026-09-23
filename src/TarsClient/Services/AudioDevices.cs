using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace TarsClient.Services;

public sealed record AudioDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Device lists, lookup by id, and change notifications (headset unplugged / default switched).</summary>
public sealed class AudioDevices : IMMNotificationClient, IDisposable
{
    readonly MMDeviceEnumerator _en = new();
    readonly System.Windows.Threading.DispatcherTimer _debounce;

    /// <summary>Raised on the UI thread, debounced, when anything about the endpoints changes.</summary>
    public event Action? Changed;

    public AudioDevices()
    {
        _debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Changed?.Invoke(); };
        _en.RegisterEndpointNotificationCallback(this);
    }

    public static MMDevice? Find(MMDeviceEnumerator en, DataFlow flow, string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            var d = en.GetDevice(id);
            return d.State == DeviceState.Active && d.DataFlow == flow ? d : null;
        }
        catch { return null; }
    }

    public static List<AudioDevice> List(DataFlow flow)
    {
        var list = new List<AudioDevice> { new("", "DEFAULT") };
        using var en = new MMDeviceEnumerator();
        foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
            list.Add(new(d.ID, d.FriendlyName));
        return list;
    }

    void Poke() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Poke();
    public void OnDeviceAdded(string pwstrDeviceId) => Poke();
    public void OnDeviceRemoved(string deviceId) => Poke();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { if (role == Role.Multimedia || role == Role.Communications) Poke(); }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        try { _en.UnregisterEndpointNotificationCallback(this); } catch { }
        _en.Dispose();
    }
}
