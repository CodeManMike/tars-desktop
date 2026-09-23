using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;

namespace TarsClient.Services;

/// <summary>A finished, transcribed utterance from the local STT.</summary>
public sealed record Utterance(string Text, string Source, int SpeechMs, int SttMs, double LogProb, double NoSpeechProb, string Model);

/// <summary>
/// Local speech-to-text: streams mic frames to the sidecar's /stt/stream (Silero VAD + faster-whisper)
/// and raises utterances. Connects whenever the sidecar is up; <see cref="IsReady"/> says whether frames should go here
/// (true) or to the server as before (false). Events arrive on the UI thread.
/// </summary>
public sealed class SttClient : IDisposable
{
    readonly Dispatcher _ui;
    readonly Func<string> _sidecarUrl;
    readonly Func<(string model, string device)> _modelChoice;
    Channel<(bool binary, byte[] data)> _out = NewChannel();
    readonly CancellationTokenSource _life = new();
    Task? _loop;
    (string model, string device) _sentConfig;

    public bool IsReady { get; private set; }
    public event Action<bool>? ReadyChanged;
    public event Action<bool>? SpeechChanged;          // VAD: someone started / stopped talking
    public event Action? Transcribing;
    public event Action<Utterance>? UtteranceReady;
    public event Action<string, string>? Rejected;     // reason, text

    public SttClient(Dispatcher ui, Func<string> sidecarUrl, Func<(string model, string device)> modelChoice)
    {
        _ui = ui;
        _sidecarUrl = sidecarUrl;
        _modelChoice = modelChoice;
    }

    static Channel<(bool, byte[])> NewChannel() =>
        Channel.CreateBounded<(bool, byte[])>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public void Start() => _loop ??= Task.Run(() => RunAsync(_life.Token));

    public void SendAudio(byte[] pcm) { if (IsReady) _out.Writer.TryWrite((true, pcm)); }
    public void PttStart() => Control(new { type = "ptt", state = "start" });
    public void PttEnd() => Control(new { type = "ptt", state = "end" });
    /// <summary>Drop any half-heard utterance (TARS started talking).</summary>
    public void Reset() => Control(new { type = "reset" });

    /// <summary>Re-send the model/device choice (game mode on/off, settings changed).</summary>
    public void Reconfigure()
    {
        var c = _modelChoice();
        if (c == _sentConfig) return;
        _sentConfig = c;
        Control(new { type = "config", model = c.model, device = c.device, hotwords = "TARS" });
    }

    void Control(object o) { if (IsReady) _out.Writer.TryWrite((false, JsonSerializer.SerializeToUtf8Bytes(o))); }

    async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var baseUrl = _sidecarUrl().TrimEnd('/');
                var uri = new Uri(baseUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/stt/stream");
                using var ws = new ClientWebSocket();
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(3000);
                    await ws.ConnectAsync(uri, cts.Token);
                }
                _out = NewChannel();
                SetReady(true);
                _sentConfig = default;
                _ = _ui.BeginInvoke(Reconfigure);

                using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var send = SendLoop(ws, session.Token);
                await ReceiveLoop(ws, session.Token);
                session.Cancel();
                try { await send; } catch { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { /* sidecar not up yet / restarting */ }
            SetReady(false);
            try { await Task.Delay(3000, ct); } catch { break; }
        }
    }

    void SetReady(bool ready)
    {
        if (IsReady == ready) return;
        IsReady = ready;
        Log.Write($"stt: local {(ready ? "connected" : "unavailable")}");
        _ = _ui.BeginInvoke(() => ReadyChanged?.Invoke(ready));
    }

    async Task SendLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var reader = _out.Reader;
        while (await reader.WaitToReadAsync(ct))
            while (reader.TryRead(out var item))
                await ws.SendAsync(item.data, item.binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, ct);
    }

    async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            try { Dispatch(JsonDocument.Parse(ms.ToArray()).RootElement.Clone()); }
            catch (JsonException) { }
        }
    }

    void Dispatch(JsonElement m)
    {
        string S(string k) => m.TryGetProperty(k, out var v) ? v.ToString() : "";
        double D(string k) => m.TryGetProperty(k, out var v) && v.TryGetDouble(out var d) ? d : 0;
        switch (S("type"))
        {
            case "vad":
                bool speech = m.TryGetProperty("speech", out var sp) && sp.GetBoolean();
                _ = _ui.BeginInvoke(() => SpeechChanged?.Invoke(speech));
                break;
            case "transcribing":
                _ = _ui.BeginInvoke(() => Transcribing?.Invoke());
                break;
            case "utterance":
                var u = new Utterance(S("text"), S("source"), (int)D("speech_ms"), (int)D("stt_ms"), D("logprob"), D("no_speech_prob"), S("model"));
                _ = _ui.BeginInvoke(() => UtteranceReady?.Invoke(u));
                break;
            case "rejected":
                var (reason, text) = (S("reason"), S("text"));
                _ = _ui.BeginInvoke(() => Rejected?.Invoke(reason, text));
                break;
        }
    }

    public void Dispose() => _life.Cancel();
}
