// One-shot generator for src/VictusControl.App/app.ico.
// Usage: dotnet run --project tools/IconGen -- <output.ico>
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

string outPath = args.Length > 0 ? args[0] : Path.Combine("..", "..", "src", "VictusControl.App", "app.ico");

int[] sizes = [16, 20, 24, 32, 48, 256];

List<byte[]> blobs = [];
foreach (int size in sizes)
{
    using Bitmap bmp = Render(size);
    blobs.Add(size == 256 ? EncodePng(bmp) : EncodeDib(bmp));
}

using var fs = File.Create(outPath);
using var bw = new BinaryWriter(fs);

bw.Write((ushort)0);            // reserved
bw.Write((ushort)1);            // type: icon
bw.Write((ushort)sizes.Length); // image count

uint offset = (uint)(6 + 16 * sizes.Length);
for (int i = 0; i < sizes.Length; i++)
{
    int s = sizes[i];
    bw.Write((byte)(s >= 256 ? 0 : s));   // width
    bw.Write((byte)(s >= 256 ? 0 : s));   // height
    bw.Write((byte)0);                    // palette entries
    bw.Write((byte)0);                    // reserved
    bw.Write((ushort)1);                  // color planes
    bw.Write((ushort)32);                 // bits per pixel
    bw.Write((uint)blobs[i].Length);
    bw.Write(offset);
    offset += (uint)blobs[i].Length;
}
foreach (byte[] blob in blobs)
    bw.Write(blob);

Console.WriteLine($"Icon written: {Path.GetFullPath(outPath)} ({new FileInfo(outPath).Length} bytes)");

static Bitmap Render(int size)
{
    var bmp = new Bitmap(size, size);
    using Graphics g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.Clear(Color.Transparent);

    var rect = new Rectangle(0, 0, size, size);
    using var gradient = new LinearGradientBrush(rect, Color.FromArgb(34, 40, 52), Color.FromArgb(16, 18, 26), 45f);

    int corner = Math.Max(2, size * 22 / 100);
    using var path = new GraphicsPath();
    path.AddArc(rect.X, rect.Y, 2 * corner, 2 * corner, 180, 90);
    path.AddArc(rect.Right - 2 * corner, rect.Y, 2 * corner, 2 * corner, 270, 90);
    path.AddArc(rect.Right - 2 * corner, rect.Bottom - 2 * corner, 2 * corner, 2 * corner, 0, 90);
    path.AddArc(rect.X, rect.Bottom - 2 * corner, 2 * corner, 2 * corner, 90, 90);
    path.CloseFigure();

    g.FillPath(gradient, path);
    using var border = new Pen(Color.FromArgb(76, 201, 240), Math.Max(1f, size * 0.045f)) { Alignment = PenAlignment.Inset };
    g.DrawPath(border, path);

    PointF[] bolt =
    [
        new(0.585f * size, 0.20f * size),
        new(0.315f * size, 0.565f * size),
        new(0.505f * size, 0.565f * size),
        new(0.415f * size, 0.80f * size),
        new(0.685f * size, 0.435f * size),
        new(0.495f * size, 0.435f * size),
    ];
    using var fill = new SolidBrush(Color.FromArgb(132, 228, 255));
    g.FillPolygon(fill, bolt);

    return bmp;
}

static byte[] EncodePng(Bitmap bmp)
{
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

// ICO uncompressed DIB entry: BITMAPINFOHEADER (height doubled) + BGRA rows + AND mask.
static byte[] EncodeDib(Bitmap bmp)
{
    int w = bmp.Width, h = bmp.Height;
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);

    bw.Write(40u);              // biSize
    bw.Write(w);
    bw.Write(h * 2);
    bw.Write((ushort)1);        // planes
    bw.Write((ushort)32);       // bpp
    bw.Write(0u);               // compression
    bw.Write(w * h * 4);        // image size
    bw.Write(0); bw.Write(0);   // ppm
    bw.Write(0u); bw.Write(0u); // palette / important

    var rect = new Rectangle(0, 0, w, h);
    BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    byte[] row = new byte[w * 4];
    for (int y = h - 1; y >= 0; y--)
    {
        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, w * 4);
        bw.Write(row); // GDI+ ARGB in memory is already BGRA byte order
    }
    bmp.UnlockBits(data);

    byte[] andRow = new byte[((w + 31) >> 5) << 2];
    for (int y = 0; y < h; y++)
        bw.Write(andRow);

    bw.Flush();
    return ms.ToArray();
}
