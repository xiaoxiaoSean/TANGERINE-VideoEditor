using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TANGERINE_VideoEditor;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

/// <summary>
/// Verifies observable title motion endpoints and safe-area/background geometry.
/// The STA entry point exercises the real WPF title rasterizer, including font
/// layout, and leaves a labeled contact sheet for visual review. No media encoder
/// or external test framework is required for this focused geometry regression.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string root = RepositoryRoot();
        string output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(root, "artifacts", "title-style-check");
        Directory.CreateDirectory(output);
        int count = 0;
        void Equal(double actual, double expected, string label)
        {
            if (!double.IsFinite(actual) || Math.Abs(actual - expected) > 1e-7)
                throw new InvalidOperationException($"TITLE-CHECK: {label}: {actual} != {expected}");
            count++;
        }
        ClipText Style(string id) => (ClipText)typeof(MainWindow)
            .GetMethod("CreateTitleStyle", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [id])!;
        TimelineClip Clip(string id) => new() { Kind = ClipKind.Text, TemplateId = id, DurationSeconds = 5, Text = Style(id) };

        TimelineClip crawl = Clip("title.crawl");
        Equal(DynamicTitlePreset.HorizontalOffset(crawl, 0, 1920), 1536, "crawl starts to the right");
        Equal(DynamicTitlePreset.HorizontalOffset(crawl, 2.5, 1920), 0, "crawl crosses its original position");
        Equal(DynamicTitlePreset.HorizontalOffset(crawl, 5, 1920), -1536, "crawl leaves to the left");
        crawl.Text!.Motion!.ScrollDirection = "Right";
        Equal(DynamicTitlePreset.HorizontalOffset(crawl, 0, 1920), -1536, "crawl direction reverses");

        TimelineClip zoom = Clip("title.zoom");
        Equal(DynamicTitlePreset.ScaleMultiplier(zoom, 0), .65, "zoom start");
        Equal(DynamicTitlePreset.ScaleMultiplier(zoom, 2.5), .975, "zoom midpoint");
        Equal(DynamicTitlePreset.ScaleMultiplier(zoom, 5), 1.3, "zoom end");
        zoom.Text!.Motion = new TitleMotion { DelaySeconds = 1, ZoomSeconds = 2, ZoomStartScale = 2, ZoomEndScale = .5, Easing = "Linear" };
        Equal(DynamicTitlePreset.ScaleMultiplier(zoom, .5), 2, "zoom waits before delay");
        Equal(DynamicTitlePreset.ScaleMultiplier(zoom, 2), 1.25, "zoom supports a decreasing scale");
        Equal(DynamicTitlePreset.ScaleMultiplier(zoom, 4), .5, "zoom holds its final scale");

        TimelineClip kinetic = Clip("title.kinetic");
        Equal(DynamicTitlePreset.VerticalOffset(kinetic, 0, 1080), 345.6, "kinetic starts below");
        Equal(DynamicTitlePreset.ScaleMultiplier(kinetic, 0), .45, "kinetic starts small");
        Equal(DynamicTitlePreset.VerticalOffset(kinetic, 1, 1080), 0, "kinetic reaches position");
        Equal(DynamicTitlePreset.ScaleMultiplier(kinetic, 1), 1, "kinetic reaches normal scale");
        TimelineClip lower = Clip("title.clean-lower-third");
        Point pos = TitleLayout.TextOrigin(lower.Text!, 1920, 1080, new Size(500, 100));
        Equal(pos.X, 115.2, "lower-third horizontal safe area");
        Equal(pos.Y, 872, "lower-third vertical safe area");

        TimelineClip box = Clip("title.box-reveal");
        Equal(DynamicTitlePreset.OpacityMultiplier(box, 0), 0, "box starts transparent");
        Equal(DynamicTitlePreset.OpacityMultiplier(box, 1), 1, "box becomes opaque");
        Equal(DynamicTitlePreset.OpacityMultiplier(box, 5), 0, "box ends transparent");
        Border bg = TitleLayout.Background(box.Text!, new Size(500, 100), 1, Brushes.Red)!;
        Equal(bg.Width, 576, "background horizontal padding");
        Equal(bg.Height, 140, "background vertical padding");
        Equal(bg.CornerRadius.TopLeft, 18, "background corner radius");
        ClipText roundTrip = JsonSerializer.Deserialize<ClipText>(JsonSerializer.Serialize(box.Text))!;
        Equal(roundTrip.BackgroundPaddingX, 38, "background settings survive JSON");
        Equal(roundTrip.Motion!.FadeInSeconds!.Value, .65, "motion settings survive JSON");

        // The quarter-time values distinguish the selectable interpolation
        // functions, so an implementation accidentally ignoring Easing fails.
        TimelineClip easing = Clip("title.zoom");
        easing.Text!.Motion = new TitleMotion { ZoomSeconds = 4, ZoomStartScale = 1, ZoomEndScale = 2 };
        foreach ((string id, double progress) in new[] { ("Linear", .25), ("EaseIn", .0625), ("EaseOut", .4375), ("Smooth", .15625) })
        {
            easing.Text.Motion.Easing = id;
            Equal(DynamicTitlePreset.ScaleMultiplier(easing, 1), 1 + progress, $"{id} curve at quarter time");
        }
        foreach (BuiltinPreset preset in BuiltinCatalog.Titles)
        {
            string path = Path.Combine(output, preset.Id + ".png");
            TitleBitmapRenderer.Render(Clip(preset.Id), new ProjectSequence { Width = 1920, Height = 1080 }, path);
            using FileStream stream = File.OpenRead(path);
            BitmapFrame frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Equal(frame.PixelWidth, 1920, preset.Id + " raster width");
            Equal(frame.PixelHeight, 1080, preset.Id + " raster height");
        }
        ContactSheet(output);
        Console.WriteLine($"PASS: {count} numeric/layout/raster checks; {BuiltinCatalog.Titles.Count} title PNGs");
        Console.WriteLine(Path.Combine(output, "title-styles.png"));
    }

    private static void ContactSheet(string output)
    {
        string[] ids = ["title.clean-lower-third", "title.box-reveal", "title.outline-pop", "title.crawl", "title.zoom", "title.kinetic", "title.credits", "title.handwritten", "title.quote"];
        const double width = 1920;
        double cellWidth = width / 3;
        double cellHeight = cellWidth * 9 / 16 + cellWidth * .065;
        var canvas = new Canvas { Width = width, Height = cellHeight * 3, Background = new SolidColorBrush(Color.FromRgb(19, 23, 32)) };
        for (int i = 0; i < ids.Length; i++)
        {
            double left = i % 3 * cellWidth;
            double top = i / 3 * cellHeight;
            var bitmap = new BitmapImage(new Uri(Path.Combine(output, ids[i] + ".png"), UriKind.Absolute));
            var image = new Image { Source = bitmap, Width = cellWidth, Height = cellWidth * 9 / 16 };
            Canvas.SetLeft(image, left); Canvas.SetTop(image, top); canvas.Children.Add(image);
            var label = new TextBlock
            {
                Text = BuiltinCatalog.Titles.First(preset => preset.Id == ids[i]).Name,
                FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = cellWidth * .035,
                Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(33, 38, 50))
            };
            Canvas.SetLeft(label, left + cellWidth * .025); Canvas.SetTop(label, top + cellWidth * 9 / 16);
            canvas.Children.Add(label);
        }
        canvas.Measure(new Size(canvas.Width, canvas.Height));
        canvas.Arrange(new Rect(0, 0, canvas.Width, canvas.Height));
        canvas.UpdateLayout();
        var raster = new RenderTargetBitmap((int)canvas.Width, (int)canvas.Height, 96, 96, PixelFormats.Pbgra32);
        raster.Render(canvas);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(raster));
        using FileStream file = File.Create(Path.Combine(output, "title-styles.png")); encoder.Save(file);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TANGERINE-VideoEditor.slnx")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git"))) return directory.FullName;
        throw new InvalidOperationException("TITLE-CHECK: run from a checkout or pass an output directory.");
    }
}
