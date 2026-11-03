namespace CrossWin;

public enum SupportLevel { Supported, Experimental, Unsupported }

/// <summary>
/// Honest support verdicts. "Supported" means CrossWin has run a comparable sample end to end on a real host,
/// not that this particular app has been tested. See ARCHITECTURE.md evidence ledger.
/// </summary>
public sealed record Assessment(SupportLevel Level, string Summary)
{
    public static Assessment For(ProjectInfo p)
    {
        if (!p.IsApp || p.Ui == UiFramework.None)
            return new(SupportLevel.Unsupported, "not a WinForms/WPF application project (library or non-UI).");
        if (p.IsNetFramework || !p.SdkStyle)
            return new(SupportLevel.Unsupported,
                ".NET Framework / legacy project files are not supported yet (needs Wine Mono or .NET Framework in Wine and a non-Windows build path). Modern .NET (6+) projects are supported.");
        int version = p.ChosenTfm is { } t ? ProjectInspector.ModernVersion(t) ?? 0 : 0;
        if (version < 6)
            return new(SupportLevel.Experimental, $"{p.ChosenTfm} is out of support; build may work, the CrossWin agent (snap/input) is disabled.");
        if (p.Findings.Count > 0)
            return new(SupportLevel.Experimental, "Wine + self-contained Windows .NET runtime; see risks below.");
        return p.Ui switch
        {
            UiFramework.WinForms => new(SupportLevel.Supported, "Wine + self-contained Windows .NET runtime; path verified with WinForms samples, not with this app."),
            _ => new(SupportLevel.Supported, "Wine + self-contained Windows .NET runtime, WPF software rendering; path verified with WPF samples, not with this app."),
        };
    }
}
