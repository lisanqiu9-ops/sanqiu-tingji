using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SanqiuTingji.App;

/// No-API meeting notes workshop dialog: copies template/glossary-aware prompts for external
/// subscription chats (Gemini / OpenAI / Muse) and imports the structured JSON reply back into session.json.
public sealed class NotesWorkshopWindow : Window
{
    private readonly AppModel model;
    private readonly string sessionId;
    private readonly Session snapshot;
    private readonly NoteTemplate template;
    private readonly NotesPromptPackage package;
    private readonly TextBox promptBox;
    private readonly TextBox replyBox;
    private readonly TextBlock statusText;
    private readonly StackPanel chunkBar;
    private readonly Button mergeBtn;
    private readonly Dictionary<int, string> partialReplies = new();
    private int currentChunk;
    private bool inMergeMode;
    private bool imported;

    public static bool ShowFor(Window? owner, AppModel model, string sessionId)
    {
        var snap = model.Store.Snapshot(sessionId);
        if (snap == null || snap.Lines.Count == 0)
        {
            Dialog.Confirm(owner, "暂无逐字稿", "这场会议还没有识别到语音内容，录完或导入音视频后再生成纪要。", "确定");
            return false;
        }
        var win = new NotesWorkshopWindow(model, snap) { Owner = owner?.IsVisible == true ? owner : null };
        if (win.Owner == null) win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        win.ShowDialog();
        return win.imported;
    }

