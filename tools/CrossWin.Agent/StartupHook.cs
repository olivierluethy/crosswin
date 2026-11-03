// CrossWin in-process agent, injected with DOTNET_STARTUP_HOOKS (the app is not modified).
// Wine's PrintWindow cannot render windows of another process, so capture happens here.
//
// Protocol (directory given by CROSSWIN_AGENT_DIR):
//   CrossWin writes  snap.request    -> agent writes snap-<n>-<title>.png for each visible
//                                       top-level window of this process, then snap.done
//   CrossWin writes  input.request   -> lines "click <x> <y>" (window-relative pixels) or
//                                       "type <text>", injected with SendInput,
//                                       or "wait <ms>"; then input.done
//   agent writes     agent.ready     once the hook is running
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal class StartupHook
{
    public static void Initialize()
    {
        string? dir = Environment.GetEnvironmentVariable("CROSSWIN_AGENT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var thread = new Thread(() => Loop(dir)) { IsBackground = true, Name = "CrossWin.Agent" };
        thread.Start();
    }

    private static void Loop(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "agent.ready"), Environment.ProcessId.ToString());
        while (true)
        {
            Thread.Sleep(200);
            Handle(dir, "snap", _ => Snap.CaptureAll(dir));
            Handle(dir, "input", Input.Run);
        }
    }

    private static void Handle(string dir, string name, Func<string, string> action)
    {
        string request = Path.Combine(dir, name + ".request");
        if (!File.Exists(request)) return;
        string body;
        try { body = File.ReadAllText(request); File.Delete(request); } catch (IOException) { return; }
        string report;
        try { report = action(body); }
        catch (Exception ex) { report = "ERROR " + ex; }
        File.WriteAllText(Path.Combine(dir, name + ".done"), report);
    }
}

internal static unsafe class Snap
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    public static (IntPtr Handle, int Left, int Top) MainWindow()
    {
        foreach (var (h, _) in VisibleWindows())
        {
            GetWindowRect(h, out var r);
            return (h, r.Left, r.Top);
        }
        return (IntPtr.Zero, 0, 0);
    }

    private static List<(IntPtr, string)> VisibleWindows()
    {
        uint self = (uint)Environment.ProcessId;
        var windows = new List<(IntPtr, string)>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid != self || !IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            windows.Add((h, sb.ToString()));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    public static string CaptureAll(string dir)
    {
        var windows = VisibleWindows();
        var report = new StringBuilder();
        int n = 0;
        foreach (var (h, title) in windows)
        {
            GetWindowRect(h, out var r);
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            if (w <= 0 || ht <= 0) continue;
            byte[]? bgra = Render(h, w, ht);
            if (bgra == null) { report.AppendLine($"FAILED\t{title}"); continue; }
            string safe = string.Concat(title.Split(Path.GetInvalidFileNameChars()));
            string path = Path.Combine(dir, $"snap-{n++}-{safe}.png");
            File.WriteAllBytes(path, Png.Encode(bgra, w, ht));
            report.AppendLine($"{path}\t{w}x{ht}\t{title}");
        }
        return report.ToString();
    }

    private static byte[]? Render(IntPtr hwnd, int w, int h)
    {
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, // top-down
            biPlanes = 1, biBitCount = 32,
        };
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr bmp = CreateDIBSection(dc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (bmp == IntPtr.Zero) { DeleteDC(dc); return null; }
        IntPtr old = SelectObject(dc, bmp);
        bool ok = PrintWindow(hwnd, dc, 2 /* PW_RENDERFULLCONTENT */);
        GdiFlush();
        byte[]? result = null;
        if (ok)
        {
            result = new byte[w * h * 4];
            Marshal.Copy(bits, result, 0, result.Length);
        }
        SelectObject(dc, old);
        DeleteObject(bmp);
        DeleteDC(dc);
        return result;
    }
}

internal static class Input
{
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public UNION u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct UNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }

    private const uint MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;

    public static string Run(string script)
    {
        var (hwnd, left, top) = Snap.MainWindow();
        if (hwnd == IntPtr.Zero) return "ERROR no visible window";
        SetForegroundWindow(hwnd);
        Thread.Sleep(150);
        var log = new StringBuilder();
        foreach (string raw in script.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("click "))
            {
                string[] p = line.Split(' ');
                int x = left + int.Parse(p[1]), y = top + int.Parse(p[2]);
                SetCursorPos(x, y);
                Thread.Sleep(50);
                Send(Mouse(MOUSEEVENTF_LEFTDOWN), Mouse(MOUSEEVENTF_LEFTUP));
            }
            else if (line.StartsWith("wait "))
            {
                // Two clicks closer than the double-click time are a double-click, as on Windows.
                Thread.Sleep(int.Parse(line.Substring(5)));
            }
            else if (line.StartsWith("type "))
            {
                foreach (char c in line.Substring(5))
                    Send(Key(c, KEYEVENTF_UNICODE), Key(c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
            }
            else if (line.Length > 0) { log.AppendLine("ignored: " + line); continue; }
            else continue;
            Thread.Sleep(150);
            log.AppendLine("ok: " + line);
        }
        return log.ToString();
    }

    private static INPUT Mouse(uint flags) => new() { type = 0, u = new UNION { mi = new MOUSEINPUT { dwFlags = flags } } };
    private static INPUT Key(char c, uint flags) => new() { type = 1, u = new UNION { ki = new KEYBDINPUT { wScan = c, dwFlags = flags } } };
    private static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
}

internal static class Png
{
    // Minimal RGB PNG encoder for BGRA input (GDI leaves alpha undefined, so it is dropped).
    public static byte[] Encode(byte[] bgra, int w, int h)
    {
        var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + w * 3];
            for (int y = 0; y < h; y++)
            {
                row[0] = 0;
                for (int x = 0; x < w; x++)
                {
                    int s = (y * w + x) * 4, d = 1 + x * 3;
                    row[d] = bgra[s + 2]; row[d + 1] = bgra[s + 1]; row[d + 2] = bgra[s];
                }
                z.Write(row);
            }
        }
        var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)w); WriteBE(ihdr, 4, (uint)h);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit truecolor
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBE(len, 0, (uint)data.Length); s.Write(len);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes); s.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        var c = new byte[4]; WriteBE(c, 0, crc); s.Write(c);
    }

    private static uint Crc(uint crc, byte[] data)
    {
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return crc;
    }

    private static void WriteBE(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }
}
