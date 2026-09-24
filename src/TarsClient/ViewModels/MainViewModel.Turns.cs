namespace TarsClient.ViewModels;

/// <summary>Turns: the typewriter, the scrollback, the status tag and the text prompt.</summary>
public sealed partial class MainViewModel
{
    #region Fields

    private string _replyTarget = "";
    private int _replyShown;
    private DateTime _typeLast;
    private TermLine? _replyLine;
    private bool _hadTurn;

    #endregion

    #region Commands

    [RelayCommand]
    private void SendPrompt()
    {
        var text = PromptText.Trim();
        PromptText = "";
        PromptActive = IsExpanded;
        if (text.Length == 0) return;
        if (!IsConnected)
        {
            Add(LineKind.Error, "NO CARRIER: not sent");
            return;
        }
        StopSpeech();
        Server.SendJson(ClientMessages.Text(text, S.SpeakTyped));
    }

    #endregion

    #region Private Methods

    private void BeginTurn(string text)
    {
        if (S.PopOnReply && !IsVisible) ShowRequested?.Invoke(false);
        FinishTyping();
        _hadTurn = true;
        LastUtterance = "> " + text;
        Add(LineKind.User, "> " + text);
        _replyTarget = "";
        _replyShown = 0;
        _replyLine = null;
        ReplyText = "";
    }

    /// <summary><c>delta</c> text is appended raw; each <c>reply</c> sentence is joined with a space.</summary>
    private void AppendReply(string text, bool raw)
    {
        if (text.Length == 0) return;
        // Never lag behind the audio: a new sentence finishes the current one instantly.
        FinishTyping();
        _replyTarget = raw || _replyTarget.Length == 0 ? _replyTarget + text : _replyTarget.TrimEnd() + " " + text.TrimStart();
        if (_replyLine == null)
        {
            _replyLine = new TermLine(LineKind.Tars, $"{AssistantName}: ");
            AddLine(_replyLine);
        }
        if (!IsVisible)
        {
            FinishTyping();
            return;
        }
        CursorVisible = true;
        _typeLast = DateTime.UtcNow;
        _typeTimer.Start();
    }

    private void TypeTick()
    {
        var now = DateTime.UtcNow;
        int add = (int)Math.Max(1, (now - _typeLast).TotalSeconds * Math.Max(10, S.TypewriterCps));
        _typeLast = now;
        _replyShown = Math.Min(_replyTarget.Length, _replyShown + add);
        RenderReply();
        if (_replyShown < _replyTarget.Length) return;
        _typeTimer.Stop();
        CursorVisible = Playback.IsPlaying;
    }

    private void FinishTyping()
    {
        _typeTimer.Stop();
        _replyShown = _replyTarget.Length;
        RenderReply();
    }

    private void RenderReply()
    {
        var shown = _replyTarget[.._replyShown];
        ReplyText = shown.Length == 0 ? "" : $"{AssistantName}: {shown}";
        if (_replyLine != null) _replyLine.Text = $"{AssistantName}: {shown}";
    }

    private void AddLine(TermLine line)
    {
        Lines.Add(line);
        while (Lines.Count > MaxLines) Lines.RemoveAt(0);
    }

    private void RecomputeStatus()
    {
        StatusTag = ComputeStatus();
        if (!Playback.IsPlaying && _replyShown >= _replyTarget.Length) CursorVisible = false;
    }

    /// <summary>The title-bar tag, most urgent first.</summary>
    private string ComputeStatus() => true switch
    {
        _ when AlarmActive => "ALARM",
        _ when !IsConnected => Server.IsDenied ? "ACCESS DENIED" : "NO CARRIER",
        _ when Playback.IsPlaying => "SPEAKING",
        _ when _pttHeld => "HEARING",
        _ when _localState is "hearing" or "transcribing" => _localState.ToUpperInvariant(),
        _ when _serverState is "hearing" or "transcribing" or "thinking" => _serverState.ToUpperInvariant(),
        _ when _micBlocked => "MIC BLOCKED",
        _ when Capture.IsEndpointMuted => "MIC MUTED",
        _ when Voice.State == VoiceState.Warming && _serverState != "idle" => "VOICE: WARMING",
        _ => S.Mode == "ptt" ? "STANDBY" : "LISTENING",
    };

    private void UpdateVoiceTag()
    {
        VoiceTag = Voice.State switch
        {
            VoiceState.Online => "",
            VoiceState.Warming => "VOICE: WARMING",
            VoiceState.GameMode => "VOICE: GAME",
            VoiceState.NotInstalled => "VOICE: NOT INSTALLED",
            VoiceState.Starting => "VOICE: BOOTING",
            _ => "VOICE: OFFLINE",
        };
        Settings.RefreshVoice();
        RecomputeStatus();
    }

    #endregion
}
