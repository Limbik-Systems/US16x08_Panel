#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property OutputType=Exe
#:property PublishAot=false

// Renders the flat dark-mode mixing-board icon for US16x08_Panel at every Windows icon size and packs
// them into a PNG-compressed .ico. Run from the project root:
//   dotnet run --file Assets\make_icon.cs -- Assets
// Writes Assets\US16x08_Panel.ico and Assets\US16x08_Panel_256.png (preview).
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

string outDir = args.Length > 0 ? args[0] : ".";
int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

// Panel palette (App.xaml)
Brush B(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
var bg = B("#1E1E1E");
var border = B("#3A3A3A");
var slot = B("#0C0C0C");
var tick = B("#4A4A4A");
var capLine = B("#141414");
var amber = B("#FFB000");
var cyan = B("#00D4FF");

byte[] RenderPng(int px)
{
    bool small = px <= 32;
    var dv = new DrawingVisual();
    using (var dc = dv.RenderOpen())
    {
        double s = px / 256.0;
        dc.PushTransform(new ScaleTransform(s, s));

        // Body: rounded square with a thin lighter border so it reads on dark taskbars
        double inset = small ? 8 : 10;
        dc.DrawRoundedRectangle(bg, new Pen(border, small ? 12 : 8), new Rect(inset, inset, 256 - 2 * inset, 256 - 2 * inset), 52, 52);

        // Faders: x centres, cap centre y, colour. Small sizes use 3 thicker faders.
        (double x, double y, Brush c)[] faders = small
            ? new[] { (68.0, 150.0, amber), (128.0, 92.0, cyan), (188.0, 132.0, amber) }
            : new[] { (62.0, 150.0, amber), (106.0, 96.0, amber), (150.0, 128.0, cyan), (194.0, 168.0, amber) };

        double slotW = small ? 18 : 10;
        double capW = small ? 52 : 38, capH = small ? 34 : 22;
        double top = small ? 50 : 48, bottom = small ? 206 : 208;

        foreach (var f in faders)
        {
            // Scale ticks beside each slot (large sizes only)
            if (!small)
                for (double ty = top + 12; ty <= bottom - 12; ty += 20)
                    dc.DrawRectangle(tick, null, new Rect(f.x - 19, ty - 1.5, 7, 3));

            dc.DrawRoundedRectangle(slot, null, new Rect(f.x - slotW / 2, top, slotW, bottom - top), slotW / 2, slotW / 2);
            dc.DrawRoundedRectangle(f.c, null, new Rect(f.x - capW / 2, f.y - capH / 2, capW, capH), small ? 7 : 5, small ? 7 : 5);
            if (!small)
                dc.DrawRectangle(capLine, null, new Rect(f.x - capW / 2 + 5, f.y - 1.5, capW - 10, 3));
        }
        dc.Pop();
    }

    var bmp = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(dv);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}

// WPF rendering objects must be created on an STA thread
byte[][] pngs = null!;
var sta = new Thread(() => pngs = sizes.Select(RenderPng).ToArray());
sta.SetApartmentState(ApartmentState.STA);
sta.Start();
sta.Join();
File.WriteAllBytes(Path.Combine(outDir, "US16x08_Panel_256.png"), pngs[^1]);
File.WriteAllBytes(Path.Combine(outDir, "US16x08_Panel_32.png"), pngs[Array.IndexOf(sizes, 32)]);

// ICO container: ICONDIR + ICONDIRENTRY[n] + PNG payloads (PNG entries are valid in Vista+ icons)
using (var fs = File.Create(Path.Combine(outDir, "US16x08_Panel.ico")))
using (var w = new BinaryWriter(fs))
{
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
        w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write((ushort)32);
        w.Write(pngs[i].Length); w.Write(offset);
        offset += pngs[i].Length;
    }
    foreach (var p in pngs) w.Write(p);
}
Console.WriteLine($"ICON_OK {string.Join(",", sizes)}");
