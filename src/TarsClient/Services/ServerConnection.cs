using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;

namespace TarsClient.Services;

/// <summary>
/// The WebSocket to the TARS server: pinned TLS, JSON text frames, binary audio frames,
/// reconnect forever with backoff (0.5 s … 10 s). Close code 4403 stops retrying until <see cref="Restart"/>.
/// Events are raised on the UI dispatcher.
/// </summary>
public sealed class ServerConnection : IDisposable
{
    readonly Dispatcher _ui;
    readonly Func<(string url, string key)> _config;
    Channel<(bool binary, byte[] data)> _out = NewChannel();
    CancellationTokenSource _life = new();
    TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task? _loop;

    public event Action? Connected;
    public event Action<string?>? Disconnected;         // reason (null = ordinary drop)
    public event Action? AccessDenied;
    public event Action<int>? RetryFailed;               // attempt number
    public event Action<string, JsonElement>? Message;
    public event Action<byte[]>? Binary;

    public bool IsConnected { get; private set; }
    public bool IsDenied { get; private set; }

    public ServerConnection(Dispatcher ui, Func<(string url, string key)> config)
    {
        _ui = ui;
        _config = config;
    }

    static Channel<(bool, byte[])> NewChannel() =>
        Channel.CreateBounded<(bool, byte[])>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public void Start() => _loop ??= Task.Run(() => RunAsync(_life.Token));

    /// <summary>Reconnect now (settings changed, or after ACCESS DENIED).</summary>
    public void Restart()
    {
        IsDenied = false;
        _resume.TrySetResult();
        _current?.Abort();
    }

    public void SendJson(object message)
    {
        if (!IsConnected) return;
        _out.Writer.TryWrite((false, JsonSerializer.SerializeToUtf8Bytes(message)));
    }

    public void SendAudio(byte[] pcm)
    {
        if (IsConnected) _out.Writer.TryWrite((true, pcm));
    }

    ClientWebSocket? _current;

    async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            if (IsDenied)
            {
                await _resume.Task.WaitAsync(ct).ConfigureAwait(false);
                _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
                attempt = 0;
            }

            var (url, key) = _config();
            using var ws = new ClientWebSocket();
            _current = ws;
            ws.Options.RemoteCertificateValidationCallback = Tls.Validate;
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            if (!string.IsNullOrWhiteSpace(key)) ws.Options.SetRequestHeader("x-tars-key", key.Trim());

            string? reason = null;
            try
            {
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(8));
                    await ws.ConnectAsync(new Uri(url), connectCts.Token).ConfigureAwait(false);
                }
                attempt = 0;
                _out = NewChannel();                     // drop audio queued while offline
                IsConnected = true;
                _ = _ui.BeginInvoke(() => Connected?.Invoke());

                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var send = SendLoopAsync(ws, sessionCts.Token);
                await ReceiveLoopAsync(ws, sessionCts.Token).ConfigureAwait(false);
                sessionCts.Cancel();
                try { await send.ConfigureAwait(false); } catch { }

                if ((int?)ws.CloseStatus == 4403) IsDenied = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                reason = ex is WebSocketException { InnerException: { } inner } ? inner.Message : ex.Message;
                if ((int?)ws.CloseStatus == 4403 || reason.Contains("403")) IsDenied = true;
            }
            finally
            {
                _current = null;
                if (IsConnected)
                {
                    IsConnected = false;
                    var r = reason;
                    _ = _ui.BeginInvoke(() => Disconnected?.Invoke(r));
                }
            }

            if (IsDenied)
            {
                Log.Write("ws: access denied (4403)");
                _ = _ui.BeginInvoke(() => AccessDenied?.Invoke());
                continue;
            }

            attempt++;
            var a = attempt;
            if (reason != null) Log.Write($"ws: attempt {a} failed: {reason}");
            _ = _ui.BeginInvoke(() => RetryFailed?.Invoke(a));
            var delay = TimeSpan.FromMilliseconds(Math.Min(10_000, 500 * Math.Pow(2, Math.Min(attempt - 1, 5))));
            try { await Task.WhenAny(Task.Delay(delay, ct), _resume.Task).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (_resume.Task.IsCompleted) _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    async Task SendLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var reader = _out.Reader;
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (reader.TryRead(out var item))
            {
                if (ws.State != WebSocketState.Open) return;
                await ws.SendAsync(item.data, item.binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, ct)
                        .ConfigureAwait(false);
            }
        }
    }

    async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            ValueWebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);

            var data = ms.ToArray();
            if (r.MessageType == WebSocketMessageType.Binary)
            {
                _ = _ui.BeginInvoke(() => Binary?.Invoke(data));
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement.Clone();
                var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                _ = _ui.BeginInvoke(() => Message?.Invoke(type, root));
            }
            catch (JsonException ex) { Log.Write($"ws: bad json: {ex.Message}: {Encoding.UTF8.GetString(data)}"); }
        }
    }

    public void Dispose()
    {
        _life.Cancel();
        _current?.Abort();
    }
}
