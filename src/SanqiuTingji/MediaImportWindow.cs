using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SanqiuTingji.Localization;

namespace SanqiuTingji.App;

/// Lightweight non-blocking intake dialog: pick or drop one/more local media files,
/// or paste a video link, optionally toggle speaker diarization, then hand off to
/// the background MediaImportQueue and close immediately so the main window stays free.
public sealed class MediaImportWindow : Window
{
    private readonly AppModel model;
    private readonly TextBox link = new();
    private readonly CheckBox speakersCheck = new();
    private readonly TextBlock hintNote = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = Theme.Warn,
        FontSize = 12,
        Margin = new(2, 10, 0, 0),
        Visibility = Visibility.Collapsed,
    };
    private readonly Button fileBtn = new() { Padding = new(22, 8, 22, 8), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button downloadBtn = new() { Padding = new(16, 7, 16, 7), Margin = new(8, 0, 0, 0), IsEnabled = false };
    private readonly Button closeBtn = new() { Padding = new(18, 6, 18, 6), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 16, 0, 0) };
    private readonly Border drop = new();
    private readonly System.Windows.Shapes.Rectangle outline = new()
    {
        Stroke = Theme.Line,
        StrokeThickness = 1.5,
        StrokeDashArray = [4, 3],
        RadiusX = 12,
        RadiusY = 12,
        IsHitTestVisible = false,
    };

    public MediaImportWindow(AppModel model)
    {
        this.model = model;
        Width = 530;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)FindResource("Ground");
        Foreground = Theme.Ink;
        Icon = Icons.Mark(64, false);
        AllowDrop = true;
        Native.ApplyLiquidGlass(this, dark: false);
        UiText.Set(this, "Title", () => $"{Brand.Name} · 导入音视频");

        var panel = new StackPanel { Margin = new(24, 22, 24, 20) };
        var heading = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold };
        UiText.Set(heading, "Text", () => L.T("导入音视频"));
        panel.Children.Add(heading);
        var sub = new TextBlock { Style = (Style)FindResource("Helper"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 16) };
        sub.Text = "投放后自动转入后台识别，不挡主窗口，支持一次选择或拖入多个文件";
        panel.Children.Add(sub);

        // Whole-window drop target visually anchored on the dashed card.
        var inside = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        inside.Children.Add(new TextBlock
        {
            Text = "\uE8B5",
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 28,
            Foreground = Theme.Primary,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new(0, 0, 0, 12),
        });
        fileBtn.Style = (Style)FindResource("PrimaryBtn");
        fileBtn.Content = "选择本地文件（可多选）";
        inside.Children.Add(fileBtn);
        var dropHint = new TextBlock
        {
            FontSize = 12,
            Foreground = Theme.Grey2,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new(0, 10, 0, 0),
            Text = "直接拖入此窗口任意位置即可 · 支持 WAV、MP3、M4A、MP4、FLAC 等",
        };
        inside.Children.Add(dropHint);
        drop.Child = inside;
        drop.Background = System.Windows.Media.Brushes.White;
        drop.CornerRadius = new CornerRadius(12);
        drop.Padding = new Thickness(20, 24, 20, 22);

        var target = new Grid();
        target.Children.Add(drop);
        target.Children.Add(outline);
        panel.Children.Add(target);

        // Speed/accuracy option: speaker diarization toggle.
        speakersCheck.IsChecked = model.Settings.SpeakerVoices;
        speakersCheck.Margin = new Thickness(2, 14, 0, 0);
        speakersCheck.Content = "同时区分发言人（单人录音或赶时间可取消，识别提速约 2～3 倍）";
        speakersCheck.FontSize = 12.5;
        speakersCheck.Foreground = Theme.Ink;
        panel.Children.Add(speakersCheck);

        // Or paste a video link.
        var or = new Grid { Margin = new(0, 16, 0, 10) };
        or.ColumnDefinitions.Add(new ColumnDefinition());
        or.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        or.ColumnDefinitions.Add(new ColumnDefinition());
        var orText = new TextBlock { FontSize = 12, Foreground = Theme.Grey2, Margin = new(10, 0, 10, 0), Text = "或粘贴视频链接" };
        Grid.SetColumn(orText, 1);
        foreach (var col in new[] { 0, 2 })
        {
            var line = new Border { Height = 1, Background = Theme.Line, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(line, col);
            or.Children.Add(line);
        }
        or.Children.Add(orText);
        panel.Children.Add(or);

        var linkRow = new DockPanel();
        downloadBtn.Style = (Style)FindResource("Btn");
        downloadBtn.Content = "加入后台识别";
        DockPanel.SetDock(downloadBtn, Dock.Right);
        linkRow.Children.Add(downloadBtn);
        link.Style = (Style)FindResource("Field");
        link.Tag = "B 站、YouTube 等视频页面的链接";
        link.TextChanged += (_, _) => downloadBtn.IsEnabled = link.Text.Trim().Length > 0;
        link.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && downloadBtn.IsEnabled) SubmitLink();
        };
        linkRow.Children.Add(link);
        panel.Children.Add(linkRow);

        panel.Children.Add(hintNote);

        closeBtn.Style = (Style)FindResource("Btn");
        closeBtn.Content = "关闭";
        closeBtn.Click += (_, _) => Close();
        panel.Children.Add(closeBtn);

        Content = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("SecondaryGlassBg"),
            Child = panel,
        };

        // Window-level preview drag events so dragging anywhere on the window works reliably.
        PreviewDragEnter += (_, e) => HandleDragHover(e, true);
        PreviewDragOver += (_, e) => HandleDragHover(e, true);
        PreviewDragLeave += (_, _) => Light(false);
        PreviewDrop += (_, e) => HandleDrop(e);

        fileBtn.Click += (_, _) =>
        {
            var picker = new OpenFileDialog
            {
                Multiselect = true,
                Filter = L.T("音视频文件") + "|" + string.Join(";", MediaImportQueue.SupportedExtensions.Select(k => "*" + k)) + "|" + L.T("所有文件") + "|*.*",
            };
            if (picker.ShowDialog(this) == true && picker.FileNames.Length > 0)
                SubmitFiles(picker.FileNames);
        };
        downloadBtn.Click += (_, _) => SubmitLink();
    }

    private void Light(bool on)
    {
        drop.Background = on ? Theme.PrimarySoft : System.Windows.Media.Brushes.White;
        outline.Stroke = on ? Theme.Primary : Theme.Line;
    }

    private static List<string> ExtractDroppedFiles(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            return MediaImportQueue.FilterSupportedFiles(files);
        return [];
    }

    private void HandleDragHover(DragEventArgs e, bool on)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var valid = ExtractDroppedFiles(e);
        e.Effects = valid.Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        Light(on && valid.Count > 0);
        if (valid.Count == 0)
        {
            hintNote.Text = "不支持该文件格式，请拖入 WAV / MP3 / M4A / MP4 / FLAC / MKV 等音视频文件。";
            hintNote.Visibility = Visibility.Visible;
        }
        else
        {
            hintNote.Visibility = Visibility.Collapsed;
        }
    }

    private void HandleDrop(DragEventArgs e)
    {
        Light(false);
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        var valid = ExtractDroppedFiles(e);
        if (valid.Count == 0)
        {
            hintNote.Text = "未识别到可导入的音视频文件（支持 WAV、MP3、M4A、MP4、FLAC、MOV、MKV 等）。";
            hintNote.Visibility = Visibility.Visible;
            return;
        }
        SubmitFiles(valid);
    }

    private void SubmitFiles(IEnumerable<string> paths)
    {
        var added = model.Imports.EnqueueFiles(paths, speakersCheck.IsChecked == true);
        if (added == 0)
        {
            hintNote.Text = "所选文件不在支持的音视频格式范围内。";
            hintNote.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
        Close();
    }

    private void SubmitLink()
    {
        if (!model.Imports.EnqueueLink(link.Text, speakersCheck.IsChecked == true)) return;
        DialogResult = true;
        Close();
    }
}
