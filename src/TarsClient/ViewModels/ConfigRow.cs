namespace TarsClient.ViewModels;

/// <summary>One server <c>.env</c> field on the SERVER tab, with its edit state.</summary>
public sealed partial class ConfigRow : ObservableObject
{
    #region Properties

    /// <summary>The field as the server described it.</summary>
    public required ConfigField Field { get; init; }

    /// <summary>The variable name.</summary>
    public string Key => Field.Key;

    /// <summary>What it does.</summary>
    public string Description => Field.Description;

    /// <summary>The server only shows it masked.</summary>
    public bool Secret => Field.Secret;

    /// <summary>An on/off field.</summary>
    public bool IsBool => Field.Type == "bool";

    /// <summary>A field with a fixed set of values.</summary>
    public bool IsChoice => Field.Type.StartsWith("choice:", StringComparison.Ordinal);

    /// <summary>The allowed values of a choice field.</summary>
    public string[] Choices => IsChoice ? Field.Type["choice:".Length..].Split(',') : [];

    /// <summary>The value as loaded, to tell what changed.</summary>
    public string Original { get; set; } = "";

    /// <summary>The value (edited or not).</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = "";

    /// <summary>The inline editor is open.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    /// <summary>The inline editor's text.</summary>
    [ObservableProperty]
    public partial string EditText { get; set; } = "";

    /// <summary>The server saved a change that needs a restart.</summary>
    [ObservableProperty]
    public partial bool ServerPending { get; set; }

    /// <summary>Edited here and not yet applied.</summary>
    public bool Changed => Value != Original;

    /// <summary>Edited here, or waiting for a server restart.</summary>
    public bool Pending => Changed || ServerPending;

    /// <summary>What the row shows: ON/OFF for bools, the mask for untouched secrets.</summary>
    public string Display => (Secret, Changed, IsBool) switch
    {
        (true, false, _) => Value,
        (_, _, true) => Value is "1" or "true" or "True" ? "ON" : "OFF",
        (true, true, _) => "••••(new)",
        _ => Value,
    };

    #endregion

    #region Private Methods

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(Pending));
        OnPropertyChanged(nameof(Changed));
    }

    #endregion
}
