using System.Collections.Specialized;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using RawSpeed.App.Models;
using RawSpeed.App.Services;

namespace RawSpeed.App.ViewModels;

public sealed partial class EditorViewModel
{
    private const int ImportBatchSize = 64;
    private readonly Dispatcher _importDispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<string, List<PhotoItem>> _photoIndex = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _importCancellation;
    private bool _importIndexInitialized;
    private bool _appendingImportBatch;
    private bool _isImporting;
    private string _importStatus = "";

    public bool IsImporting
    {
        get => _isImporting;
        private set { if (SetProperty(ref _isImporting, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public string ImportStatus { get => _importStatus; private set => SetProperty(ref _importStatus, value); }

    /// <summary>Scans files/folders off the UI thread and publishes bounded, cancellable UI batches.</summary>
    public async Task AddFilesAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        _importDispatcher.VerifyAccess();
        if (_disposed) return;
        ArgumentNullException.ThrowIfNull(paths);
        EnsureImportIndex();
        CancelImport();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _importCancellation = cancellation;
        var token = cancellation.Token;
        int added = 0;
        bool selectedFirst = false;
        IsImporting = true;
        ImportStatus = "正在扫描照片…";
        try
        {
            await Task.Run(async () =>
            {
                var batch = new List<PhotoImportService.Photo>(ImportBatchSize);
                bool firstBatch = true;
                foreach (var photo in PhotoImportService.Enumerate(paths, _ratings, token))
                {
                    batch.Add(photo);
                    // Start the first preview before scanning/rating the rest of a large input.
                    if (firstBatch || batch.Count == ImportBatchSize)
                    {
                        await PublishAsync(batch);
                        batch.Clear();
                        firstBatch = false;
                    }
                }
                if (batch.Count > 0) await PublishAsync(batch);
            }, token);
            token.ThrowIfCancellationRequested();
            if (!selectedFirst) Error = "没有找到支持的文件。请打开 JPG、NEF 或 ORF。";
            ImportStatus = $"导入完成 · 新增 {added:N0} 张照片";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (_importCancellation == cancellation && !_disposed)
                ImportStatus = $"导入已取消 · 已保留 {added:N0} 张照片";
        }
        catch (Exception exception)
        {
            if (_importCancellation == cancellation && !_disposed)
            {
                ImportStatus = $"导入未完成 · 已保留 {added:N0} 张照片";
                ReportError(exception);
            }
        }
        finally
        {
            if (_importCancellation == cancellation)
            {
                _importCancellation = null;
                IsImporting = false;
            }
        }

        async Task PublishAsync(List<PhotoImportService.Photo> batch)
        {
            token.ThrowIfCancellationRequested();
            await _importDispatcher.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                if (_disposed || _importCancellation != cancellation) return;
                var first = AppendImportBatch(batch, ref added, token);
                token.ThrowIfCancellationRequested();
                if (!selectedFirst && first is not null)
                {
                    selectedFirst = true;
                    SelectedPhoto = first;
                }
                ImportStatus = $"正在导入 · 已添加 {added:N0} 张照片";
            }, DispatcherPriority.Background, token);
        }
    }

    // Kept for existing callers that require immediate collection/selection changes.
    // Application entry points use AddFilesAsync so disk work never runs on the UI thread.
    private void AddFilesSynchronously(IEnumerable<string> paths)
    {
        _importDispatcher.VerifyAccess();
        if (_disposed) return;
        EnsureImportIndex();
        CancelImport();
        int added = 0;
        var first = AppendImportBatch(PhotoImportService.Enumerate(paths, _ratings, CancellationToken.None), ref added);
        if (first is not null) SelectedPhoto = first;
        else Error = "没有找到支持的文件。请打开 JPG、NEF 或 ORF。";
    }

    private PhotoItem? AppendImportBatch(IEnumerable<PhotoImportService.Photo> photos, ref int added, CancellationToken token = default)
    {
        PhotoItem? first = null;
        _appendingImportBatch = true;
        try
        {
            foreach (var photo in photos)
            {
                token.ThrowIfCancellationRequested();
                PhotoItem item;
                if (_photoIndex.TryGetValue(photo.Path, out var existing)) item = existing.First();
                else
                {
                    item = new PhotoItem(photo.Path) { Rating = photo.Rating };
                    Photos.Add(item);
                    added++;
                }
                first ??= item;
            }
        }
        finally
        {
            _appendingImportBatch = false;
            OnPropertyChanged(nameof(PhotoCount));
        }
        return first;
    }

    private void EnsureImportIndex()
    {
        if (_importIndexInitialized) return;
        foreach (var photo in Photos) IndexPhoto(photo);
        Photos.CollectionChanged += ImportCollectionChanged;
        _importIndexInitialized = true;
    }

    private void ImportCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _photoIndex.Clear();
            foreach (var photo in Photos) IndexPhoto(photo);
        }
        else if (e.Action != NotifyCollectionChangedAction.Move)
        {
            if (e.OldItems is not null)
                foreach (PhotoItem photo in e.OldItems)
                {
                    string key = Path.GetFullPath(photo.Path);
                    if (_photoIndex.TryGetValue(key, out var items) && items.Remove(photo) && items.Count == 0)
                        _photoIndex.Remove(key);
                }
            if (e.NewItems is not null)
                foreach (PhotoItem photo in e.NewItems) IndexPhoto(photo);
        }
        if (!_appendingImportBatch) OnPropertyChanged(nameof(PhotoCount));
    }

    private void IndexPhoto(PhotoItem photo)
    {
        string key = Path.GetFullPath(photo.Path);
        if (!_photoIndex.TryGetValue(key, out var items)) _photoIndex[key] = items = [];
        items.Add(photo);
    }

    private void CancelImport() => _importCancellation?.Cancel();

    private void DisposeImport()
    {
        CancelImport();
        if (_importIndexInitialized) Photos.CollectionChanged -= ImportCollectionChanged;
        _photoIndex.Clear();
    }
}
