using System.Net.WebSockets;
using System.Threading.Channels;

namespace TarsClient.Services;

/// <summary>
/// The WebSocket to the TARS server: pinned TLS, JSON text frames, binary audio frames, reconnecting forever with
/// backoff (0.5 s … 10 s). A 4403 close (or an HTTP 403 handshake) means ACCESS DENIED, and we stop retrying until
/// <see cref="Restart"/>. Events are raised on the UI dispatcher.
/// </summary>
public sealed class ServerConnection : IDisposable
{
    #region Fields

    private const int AccessDeniedCode = 4403;

    private readonly Dispatcher _ui;
    private readonly Func<(string url, string key)> _config;
    private readonly CancellationTokenSource _life = new();
    private Channel<(bool binary, byte[] data)> _out = NewChannel();
    private TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ClientWebSocket? _current;
    private Task? _loop;

    #endregion

    #region Constructor

    /// <summary>Creates the connection; <paramref name="config"/> is read on every (re)connect.</summary>
    public ServerConnection(Dispatcher ui, Func<(string url, string key)> config)
    {
        _ui = ui;
        _config = config;
    }

    #endregion

    #region Properties

    /// <summary>Whether the socket is open.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>Whether the server refused this PC (and we've stopped retrying).</summary>
    public bool IsDenied { get; private set; }

    #endregion

    #region Events

    /// <summary>The socket opened.</summary>
    public event Action? Connected;

    /// <summary>The socket closed; the argument is the failure reason, or null for an ordinary drop.</summary>
    public event Action<string?>? Disconnected;

    /// <summary>The server refused this PC.</summary>
    public event Action? AccessDenied;

    /// <summary>A reconnect attempt failed (the argument is the attempt number).</summary>
    public event Action<int>? RetryFailed;

    /// <summary>A JSON message arrived: its <c>type</c> and the whole object.</summary>
    public event Action<string, JsonElement>? Message;

    /// <summary>A binary frame arrived (a server WAV).</summary>
    public event Action<byte[]>? Binary;

    #endregion

    #region Public Methods

    /// <summary>Starts the connect loop (idempotent).</summary>
    public void Start() => _loop ??= Task.Run(() => RunAsync(_life.Token));

    /// <summary>Reconnects now: settings changed, or we're retrying after ACCESS DENIED.</summary>
    public void Restart()
    {
        IsDenied = false;
        _resume.TrySetResult();
        _current?.Abort();
    }

    /// <summary>Queues a JSON message (dropped while disconnected).</summary>
    public void SendJson(object message)
    {
        if (IsConnected) _out.Writer.TryWrite((false, JsonSerializer.SerializeToUtf8Bytes(message)));
    }

    /// <summary>Queues a PCM frame (dropped while disconnected).</summary>
    public void SendAudio(byte[] pcm)
    {
        if (IsConnected) _out.Writer.TryWrite((true, pcm));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _life.Cancel();
        _current?.Abort();
    }

    #endregion

    #region Private Methods

    private static Channel<(bool, byte[])> NewChannel() =>
        Channel.CreateBounded<(bool, byte[])>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private async Task RunAsync(CancellationToken ct)
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

            string? reason;
            try { reason = await SessionAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }

            if (reason == null) attempt = 0;
            if (IsDenied)
            {
                Log.Write("ws: access denied (4403)");
                _ = _ui.BeginInvoke(() => AccessDenied?.Invoke());
                continue;
            }

            int failed = ++attempt;
            if (reason != null) Log.Write($"ws: attempt {failed} failed: {reason}");
            _ = _ui.BeginInvoke(() => RetryFailed?.Invoke(failed));
            var delay = TimeSpan.FromMilliseconds(Math.Min(10_000, 500 * Math.Pow(2, Math.Min(attempt - 1, 5))));
            try { await Task.WhenAny(Task.Delay(delay, ct), _resume.Task).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (_resume.Task.IsCompleted) _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>One connection, from connect to close. Returns the failure reason, or null if it connected at all.</summary>
    private async Task<string?> SessionAsync(CancellationToken ct)
    {
        var (url, key) = _config();
        using var ws = new ClientWebSocket();
        _current = ws;
        ws.Options.RemoteCertificateValidationCallback = Tls.Validate;
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        if (!string.IsNullOrWhiteSpace(key)) ws.Options.SetRequestHeader("x-tars-key", key.Trim());

        string? reason = null;
        bool opened = false;
        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(8));
                await ws.ConnectAsync(new Uri(url), connectCts.Token).ConfigureAwait(false);
            }
            opened = true;
            _out = NewChannel();                     // audio queued while offline is stale
            IsConnected = true;
            _ = _ui.BeginInvoke(() => Connected?.Invoke());

            using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var send = SendLoopAsync(ws, session.Token);
            await ReceiveLoopAsync(ws, session.Token).ConfigureAwait(false);
            session.Cancel();
            try { await send.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Anything can go wrong on a network connection (or a URL mistyped in settings); the loop must survive it.
            reason = ex is WebSocketException { InnerException: { } inner } ? inner.Message : ex.Message;
        }
        finally
        {
            _current = null;
            if (IsConnected)
            {
                IsConnected = false;
                var why = reason;
                _ = _ui.BeginInvoke(() => Disconnected?.Invoke(why));
            }
        }

        // The server closes with 4403; an older one refuses the handshake with HTTP 403.
        if ((int?)ws.CloseStatus == AccessDeniedCode || (reason?.Contains("403", StringComparison.Ordinal) ?? false)) IsDenied = true;
        return opened ? null : reason ?? "connection failed";
    }

    private async Task SendLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var reader = _out.Reader;
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (reader.TryRead(out var item))
            {
                if (ws.State != WebSocketState.Open) return;
                var type = item.binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text;
                await ws.SendAsync(item.data, type, true, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
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
            Dispatch(data);
        }
    }

    private void Dispatch(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement.Clone();
            var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            _ = _ui.BeginInvoke(() => Message?.Invoke(type, root));
        }
        catch (JsonException ex) { Log.Write($"ws: bad json: {ex.Message}: {Encoding.UTF8.GetString(data)}"); }
    }

    #endregion
}
