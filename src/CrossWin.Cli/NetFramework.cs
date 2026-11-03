using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CrossWin;

/// <summary>
/// .NET Framework (net4x) support: build legacy projects with the .NET SDK and run them on Wine Mono,
/// the open-source .NET Framework reimplementation that Wine uses for mscoree.
/// </summary>
public static class NetFramework
{
    private static readonly XNamespace Msb = "http://schemas.microsoft.com/developer/msbuild/2003";

    // Item types copied verbatim (with metadata) from a legacy project.
    private static readonly HashSet<string> ItemTypes = new()
    {
        "Compile", "Page", "ApplicationDefinition", "Resource", "EmbeddedResource", "None", "Content", "SplashScreen",
    };

    // Properties carried over unchanged when present.
    private static readonly string[] CopiedProperties =
    {
        "OutputType", "RootNamespace", "AssemblyName", "ApplicationIcon", "ApplicationManifest", "StartupObject",
        "AllowUnsafeBlocks", "LangVersion", "PlatformTarget", "Prefer32Bit", "AutoGenerateBindingRedirects",
        "DefineConstants",
    };

    /// <summary>
    /// Replaces every legacy (non-SDK) .csproj under <paramref name="root"/> — a CrossWin work copy, never the
    /// user's tree — with an equivalent SDK-style project. Files keep their paths, so ProjectReferences still
    /// resolve. Returns warnings about things that could not be translated.
    /// </summary>
    public static List<string> ShadowLegacyProjects(string root)
    {
        var warnings = new List<string>();
        foreach (string csproj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            var doc = XDocument.Load(csproj);
            if (doc.Root!.Attribute("Sdk") != null) continue;
            string rel = Path.GetRelativePath(root, csproj);
            File.WriteAllText(csproj, Shadow(doc, Path.GetDirectoryName(csproj)!, rel, warnings), new UTF8Encoding(false));
        }
        return warnings;
    }

    private static string Shadow(XDocument legacy, string dir, string rel, List<string> warnings)
    {
        var root = legacy.Root!;
        string? Prop(string name) => root.Descendants()
            .Where(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase) && !InConfigurationGroup(e, "Release"))
            .Select(e => e.Value.Trim()).LastOrDefault();

        string tfm = ToTfm(Prop("TargetFrameworkVersion") ?? "v4.7.2");
        var refs = root.Descendants(Msb + "Reference").ToList();
        bool wpf = refs.Any(r => ((string?)r.Attribute("Include") ?? "").StartsWith("PresentationFramework"));
        bool winforms = refs.Any(r => ((string?)r.Attribute("Include") ?? "").StartsWith("System.Windows.Forms"));

        var props = new XElement("PropertyGroup",
            new XElement("TargetFramework", tfm),
            new XElement("EnableDefaultItems", "false"),
            new XElement("GenerateAssemblyInfo", "false"),
            new XElement("EnableWindowsTargeting", "true"),
            // Legacy projects compile with the warning set of their era; do not fail on newer analyzers.
            new XElement("TreatWarningsAsErrors", "false"));
        if (wpf) props.Add(new XElement("UseWPF", "true"));
        if (winforms) props.Add(new XElement("UseWindowsForms", "true"));
        foreach (string name in CopiedProperties)
            if (Prop(name) is { Length: > 0 } value) props.Add(new XElement(name, value));

        var items = new XElement("ItemGroup");
        foreach (var element in root.Descendants().Where(e => e.Parent?.Name.LocalName == "ItemGroup"))
        {
            string type = element.Name.LocalName;
            string include = (string?)element.Attribute("Include") ?? "";
            if (ItemTypes.Contains(type))
            {
                items.Add(StripNamespace(element));
            }
            else if (type == "Reference")
            {
                string? hint = element.Elements().FirstOrDefault(e => e.Name.LocalName == "HintPath")?.Value;
                // DLLs from the old packages\ folder come back through packages.config -> PackageReference.
                if (hint != null && Regex.IsMatch(hint, @"(^|[\\/])packages[\\/]", RegexOptions.IgnoreCase)) continue;
                items.Add(hint == null
                    ? new XElement("Reference", new XAttribute("Include", include.Split(',')[0].Trim()))
                    : StripNamespace(element));
            }
            else if (type == "ProjectReference" || type == "PackageReference")
            {
                items.Add(StripNamespace(element));
            }
            else if (type == "COMReference")
            {
                warnings.Add($"{rel}: COMReference '{include}' skipped (COM interop is not supported).");
            }
        }

        string packagesConfig = Path.Combine(dir, "packages.config");
        if (File.Exists(packagesConfig))
        {
            foreach (var package in XDocument.Load(packagesConfig).Descendants("package"))
                items.Add(new XElement("PackageReference",
                    new XAttribute("Include", (string?)package.Attribute("id") ?? ""),
                    new XAttribute("Version", (string?)package.Attribute("version") ?? "")));
        }

