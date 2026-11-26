using System.IO;
using System.Windows;

namespace SanqiuTingji.App;

public partial class MainWindow
{
    private void InitMediaImport()
    {
        model.Imports.Changed += OnImportProgressChanged;
        model.Imports.Imported += OnMediaImported;
        model.Imports.Failed += OnMediaImportFailed;
        OnImportProgressChanged(model.Imports.Snapshot);
    }

    private void DisposeMediaImport()
    {
        model.Imports.Changed -= OnImportProgressChanged;
        model.Imports.Imported -= OnMediaImported;
        model.Imports.Failed -= OnMediaImportFailed;
    }

    private void ImportMedia_Click(object sender, RoutedEventArgs e)
    {
        var window = new MediaImportWindow(model) { Owner = this };
        window.ShowDialog();
    }

    private void CancelImport_Click(object sender, RoutedEventArgs e)
    {
        model.Imports.CancelCurrent();
    }

    private void OnImportProgressChanged(MediaImportSnapshot snap)
    {
        if (ImportTaskCard == null) return;
        if (!snap.IsActive)
        {
            ImportTaskCard.Visibility = Visibility.Collapsed;
            return;
        }
        ImportTaskCard.Visibility = Visibility.Visible;
        ImportTaskTitle.Text = snap.CurrentName;
        ImportTaskStage.Text = snap.StageText;
        ImportTaskDetail.Text = snap.DetailText;
        ImportTaskDetail.Visibility = string.IsNullOrWhiteSpace(snap.DetailText) ? Visibility.Collapsed : Visibility.Visible;
        ImportTaskBar.IsIndeterminate = snap.IsIndeterminate;
        if (!snap.IsIndeterminate) ImportTaskBar.Value = snap.Fraction;
        if (snap.PendingCount > 0)
        {
            ImportQueueBadgeText.Text = $"队列 +{snap.PendingCount}";
            ImportQueueBadge.Visibility = Visibility.Visible;
        }
        else
        {
            ImportQueueBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void OnMediaImported(string id, string title, int lineCount)
    {
        SearchBox.Clear();
        archiveScope = 0;
        ActiveScope.IsChecked = true;
        foreach (var group in treeGroups)
            if (group.Id == "") group.IsExpanded = true;
        selected = id;
        RefreshList();
        ShowDetail(id, keepTab: false);
        ShowGlassToast($"已导入「{title}」· 共 {lineCount} 句（原声录音后台保存中）");
    }

    private void OnMediaImportFailed(string message)
    {
        ShowGlassToast(message);
    }

    // MARK: Whole-window Drag & Drop Import

    private void MainWindow_DragEnter(object sender, DragEventArgs e) => HandleMainDrag(e);

    private void MainWindow_DragOver(object sender, DragEventArgs e) => HandleMainDrag(e);

    private void MainWindow_DragLeave(object sender, DragEventArgs e)
    {
        // Hide overlay only when cursor leaves the outer window bounds.
        var pt = e.GetPosition(this);
        if (pt.X <= 1 || pt.Y <= 1 || pt.X >= ActualWidth - 1 || pt.Y >= ActualHeight - 1)
            DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void HandleMainDrag(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var raw = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        var valid = MediaImportQueue.FilterSupportedFiles(raw);
        if (valid.Count > 0)
        {
            e.Effects = DragDropEffects.Copy;
            DropOverlayTitle.Text = valid.Count == 1
                ? $"松开导入「{Path.GetFileName(valid[0])}」"
                : $"松开批量导入 {valid.Count} 个音视频文件";
            DropOverlaySub.Text = "后台自动转写，不打断当前浏览 · 左侧边栏可查看实时进度与预计剩余时间";
            DropOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            DropOverlayTitle.Text = "暂不支持此文件格式";
            DropOverlaySub.Text = "请拖入 WAV、MP3、M4A、MP4、FLAC、MOV、MKV、WEBM 等音视频文件";
            DropOverlay.Visibility = Visibility.Visible;
        }
        e.Handled = true;
    }

    private void MainWindow_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        var raw = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        var valid = MediaImportQueue.FilterSupportedFiles(raw);
        if (valid.Count == 0)
        {
            ShowGlassToast("未检测到可导入的音视频文件（支持 WAV / MP3 / M4A / MP4 / FLAC 等）");
            return;
        }
        var count = model.Imports.EnqueueFiles(valid, model.Settings.SpeakerVoices);
        if (count > 0)
            ShowGlassToast(count == 1
                ? $"已加入后台导入：{Path.GetFileName(valid[0])}"
                : $"已加入后台导入队列：共 {count} 个文件");
    }
}
