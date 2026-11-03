// CrossWin in-process agent, injected with DOTNET_STARTUP_HOOKS (the app is not modified).
// Wine's PrintWindow cannot render windows of another process, so capture happens here.
//
// Protocol (directory given by CROSSWIN_AGENT_DIR):
//   CrossWin writes  snap.request    -> agent writes snap-<n>-<title>.png for each visible
//                                       top-level window of this process, then snap.done
//   CrossWin writes  input.request   -> lines "click <x> <y>" (client-area pixels, as in snap PNGs) or
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
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    private const uint SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    /// <summary>
    /// The window input coordinates refer to: the largest titled top-level window (untitled ones are popups,
    /// tooltips or helper windows), and the screen position of its client origin.
    /// </summary>
    public static (IntPtr Handle, int Left, int Top) MainWindow()
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = -1;
        foreach (var (h, title) in VisibleWindows())
        {
            GetClientRect(h, out var r);
            long area = (long)r.Right * r.Bottom + (title.Length > 0 ? 1L << 40 : 0);
            if (area > bestArea) { best = h; bestArea = area; }
        }
        if (best == IntPtr.Zero) return (IntPtr.Zero, 0, 0);
        var origin = new POINT();
        ClientToScreen(best, ref origin);
        return (best, origin.X, origin.Y);
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

    /// <summary>Captures the client area of each window; image pixels match "click X Y" coordinates.</summary>
    public static string CaptureAll(string dir)
    {
        var report = new StringBuilder();
        int n = 0;
        foreach (var (h, title) in VisibleWindows())
        {
            GetClientRect(h, out var r);
            int w = r.Right, ht = r.Bottom;
            if (w <= 0 || ht <= 0) continue;
            var (bgra, method) = Render(h, w, ht);
            if (bgra == null) { report.AppendLine($"FAILED\t{title}"); continue; }
            string safe = string.Concat(title.Split(Path.GetInvalidFileNameChars()));
            string path = Path.Combine(dir, $"snap-{n++}-{safe}.png");
            File.WriteAllBytes(path, Png.Encode(bgra, w, ht));
            var origin = new POINT();
            ClientToScreen(h, ref origin);
            // Screen origin lets the host re-capture the same area (needed on X11, see Commands.RecaptureX11).
            report.AppendLine($"{path}\t{w}x{ht}\t{title}\t{method}\t{origin.X},{origin.Y}");
        }
        return report.ToString();
    }

    private static (byte[]?, string) Render(IntPtr hwnd, int w, int h)
    {
        // 1) Read back what the window has presented (Wine keeps a window surface). Works for GDI and for
        //    WPF software rendering on both the macOS and X11 drivers, unlike WM_PRINT which WPF ignores.
        byte[]? pixels = WithDib(w, h, dc =>
        {
            IntPtr src = GetDC(hwnd);
            bool ok = BitBlt(dc, 0, 0, w, h, src, 0, 0, SRCCOPY);
            ReleaseDC(hwnd, src);
            return ok;
        });
        if (pixels != null && !Uniform(pixels)) return (pixels, "window-dc");

        // 2) Ask the window (and each child, which Wine's X11 driver leaves out) to paint itself.
        pixels = WithDib(w, h, dc =>
        {
            bool ok = PrintWindow(hwnd, dc, 1 /* PW_CLIENTONLY */ | 2 /* PW_RENDERFULLCONTENT */);
            if (ok)
            {
                var origin = new POINT();
                ClientToScreen(hwnd, ref origin);
                ComposeChildren(dc, hwnd, origin);
            }
            return ok;
        });
        return (pixels, "print-window");
    }

    private static bool Uniform(byte[] bgra)
    {
        for (int i = 4; i < bgra.Length; i += 4)
            if (bgra[i] != bgra[0] || bgra[i + 1] != bgra[1] || bgra[i + 2] != bgra[2]) return false;
        return true;
    }

    private static byte[]? WithDib(int w, int h, Func<IntPtr, bool> draw)
    {
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, // top-down
            biPlanes = 1, biBitCount = 32,
        };
        IntPtr bmp = CreateDIBSection(dc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (bmp == IntPtr.Zero) { DeleteDC(dc); return null; }
        IntPtr old = SelectObject(dc, bmp);
        byte[]? result = null;
        if (draw(dc))
        {
            GdiFlush();
            result = new byte[w * h * 4];
            Marshal.Copy(bits, result, 0, result.Length);
        }
        SelectObject(dc, old);
        DeleteObject(bmp);
        DeleteDC(dc);
        return result;
    }

    private static void ComposeChildren(IntPtr target, IntPtr parent, POINT origin)
    {
        // GW_CHILD/GW_HWNDNEXT walk siblings top-most first; paint bottom-most first.
        var children = new List<IntPtr>();
        for (IntPtr c = GetWindow(parent, 5 /* GW_CHILD */); c != IntPtr.Zero; c = GetWindow(c, 2 /* GW_HWNDNEXT */))
            if (IsWindowVisible(c)) children.Add(c);
        children.Reverse();
        foreach (IntPtr c in children)
        {
            GetWindowRect(c, out var r);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) continue;
            WithDib(w, h, dc =>
            {
                if (PrintWindow(c, dc, 0))
                    BitBlt(target, r.Left - origin.X, r.Top - origin.Y, w, h, dc, 0, 0, SRCCOPY);
                return false;
            });
            ComposeChildren(target, c, origin);
        }
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
        Trace("input: find window");
        var (hwnd, left, top) = Snap.MainWindow();
        Trace($"input: window {hwnd} at {left},{top}");
        if (hwnd == IntPtr.Zero) return "ERROR no visible window";
        SetForegroundWindow(hwnd);
        Trace("input: foreground set");
        Thread.Sleep(150);
        var log = new StringBuilder();
        foreach (string raw in script.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("click "))
            {
                string[] p = line.Split(' ');
                int x = left + int.Parse(p[1]), y = top + int.Parse(p[2]);
                Trace($"click: SetCursorPos {x},{y}");
                SetCursorPos(x, y);
                Trace("click: cursor set");
                Thread.Sleep(50);
                Send(Mouse(MOUSEEVENTF_LEFTDOWN), Mouse(MOUSEEVENTF_LEFTUP));
                Trace("click: sent");
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

    // Opt-in diagnostics (CROSSWIN_AGENT_TRACE=1): step log in <session>/trace.log to locate hangs.
    internal static void Trace(string msg)
    {
        if (Environment.GetEnvironmentVariable("CROSSWIN_AGENT_TRACE") != "1") return;
        string? dir = Environment.GetEnvironmentVariable("CROSSWIN_AGENT_DIR");
        if (dir != null) File.AppendAllText(Path.Combine(dir, "trace.log"), $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
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
