using System.Text.RegularExpressions;

namespace CrossWin.Mobile.Fixtures;

/// <summary>
/// Puts CrossWin's test data on an Android emulator or phone the way a real user's data would be there: photos in the
/// camera roll (indexed by the media scanner, so apps see their EXIF date and place), short videos, address-book
/// contacts with birthdays, files in Downloads — plus device state apps react to (location, date, time zone, locale,
/// photo access). Everything CrossWin adds is marked and removed again with <c>--reset</c>.
/// </summary>
public sealed class AndroidSeeder
{
    private const string PhotoDir = "/sdcard/DCIM/CrossWin";
    private const string FileDir = "/sdcard/Download/CrossWin";
    private const string ContactMarker = "crosswin-test";

    private readonly HostEnv _env;
    private readonly AndroidTarget _t;
    public AndroidSeeder(HostEnv env, AndroidTarget t) { _env = env; _t = t; }

    public sealed record Options(
        int? PhotoCount = null, bool Videos = true, bool Contacts = true, bool Files = true,
        DateTime? Date = null, string? TimeZone = null, string? Locale = null,
        string? Location = null, string? PhotoAccess = null, string? Package = null);

    public void Seed(Options o)
    {
        DateTime today = (o.Date ?? DateTime.Now).Date;
        if (o.Date is { } date) SetDate(date);
        if (o.TimeZone != null) SetTimeZone(o.TimeZone);
        if (o.Locale != null) SetLocale(o.Locale);

        Remove();
        string cache = Path.Combine(_env.Home, "fixtures", $"{today:yyyy-MM-dd}-{o.PhotoCount ?? 0}");
        var photos = Generate(cache, today, o.PhotoCount);
        Push(Path.Combine(cache, "photos"), PhotoDir);
        string user = Path.Combine(_env.Home, "fixtures", "user");
        int extra = 0;
        if (Directory.Exists(user) && Directory.EnumerateFiles(user).Any())
        {
            extra = Directory.EnumerateFiles(user).Count();
            Push(user, PhotoDir);
        }
        if (o.Videos) RecordVideos();
        int indexed = Scan(PhotoDir, photos.Count + extra);
        Console.WriteLine($"  photos: {photos.Count} generated{(extra > 0 ? $" + {extra} of yours" : "")} in {PhotoDir}, {indexed} indexed by the media library");

        if (o.Files)
        {
            Directory.CreateDirectory(Path.Combine(cache, "files"));
            File.WriteAllBytes(Path.Combine(cache, "files", "Invitation.pdf"), TestData.Pdf("Birthday party invitation"));
            File.WriteAllBytes(Path.Combine(cache, "files", "Holiday plan.pdf"), TestData.Pdf("Holiday plan"));
            File.WriteAllText(Path.Combine(cache, "files", "contacts.vcf"), TestData.VCards(TestData.Contacts(today)));
            foreach (var p in photos.Take(3)) File.Copy(Path.Combine(cache, "photos", p.FileName), Path.Combine(cache, "files", "Downloaded " + p.FileName), true);
            Push(Path.Combine(cache, "files"), FileDir);
            Scan(FileDir, 0);
            Console.WriteLine($"  files:  2 PDFs, a vCard and 3 images in {FileDir}");
        }
        if (o.Contacts)
        {
            var contacts = TestData.Contacts(today);
            foreach (var c in contacts) InsertContact(c);
            Console.WriteLine($"  contacts: {contacts.Count} with birthdays ({string.Join(", ", contacts.Select(c => $"{c.First} {c.Day:D2}.{c.Month:D2}."))})");
        }
        if (o.Location != null) SetLocation(o.Location);
        if (o.PhotoAccess != null && o.Package != null) SetPhotoAccess(o.Package, o.PhotoAccess);
    }

    public bool Seeded => _t.Shell("ls", PhotoDir).ExitCode == 0;

    /// <summary>Removes everything CrossWin seeded (photos, files, contacts).</summary>
    public void Remove()
    {
        _t.Shell("rm", "-rf", PhotoDir, FileDir);
        Scan(PhotoDir, 0);
        // Contacts carrying the CrossWin note.
        var q = _t.Shell("content", "query", "--uri", "content://com.android.contacts/data", "--projection", "raw_contact_id",
            "--where", $"\"data1='{ContactMarker}'\"");
        foreach (Match m in Regex.Matches(q.Output, @"raw_contact_id=(\d+)"))
            _t.Shell("content", "delete", "--uri", "content://com.android.contacts/raw_contacts", "--where", $"\"_id={m.Groups[1].Value}\"");
    }

    // ------------------------------------------------------------------ photos and videos

