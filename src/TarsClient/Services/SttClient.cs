using System.Net.WebSockets;
using System.Threading.Channels;

namespace TarsClient.Services;

/// <summary>
/// Local speech-to-text: we stream mic frames to the sidecar's <c>/stt/stream</c> (Silero VAD + faster-whisper) and
/// raise utterances. We connect whenever the sidecar is up; <see cref="IsReady"/> says whether frames should come here
/// (true) or go to the server as before (false). Events arrive on the UI thread.
/// </summary>
public sealed class SttClient : IDisposable
{
    #region Fields

    private readonly Dispatcher _ui;
    private readonly Func<string> _sidecarUrl;
    private readonly Func<(string model, string device, string speaker, double threshold)> _config;
    private readonly CancellationTokenSource _life = new();
    private Channel<(bool binary, byte[] data)> _out = NewChannel();
    private (string model, string device, string speaker, double threshold) _sentConfig;
    private Task? _loop;

    #endregion

    #region Constructor

    /// <summary>Creates the client; <paramref name="config"/> is re-read on every <see cref="Reconfigure"/>.</summary>
    public SttClient(Dispatcher ui, Func<string> sidecarUrl, Func<(string model, string device, string speaker, double threshold)> config)
    {
        _ui = ui;
        _sidecarUrl = sidecarUrl;
        _config = config;
    }

    #endregion

    #region Properties

    /// <summary>Whether the sidecar's STT stream is connected.</summary>
    public bool IsReady { get; private set; }

    #endregion

    #region Events

    /// <summary><see cref="IsReady"/> changed.</summary>
    public event Action<bool>? ReadyChanged;

    /// <summary>The VAD heard someone start (true) or stop (false) talking.</summary>
    public event Action<bool>? SpeechChanged;

    /// <summary>Whisper is working on an utterance.</summary>
    public event Action? Transcribing;

    /// <summary>A finished transcript.</summary>
    public event Action<Utterance>? UtteranceReady;

    /// <summary>The sidecar dropped an utterance: reason and (possibly empty) text.</summary>
    public event Action<string, string>? Rejected;

    #endregion

    #region Public Methods

    /// <summary>Starts the connect loop (idempotent).</summary>
    public void Start() => _loop ??= Task.Run(() => RunAsync(_life.Token));

    /// <summary>Queues a 16 kHz PCM frame.</summary>
    public void SendAudio(byte[] pcm)
    {
        if (IsReady) _out.Writer.TryWrite((true, pcm));
    }

    /// <summary>Opens a push-to-talk bracket.</summary>
    public void PttStart() => Control(SidecarMessages.Ptt(true));

    /// <summary>Closes the push-to-talk bracket; the sidecar transcribes it as one utterance.</summary>
    public void PttEnd() => Control(SidecarMessages.Ptt(false));

    /// <summary>Drops any half-heard utterance (TARS started talking).</summary>
    public void Reset() => Control(SidecarMessages.Reset());

    /// <summary>Re-sends model, device and voice lock when they changed (game mode, settings).</summary>
    public void Reconfigure()
    {
        var c = _config();
        if (c == _sentConfig) return;
        _sentConfig = c;
        Control(SidecarMessages.Config(c.model, c.device, c.speaker, c.threshold));
    }

    /// <inheritdoc />
    public void Dispose() => _life.Cancel();

    #endregion

    #region Private Methods

    private static Channel<(bool, byte[])> NewChannel() =>
        Channel.CreateBounded<(bool, byte[])>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private void Control(object message)
    {
        if (IsReady) _out.Writer.TryWrite((false, JsonSerializer.SerializeToUtf8Bytes(message)));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await SessionAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception) when (!ct.IsCancellationRequested) { /* sidecar not up yet, or restarting: the loop must survive */ }

            SetReady(false);
            try { await Task.Delay(3000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SessionAsync(CancellationToken ct)
    {
        var baseUrl = _sidecarUrl().TrimEnd('/');
        var uri = new Uri(baseUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/stt/stream");
        using var ws = new ClientWebSocket();
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(3000);
            await ws.ConnectAsync(uri, connectCts.Token);
        }

        _out = NewChannel();
        SetReady(true);
        _sentConfig = default;
        _ = _ui.BeginInvoke(Reconfigure);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var send = SendLoopAsync(ws, session.Token);
        await ReceiveLoopAsync(ws, session.Token);
        session.Cancel();
        try { await send; }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
    }

    private void SetReady(bool ready)
    {
        if (IsReady == ready) return;
        IsReady = ready;
        Log.Write($"stt: local {(ready ? "connected" : "unavailable")}");
        _ = _ui.BeginInvoke(() => ReadyChanged?.Invoke(ready));
    }

    private async Task SendLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var reader = _out.Reader;
        while (await reader.WaitToReadAsync(ct))
        {
            while (reader.TryRead(out var item))
            {
                var type = item.binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text;
                await ws.SendAsync(item.data, type, true, ct);
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);

            try { Dispatch(JsonDocument.Parse(ms.ToArray()).RootElement.Clone()); }
            catch (JsonException) { }
        }
    }

    private void Dispatch(JsonElement m)
    {
        switch (Str(m, "type"))
        {
            case "vad":
                bool speech = m.TryGetProperty("speech", out var sp) && sp.GetBoolean();
                _ = _ui.BeginInvoke(() => SpeechChanged?.Invoke(speech));
                break;
            case "transcribing":
                _ = _ui.BeginInvoke(() => Transcribing?.Invoke());
                break;
            case "utterance":
                var utterance = ParseUtterance(m);
                _ = _ui.BeginInvoke(() => UtteranceReady?.Invoke(utterance));
                break;
            case "rejected":
                var (reason, text) = (Str(m, "reason"), Str(m, "text"));
                _ = _ui.BeginInvoke(() => Rejected?.Invoke(reason, text));
                break;
        }
    }

    /// <summary>Reads a sidecar <c>utterance</c> message; missing numbers read as 0.</summary>
    internal static Utterance ParseUtterance(JsonElement m) =>
        new(Str(m, "text"), Str(m, "source"), (int)Num(m, "speech_ms"), (int)Num(m, "duration_ms"), (int)Num(m, "stt_ms"),
            Num(m, "logprob"), Num(m, "no_speech_prob"), Str(m, "model"));

    private static string Str(JsonElement m, string key) => m.TryGetProperty(key, out var v) ? v.ToString() : "";

    private static double Num(JsonElement m, string key) => m.TryGetProperty(key, out var v) && v.TryGetDouble(out var d) ? d : 0;

    #endregion
}
