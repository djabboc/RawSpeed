using System.IO;
using System.Windows;
using Microsoft.Win32;
using RawSpeed.App.Models;
using RawSpeed.Imaging;

namespace RawSpeed.App.Views;

/// <summary>Collects and validates an export request. The caller performs rendering and file writing after acceptance.</summary>
public partial class ExportWindow : Window
{
    public ExportWindow(int sourceWidth, int sourceHeight, string? sourcePath = null)
    {
        InitializeComponent();
        Model = new ExportDialogModel(sourceWidth, sourceHeight, sourcePath);
        DataContext = Model;
    }

    public ExportDialogModel Model { get; }
    public ExportOptions Options { get; private set; } = new();
    public string DestinationPath { get; private set; } = "";

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        ActionError.Text = "";
        if (!Model.TryBuildOptions(out var options, out string error))
        {
            ActionError.Text = error;
            return;
        }
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "选择导出位置（保留原图）",
                FileName = (string.IsNullOrWhiteSpace(Model.SourcePath) ? "photo" : Path.GetFileNameWithoutExtension(Model.SourcePath)) + "-edited" + options.Extension,
                DefaultExt = options.Extension,
                Filter = options.Format switch
                {
                    ExportFormat.Jpeg => "JPEG 图像|*.jpg;*.jpeg",
                    ExportFormat.Png => "PNG 图像|*.png",
                    ExportFormat.Tiff => "TIFF 图像|*.tif;*.tiff",
                    _ => throw new ArgumentOutOfRangeException(),
                },
                AddExtension = true,
                CheckPathExists = true,
                OverwritePrompt = false,
            };
            if (!string.IsNullOrWhiteSpace(Model.SourcePath))
                dialog.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(Model.SourcePath));
            if (dialog.ShowDialog(this) != true) return;
            string destination = Path.GetFullPath(dialog.FileName);
            if (File.Exists(destination))
            {
                ActionError.Text = "目标文件已经存在。为保留现有照片，请选择一个新的文件名。";
                return;
            }
            if (ExportOptions.FormatFromPath(destination) != options.Format)
            {
                ActionError.Text = "文件扩展名与所选格式不一致，请重新选择文件名。";
                return;
            }
            Options = options;
            DestinationPath = destination;
            DialogResult = true;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            ActionError.Text = exception.Message;
        }
    }
}
