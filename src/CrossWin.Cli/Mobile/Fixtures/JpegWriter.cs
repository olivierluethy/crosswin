using System.Text;

namespace CrossWin.Mobile.Fixtures;

/// <summary>
/// A small baseline JPEG encoder (4:4:4, standard Annex K tables) with an EXIF block carrying the capture time and
/// GPS position — what a phone's photo library reads to sort pictures by day and place. Pure C#, so test photos can
/// be made on any host without image libraries.
/// </summary>
public static class JpegWriter
{
    public sealed record Exif(DateTime Taken, double? Latitude = null, double? Longitude = null, string Model = "CrossWin Test Camera");

    /// <param name="rgb">Width × height × 3 bytes, row-major.</param>
    public static byte[] Encode(byte[] rgb, int width, int height, Exif exif, int quality = 88)
    {
        var o = new MemoryStream();
        o.Write(new byte[] { 0xFF, 0xD8 });
        WriteExif(o, exif, width, height);
        var (ql, qc) = Tables(quality);
        WriteDqt(o, ql, qc);
        WriteSof(o, width, height);
        WriteDht(o);
        WriteSos(o);
        var bw = new BitWriter(o);
        var dcL = Huff(DcLumBits, DcLumVals); var acL = Huff(AcLumBits, AcLumVals);
        var dcC = Huff(DcChrBits, DcChrVals); var acC = Huff(AcChrBits, AcChrVals);
        int prevY = 0, prevCb = 0, prevCr = 0;
        var y = new float[64]; var cb = new float[64]; var cr = new float[64];
        for (int by = 0; by < height; by += 8)
        for (int bx = 0; bx < width; bx += 8)
        {
            for (int j = 0; j < 8; j++)
            for (int i = 0; i < 8; i++)
            {
                int px = Math.Min(bx + i, width - 1), py = Math.Min(by + j, height - 1);
                int k = (py * width + px) * 3;
                float r = rgb[k], g = rgb[k + 1], b = rgb[k + 2];
                y[j * 8 + i] = 0.299f * r + 0.587f * g + 0.114f * b - 128;
                cb[j * 8 + i] = -0.168736f * r - 0.331264f * g + 0.5f * b;
                cr[j * 8 + i] = 0.5f * r - 0.418688f * g - 0.081312f * b;
            }
            prevY = Block(bw, y, ql, prevY, dcL, acL);
            prevCb = Block(bw, cb, qc, prevCb, dcC, acC);
            prevCr = Block(bw, cr, qc, prevCr, dcC, acC);
        }
        bw.Flush();
        o.Write(new byte[] { 0xFF, 0xD9 });
        return o.ToArray();
    }

    // ------------------------------------------------------------------ entropy coding

    private static readonly float[,] Cos = BuildCos();
    private static float[,] BuildCos()
    {
        var c = new float[8, 8];
        for (int u = 0; u < 8; u++)
        for (int x = 0; x < 8; x++)
            c[u, x] = (float)((u == 0 ? Math.Sqrt(0.5) : 1) / 2 * Math.Cos((2 * x + 1) * u * Math.PI / 16));
        return c;
    }

    private static int Block(BitWriter bw, float[] f, int[] q, int prevDc, (int Code, int Len)[] dc, (int Code, int Len)[] ac)
    {
        var tmp = new float[64];
        for (int v = 0; v < 8; v++) // rows: tmp[y][u] = Σx c[u,x] f[y][x]
        for (int u = 0; u < 8; u++)
        {
            float s = 0;
            for (int x = 0; x < 8; x++) s += Cos[u, x] * f[v * 8 + x];
            tmp[v * 8 + u] = s;
        }
        var coef = new int[64];
        for (int v = 0; v < 8; v++)
        for (int u = 0; u < 8; u++)
        {
            float s = 0;
            for (int yy = 0; yy < 8; yy++) s += Cos[v, yy] * tmp[yy * 8 + u];
            coef[v * 8 + u] = (int)MathF.Round(s / q[v * 8 + u]);
        }
        int dcVal = coef[0];
        int diff = dcVal - prevDc;
        int size = Category(diff);
        bw.Write(dc[size].Code, dc[size].Len);
        if (size > 0) bw.Write(Bits(diff, size), size);

        int run = 0;
        for (int i = 1; i < 64; i++)
        {
            int c = coef[ZigZag[i]];
            if (c == 0) { run++; continue; }
            while (run > 15) { bw.Write(ac[0xF0].Code, ac[0xF0].Len); run -= 16; }
            int s = Category(c);
            int sym = (run << 4) | s;
            bw.Write(ac[sym].Code, ac[sym].Len);
            bw.Write(Bits(c, s), s);
            run = 0;
        }
        if (run > 0) bw.Write(ac[0x00].Code, ac[0x00].Len);
        return dcVal;
    }

