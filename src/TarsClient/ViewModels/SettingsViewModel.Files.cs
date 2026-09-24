namespace TarsClient.ViewModels;

/// <summary>FILES tab: the server's persona and knowledge files.</summary>
public sealed partial class SettingsViewModel
{
    #region Fields

    private bool _loadingFile;

    #endregion

    #region Properties

    /// <summary>The server's editable files, persona first.</summary>
    public ObservableCollection<ServerFile> Files { get; } = [];

    /// <summary>The file in the editor.</summary>
    [ObservableProperty]
    public partial ServerFile? SelectedFile { get; set; }

    /// <summary>The editor's text (CRLF while editing, LF on the server).</summary>
    [ObservableProperty]
    public partial string EditorText { get; set; } = "";

    /// <summary>The editor has unsaved changes.</summary>
    [ObservableProperty]
    public partial bool Dirty { get; set; }

    /// <summary>The name for a new knowledge file.</summary>
    [ObservableProperty]
    public partial string NewFileName { get; set; } = "";

    #endregion

    #region Commands

    [RelayCommand]
    private async Task LoadFiles()
    {
        try
        {
            var files = await _main.Admin.GetFilesAsync();
            var keep = SelectedFile;
            Files.Clear();
            foreach (var file in files.OrderBy(f => f.Area == "persona" ? 0 : 1).ThenBy(f => f.Name)) Files.Add(file);
            Say($"{files.Count} files");
            if (keep != null) SelectedFile = Files.FirstOrDefault(f => f.Area == keep.Area && f.Name == keep.Name);
        }
        catch (Exception ex) when (IsAdminFailure(ex))
        {
            Say(ex.Message);
        }
    }

    [RelayCommand]
    private async Task SaveFile()
    {
        if (SelectedFile is not { } file) return;
        try
        {
            await _main.Admin.PutFileAsync(file.Area, file.Name, EditorText.Replace("\r\n", "\n"));
            Dirty = false;
            Say(file.Area == "knowledge" ? $"saved {file.Name} · re-indexed" : $"saved {file.Name} · applies on the next answer");
        }
        catch (Exception ex) when (IsAdminFailure(ex))
        {
            Say(ex.Message);
        }
    }

    /// <summary>Creates a knowledge file (<c>.md</c> is added when missing) and opens it.</summary>
    [RelayCommand]
    private async Task NewFile()
    {
        var name = NewFileName.Trim();
        if (name.Length == 0)
        {
            Say("type a name first, e.g. cars.md");
            return;
        }
        if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) name += ".md";
        try
        {
            await _main.Admin.PutFileAsync("knowledge", name, $"# {Path.GetFileNameWithoutExtension(name)}\n");
            NewFileName = "";
            await LoadFiles();
            SelectedFile = Files.FirstOrDefault(f => f.Area == "knowledge" && f.Name == name);
        }
        catch (Exception ex) when (IsAdminFailure(ex))
        {
            Say(ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteFile()
    {
        if (SelectedFile is not { } file) return;
        if (!file.Deletable)
        {
            Say($"{file.Name} is protected");
            return;
        }
        if (!await ConfirmAsync($"Delete knowledge/{file.Name}? This cannot be undone.")) return;
        try
        {
            await _main.Admin.DeleteFileAsync(file.Name);
            SelectedFile = null;
            SetEditorText("");
            await LoadFiles();
            Say($"deleted {file.Name}");
        }
        catch (Exception ex) when (IsAdminFailure(ex))
        {
            Say(ex.Message);
        }
    }

    #endregion

    #region Private Methods

    private async Task OpenFile(ServerFile? file)
    {
        if (file == null) return;
        try
        {
            var text = await _main.Admin.GetFileAsync(file.Area, file.Name);
            SetEditorText(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            Say($"{file.Area}/{file.Name} · {file.Bytes} bytes");
        }
        catch (Exception ex) when (IsAdminFailure(ex))
        {
            Say(ex.Message);
        }
    }

    /// <summary>Replaces the editor's text without marking it dirty.</summary>
    private void SetEditorText(string text)
    {
        _loadingFile = true;
        EditorText = text;
        _loadingFile = false;
        Dirty = false;
    }

    partial void OnSelectedFileChanged(ServerFile? value) => _ = OpenFile(value);

    partial void OnEditorTextChanged(string value)
    {
        if (!_loadingFile) Dirty = true;
    }

    #endregion
}
