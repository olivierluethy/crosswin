namespace CrossWin.Mobile;

public enum MobileOs { Android, Ios }

/// <summary>
/// A screen to test on. Android entries reproduce the named phone's screen geometry (pixels and density, hence
/// the dp size apps lay out against) on a stock Android emulator — not the vendor's firmware or skin. iOS entries
/// carry the model's point size and safe-area insets for the preview. Insets are in dp/pt.
/// </summary>
public sealed record MobileDevice(
    string Id,
    string Name,
    MobileOs Os,
    int WidthPx,
    int HeightPx,
    int Density,
    int TopInset,
    int BottomInset)
{
    public int LogicalWidth => Os == MobileOs.Android ? WidthPx * 160 / Density : WidthPx / Density;
    public int LogicalHeight => Os == MobileOs.Android ? HeightPx * 160 / Density : HeightPx / Density;
    public double PixelRatio => Os == MobileOs.Android ? Density / 160.0 : Density;

    /// <summary>Logical size apps see (Android dp; iOS points).</summary>
    public string LogicalSize => $"{LogicalWidth}x{LogicalHeight} {(Os == MobileOs.Android ? "dp" : "pt")}";
}

public static class DeviceCatalog
{
    // Android densities must be values the emulator accepts (120 … 640).
    public static readonly IReadOnlyList<MobileDevice> All = new MobileDevice[]
    {
        new("pixel-8", "Google Pixel 8", MobileOs.Android, 1080, 2400, 420, 32, 24),
        new("pixel-8-pro", "Google Pixel 8 Pro", MobileOs.Android, 1344, 2992, 480, 36, 24),
        new("galaxy-s24", "Samsung Galaxy S24", MobileOs.Android, 1080, 2340, 480, 34, 24),
        new("galaxy-s24-ultra", "Samsung Galaxy S24 Ultra (FHD+)", MobileOs.Android, 1080, 2340, 450, 34, 24),
        new("galaxy-a15", "Samsung Galaxy A15", MobileOs.Android, 1080, 2340, 450, 34, 48),
        new("moto-g54", "Motorola moto g54", MobileOs.Android, 1080, 2400, 400, 32, 24),
        new("moto-e13", "Motorola moto e13 (HD+)", MobileOs.Android, 720, 1600, 280, 28, 48),
        new("small-360", "Small Android 360x640 dp", MobileOs.Android, 720, 1280, 320, 24, 48),
        new("galaxy-tab-s9", "Samsung Galaxy Tab S9", MobileOs.Android, 1600, 2560, 320, 24, 24),

        // iOS: Density is the point scale (@2x/@3x).
        new("iphone-se", "iPhone SE (3rd generation)", MobileOs.Ios, 750, 1334, 2, 20, 0),
        new("iphone-16", "iPhone 16", MobileOs.Ios, 1179, 2556, 3, 59, 34),
        new("iphone-16-pro", "iPhone 16 Pro", MobileOs.Ios, 1206, 2622, 3, 62, 34),
        new("iphone-16-pro-max", "iPhone 16 Pro Max", MobileOs.Ios, 1320, 2868, 3, 62, 34),
        new("ipad-air", "iPad Air 11-inch (M2)", MobileOs.Ios, 1640, 2360, 2, 24, 20),
    };

    public static IReadOnlyList<MobileDevice> Android => All.Where(d => d.Os == MobileOs.Android).ToList();
    public static IReadOnlyList<MobileDevice> Ios => All.Where(d => d.Os == MobileOs.Ios).ToList();

    /// <summary>"pixel-8,galaxy-s24", "all", "android", "ios" or "phones" (no tablets).</summary>
    public static List<MobileDevice> Resolve(string spec)
    {
        var result = new List<MobileDevice>();
        foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            IEnumerable<MobileDevice> pick = part.ToLowerInvariant() switch
            {
                "all" => All,
                "android" => Android,
                "ios" => Ios,
                "phones" => All.Where(d => !d.Id.Contains("tab") && !d.Id.Contains("ipad")),
                _ => All.Where(d => d.Id.Equals(part, StringComparison.OrdinalIgnoreCase)) is var m && m.Any()
                    ? m
                    : throw new CrossWinException($"Unknown device '{part}'. Run 'crosswin mobile devices' for the list."),
            };
            foreach (var d in pick) if (!result.Contains(d)) result.Add(d);
        }
        return result;
    }

    /// <summary>Stable emulator console port per Android device (5554, 5556, …), so serials never clash.</summary>
    public static int Port(MobileDevice d) => 5554 + 2 * Android.ToList().IndexOf(d);
}