    private static int Category(int v) { v = Math.Abs(v); int n = 0; while (v > 0) { n++; v >>= 1; } return n; }
    private static int Bits(int v, int size) => v >= 0 ? v : (v - 1) & ((1 << size) - 1);

    private static (int, int)[] Huff(byte[] bits, byte[] vals)
    {
        var table = new (int, int)[256];
        int code = 0, k = 0;
        for (int len = 1; len <= 16; len++)
        {
            for (int i = 0; i < bits[len - 1]; i++) table[vals[k++]] = (code++, len);
            code <<= 1;
        }
        return table;
    }

    private sealed class BitWriter(Stream s)
    {
        private int _acc, _n;
        public void Write(int code, int len)
        {
            for (int i = len - 1; i >= 0; i--)
            {
                _acc = (_acc << 1) | ((code >> i) & 1);
                if (++_n == 8) Emit();
            }
        }
        private void Emit()
        {
            s.WriteByte((byte)_acc);
            if (_acc == 0xFF) s.WriteByte(0);
            _acc = 0; _n = 0;
        }
        public void Flush() { while (_n != 0) Write(1, 1); }
    }

    // ------------------------------------------------------------------ segments

    private static void Segment(Stream o, byte marker, byte[] body)
    {
        o.WriteByte(0xFF); o.WriteByte(marker);
        o.WriteByte((byte)((body.Length + 2) >> 8)); o.WriteByte((byte)(body.Length + 2));
        o.Write(body);
    }

    private static (int[], int[]) Tables(int quality)
    {
        int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        int[] Scale(int[] t) => t.Select(v => Math.Clamp((v * scale + 50) / 100, 1, 255)).ToArray();
        return (Scale(QLum), Scale(QChr));
    }

    private static void WriteDqt(Stream o, int[] ql, int[] qc)
    {
        var b = new List<byte> { 0 };
        for (int i = 0; i < 64; i++) b.Add((byte)ql[ZigZag[i]]);
        b.Add(1);
        for (int i = 0; i < 64; i++) b.Add((byte)qc[ZigZag[i]]);
        Segment(o, 0xDB, b.ToArray());
    }

    private static void WriteSof(Stream o, int w, int h) => Segment(o, 0xC0, new byte[]
    {
        8, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 3,
        1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1,
    });

    private static void WriteDht(Stream o)
    {
        var b = new List<byte>();
        void Table(byte cls, byte[] bits, byte[] vals) { b.Add(cls); b.AddRange(bits); b.AddRange(vals); }
        Table(0x00, DcLumBits, DcLumVals);
        Table(0x10, AcLumBits, AcLumVals);
        Table(0x01, DcChrBits, DcChrVals);
        Table(0x11, AcChrBits, AcChrVals);
        Segment(o, 0xC4, b.ToArray());
    }

    private static void WriteSos(Stream o) => Segment(o, 0xDA, new byte[] { 3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0 });

    // ------------------------------------------------------------------ EXIF (big-endian TIFF)

