using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CrossWin;

public enum UiFramework { None, WinForms, Wpf, Both }

public sealed record ProjectInfo(
    string Path,
    string Name,
    bool SdkStyle,
    IReadOnlyList<string> TargetFrameworks,
    UiFramework Ui,
    string OutputType,
    string Platform,
    bool Prefer32Bit,
    string AssemblyName,
    IReadOnlyList<string> Packages,
    IReadOnlyList<string> Findings)
{
    public bool IsApp => OutputType is "WinExe" or "Exe";

    /// <summary>The single TFM CrossWin builds: the highest modern .NET one, else the first.</summary>
    public string? ChosenTfm => TargetFrameworks
        .OrderByDescending(t => ProjectInspector.ModernVersion(t) ?? -1)
        .FirstOrDefault();

    public bool IsModernDotnet => ChosenTfm is { } t && ProjectInspector.ModernVersion(t) is >= 5;
    public bool IsNetFramework => ChosenTfm is { } t && (t.StartsWith("net4") || t.StartsWith("v4") || t.StartsWith("net3") || t.StartsWith("v3"));
}

/// <summary>Reads .csproj files without MSBuild evaluation (cheap, works before any SDK restore).</summary>
public static class ProjectInspector
{
    // Packages/APIs that have known reasons to fail or degrade under Wine. Reported, never hidden.
    private static readonly (string Pattern, string Message)[] RiskyPackages =
    {
        ("Microsoft.Web.WebView2", "WebView2 needs the Edge WebView2 runtime inside Wine; not provisioned by CrossWin (likely to fail)."),
        ("CefSharp", "CefSharp embeds Chromium; heavy native dependency, untested under CrossWin."),
        ("SharpDX", "Direct3D interop; depends on Wine's D3D->Vulkan/Metal translation (untested)."),
        ("Microsoft.Windows.SDK.Contracts", "WinRT APIs; many are not implemented by Wine."),
        ("System.Management", "WMI queries are only partially implemented by Wine."),
    };

    private static readonly (Regex Pattern, string Message)[] RiskySource =
    {
        (new Regex(@"(FontFamily\s*=\s*""|Value\s*=\s*""|FontFamily\(\s*"")[^""]*Segoe (MDL2 Assets|Fluent Icons)"), "Draws icons with a proprietary Windows icon font (Segoe MDL2 Assets / Segoe Fluent Icons) that Wine lacks; icons show as empty boxes. Copy segmdl2.ttf / SegoeIcons.ttf from a licensed Windows (C:\\Windows\\Fonts) into ~/.crosswin/fonts, or embed an open icon font in the app."),
        (new Regex(@"\bWebBrowser\b"), "Uses the WebBrowser control (IE/MSHTML); Wine needs Wine Gecko which CrossWin does not install yet."),
        (new Regex(@"\bD3DImage\b"), "Uses D3DImage (Direct3D interop in WPF); untested."),
        (new Regex(@"<MediaElement\b"), "Plays media with MediaElement (Windows Media Player / Media Foundation); usually missing under Wine."),
        (new Regex(@"\bHwndHost\b"), "Hosts native HWND content in WPF (HwndHost); may render incorrectly."),
        (new Regex(@"\bAllowsTransparency\s*=\s*""True""", RegexOptions.IgnoreCase), "WPF layered/transparent windows (AllowsTransparency) are a known weak spot under Wine."),
        (new Regex(@"DllImport\(\s*""(?!user32|kernel32|gdi32|shell32|ole32|oleaut32|comctl32|comdlg32|advapi32|dwmapi|uxtheme|winmm|ntdll|shcore)([^""]+)""", RegexOptions.IgnoreCase), "P/Invokes a non-system native DLL; it must be present as a Windows binary next to the app."),
    };

