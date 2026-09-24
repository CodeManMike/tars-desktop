using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace TarsClient.Services;

/// <summary>Device lists, lookup by id, and change notifications (headset unplugged, default switched).</summary>
public sealed class AudioDevices : IMMNotificationClient, IDisposable
{
    #region Fields

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly DispatcherTimer _debounce;

    #endregion

    #region Constructor

    /// <summary>Subscribes to endpoint notifications.</summary>
    public AudioDevices()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Changed?.Invoke();
        };
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    #endregion

    #region Events

    /// <summary>Raised on the UI thread, debounced, when anything about the endpoints changes.</summary>
    public event Action? Changed;

    #endregion

    #region Public Methods

    /// <summary>The active endpoint with <paramref name="id"/> and <paramref name="flow"/>, or null (empty id means default).</summary>
    public static MMDevice? Find(MMDeviceEnumerator enumerator, DataFlow flow, string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            var device = enumerator.GetDevice(id);
            return device.State == DeviceState.Active && device.DataFlow == flow ? device : null;
        }
        catch (System.Runtime.InteropServices.COMException) { return null; }
    }

    /// <summary>"DEFAULT" followed by every active endpoint of <paramref name="flow"/>.</summary>
    public static List<AudioDevice> List(DataFlow flow)
    {
        var list = new List<AudioDevice> { new("", "DEFAULT") };
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            list.Add(new(device.ID, device.FriendlyName));
        return list;
    }

    /// <inheritdoc />
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Poke();

    /// <inheritdoc />
    public void OnDeviceAdded(string pwstrDeviceId) => Poke();

    /// <inheritdoc />
    public void OnDeviceRemoved(string deviceId) => Poke();

    /// <inheritdoc />
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (role is Role.Multimedia or Role.Communications) Poke();
    }

    /// <inheritdoc />
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    /// <inheritdoc />
    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(this); }
        catch (System.Runtime.InteropServices.COMException) { }
        _enumerator.Dispose();
    }

    #endregion

    #region Private Methods

    /// <summary>Notifications arrive on a COM thread; we restart the debounce on the UI thread.</summary>
    private void Poke() => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        _debounce.Stop();
        _debounce.Start();
    });

    #endregion
}
