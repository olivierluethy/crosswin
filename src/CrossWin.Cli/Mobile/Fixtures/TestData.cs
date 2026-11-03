namespace CrossWin.Mobile.Fixtures;

/// <summary>
/// The test data CrossWin puts on a device: photos that look like a real camera roll (trips grouped by day and
/// place, several years, "on this day" pictures, portrait/landscape/square), contacts with birthdays and a few
/// files. Everything is generated — no downloads, no third-party images — and deterministic for a given day.
/// </summary>
public static class TestData
{
    public sealed record Place(string Name, double Lat, double Lon, Scene Scene);
    public enum Scene { Lake, Alps, City, Beach, Forest, Sunset }

    public sealed record Photo(string FileName, DateTime Taken, Place Place, int Width, int Height);
    public sealed record Contact(string First, string Last, string Phone, string? Email, int? Year, int Month, int Day);

    public static readonly Place[] Places =
    {
        new("Zurich", 47.3769, 8.5417, Scene.Lake),
        new("Lucerne", 47.0502, 8.3093, Scene.Lake),
        new("Zermatt", 46.0207, 7.7491, Scene.Alps),
        new("Rome", 41.9028, 12.4964, Scene.City),
        new("Paris", 48.8566, 2.3522, Scene.City),
        new("Barcelona", 41.3874, 2.1686, Scene.Beach),
        new("Lisbon", 38.7223, -9.1393, Scene.Sunset),
        new("Black Forest", 48.0, 8.2, Scene.Forest),
        new("New York", 40.7128, -74.006, Scene.City),
        new("Tokyo", 35.6762, 139.6503, Scene.City),
    };

    /// <summary>~70 photos relative to <paramref name="today"/>: trips (5–9 photos a day), single days, this day in earlier years.</summary>
    public static List<Photo> Photos(DateTime today)
    {
        var rng = new Random(20261103);
        var list = new List<Photo>();
        Place P(string n) => Places.First(p => p.Name == n);
        void Day(DateTime day, Place place, int count)
        {
            var t = day.Date.AddHours(9 + rng.Next(0, 2));
            for (int i = 0; i < count; i++)
            {
                t = t.AddMinutes(12 + rng.Next(0, 70));
                (int w, int h) = (i % 5) switch { 1 => (900, 1200), 3 => (1080, 1080), 4 => (720, 1280), _ => (1200, 900) };
                list.Add(new Photo($"CW_{t:yyyyMMdd_HHmmss}.jpg", t, place, w, h));
            }
        }

        // Recent: today, yesterday, last week.
        Day(today, P("Zurich"), 3);
        Day(today.AddDays(-1), P("Zurich"), 4);
        Day(today.AddDays(-6), P("Black Forest"), 5);
        // A trip last month (several days, one place).
        for (int d = 0; d < 3; d++) Day(today.AddDays(-34 + d), P("Lisbon"), 6 + d);
        // Summer holiday this year and last year.
        Day(new DateTime(today.Year - (today.Month < 8 ? 1 : 0), 7, 14), P("Barcelona"), 9);
        Day(new DateTime(today.Year - 1, 8, 2), P("Zermatt"), 7);
        // City trips in earlier years.
        Day(new DateTime(today.Year - 2, 4, 19), P("Rome"), 8);
        Day(new DateTime(today.Year - 3, 10, 5), P("Paris"), 6);
        Day(new DateTime(today.Year - 3, 3, 11), P("New York"), 5);
        // "On this day": the same date in earlier years, and a few days ahead (upcoming memories).
        Day(today.AddYears(-1), P("Lucerne"), 6);
        Day(today.AddYears(-2), P("Tokyo"), 7);
        Day(today.AddYears(-3).AddDays(5), P("Lucerne"), 6);
        return list.OrderBy(p => p.Taken).ToList();
    }

    /// <summary>Contacts with birthdays: one in 3 days, one next week, one without a year, one on 29 February.</summary>
    public static List<Contact> Contacts(DateTime today)
    {
        DateTime In(int days) => today.Date.AddDays(days);
        var soon = In(3); var week = In(8); var month = In(25); var past = In(-10);
        return new()
        {
            new("Anna", "Meier", "+41 79 123 45 67", "anna.meier@example.com", 1988, soon.Month, soon.Day),
            new("Luca", "Bernasconi", "+41 76 234 56 78", null, 1995, week.Month, week.Day),
            new("Sofia", "Keller", "+41 78 345 67 89", "sofia@example.org", null, month.Month, month.Day),
            new("Jonas", "Weber", "+49 151 2345 6789", null, 1980, 2, 29), // a leap year: 29 Feb must exist
            new("Mia", "Rossi", "+39 333 123 4567", "mia.rossi@example.it", 2001, past.Month, past.Day),
            new("Grosspapi", "Huber", "+41 44 555 12 34", null, 1946, 12, 24),
        };
    }

    // ------------------------------------------------------------------ rendering

