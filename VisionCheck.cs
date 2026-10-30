using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenGuide;

// EXPLICIT TEST FIXTURES ONLY. Never used by normal startup or screenshot guidance.
// No desktop capture, saved user images, mouse operations, or application interaction.
internal static class VisionCheck
{
    internal static async Task<int> Run()
    {
        var config = new ModelProfileStore().LoadActiveOrLegacy(); if (!config.Ready) return 2;
        var mapper = new VisionMapper(config); var results = new List<object>(); int passed = 0;
        var settings = new Rect(70, 140, 170, 54); var find = new Rect(380, 140, 170, 54);
        var file = new Rect(30, 52, 42, 28); var create = new Rect(24, 92, 258, 36);
        var basic = Draw(false, false); var closedMenu = Draw(true, false); var openMenu = Draw(true, true);
        var cases = new (string Name, string Question, PixelSnapshot Image, Rect Expected, string Action, GuideMemory Memory)[] {
            ("read Settings from synthetic image", "设置在哪", basic, settings, "point", new([])),
            ("read Find from synthetic image", "查找在哪里", basic, find, "point", new([])),
            ("inspect File for new machine", "怎么创建虚拟机", closedMenu, file, "open_entry", new([])),
            ("continue to revealed menu row", "怎么创建虚拟机", openMenu, create, "point", new([new(1, "File", "ImageEntry", "当前截图", "open_entry", "clicked")])),
            ("unsupported request refused", "帮我写一首诗", basic, Rect.Empty, "not_found", new([]))
        };
        foreach (var c in cases)
        {
            var decision = await mapper.Select(c.Question, "synthetic-public-test", c.Image, c.Memory, CancellationToken.None);
            var box = decision.Box == null ? Rect.Empty : ScreenCapture.MapBox(c.Image, decision.Box);
            bool ok = decision.Action == c.Action && (c.Expected.IsEmpty ? !decision.CanPoint : decision.CanPoint &&
                !box.IsEmpty && c.Expected.Contains(new Point(box.X + box.Width / 2, box.Y + box.Height / 2)) && IntersectionOverUnion(c.Expected, box) >= 0.35);
            if (ok) passed++;
            results.Add(new { c.Name, passed = ok, decision.Action, decision.Confidence,
                bounds = box.IsEmpty ? null : box.ToString(CultureInfo.InvariantCulture), decision.Message });
            Log.Write("vision-check", $"case={results.Count} passed={ok}");
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "vision-check.json"), JsonSerializer.Serialize(new {
            date = DateTimeOffset.Now, model = config.Model,
            scope = "Live image API with code-drawn public test diagrams only; no desktop capture or VMware acceptance",
            passed, total = results.Count, results
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return passed == results.Count ? 0 : 1;
    }
    internal static double IntersectionOverUnion(Rect a, Rect b)
    {
        var intersection = Rect.Intersect(a, b);
        double area = intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
        return area / (a.Width * a.Height + b.Width * b.Height - area);
    }
    private static PixelSnapshot Draw(bool menus, bool expanded)
    {
        const int width = 640, height = 360;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.WhiteSmoke, null, new(0, 0, width, height));
            void Text(string value, double x, double y, double size = 18) => dc.DrawText(new FormattedText(value, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Brushes.Black, 1), new(x, y));
            void Button(Rect r, string label) { dc.DrawRectangle(Brushes.White, new Pen(Brushes.DimGray, 1), r); Text(label, r.X + 12, r.Y + 12); }
            Text("SYNTHETIC TEST - NOT A DESKTOP SCREENSHOT", 12, 12, 16);
            if (!menus)
            {
                Button(new(70, 140, 170, 54), "设置 Settings"); Button(new(380, 140, 170, 54), "查找 Find");
            }
            else
            {
                dc.DrawRectangle(Brushes.White, new Pen(Brushes.Gray, 1), new(0, 48, width, 38));
                Text("File", 32, 54); Text("Edit", 105, 54); Text("View", 175, 54); Text("Help", 252, 54);
                Text("Public diagram for menu continuation", 40, 240);
                if (expanded)
                {
                    dc.DrawRectangle(Brushes.White, new Pen(Brushes.Gray, 1), new(22, 86, 264, 120));
                    Text("New Virtual Machine...", 36, 98); Text("Open...", 36, 136); Text("Exit", 36, 174);
                }
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
        return new(0, new(0, 0, width, height), new(0, 0, width, height), width, height, pixels, []);
    }
}
