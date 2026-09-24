namespace TarsClient.ViewModels;

/// <summary>What a scrollback line is, which picks its colour.</summary>
public enum LineKind
{
    /// <summary>What you said or typed.</summary>
    User,

    /// <summary>A reply from TARS.</summary>
    Tars,

    /// <summary>Details: tools, timings, ignored speech.</summary>
    Meta,

    /// <summary>Something went wrong.</summary>
    Error,

    /// <summary>Boot and connection lines.</summary>
    System,
}