    /// <summary>A photo-like scene for the place, with the place and date printed in a corner.</summary>
    public static byte[] Render(Photo p)
    {
        int w = p.Width, h = p.Height;
        var px = new byte[w * h * 3];
        var rng = new Random(p.FileName.GetHashCode() & 0x7fffffff);
        var (skyTop, skyBottom, ground) = Palette(p.Place.Scene, p.Taken, rng);
        double horizon = 0.55 + rng.NextDouble() * 0.15;
        double sunX = 0.2 + rng.NextDouble() * 0.6, sunY = horizon - 0.15 - rng.NextDouble() * 0.2;
        double phase = rng.NextDouble() * 10;

        for (int y = 0; y < h; y++)
        {
            double fy = (double)y / h;
            for (int x = 0; x < w; x++)
            {
                double fx = (double)x / w;
                (double r, double g, double b) c;
                double ridge = Ridge(p.Place.Scene, fx, phase, horizon);
                if (fy < ridge)
                {
                    c = Mix(skyTop, skyBottom, fy / ridge);
                    double ds = Math.Sqrt((fx - sunX) * (fx - sunX) * (w / (double)h) * (w / (double)h) + (fy - sunY) * (fy - sunY));
                    if (ds < 0.05) c = (255, 244, 214);
                    else if (ds < 0.18) c = Mix((255, 230, 180), c, (ds - 0.05) / 0.13);
                }
                else
                {
                    double depth = (fy - ridge) / Math.Max(0.01, 1 - ridge);
                    c = Mix(ground.Item1, ground.Item2, depth);
                    if (p.Place.Scene is Scene.Lake or Scene.Beach && fy > horizon + 0.05 && fy < horizon + 0.25)
                        c = Mix(c, (90, 140, 190), 0.35 + 0.1 * Math.Sin(fx * 80 + fy * 40)); // water
                }
                if (p.Place.Scene == Scene.City && fy > horizon - 0.25 && fy < horizon)
                {
                    int col = (int)(fx * 22);
                    double top = horizon - 0.08 - ((col * 7919 + (int)phase) % 17) / 100.0;
                    bool window = (int)(fx * 220) % 4 == 0 && (int)(fy * 300) % 5 == 0;
                    if (fy > top) c = window ? (240, 210, 140) : Mix((55, 60, 75), (95, 98, 110), (fy - top) * 6);
                }
                double grain = (rng.NextDouble() - 0.5) * 10; // sensor noise, so it compresses like a photo
                int k = (y * w + x) * 3;
                px[k] = Byte(c.r + grain); px[k + 1] = Byte(c.g + grain); px[k + 2] = Byte(c.b + grain);
            }
        }
        string label = $"{p.Place.Name.ToUpperInvariant()}  {p.Taken:dd.MM.yyyy HH:mm}";
        int scale = Math.Max(2, w / 260);
        BitmapFont.Draw(px, w, h, label, 6 * scale, h - 12 * scale, scale);
        return JpegWriter.Encode(px, w, h, new JpegWriter.Exif(p.Taken, p.Place.Lat, p.Place.Lon));
    }

    private static double Ridge(Scene s, double fx, double phase, double horizon) => s switch
    {
        Scene.Alps => horizon - 0.25 + 0.18 * Math.Abs(Math.Sin(fx * 5 + phase)) * (0.6 + 0.4 * Math.Sin(fx * 13 + phase)),
        Scene.Forest => horizon - 0.08 + 0.04 * Math.Sin(fx * 90 + phase) + 0.03 * Math.Sin(fx * 23),
        Scene.Lake => horizon - 0.06 + 0.05 * Math.Sin(fx * 4 + phase),
        _ => horizon,
    };

    private static ((double, double, double), (double, double, double), ((double, double, double), (double, double, double))) Palette(Scene s, DateTime t, Random rng)
    {
        bool evening = t.Hour >= 17;
        return s switch
        {
            Scene.Sunset => ((52, 60, 120), (250, 150, 90), ((80, 60, 70), (40, 30, 40))),
            Scene.Beach => ((70, 150, 230), (180, 220, 245), ((235, 214, 165), (210, 185, 130))),
            Scene.Alps => ((60, 120, 200), (200, 225, 245), ((110, 140, 95), (60, 90, 55))),
            Scene.Forest => ((120, 170, 210), (210, 230, 235), ((45, 90, 50), (25, 55, 30))),
            Scene.City => evening ? ((40, 50, 100), (230, 140, 100), ((70, 70, 80), (40, 40, 48))) : ((90, 150, 220), (200, 220, 240), ((120, 115, 110), (80, 76, 72))),
            _ => ((80, 140, 215), (190, 215, 240), ((90, 130, 90), (50, 80, 55))),
        };
    }

    private static (double r, double g, double b) Mix((double r, double g, double b) a, (double r, double g, double b) b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return (a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t);
    }

    private static byte Byte(double v) => (byte)Math.Clamp((int)v, 0, 255);

    // ------------------------------------------------------------------ files and contacts

    /// <summary>A one-page PDF with a title, for import/share tests.</summary>
    public static byte[] Pdf(string title)
    {
        string text = title.Replace("(", "").Replace(")", "");
        string stream = $"BT /F1 28 Tf 72 720 Td ({text}) Tj ET\nBT /F1 14 Tf 72 690 Td (Test document generated by CrossWin) Tj ET";
        string[] objs =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objs.Length; i++) { offsets.Add(sb.Length); sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objs.Length + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objs.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>The contacts as one vCard 3.0 file (also usable on any phone or the preview).</summary>
    public static string VCards(IEnumerable<Contact> contacts) => string.Concat(contacts.Select(c =>
        "BEGIN:VCARD\r\nVERSION:3.0\r\n" +
        $"N:{c.Last};{c.First};;;\r\nFN:{c.First} {c.Last}\r\nTEL;TYPE=CELL:{c.Phone}\r\n" +
        (c.Email != null ? $"EMAIL:{c.Email}\r\n" : "") +
        $"BDAY:{(c.Year is { } y ? $"{y:D4}" : "--")}-{c.Month:D2}-{c.Day:D2}\r\nNOTE:crosswin-test\r\nEND:VCARD\r\n"));
}