    private static void WriteExif(Stream o, Exif e, int w, int h)
    {
        string when = e.Taken.ToString("yyyy:MM:dd HH:mm:ss") + "\0";
        var ifd0 = new Ifd();
        ifd0.Ascii(0x010F, "CrossWin\0");
        ifd0.Ascii(0x0110, e.Model + "\0");
        ifd0.Short(0x0112, 1); // orientation: top-left
        ifd0.Ascii(0x0132, when);
        var exif = new Ifd();
        exif.Ascii(0x9003, when); // DateTimeOriginal
        exif.Ascii(0x9004, when); // DateTimeDigitized
        exif.Long(0xA002, (uint)w);
        exif.Long(0xA003, (uint)h);
        Ifd? gps = null;
        if (e.Latitude is { } lat && e.Longitude is { } lon)
        {
            gps = new Ifd();
            gps.Bytes(0x0000, new byte[] { 2, 3, 0, 0 });
            gps.Ascii(0x0001, lat >= 0 ? "N\0" : "S\0");
            gps.Rationals(0x0002, Dms(Math.Abs(lat)));
            gps.Ascii(0x0003, lon >= 0 ? "E\0" : "W\0");
            gps.Rationals(0x0004, Dms(Math.Abs(lon)));
        }
        ifd0.Long(0x8769, 0); // Exif IFD pointer, patched below
        if (gps != null) ifd0.Long(0x8825, 0);

        // Layout: header(8) | IFD0 | Exif IFD | GPS IFD, each followed by its out-of-line data.
        uint off0 = 8;
        uint offExif = off0 + ifd0.Size(off0);
        uint offGps = offExif + exif.Size(offExif);
        ifd0.Patch(0x8769, offExif);
        if (gps != null) ifd0.Patch(0x8825, offGps);

        var tiff = new List<byte> { (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8 };
        tiff.AddRange(ifd0.Bytes(off0));
        tiff.AddRange(exif.Bytes(offExif));
        if (gps != null) tiff.AddRange(gps.Bytes(offGps));
        var body = new List<byte>(Encoding.ASCII.GetBytes("Exif\0\0"));
        body.AddRange(tiff);
        Segment(o, 0xE1, body.ToArray());
    }

    private static (uint, uint)[] Dms(double deg)
    {
        int d = (int)deg;
        double mf = (deg - d) * 60;
        int m = (int)mf;
        double s = (mf - m) * 60;
        return new[] { ((uint)d, 1u), ((uint)m, 1u), ((uint)Math.Round(s * 1000), 1000u) };
    }

    private sealed class Ifd
    {
        private readonly List<(ushort Tag, ushort Type, uint Count, byte[] Data)> _e = new();
        private void Add(ushort tag, ushort type, uint count, byte[] data) => _e.Add((tag, type, count, data));
        public void Ascii(ushort t, string s) => Add(t, 2, (uint)s.Length, Encoding.ASCII.GetBytes(s));
        public void Short(ushort t, ushort v) => Add(t, 3, 1, new[] { (byte)(v >> 8), (byte)v });
        public void Long(ushort t, uint v) => Add(t, 4, 1, Be(v));
        public void Bytes(ushort t, byte[] v) => Add(t, 1, (uint)v.Length, v);
        public void Rationals(ushort t, (uint N, uint D)[] v) => Add(t, 5, (uint)v.Length, v.SelectMany(r => Be(r.N).Concat(Be(r.D))).ToArray());
        public void Patch(ushort t, uint v) { int i = _e.FindIndex(x => x.Tag == t); _e[i] = (t, 4, 1, Be(v)); }

        private int DataSize => _e.Where(x => x.Data.Length > 4).Sum(x => x.Data.Length + (x.Data.Length & 1));
        public uint Size(uint _) => (uint)(2 + 12 * _e.Count + 4 + DataSize);

        public byte[] Bytes(uint at)
        {
            var sorted = _e.OrderBy(x => x.Tag).ToList();
            var head = new List<byte> { (byte)(sorted.Count >> 8), (byte)sorted.Count };
            var data = new List<byte>();
            uint dataAt = at + (uint)(2 + 12 * sorted.Count + 4);
            foreach (var (tag, type, count, d) in sorted)
            {
                head.AddRange(new[] { (byte)(tag >> 8), (byte)tag, (byte)(type >> 8), (byte)type });
                head.AddRange(Be(count));
                if (d.Length <= 4) { head.AddRange(d); head.AddRange(new byte[4 - d.Length]); }
                else
                {
                    head.AddRange(Be(dataAt + (uint)data.Count));
                    data.AddRange(d);
                    if ((d.Length & 1) == 1) data.Add(0);
                }
            }
            head.AddRange(new byte[4]); // no next IFD
            head.AddRange(data);
            return head.ToArray();
        }
    }

    private static byte[] Be(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    // ------------------------------------------------------------------ standard tables (ITU T.81 Annex K)

    private static readonly int[] ZigZag =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    };

    private static readonly int[] QLum =
    {
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    };

    private static readonly int[] QChr = Enumerable.Range(0, 64).Select(i => (i / 8, i % 8) switch
    {
        (0, 0) => 17, (0, 1) or (1, 0) => 18, (0, 2) or (2, 0) => 24, (1, 1) => 21, (1, 2) or (2, 1) => 26,
        (0, 3) or (3, 0) => 47, (1, 3) or (3, 1) => 66, (2, 2) => 56, _ => 99,
    }).ToArray();

    private static readonly byte[] DcLumBits = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] DcLumVals = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
    private static readonly byte[] DcChrBits = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
    private static readonly byte[] DcChrVals = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
    private static readonly byte[] AcLumBits = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d };
    private static readonly byte[] AcLumVals =
    {
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08,
        0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
        0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6,
        0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    };
    private static readonly byte[] AcChrBits = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };
    private static readonly byte[] AcChrVals =
    {
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
        0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0, 0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
        0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4,
        0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    };
}
