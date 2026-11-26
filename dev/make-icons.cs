#:property TargetFramework=net10.0-windows
#:property UseWindowsForms=true
#:property PublishAot=false
#:property Nullable=enable

// Draws CopyMaster's icon and writes every file made from it. Run from the repo folder:
//
//   dotnet run --file dev\make-icons.cs
//
//   assets\CopyMaster.ico          the exe's icon, the window's, and the DLL's menu items (16 to 256 px)
//   package\Assets\*.png           the logos the package manifest names (Windows shows none of them, since
//                                  the package has no Start menu entry, but the manifest needs them)
//
// The picture: a light sheet behind a blue one with a white arrow on it, a copy on its way. Drawn from
// shapes on a 256-unit grid at each size (no scaling of one bitmap), outlines at least a pixel wide, so
// the 16-pixel menu icon stays crisp. The outputs are committed; run this only to change the picture.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

if (!File.Exists("CopyMaster.slnx"))
{
    Console.Error.WriteLine("Run this from the repo folder (where CopyMaster.slnx is).");
    return 2;
}

int[] icoSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
var frames = icoSizes.Select(size => (size, image: Draw(size))).ToList();
Directory.CreateDirectory("assets");
WriteIco(Path.Combine("assets", "CopyMaster.ico"), frames);

string packageAssets = Path.Combine("package", "Assets");
Directory.CreateDirectory(packageAssets);
foreach (var (name, size) in new[] { ("Square44x44Logo.png", 44), ("Square150x150Logo.png", 150), ("StoreLogo.png", 50) })
{
    using Bitmap logo = Draw(size);
    logo.Save(Path.Combine(packageAssets, name), ImageFormat.Png);
}

if (args.Length > 0) // a preview strip, to look at: every size side by side, on dark and on light
{
    int width = icoSizes.Sum() + 10 * (icoSizes.Length + 1);
    using var strip = new Bitmap(width, 2 * (256 + 20));
    using var g = Graphics.FromImage(strip);
    g.FillRectangle(new SolidBrush(Color.FromArgb(0x20, 0x20, 0x20)), 0, 0, width, 276);
    g.FillRectangle(Brushes.White, 0, 276, width, 276);
    int x = 10;
    foreach (var (size, image) in frames)
    {
        g.DrawImageUnscaled(image, x, 10);
        g.DrawImageUnscaled(image, x, 286);
        x += size + 10;
    }
    strip.Save(args[0], ImageFormat.Png);
}

foreach (var (_, image) in frames) image.Dispose();
Console.WriteLine($"Wrote assets\\CopyMaster.ico ({string.Join(", ", icoSizes)} px) and {packageAssets}\\*.png.");
return 0;

static Bitmap Draw(int size)
{
    var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bitmap);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);
    float unit = size / 256f;
    float Px(float units) => units * unit;
    float line = Math.Max(1f, Px(10)); // outlines never thinner than a pixel

    // The sheet behind: light, with a dark edge so it shows on a light menu too.
    using (var back = RoundedRect(Px(26), Px(18), Px(140), Px(176), Px(20)))
    {
        g.FillPath(new SolidBrush(Color.FromArgb(0xE3, 0xEB, 0xF5)), back);
        g.DrawPath(new Pen(Color.FromArgb(0x4A, 0x64, 0x84), line), back);
    }
    // The sheet in front: CopyMaster blue.
    using (var front = RoundedRect(Px(92), Px(62), Px(140), Px(176), Px(20)))
    {
        g.FillPath(new SolidBrush(Color.FromArgb(0x1E, 0x7B, 0xD8)), front);
        g.DrawPath(new Pen(Color.FromArgb(0x0E, 0x4A, 0x96), line), front);
    }
    // The arrow on it, pointing right.
    float cy = Px(150);
    float shaft = Math.Max(1.5f, Px(28));
    using var arrow = new GraphicsPath();
    arrow.AddPolygon(new[]
    {
        new PointF(Px(116), cy - shaft / 2), new PointF(Px(162), cy - shaft / 2), new PointF(Px(162), cy - Px(38)),
        new PointF(Px(210), cy), new PointF(Px(162), cy + Px(38)), new PointF(Px(162), cy + shaft / 2),
        new PointF(Px(116), cy + shaft / 2),
    });
    g.FillPath(Brushes.White, arrow);
    return bitmap;
}

static GraphicsPath RoundedRect(float x, float y, float width, float height, float radius)
{
    var path = new GraphicsPath();
    float d = Math.Max(1f, radius * 2);
    path.AddArc(x, y, d, d, 180, 90);
    path.AddArc(x + width - d, y, d, d, 270, 90);
    path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
    path.AddArc(x, y + height - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}

// An .ico: 32-bit DIB frames (with their AND mask) up to 128 px, a PNG frame for 256, as Windows' own are.
static void WriteIco(string path, List<(int size, Bitmap image)> frames)
{
    var images = frames.Select(f => f.size >= 256 ? Png(f.image) : Dib(f.image)).ToList();
    using var file = new BinaryWriter(File.Create(path));
    file.Write((ushort)0); // reserved
    file.Write((ushort)1); // an icon
    file.Write((ushort)frames.Count);
    int offset = 6 + 16 * frames.Count;
    for (int i = 0; i < frames.Count; ++i)
    {
        int size = frames[i].size;
        file.Write((byte)(size >= 256 ? 0 : size));
        file.Write((byte)(size >= 256 ? 0 : size));
        file.Write((byte)0); // colours: not a palette
        file.Write((byte)0);
        file.Write((ushort)1); // planes
        file.Write((ushort)32); // bits a pixel
        file.Write(images[i].Length);
        file.Write(offset);
        offset += images[i].Length;
    }
    foreach (byte[] image in images) file.Write(image);
}

static byte[] Png(Bitmap image)
{
    using var stream = new MemoryStream();
    image.Save(stream, ImageFormat.Png);
    return stream.ToArray();
}

static byte[] Dib(Bitmap image)
{
    int size = image.Width;
    var data = image.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var pixels = new byte[size * size * 4]; // BGRA, top-down, straight alpha
    for (int y = 0; y < size; ++y) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * size * 4, size * 4);
    image.UnlockBits(data);

    int maskStride = (size + 31) / 32 * 4;
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write(40); // BITMAPINFOHEADER
    writer.Write(size);
    writer.Write(size * 2); // the colour image and the mask, one above the other
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write(0); // BI_RGB
    writer.Write(size * size * 4 + maskStride * size);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);
    for (int y = size - 1; y >= 0; --y) writer.Write(pixels, y * size * 4, size * 4); // bottom-up
    for (int y = size - 1; y >= 0; --y)
    {
        var row = new byte[maskStride];
        for (int x = 0; x < size; ++x)
        {
            if (pixels[(y * size + x) * 4 + 3] == 0) row[x / 8] |= (byte)(0x80 >> (x % 8)); // transparent
        }
        writer.Write(row);
    }
    writer.Flush();
    return stream.ToArray();
}
