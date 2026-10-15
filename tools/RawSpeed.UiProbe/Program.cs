using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RawSpeed.App;
using RawSpeed.App.Controls;
using RawSpeed.App.ViewModels;

namespace RawSpeed.UiProbe;

internal static class Program
{
    private static int _exitCode;
    private static readonly List<string> Checks = [];
    private static readonly BindingTraceListener BindingTrace = new();

    [STAThread]
    private static int Main(string[] args)
    {
        string output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("artifacts", "ui"));
        Directory.CreateDirectory(output);
        string previousFailure = Path.Combine(output, "failure.txt");
        if (File.Exists(previousFailure)) File.Delete(previousFailure);
        var application = new RawSpeed.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.InitializeComponent();
        // Do not call Application.Run or Window.Show: the probe never creates a visible desktop window.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(BindingTrace);
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try { await ProbeAsync(output); }
            catch (Exception exception)
            {
                _exitCode = 1;
                Console.Error.WriteLine(exception);
                await File.WriteAllTextAsync(Path.Combine(output, "failure.txt"), exception.ToString());
            }
            finally
            {
                await File.WriteAllTextAsync(Path.Combine(output, "binding-trace.txt"), BindingTrace.Text);
                await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
                {
                    kind = "WPF in-process component rendering; not a desktop screenshot or system UI test",
                    timestampUtc = DateTimeOffset.UtcNow,
                    success = _exitCode == 0,
                    checks = Checks,
                    bindingTrace = BindingTrace.Text,
                    runtime = Environment.Version.ToString()
                }, new JsonSerializerOptions { WriteIndented = true }));
                // Application.Shutdown would close registered windows and run their Closing handlers.
                // Shut down only this probe's dispatcher, leaving all system-UI entry points unused.
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        }));
        Dispatcher.Run();
        return _exitCode;
    }

    private static async Task ProbeAsync(string output)
    {
        var window = new MainWindow();
        window.ApplyTemplate();
        Check(!window.IsVisible, "MainWindow initialized with real application resources without showing a window");
        var editor = window.DataContext as EditorViewModel ?? throw new InvalidOperationException("Unexpected MainWindow data context.");
        using (editor)
        {
            var content = window.Content as FrameworkElement ?? throw new InvalidOperationException("MainWindow content is missing.");
            // Detach only the content so collapsed, never-shown Window visibility cannot suppress rendering.
            // Keep the real MainWindow view model and event handlers attached to the actual XAML controls.
            window.Content = null;
            content.DataContext = editor;
            var host = new Border { Background = window.Background, Child = content };
            TextElement.SetFontFamily(host, window.FontFamily);
            TextElement.SetFontSize(host, window.FontSize);
            TextElement.SetForeground(host, window.Foreground);
            await LayoutAsync(host, 1480, 960);
            var buttons = Descendants<Button>(host).ToArray();
            var open = buttons.First(button => ReferenceEquals(button.Command, editor.OpenCommand));
            var save = buttons.First(button => ReferenceEquals(button.Command, editor.SaveCommand));
            var export = buttons.First(button => ReferenceEquals(button.Command, editor.ExportCommand));
            Check(open.IsEnabled && !save.IsEnabled && !export.IsEnabled, "Open/save/export bindings reflect the empty-state command permissions");
            SaveRender(host, 1480, 960, Path.Combine(output, "window-empty.png"));

            string fixture = Path.Combine(output, "ui-fixture.jpg");
            CreateFixture(fixture);
            editor.AddFiles([fixture]);
            await UntilAsync(() => !editor.IsLoading, "Loading generated JPEG");
            Check(editor.HasImage && editor.Preview is not null && !editor.HasError, "Real JPEG fixture decoded and rendered without a view-model error");
            BitmapSource? before = editor.Preview;
            editor.Exposure = 0.25;
            editor.Contrast = 16;
            editor.Saturation = 8;
            editor.CurveShadows = -16;
            editor.CurveMidtones = 14;
            editor.CurveHighlights = 7;
            await UntilAsync(() => editor.Preview is not null && !ReferenceEquals(before, editor.Preview), "Applying image adjustments");
            await LayoutAsync(host, 1480, 960);
            Check(save.IsEnabled && export.IsEnabled && editor.UndoCommand.CanExecute(null), "Loaded-state save/export/undo command bindings are enabled");

            var photo = (Image)window.FindName("PhotoImage");
            Check(photo.Source == editor.Preview && photo.ActualWidth > 100 && photo.ActualHeight > 100,
                "PhotoImage binds the current preview and receives a usable measured size");
            Check(editor.Photos[0].Thumbnail is { PixelWidth: <= 160, PixelHeight: <= 160 }, "Filmstrip uses a bounded thumbnail");
            CheckHeaderBounds(host, save, export, 1480, 960);

            var histogram = Descendants<HistogramView>(host).Single();
            var curve = Descendants<CurveView>(host).Single();
            Check(ReferenceEquals(histogram.Values, editor.Histogram) && histogram.Values is { Length: 768 }, "Histogram binding receives all three RGB channels");
            Check(curve.Settings == editor.Settings, "Curve preview receives the effective edit settings");
            Check(HasPaintedContent(SaveElementRender(histogram, Path.Combine(output, "histogram-render.png"))), "Histogram OnRender produces nonempty colored geometry");
            Check(HasPaintedContent(SaveElementRender(curve, Path.Combine(output, "curve-render.png"))), "Curve OnRender produces nonempty curve/grid geometry");
            SaveRender(host, 1480, 960, Path.Combine(output, "window-render.png"));

            var tabs = Descendants<TabControl>(host).Single();
            tabs.SelectedIndex = 1;
            await LayoutAsync(host, 1480, 960);
            var metadata = (ListView)window.FindName("MetadataList");
            Check(metadata.Items.Count > 0 && ReferenceEquals(metadata.ItemsSource, editor.MetadataView), "EXIF tab creates and binds its metadata list");
            var search = Descendants<TextBox>(host).First(textBox => BindingOperations.GetBinding(textBox, TextBox.TextProperty)?.Path.Path == nameof(EditorViewModel.MetadataSearch));
            search.Text = "a-field-that-does-not-exist-04ea";
            await LayoutAsync(host, 1480, 960);
            Check(editor.MetadataSearch == search.Text && metadata.Items.Count == 0, "EXIF search TextBox updates the view model and filters the list");
            search.Text = "";
            await LayoutAsync(host, 1480, 960);
            SaveRender(host, 1480, 960, Path.Combine(output, "window-exif.png"));
            tabs.SelectedIndex = 0;
            await LayoutAsync(host, 1080, 720);
            CheckHeaderBounds(host, save, export, 1080, 720);
            Check(photo.ActualWidth > 100 && photo.ActualHeight > 100, "Minimum supported layout retains a usable image viewport");
            SaveRender(host, 1080, 720, Path.Combine(output, "window-minimum.png"));

            Check(BindingTrace.Text.Length == 0, "No WPF binding warning/error was emitted while creating, editing, rendering and switching tabs");
            Check(!window.IsVisible, "MainWindow remained hidden throughout component verification");
            Check(window.Background is SolidColorBrush background && background.Color == Color.FromRgb(17, 20, 25),
                $"MainWindow applies its intended dark background (actual {window.Background}, explicit style {window.Style is not null})");
            var exportText = Descendants<TextBlock>(export).FirstOrDefault();
            Check(exportText is not null && exportText.Foreground is SolidColorBrush textBrush &&
                textBrush.Color == Color.FromRgb(22, 34, 13), "Primary action text inherits the intended dark foreground on its light green background");
            var fileLabels = Descendants<TextBlock>(host).Where(label => label.Text == Path.GetFileName(fixture)).ToArray();
            Check(fileLabels.Length >= 2 && fileLabels.All(label => label.Foreground is SolidColorBrush brush &&
                brush.Color.R > 150 && brush.Color.G > 150 && brush.Color.B > 150),
                "Both filmstrip and photo-header filenames retain readable light text on dark surfaces");
            Console.WriteLine($"PASS {Checks.Count} WPF component checks; renders saved to {output}");
        }
    }

    private static async Task LayoutAsync(FrameworkElement host, int width, int height)
    {
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        CommandManagerRefresh();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
    }

    private static void CommandManagerRefresh() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();

    private static void CheckHeaderBounds(FrameworkElement host, Button save, Button export, int width, int height)
    {
        foreach (var button in new[] { save, export })
        {
            Rect bounds = button.TransformToAncestor(host).TransformBounds(new Rect(button.RenderSize));
            Check(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= width && bounds.Bottom <= height && bounds.Width > 30 && bounds.Height > 15,
                $"Header action '{button.Content}' remains within the {width}×{height} component layout");
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        foreach (T child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static RenderTargetBitmap SaveElementRender(FrameworkElement element, string path)
    {
        int width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        // Isolate the same concrete drawing control and data from ScrollViewer clipping/margins.
        FrameworkElement isolated = element switch
        {
            HistogramView histogram => new HistogramView { Values = histogram.Values },
            CurveView curve => new CurveView { Settings = curve.Settings },
            _ => throw new NotSupportedException("Only the custom chart controls have isolated renders.")
        };
        isolated.Measure(new Size(width, height));
        isolated.Arrange(new Rect(0, 0, width, height));
        isolated.UpdateLayout();
        return SaveRender(isolated, width, height, path);
    }

    private static RenderTargetBitmap SaveRender(Visual visual, int width, int height, string path)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return bitmap;
    }

    private static bool HasPaintedContent(BitmapSource bitmap)
    {
        if (bitmap.PixelWidth < 20 || bitmap.PixelHeight < 20) return false;
        byte[] bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        var colors = new HashSet<uint>();
        for (int i = 0; i < bytes.Length; i += 4)
            if (bytes[i + 3] > 0) colors.Add((uint)(bytes[i] | bytes[i + 1] << 8 | bytes[i + 2] << 16 | bytes[i + 3] << 24));
        return colors.Count > 12;
    }

    private static void CreateFixture(string path)
    {
        const int width = 1200, height = 800;
        byte[] bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            double u = x / (double)width, v = y / (double)height;
            double mountain = 0.52 + 0.10 * Math.Sin(u * 9) + 0.06 * Math.Sin(u * 21);
            bool land = v > mountain;
            double glow = Math.Exp(-((u - 0.72) * (u - 0.72) + (v - 0.25) * (v - 0.25)) * 22);
            int i = (y * width + x) * 4;
            bytes[i] = (byte)Math.Clamp(land ? 55 + v * 37 : 150 + (1 - v) * 50 - glow * 45, 0, 255);
            bytes[i + 1] = (byte)Math.Clamp(land ? 53 + v * 45 : 115 + (1 - v) * 44 + glow * 80, 0, 255);
            bytes[i + 2] = (byte)Math.Clamp(land ? 35 + v * 35 : 90 + (1 - v) * 50 + glow * 150, 0, 255);
            bytes[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task UntilAsync(Func<bool> condition, string operation)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException(operation);
            await Task.Delay(10);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Checks.Add(message);
        Console.WriteLine("PASS " + message);
    }

    private sealed class BindingTraceListener : TraceListener
    {
        private readonly StringBuilder _text = new();
        public string Text { get { lock (_text) return _text.ToString(); } }
        public override void Write(string? message) { lock (_text) _text.Append(message); }
        public override void WriteLine(string? message) { lock (_text) _text.AppendLine(message); }
    }
}