    private static List<TestData.Photo> Generate(string cache, DateTime today, int? count)
    {
        var photos = count is { } n ? Bulk(today, n) : TestData.Photos(today);
        string dir = Path.Combine(cache, "photos");
        Directory.CreateDirectory(dir);
        var missing = photos.Where(p => !File.Exists(Path.Combine(dir, p.FileName))).ToList();
        if (missing.Count > 0)
        {
            Console.WriteLine($"  generating {missing.Count} test photos ...");
            Parallel.ForEach(missing, p =>
            {
                string f = Path.Combine(dir, p.FileName);
                File.WriteAllBytes(f, TestData.Render(p));
                File.SetLastWriteTime(f, p.Taken);
            });
        }
        return photos;
    }

    /// <summary>A large library for performance tests: small photos spread over ten years, some days with many.</summary>
    private static List<TestData.Photo> Bulk(DateTime today, int n)
    {
        var rng = new Random(42);
        var list = new List<TestData.Photo>(n);
        for (int i = 0; i < n; i++)
        {
            var t = today.AddDays(-rng.Next(0, 3650)).Date.AddSeconds(rng.Next(7 * 3600, 22 * 3600) + i % 60);
            var place = TestData.Places[rng.Next(TestData.Places.Length)];
            list.Add(new TestData.Photo($"CWB_{t:yyyyMMdd_HHmmss}_{i:D5}.jpg", t, place, i % 3 == 0 ? 480 : 640, i % 3 == 0 ? 640 : 480));
        }
        return list;
    }

    private void Push(string localDir, string remoteDir)
    {
        _t.Shell("mkdir", "-p", remoteDir);
        // "dir/." pushes the contents; -a keeps the file times (the media scanner's fallback date).
        var r = _t.AdbCmd("push", "-a", "-q", Path.Combine(localDir, "."), remoteDir + "/");
        if (r.ExitCode != 0) throw new CrossWinException("adb push failed: " + r.Output.Trim());
    }

    /// <summary>Short screen recordings saved as camera-roll videos (the device's own H.264 encoder, nothing to download).</summary>
    private void RecordVideos()
    {
        _t.Shell("mkdir", "-p", PhotoDir);
        for (int i = 1; i <= 3; i++)
        {
            string file = $"{PhotoDir}/CW_VID_{DateTime.Now:yyyyMMdd}_{i}.mp4";
            // screenrecord only writes frames when the screen changes: keep it moving while recording.
            var rec = Task.Run(() => _t.Shell("screenrecord", "--time-limit", "4", "--size", "720x1280", file));
            for (int k = 0; k < 8 && !rec.IsCompleted; k++)
            {
                _t.Shell("input", "swipe", "540", k % 2 == 0 ? "1600" : "700", "540", k % 2 == 0 ? "700" : "1600", "400");
                Thread.Sleep(150);
            }
            rec.Wait();
        }
        Console.WriteLine("  videos: 3 short clips");
    }

