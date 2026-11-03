namespace CrossWin;

public enum SupportLevel { Verified, Experimental, Unsupported }

/// <summary>
/// Honest support verdicts. "Verified" means CrossWin has run a comparable sample end to end on a real host,
/// not that every app of this kind works. See ARCHITECTURE.md evidence ledger.
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
            UiFramework.WinForms => new(SupportLevel.Verified, "Wine + self-contained Windows .NET runtime (WinForms sample verified)."),
            _ => new(SupportLevel.Verified, "Wine + self-contained Windows .NET runtime, WPF software rendering (WPF sample verified)."),
        };
    }
}