        foreach (var import in root.Elements(Msb + "Import"))
        {
            string project = (string?)import.Attribute("Project") ?? "";
            if (!Regex.IsMatch(project, @"Microsoft\.(Common\.props|CSharp\.targets)$"))
                warnings.Add($"{rel}: custom import '{project}' skipped; build steps it adds will not run.");
        }

        var shadow = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XComment(" Generated by CrossWin from the legacy project file in this work copy; the original repo is unchanged. "),
            props, items);
        return shadow.ToString();
    }

    private static bool InConfigurationGroup(XElement e, string configuration)
    {
        string? condition = (string?)e.Parent?.Attribute("Condition");
        return condition != null && condition.Contains($"'{configuration}|", StringComparison.OrdinalIgnoreCase);
    }

    private static XElement StripNamespace(XElement e) => new(e.Name.LocalName,
        e.Attributes().Where(a => !a.IsNamespaceDeclaration),
        e.Nodes().Select(n => n is XElement child ? StripNamespace(child) : n));

    /// <summary>v4.7.2 -> net472, v4.8 -> net48, v4.0 -> net40, net48 stays net48.</summary>
    public static string ToTfm(string version)
    {
        if (version.StartsWith("net", StringComparison.OrdinalIgnoreCase)) return version;
        return "net" + version.TrimStart('v', 'V').Replace(".", "");
    }

    // ------------------------------------------------------------------ Wine Mono

    /// <summary>Wine Mono shipped with the Wine build or installed into the prefix.</summary>
    public static string? FindMono(HostEnv env)
    {
        foreach (string dir in MonoSearchDirs(env))
            if (Directory.Exists(dir) && Directory.EnumerateDirectories(dir, "wine-mono-*").Any()) return dir;
        string prefixMono = Path.Combine(env.Prefix, "drive_c", "windows", "mono", "mono-2.0");
        return Directory.Exists(prefixMono) ? prefixMono : null;
    }

    private static IEnumerable<string> MonoSearchDirs(HostEnv env)
    {
        if (env.Wine is { } wine)
            yield return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(wine)!, "..", "share", "wine", "mono"));
        yield return "/usr/share/wine/mono";
        yield return "/opt/wine/mono";
        yield return "/usr/local/share/wine/mono";
    }

    /// <summary>The Wine Mono version this Wine expects, read from appwiz.cpl (which installs it on Windows-side demand).</summary>
    public static string? RequiredMonoVersion(HostEnv env)
    {
        if (env.Wine is not { } wine) return null;
        string bin = Path.GetDirectoryName(Path.GetFullPath(wine))!;
        var roots = new[] { Path.Combine(bin, "..", "lib"), Path.Combine(bin, "..", "lib64"), "/usr/lib/wine", "/usr/lib/x86_64-linux-gnu/wine", "/usr/lib64/wine", "/opt/wine-stable/lib", "/opt/wine-devel/lib" };
        foreach (string r in roots.Where(Directory.Exists))
        {
            foreach (string cpl in Directory.EnumerateFiles(r, "appwiz.cpl", SearchOption.AllDirectories))
            {
                // The file name is stored as ASCII in some builds and only as UTF-16 in others (e.g. Ubuntu).
                byte[] bytes = File.ReadAllBytes(cpl);
                foreach (string text in new[] { Encoding.Latin1.GetString(bytes), Encoding.Unicode.GetString(bytes), Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1) })
                {
                    var m = Regex.Match(text, @"wine-mono-(\d+\.\d+\.\d+)-x86\.msi");
                    if (m.Success) return m.Groups[1].Value;
                }
            }
        }
        return null;
    }

    /// <summary>Downloads the matching Wine Mono MSI from WineHQ and installs it into the prefix.</summary>
    public static void InstallMono(HostEnv env)
    {
        string version = RequiredMonoVersion(env)
            ?? throw new CrossWinException("Could not determine which Wine Mono version this Wine needs (appwiz.cpl not found). Install your distribution's wine-mono package.");
        string msi = Path.Combine(env.Home, "dl", $"wine-mono-{version}-x86.msi");
        Directory.CreateDirectory(Path.GetDirectoryName(msi)!);
        if (!File.Exists(msi))
        {
            Console.WriteLine($"Downloading Wine Mono {version} (open-source .NET Framework for Wine, ~80 MB)...");
            var dl = Proc.Run("curl", new[] { "-fSL", "-o", msi, $"https://dl.winehq.org/wine/wine-mono/{version}/wine-mono-{version}-x86.msi" });
            if (dl.ExitCode != 0) { File.Delete(msi); throw new CrossWinException("Wine Mono download failed:\n" + dl.Output); }
        }
        Console.WriteLine($"Installing Wine Mono {version} into the prefix...");
        var r = Proc.Run(env.Wine!, new[] { "msiexec", "/i", HostEnv.ToWinePath(msi), "/qn" }, env.WineEnv());
        if (r.ExitCode != 0 || FindMono(env) == null)
            throw new CrossWinException("Wine Mono installation failed:\n" + r.Output);
    }
}