    /// <summary>Has the media provider index <paramref name="dir"/>; returns how many images it now lists there.</summary>
    private int Scan(string dir, int expected)
    {
        _t.Shell("content", "call", "--uri", "content://media", "--method", "scan_volume", "--arg", "external_primary");
        int Count()
        {
            var r = _t.Shell("content", "query", "--uri", "content://media/external/images/media", "--projection", "_id",
                // MediaStore stores /storage/emulated/0/…, not the /sdcard alias.
                "--where", $"\"_data LIKE '%{dir.Replace("/sdcard", "")}/%'\"");
            return Regex.Matches(r.Output, @"_id=").Count;
        }
        for (int i = 0; i < 30 && expected > 0 && Count() < expected; i++) Thread.Sleep(1000);
        if (expected > 0 && Count() < expected)
        {
            // Older images: scan the files one by one.
            var files = _t.Shell("ls", dir).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string f in files)
                _t.Shell("am", "broadcast", "-a", "android.intent.action.MEDIA_SCANNER_SCAN_FILE", "-d", $"file://{dir}/{f}");
            Thread.Sleep(3000);
        }
        return expected > 0 ? Count() : 0;
    }

    // ------------------------------------------------------------------ contacts

    private void InsertContact(TestData.Contact c)
    {
        string before = MaxRawContact();
        _t.Shell("content", "insert", "--uri", "content://com.android.contacts/raw_contacts",
            "--bind", "account_type:n:", "--bind", "account_name:n:");
        string id = MaxRawContact();
        if (id == before) throw new CrossWinException("Could not create a contact on the device.");
        void Data(string mime, params string[] binds)
        {
            var args = new List<string> { "content", "insert", "--uri", "content://com.android.contacts/data",
                "--bind", $"raw_contact_id:i:{id}", "--bind", $"mimetype:s:{mime}" };
            foreach (string b in binds) { args.Add("--bind"); args.Add("'" + b.Replace("'", "'\\''") + "'"); }
            _t.Shell(args.ToArray());
        }
        Data("vnd.android.cursor.item/name", $"data1:s:{c.First} {c.Last}", $"data2:s:{c.First}", $"data3:s:{c.Last}");
        Data("vnd.android.cursor.item/phone_v2", $"data1:s:{c.Phone}", "data2:i:2");
        if (c.Email != null) Data("vnd.android.cursor.item/email_v2", $"data1:s:{c.Email}", "data2:i:1");
        // Contacts stores birthdays as YYYY-MM-DD, or --MM-DD without a year; type 3 = birthday.
        string bday = c.Year is { } y ? $"{y:D4}-{c.Month:D2}-{c.Day:D2}" : $"--{c.Month:D2}-{c.Day:D2}";
        Data("vnd.android.cursor.item/contact_event", $"data1:s:{bday}", "data2:i:3");
        Data("vnd.android.cursor.item/note", $"data1:s:{ContactMarker}");
    }

    private string MaxRawContact()
    {
        var r = _t.Shell("content", "query", "--uri", "content://com.android.contacts/raw_contacts", "--projection", "_id", "--sort", "\"_id DESC\"");
        var m = Regex.Match(r.Output, @"_id=(\d+)");
        return m.Success ? m.Groups[1].Value : "0";
    }

    // ------------------------------------------------------------------ device state

    /// <summary>"zurich", "rome", … (a fixture place) or "LAT,LON".</summary>
    private void SetLocation(string where)
    {
        var place = TestData.Places.FirstOrDefault(p => p.Name.Replace(" ", "").Equals(where.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
        (double lat, double lon) = place != null ? (place.Lat, place.Lon)
            : where.Split(',') is [var a, var b] && double.TryParse(a, System.Globalization.CultureInfo.InvariantCulture, out double la)
              && double.TryParse(b, System.Globalization.CultureInfo.InvariantCulture, out double lo) ? (la, lo)
            : throw new CrossWinException($"Unknown location '{where}': use a place ({string.Join(", ", TestData.Places.Select(p => p.Name.ToLowerInvariant()))}) or LAT,LON.");
        if (!_t.IsEmulator) throw new CrossWinException("--location needs the emulator (a real phone uses its own GPS).");
        _t.AdbCmd("emu", "geo", "fix", lon.ToString(System.Globalization.CultureInfo.InvariantCulture), lat.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Console.WriteLine($"  location: {lat:0.####}, {lon:0.####}");
    }

    private void Root()
    {
        if (!_t.IsEmulator) throw new CrossWinException("Changing date, time zone or language needs the emulator (on a real phone change them in Settings).");
        _t.AdbCmd("root"); // adbd restarts as root (emulator images allow it)
        Thread.Sleep(1500);
        _t.AdbCmd("wait-for-device");
    }

    private void SetDate(DateTime when)
    {
        Root();
        _t.Shell("settings", "put", "global", "auto_time", "0");
        var t = when.TimeOfDay == TimeSpan.Zero ? when.Date.AddHours(9) : when;
        _t.Shell("date", t.ToString("MMddHHmmyyyy.ss"));
        Console.WriteLine($"  date: {t:yyyy-MM-dd HH:mm} (automatic time off; 'crosswin mobile seed --date now' restores it)");
    }

    public void RestoreTime()
    {
        _t.Shell("settings", "put", "global", "auto_time", "1");
    }

    private void SetTimeZone(string tz)
    {
        Root();
        _t.Shell("settings", "put", "global", "auto_time_zone", "0");
        _t.Shell("service", "call", "alarm", "3", "s16", tz);
        _t.Shell("setprop", "persist.sys.timezone", tz);
        Console.WriteLine($"  time zone: {tz}");
    }

    /// <summary>System language and region (e.g. de-CH); restarts the Android framework (~20 s).</summary>
    private void SetLocale(string locale)
    {
        Root();
        _t.Shell("setprop", "persist.sys.locale", locale);
        _t.Shell("setprop", "ctl.restart", "zygote");
        Thread.Sleep(5000);
        for (int i = 0; i < 60 && _t.Shell("getprop", "sys.boot_completed").Output.Trim() != "1"; i++) Thread.Sleep(1000);
        Thread.Sleep(3000);
        _t.Shell("input", "keyevent", "82");
        Console.WriteLine($"  language/region: {locale}");
    }

    /// <summary>full, limited (Android 14 "selected photos") or none.</summary>
    private void SetPhotoAccess(string package, string mode)
    {
        string[] full = { "android.permission.READ_MEDIA_IMAGES", "android.permission.READ_MEDIA_VIDEO" };
        const string partial = "android.permission.READ_MEDIA_VISUAL_USER_SELECTED";
        switch (mode)
        {
            case "full":
                foreach (string p in full) _t.Shell("pm", "grant", package, p);
                break;
            case "limited":
                foreach (string p in full) _t.Shell("pm", "revoke", package, p);
                _t.Shell("pm", "grant", package, partial);
                break;
            case "none":
                foreach (string p in full.Append(partial)) _t.Shell("pm", "revoke", package, p);
                break;
            default:
                throw new CrossWinException("--photo-access is full, limited or none.");
        }
        Console.WriteLine($"  photo access for {package}: {mode}");
    }
}
