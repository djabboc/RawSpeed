using System.IO;
using System.Windows.Media.Imaging;
using RawSpeed.App.Infrastructure;

namespace RawSpeed.App.Models;

public sealed class PhotoItem(string path) : ObservableObject
{
    private BitmapSource? _thumbnail;
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public string Kind => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
    public string Size => $"{new FileInfo(Path).Length / 1048576.0:F1} MB";
    public int OriginalWidth { get; set; }
    public int OriginalHeight { get; set; }
    public BitmapSource? Thumbnail { get => _thumbnail; set => SetProperty(ref _thumbnail, value); }
}