    public static IReadOnlyList<string> FindProjects(string path)
    {
        if (File.Exists(path) && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return new[] { System.IO.Path.GetFullPath(path) };
        if (!Directory.Exists(path)) return Array.Empty<string>();
        return Directory.EnumerateFiles(path, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Split(System.IO.Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj" or ".git" or "node_modules"))
            .OrderBy(p => p)
            .ToList();
    }

    public static ProjectInfo Inspect(string csproj)
    {
        var doc = XDocument.Load(csproj);
        var root = doc.Root!;
        // MSBuild property names are case-insensitive (UseWpf == UseWPF).
        string Prop(string name) => root.Descendants()
            .Where(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value.Trim()).LastOrDefault() ?? "";

        bool sdkStyle = root.Attribute("Sdk") != null || root.Elements().Any(e => e.Name.LocalName == "Sdk");
        var tfms = (Prop("TargetFrameworks") is { Length: > 0 } multi ? multi : Prop("TargetFramework"))
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (tfms.Count == 0 && Prop("TargetFrameworkVersion") is { Length: > 0 } legacy) tfms.Add(legacy);

        var references = root.Descendants().Where(e => e.Name.LocalName == "Reference")
            .Select(e => (string?)e.Attribute("Include") ?? "").ToList();
        bool wpf = Prop("UseWPF").Equals("true", StringComparison.OrdinalIgnoreCase)
            || references.Any(r => r.StartsWith("PresentationFramework"));
        bool winforms = Prop("UseWindowsForms").Equals("true", StringComparison.OrdinalIgnoreCase)
            || references.Any(r => r.StartsWith("System.Windows.Forms"));
        var ui = (wpf, winforms) switch
        {
            (true, true) => UiFramework.Both,
            (true, false) => UiFramework.Wpf,
            (false, true) => UiFramework.WinForms,
            _ => UiFramework.None,
        };

        var packages = root.Descendants().Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? "").Where(s => s.Length > 0).ToList();

        string dir = System.IO.Path.GetDirectoryName(csproj)!;
        var findings = new List<string>();
        foreach (var (pattern, message) in RiskyPackages)
            if (packages.Any(p => p.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)))
                findings.Add($"{pattern}: {message}");
        if (root.Descendants().Any(e => e.Name.LocalName == "COMReference"))
            findings.Add("COMReference: COM components must be registered inside the Wine prefix; CrossWin does not do this.");
        foreach (string t in tfms.Where(t => Regex.IsMatch(t, @"-windows10\.")))
            findings.Add($"{t}: targets a Windows 10 SDK (WinRT projections); WinRT calls may fail under Wine.");
        ScanSource(dir, findings);

        string platform = Prop("PlatformTarget") is { Length: > 0 } pt ? pt
            : Prop("Prefer32Bit").Equals("true", StringComparison.OrdinalIgnoreCase) ? "x86" : "AnyCPU";

        string name = System.IO.Path.GetFileNameWithoutExtension(csproj);
        return new ProjectInfo(csproj, name, sdkStyle, tfms, ui,
            Prop("OutputType") is { Length: > 0 } ot ? ot : "Library", platform,
            Prop("Prefer32Bit").Equals("true", StringComparison.OrdinalIgnoreCase),
            Prop("AssemblyName") is { Length: > 0 } an ? an : name, packages, findings);
    }

    private static void ScanSource(string dir, List<string> findings)
    {
        var seen = new HashSet<string>();
        var files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml"))
            .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}")
                     && !f.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}"))
            .Take(5000);
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            foreach (var (pattern, message) in RiskySource)
            {
                var m = pattern.Match(text);
                if (!m.Success) continue;
                string detail = m.Groups.Count > 1 && m.Groups[1].Success ? $"{message} ({m.Groups[1].Value})" : message;
                if (seen.Add(detail))
                    findings.Add($"{System.IO.Path.GetRelativePath(dir, file)}: {detail}");
            }
        }
    }

    /// <summary>net8.0-windows -> 8, netcoreapp3.1 -> 3, net48 -> null.</summary>
    public static int? ModernVersion(string tfm)
    {
        var m = Regex.Match(tfm, @"^net(\d+)\.\d+");
        if (m.Success) return int.Parse(m.Groups[1].Value);
        m = Regex.Match(tfm, @"^netcoreapp(\d+)\.");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }
}