    private NotesWorkshopWindow(AppModel model, Session snapshot)
    {
        this.model = model;
        this.snapshot = snapshot;
        sessionId = snapshot.Id;
        template = model.TemplateFor(snapshot);
        package = NotesWorkshop.BuildPackage(snapshot, template, model.Glossary);

        Title = $"{Brand.Name} · 纪要工坊（免 API）";
        Width = 820;
        Height = 720;
        MinWidth = 640;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("Ground");
        Foreground = Theme.Ink;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        Icon = Icons.Mark(64, false);
        Native.ApplyLiquidGlass(this, dark: false);

        var root = new Grid { Margin = new Thickness(24, 18, 24, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock
        {
            Text = $"{snapshot.DisplayTitle}  ·  模板：{template.Name}",
            FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        header.Children.Add(new TextBlock
        {
            Text = package.IsChunked
                ? $"本场共 {snapshot.Lines.Count} 句（超长会议，已自动切分为 {package.Chunks.Count} 段）。请依次复制每段提示词发给 Gemini / OpenAI / Muse，把每段 JSON 粘回保存，最后合并写入。"
                : $"本场共 {snapshot.Lines.Count} 句。点下方「一键复制提示词」发给你的 Gemini / OpenAI / Muse 订阅窗口，把回复粘回下方即可自动渲染纪要卡片并支持导出 Word。",
            FontSize = 12.5, Foreground = Theme.Grey, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Step 1: Prompt panel
        var step1Card = new Border
        {
            Style = (Style)Application.Current.FindResource("Panel"),
            Margin = new Thickness(0, 0, 0, 12),
        };
        var step1Grid = new Grid();
        step1Grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        step1Grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        step1Grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var step1Header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var copyBtn = new Button
        {
            Content = "复制提示词",
            Style = (Style)Application.Current.FindResource("PrimaryBtn"),
            Padding = new Thickness(16, 6, 16, 6),
        };
        copyBtn.Click += (_, _) => CopyPrompt();
        DockPanel.SetDock(copyBtn, Dock.Right);
        step1Header.Children.Add(copyBtn);

        mergeBtn = new Button
        {
            Content = "生成最终合并提示词",
            Style = (Style)Application.Current.FindResource("Btn"),
            Margin = new Thickness(0, 0, 8, 0),
            Visibility = package.IsChunked ? Visibility.Visible : Visibility.Collapsed,
        };
        mergeBtn.Click += (_, _) => EnterMergeMode();
        DockPanel.SetDock(mergeBtn, Dock.Right);
        step1Header.Children.Add(mergeBtn);

        step1Header.Children.Add(new TextBlock
        {
            Text = "第 1 步 · 复制整理好的提示词（已含模板、词库与时间戳）",
            FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink, VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetRow(step1Header, 0);
        step1Grid.Children.Add(step1Header);

        chunkBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = package.IsChunked ? Visibility.Visible : Visibility.Collapsed,
        };
        Grid.SetRow(chunkBar, 1);
        step1Grid.Children.Add(chunkBar);

        promptBox = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            AcceptsReturn = true,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            FontSize = 12,
            Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xF9, 0xFC)),
            BorderBrush = Theme.Line,
            Padding = new Thickness(10, 8, 10, 8),
        };
        Grid.SetRow(promptBox, 2);
        step1Grid.Children.Add(promptBox);
        step1Card.Child = step1Grid;
        Grid.SetRow(step1Card, 1);
        root.Children.Add(step1Card);

        // Step 2: Reply import panel
        var step2Card = new Border
        {
            Style = (Style)Application.Current.FindResource("Panel"),
            Margin = new Thickness(0, 0, 0, 12),
        };
        var step2Grid = new Grid();
        step2Grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        step2Grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var step2Header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var pasteBtn = new Button
        {
            Content = "从剪贴板粘贴",
            Style = (Style)Application.Current.FindResource("Btn"),
        };
        DockPanel.SetDock(pasteBtn, Dock.Right);
        step2Header.Children.Add(pasteBtn);

        step2Header.Children.Add(new TextBlock
        {
            Text = "第 2 步 · 粘贴大模型返回的纪要内容（支持直接粘贴带 ```json 代码块的整段回复）",
            FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink, VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetRow(step2Header, 0);
        step2Grid.Children.Add(step2Header);

        replyBox = new TextBox
        {
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            AcceptsReturn = true,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            FontSize = 12,
            BorderBrush = Theme.Line,
            Padding = new Thickness(10, 8, 10, 8),
        };
        pasteBtn.Click += (_, _) =>
        {
            if (Clipboard.ContainsText())
            {
                replyBox.Text = Clipboard.GetText();
                SetStatus("已从剪贴板粘贴内容，可点击右下角按钮解析。");
            }
            else
            {
                SetStatus("剪贴板里当前没有文本。", warn: true);
            }
        };
        Grid.SetRow(replyBox, 1);
        step2Grid.Children.Add(replyBox);
        step2Card.Child = step2Grid;
        Grid.SetRow(step2Card, 2);
        root.Children.Add(step2Card);

        // Footer
        var footer = new DockPanel();
        var actionButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var closeBtn = new Button
        {
            Content = "关闭",
            Style = (Style)Application.Current.FindResource("Btn"),
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true,
        };
        closeBtn.Click += (_, _) => Close();
        var importBtn = new Button
        {
            Content = package.IsChunked ? "保存当前段 / 解析写入纪要" : "解析并写入纪要",
            Style = (Style)Application.Current.FindResource("PrimaryBtn"),
            Padding = new Thickness(18, 7, 18, 7),
        };
        importBtn.Click += (_, _) => ImportCurrentReply();
        actionButtons.Children.Add(closeBtn);
        actionButtons.Children.Add(importBtn);
        DockPanel.SetDock(actionButtons, Dock.Right);
        footer.Children.Add(actionButtons);

        statusText = new TextBlock
        {
            FontSize = 12.5,
            Foreground = Theme.Grey,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 12, 0),
        };
        footer.Children.Add(statusText);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        Content = new Border
        {
            Background = (Brush)FindResource("SecondaryGlassBg"),
            Child = root,
        };
        SelectChunk(0);
    }

    private void RebuildChunkBar()
    {
        chunkBar.Children.Clear();
        if (!package.IsChunked) return;
        for (var i = 0; i < package.Chunks.Count; i++)
        {
            var idx = i;
            var done = partialReplies.ContainsKey(idx);
            var active = !inMergeMode && currentChunk == idx;
            var btn = new Button
            {
                Content = done ? $"✓ 第 {idx + 1} 段" : $"第 {idx + 1} 段",
                Style = (Style)Application.Current.FindResource(active ? "PrimaryBtn" : "Btn"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
            };
            btn.Click += (_, _) => SelectChunk(idx);
            chunkBar.Children.Add(btn);
        }
    }

    private void SelectChunk(int index)
    {
        inMergeMode = false;
        currentChunk = Math.Clamp(index, 0, package.Chunks.Count - 1);
        var chunk = package.Chunks[currentChunk];
        promptBox.Text = chunk.Prompt;
        if (package.IsChunked && partialReplies.TryGetValue(currentChunk, out var saved))
            replyBox.Text = saved;
        RebuildChunkBar();
        SetStatus(package.IsChunked
            ? $"当前：第 {chunk.Index}/{chunk.Total} 段（L{chunk.StartLine}-L{chunk.EndLine}，{chunk.Prompt.Length} 字符）。"
            : $"提示词已就绪（共 {chunk.Prompt.Length} 字符）。");
    }

    private void EnterMergeMode()
    {
        if (partialReplies.Count < package.Chunks.Count)
        {
            SetStatus($"已保存 {partialReplies.Count}/{package.Chunks.Count} 段，请先把所有分段的回复粘贴并保存后再合并。", warn: true);
            return;
        }
        inMergeMode = true;
        var ordered = Enumerable.Range(0, package.Chunks.Count).Select(i => partialReplies[i]).ToList();
        promptBox.Text = NotesWorkshop.BuildMergePrompt(snapshot, ordered, template, model.Glossary);
        replyBox.Text = "";
        RebuildChunkBar();
        SetStatus("已生成最终合并提示词：点击「复制提示词」发送给大模型，再把最终合并结果粘回下方写入。");
    }

    private void CopyPrompt()
    {
        Clipboard.SetText(promptBox.Text);
        SetStatus("✓ 已复制到剪贴板！请粘贴到 Gemini / OpenAI / Muse 对话框，再把回复粘回下方。");
    }

    private void ImportCurrentReply()
    {
        var raw = replyBox.Text;
        if (string.IsNullOrWhiteSpace(raw))
        {
            SetStatus("请先在第 2 步粘贴大模型返回的纪要内容。", warn: true);
            return;
        }

        if (package.IsChunked && !inMergeMode)
        {
            var check = NotesWorkshop.ParseReply(snapshot, raw, template);
            if (!check.Success)
            {
                SetStatus(check.Message, warn: true);
                return;
            }
            partialReplies[currentChunk] = NotesWorkshop.ExtractJsonText(raw);
            RebuildChunkBar();
            if (partialReplies.Count < package.Chunks.Count)
            {
                var next = Enumerable.Range(0, package.Chunks.Count).First(i => !partialReplies.ContainsKey(i));
                SelectChunk(next);
                replyBox.Text = "";
                SetStatus($"✓ 已保存第 {currentChunk} 段，自动切到下一段（已完成 {partialReplies.Count}/{package.Chunks.Count}）。");
            }
            else
            {
                EnterMergeMode();
            }
            return;
        }

        var result = NotesWorkshop.ImportReply(model.Store, sessionId, raw, template);
        if (!result.Success)
        {
            SetStatus(result.Message, warn: true);
            return;
        }
        model.Notes.Show(sessionId);
        imported = true;
        Close();
    }

    private void SetStatus(string text, bool warn = false)
    {
        statusText.Text = text;
        statusText.Foreground = warn ? Theme.Warn : Theme.Grey;
    }
}
