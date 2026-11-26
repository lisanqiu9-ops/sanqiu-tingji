using SanqiuTingji.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SanqiuTingji.Audio;

namespace SanqiuTingji.App;

public partial class SettingsWindow : Window
{
    private static readonly (string Id, string ShortName)[] QuickCloudPlatforms =
    [
        ("deepseek", "DeepSeek"),
        ("bailian", "通义千问"),
        ("ark", "豆包方舟"),
        ("moonshot", "Kimi"),
        ("zhipu", "智谱 GLM"),
        ("openai", "OpenAI GPT"),
        ("gemini", "Gemini API"),
        ("ollama", "本地 Ollama"),
        ("custom", "自定义…"),
    ];

    private readonly AppModel model;
    private bool loading = true;
    private string currentInstallCmd = "";

    public SettingsWindow(AppModel model)
    {
        this.model = model;
        InitializeComponent();
        Native.ApplyLiquidGlass(this, dark: false);
        UiText.Set(this, "Title", () => $"{Brand.Name} · 设置");
        Icon = Icons.Mark(64, false);
        var s = model.Settings;
        FillUiLanguages();
        PropertyChangedEventManager.AddHandler(L.Instance, LanguageChanged, "Item[]");

        VoicesOn.IsChecked = s.SpeakerVoices;
        RedoOn.IsChecked = s.RedoSpeakers;
        SaveAudioOn.IsChecked = s.SaveMeetingAudio;
        GlossaryOn.IsChecked = s.GlossaryOn;
        UiText.Set(GlossaryHelp, "Text", () => L.F("词库里有 {0} 个词条。人名、产品名、术语被认错时会自动改对，纪要也会按词库的写法来。", model.Glossary.Terms.Count));
        model.Glossary.Changed += () => Dispatcher.BeginInvoke(() => UiText.Set(GlossaryHelp, "Text", () => L.F("词库里有 {0} 个词条。人名、产品名、术语被认错时会自动改对，纪要也会按词库的写法来。", model.Glossary.Terms.Count)));
        Capturable.IsChecked = s.HideFromCaptures;
        RenderFrom();

        Fill(PresetBox, ProviderPreset.All.Select(p => (p.Id, L.T(p.Name))).ToArray(), "");
        FillTemplates();
        FillMics();

        FollowApps.IsChecked = s.FollowMeetingApps;
        AutoCliEndOn.IsChecked = s.AutoCliNotesOnEnd;
        Fill(SilenceBox, [("Prompt", L.T("先提醒再结束")), ("End", L.T("直接结束")), ("Off", L.T("不处理"))], s.SilenceEnd.ToString());
        Fill(SilenceMinBox, new[] { 5, 10, 15, 30 }.Select(m => (m.ToString(), L.F("{0} 分钟", m))).ToArray(), s.SilenceMinutes.ToString());
        LoginOn.IsChecked = s.LaunchAtLogin;
        TrayStartOn.IsChecked = s.TrayClickStarts;
        UiText.Set(AboutText, "Text", () => L.T("语音识别与发言人区分都在本机完成。开源组件与许可见安装目录里的 NOTICE。"));

        model.Ai.Changed += () => Dispatcher.BeginInvoke(RenderKey);
        model.StateChanged += () =>
        {
            if (!TemplateDefaultBox.IsDropDownOpen) FillTemplates();
        };
        Activated += (_, _) => { _ = RefreshDevices(); RenderModels(); };
        RenderAiModeUI();
        RenderProviders();
        RenderKey();
        RenderCliBridge();
        _ = RefreshDevices();
        RenderModels();
        loading = false;
    }

    public void SelectAiEngineTab() => Pages.SelectedItem = AiEngineTab;

    private static void Fill(ComboBox box, (string Tag, string Text)[] items, string selected)
    {
        box.Items.Clear();
        foreach (var (tag, text) in items)
        {
            var item = new ComboBoxItem { Content = text, Tag = tag };
            box.Items.Add(item);
            if (tag == selected) box.SelectedItem = item;
        }
        if (box.SelectedItem == null && box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void FillUiLanguages() => Fill(UiLanguageBox,
        [("auto", L.T("跟随系统")), ("zh-CN", "简体中文"), ("en", "English")], model.Settings.UiLanguage);

    private void UiLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        model.Settings.UiLanguage = TagOf(UiLanguageBox);
        model.Settings.Save();
        L.SetLanguage(model.Settings.UiLanguage);
    }

    private void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var wasLoading = loading;
        loading = true;
        FillUiLanguages();
        Fill(SilenceBox, [("Prompt", L.T("先提醒再结束")), ("End", L.T("直接结束")), ("Off", L.T("不处理"))], model.Settings.SilenceEnd.ToString());
        Fill(SilenceMinBox, new[] { 5, 10, 15, 30 }.Select(m => (m.ToString(), L.F("{0} 分钟", m))).ToArray(), model.Settings.SilenceMinutes.ToString());
        FillMics();
        FillTemplates();
        var preset = TagOf(PresetBox);
        Fill(PresetBox, ProviderPreset.All.Select(p => (p.Id, L.T(p.Name))).ToArray(), preset);
        RenderKey();
        loading = wasLoading;
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        var s = model.Settings;
        var oldCapturable = s.HideFromCaptures;
        s.SpeakerVoices = VoicesOn.IsChecked == true;
        s.RedoSpeakers = RedoOn.IsChecked == true;
        s.SaveMeetingAudio = SaveAudioOn.IsChecked == true;
        s.GlossaryOn = GlossaryOn.IsChecked == true;
        s.HideFromCaptures = Capturable.IsChecked == true;
        if (TagOf(TemplateDefaultBox) is { Length: > 0 } tid) s.DefaultTemplateId = tid;
        s.ListenMicId = TagOf(ListenMicBox) is { Length: > 0 } room ? room : null;
        s.FollowMeetingApps = FollowApps.IsChecked == true;
        s.AutoCliNotesOnEnd = AutoCliEndOn.IsChecked == true;
        s.SilenceEnd = Enum.TryParse<MeetingEndWatch.Silence>(TagOf(SilenceBox), out var sil) ? sil : MeetingEndWatch.Silence.Prompt;
        s.SilenceMinutes = int.TryParse(TagOf(SilenceMinBox), out var m) ? m : 10;
        s.TrayClickStarts = TrayStartOn.IsChecked == true;
        if (s.LaunchAtLogin != (LoginOn.IsChecked == true))
        {
            s.LaunchAtLogin = LoginOn.IsChecked == true;
            Native.SetLaunchAtLogin(s.LaunchAtLogin);
        }
        model.SaveSettings();
        if (oldCapturable != s.HideFromCaptures) ((App)Application.Current).SwitchDisplay();
    }

    // MARK: Unified AI Engine Mode Switcher

    private void SelectCliMode_Click(object sender, MouseButtonEventArgs e)
    {
        if (model.Settings.AiMode == AiEngineMode.LocalCli) return;
        model.Settings.AiMode = AiEngineMode.LocalCli;
        if (!string.IsNullOrWhiteSpace(model.Settings.CliNotesCommand))
            model.Settings.CliNotesEnabled = true;
        model.SaveSettings();
        RenderAiModeUI();
    }

    private void SelectCloudMode_Click(object sender, MouseButtonEventArgs e)
    {
        if (model.Settings.AiMode == AiEngineMode.CloudApi) return;
        model.Settings.AiMode = AiEngineMode.CloudApi;
        // Auto-create a default DeepSeek entry on first visit so user only needs to paste API Key
        if (model.Settings.Providers.Count == 0)
        {
            var p = ProviderPreset.Find("deepseek").Create();
            model.Settings.Providers.Add(p);
            model.Settings.ActiveProviderId = p.Id;
        }
        model.SaveSettings();
        model.Ai.Refresh();
        RenderAiModeUI();
        RenderProviders();
        RenderKey();
    }

    private void RenderAiModeUI()
    {
        var isCli = model.Settings.AiMode == AiEngineMode.LocalCli;
        CliModePanel.Visibility = isCli ? Visibility.Visible : Visibility.Collapsed;
        CloudModePanel.Visibility = isCli ? Visibility.Collapsed : Visibility.Visible;

        var activeBg = new SolidColorBrush(Color.FromRgb(0xF0, 0xF7, 0xFF));
        var idleBg = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFE));
        var idleBorder = new SolidColorBrush(Color.FromArgb(0x1A, 0x16, 0x28, 0x4C));
        var idleDot = new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xE0));

        ModeCliCard.Background = isCli ? activeBg : idleBg;
        ModeCliCard.BorderBrush = isCli ? Theme.Primary : idleBorder;
        ModeCliCard.BorderThickness = new Thickness(isCli ? 1.6 : 1.1);
        ModeCliDot.Fill = isCli ? Theme.Primary : idleDot;

        ModeCloudCard.Background = !isCli ? activeBg : idleBg;
        ModeCloudCard.BorderBrush = !isCli ? Theme.Primary : idleBorder;
        ModeCloudCard.BorderThickness = new Thickness(!isCli ? 1.6 : 1.1);
        ModeCloudDot.Fill = !isCli ? Theme.Primary : idleDot;
    }

    // MARK: Notes templates

    private void FillTemplates()
    {
        var was = loading;
        loading = true;
        Fill(TemplateDefaultBox, model.Templates.Select(t => (t.Id, $"{(t.BuiltIn ? L.T(t.Name) : t.Name)}　· {L.T(t.Category)}")).ToArray(), model.Settings.DefaultTemplateId);
        loading = was;
    }

    private void Templates_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenTemplates();

    // MARK: Cloud API services (Simple 3-Step Setup)

    private AiProvider? editing;

    private void RenderProviders()
    {
        var s = model.Settings;
        var was = loading;
        loading = true;
        Fill(ActiveBox, s.Providers.Select(p => (p.Id, $"{p.Name} · {(string.IsNullOrWhiteSpace(p.Model) ? "未选模型" : p.Model)}")).ToArray(), s.ActiveProvider()?.Id ?? "");
        if (s.Providers.Count == 0)
        {
            ActiveBox.Items.Add(new ComboBoxItem { Content = "点下方平台标签快速添加一个云端服务", Tag = "", IsEnabled = false });
            ActiveBox.SelectedIndex = 0;
        }
        DeleteBtn.IsEnabled = s.Providers.Count > 0;
        loading = was;
        LoadEditor(s.ActiveProvider());
        RenderQuickCloudChips();
    }

    private void RenderQuickCloudChips()
    {
        CloudQuickPresets.Children.Clear();
        var currentPreset = editing?.Preset ?? "";
        foreach (var (id, label) in QuickCloudPlatforms)
        {
            var active = string.Equals(currentPreset, id, StringComparison.OrdinalIgnoreCase);
            var chip = new Button
            {
                Content = label,
                Style = (Style)FindResource("Btn"),
                FontSize = 11.5,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = active ? Theme.Primary : Theme.Ink,
                Background = active ? new SolidColorBrush(Color.FromArgb(0x1C, 0x00, 0x7A, 0xFF)) : new SolidColorBrush(Color.FromRgb(0xF5, 0xF7, 0xFC)),
                BorderBrush = active ? new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x7A, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x18, 0x16, 0x28, 0x4C)),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 6),
            };
            var presetId = id;
            chip.Click += (_, _) => PickQuickCloudPreset(presetId);
            CloudQuickPresets.Children.Add(chip);
        }
    }

    private void PickQuickCloudPreset(string presetId)
    {
        var info = ProviderPreset.Find(presetId);
        if (editing == null)
        {
            var created = info.Create();
            model.Settings.Providers.Add(created);
            model.Settings.ActiveProviderId = created.Id;
            model.SaveSettings();
            model.Ai.Refresh();
            RenderProviders();
            RenderKey();
            KeyBox.Focus();
            return;
        }

        // If another saved service already uses this preset, switch to it; otherwise update current editor
        var existing = model.Settings.Providers.FirstOrDefault(p => p.Preset == presetId);
        if (existing != null && existing.Id != editing.Id)
        {
            model.Settings.ActiveProviderId = existing.Id;
            model.SaveSettings();
            model.Ai.Refresh();
            RenderProviders();
            RenderKey();
            return;
        }

        var was = loading;
        loading = true;
        PresetBox.SelectedItem = PresetBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == presetId);
        NameBox.Text = info.Name;
        UrlBox.Text = info.BaseUrl;
        SetModels(info.Models, info.Models.FirstOrDefault() ?? "");
        editing.Preset = presetId;
        editing.Name = info.Name;
        editing.BaseUrl = info.BaseUrl;
        editing.Model = ModelBox.Text.Trim();
        loading = was;
        model.SaveSettings();
        model.Ai.Refresh();
        RenderPresetHints(info, editing.Id);
        RenderProviders();
        RenderKey();
        if (presetId is "custom" or "ollama") CloudAdvancedExpander.IsExpanded = true;
        KeyBox.Focus();
    }

    private void LoadEditor(AiProvider? p)
    {
        editing = p;
        EditorCard.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
        if (p == null) return;
        var was = loading;
        loading = true;
        PresetBox.SelectedItem = PresetBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == p.Preset);
        NameBox.Text = p.Name;
        UrlBox.Text = p.BaseUrl;
        KeyBox.Clear();
        SetModels(p.Info.Models, p.Model);
        loading = was;
        RenderPresetHints(p.Info, p.Id);
        CloudAdvancedHeader.Text = string.IsNullOrWhiteSpace(p.BaseUrl)
            ? "接口地址与服务详情（请填写 Base URL）"
            : $"接口地址（Base URL）：{p.BaseUrl}  · 已自动填好";
        if (p.Preset is "custom" or "ollama" || string.IsNullOrWhiteSpace(p.BaseUrl))
            CloudAdvancedExpander.IsExpanded = true;
    }

    private void SetModels(IEnumerable<string> models, string current)
    {
        ModelBox.Items.Clear();
        foreach (var m in models) ModelBox.Items.Add(new ComboBoxItem { Content = m, Tag = m });
        ModelBox.Text = current;
    }

    private void RenderPresetHints(ProviderPreset info, string providerId)
    {
        UiText.Set(PresetHint, "Text", () => L.T(info.Hint ?? ""));
        PresetHint.Visibility = info.Hint == null ? Visibility.Collapsed : Visibility.Visible;
        UrlBox.IsReadOnly = false;
        var saved = AIKey.Read(providerId);
        KeyHint.Text = info.KeyOptional ? "这个服务不需要 API Key（直接点保存并验证即可）"
            : saved != null ? $"已在本机加密保存 {AIKey.Masked(saved)}（留空则保持不变）"
            : "仅在本机 Windows 凭据区加密保存，绝不写入配置明文或上传代码仓";
        UiText.Set(KeyLink, "Content", () => info.KeyOptional ? L.T("打开服务官网 ↗") : "去申请 Key ↗");
        KeyLink.Visibility = info.KeyUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ModelHint.Text = info.Models.Length > 0 ? "已默认填好推荐模型，也可直接输入或点右侧「拉取列表」" : "填好 Key 后点「拉取列表」，或直接输入模型名";
    }

    private void RenderKey()
    {
        var ai = model.Ai;
        KeyState.Text = ai.Current == null ? "尚未配置云端服务：选上方平台并填入 API Key 即可" : ai.State switch
        {
            AiClient.KeyState.None => $"{ai.Current.Name}：请粘贴 API Key 后点「保存并验证」",
            AiClient.KeyState.Checking => $"正在验证 {ai.Describe}…",
            AiClient.KeyState.Ok => $"✓ {ai.Describe} 可用 · 响应 {ai.CheckSeconds:0.0} 秒",
            AiClient.KeyState.Invalid => $"{ai.Current.Name}：API Key 无效或无权限，请检查后重试",
            AiClient.KeyState.Unreachable => $"{ai.Current.Name}：{ai.CheckError ?? "暂时连不上，请检查网络或代理"}",
            _ => $"{ai.Describe}（尚未验证，请点右侧「保存并验证」）",
        };
        KeyState.Foreground = ai.State is AiClient.KeyState.Invalid or AiClient.KeyState.Unreachable ? Theme.Warn
            : ai.State == AiClient.KeyState.Ok ? Theme.OnAir : Theme.Grey;
    }

    private void Active_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || TagOf(ActiveBox) is not { Length: > 0 } id) return;
        model.Settings.ActiveProviderId = id;
        model.SaveSettings();
        model.Ai.Refresh();
        _ = model.Ai.Check();
        LoadEditor(model.Settings.ActiveProvider());
        RenderQuickCloudChips();
    }

    private void AddProvider_Click(object sender, RoutedEventArgs e)
    {
        var p = ProviderPreset.Find("deepseek").Create();
        if (model.Settings.Providers.Any(x => x.Name == p.Name)) p.Name += $" {model.Settings.Providers.Count + 1}";
        model.Settings.Providers.Add(p);
        model.Settings.ActiveProviderId = p.Id;
        model.SaveSettings();
        model.Ai.Refresh();
        RenderProviders();
        RenderKey();
        KeyBox.Focus();
    }

    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || editing == null) return;
        var info = ProviderPreset.Find(TagOf(PresetBox));
        var oldInfo = editing.Info;
        if (NameBox.Text.Trim().Length == 0 || NameBox.Text.StartsWith(oldInfo.Name)) NameBox.Text = info.Name;
        if (info.BaseUrl.Length > 0 || oldInfo.Id != "custom") UrlBox.Text = info.BaseUrl;
        SetModels(info.Models, info.Models.FirstOrDefault() ?? "");
        editing.Preset = info.Id;
        RenderPresetHints(info, editing.Id);
        RenderQuickCloudChips();
    }

    private bool ApplyEditor()
    {
        if (editing == null) return false;
        var info = ProviderPreset.Find(TagOf(PresetBox));
        var url = info.NormalizeBaseUrl(UrlBox.Text);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
        {
            ModelHint.Text = "Base URL 格式不对，应该像 https://api.deepseek.com/v1";
            ModelHint.Foreground = Theme.Warn;
            CloudAdvancedExpander.IsExpanded = true;
            return false;
        }
        ModelHint.ClearValue(ForegroundProperty);
        editing.Preset = TagOf(PresetBox) is { Length: > 0 } preset ? preset : "custom";
        editing.Name = NameBox.Text.Trim() is { Length: > 0 } n ? n : editing.Info.Name;
        editing.BaseUrl = url.TrimEnd('/');
        UrlBox.Text = editing.BaseUrl;
        editing.Model = ModelBox.Text.Trim();
        if (KeyBox.Password.Trim() is { Length: > 0 } key) AIKey.Save(editing.Id, key);
        KeyBox.Clear();
        model.SaveSettings();
        return true;
    }

    private async void SaveProvider_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyEditor() || editing == null) return;
        if (editing.Model.Length == 0)
        {
            ModelHint.Text = "请先选择或输入一个模型名";
            ModelHint.Foreground = Theme.Warn;
            return;
        }
        model.Settings.ActiveProviderId = editing.Id;
        model.Settings.AiMode = AiEngineMode.CloudApi;
        model.SaveSettings();
        SaveBtn.IsEnabled = false;
        model.Ai.Refresh();
        RenderProviders();
        await model.Ai.Check();
        SaveBtn.IsEnabled = true;
        RenderKey();
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyEditor() || editing == null) return;
        FetchBtn.IsEnabled = false;
        ModelHint.Text = "正在拉取模型列表…";
        try
        {
            var list = await AiClient.ListModels(editing);
            SetModels(list, editing.Model);
            ModelHint.Text = list.Count == 0 ? "这个服务没有返回模型列表，请直接输入模型名" : $"共拉取到 {list.Count} 个模型，点下拉箭头选择";
            if (list.Count > 0) ModelBox.IsDropDownOpen = true;
        }
        catch (AiException ex) when (ex.Failure == AiFailure.InvalidKey)
        {
            ModelHint.Text = "API Key 不对或没有权限，请先检查 Key";
        }
        catch (Exception)
        {
            ModelHint.Text = "这个服务不支持拉取模型列表，请直接输入模型名";
        }
        FetchBtn.IsEnabled = true;
    }

    private void DeleteProvider_Click(object sender, RoutedEventArgs e)
    {
        if (editing == null) return;
        if (!Dialog.Confirm(this, L.F("删除「{0}」？", editing.Name), L.T("这个 AI 服务和它的 API key 会一起删除。"), L.T("删除"), danger: true)) return;
        AIKey.Remove(editing.Id);
        model.Settings.Providers.Remove(editing);
        if (model.Settings.ActiveProviderId == editing.Id) model.Settings.ActiveProviderId = model.Settings.Providers.FirstOrDefault()?.Id;
        model.SaveSettings();
        model.Ai.Refresh();
        if (model.Ai.HasKey) _ = model.Ai.Check();
        RenderProviders();
        RenderKey();
    }

    private void GetKey_Click(object sender, RoutedEventArgs e)
    {
        var url = ProviderPreset.Find(TagOf(PresetBox)).KeyUrl;
        if (url.Length > 0) Native.Open(url);
    }

    // MARK: Listening devices

    private void ListenMic_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        model.Settings.ListenMic = ListenMicOn.IsChecked == true;
        model.SaveSettings();
        RenderFrom();
    }

    private void RenderFrom()
    {
        var mic = model.Settings.ListenMic;
        ListenMicOn.IsChecked = mic;
        ListenMicRow.Visibility = mic ? Visibility.Visible : Visibility.Collapsed;
        UiText.Set(FromHelp, "Text", () => mic
            ? L.T("电脑放出来的声音（对方）和麦克风（你自己、会议室里的人）分开识别，两边同时说话也都能记下。用音箱外放时，麦克风收到的回声会自动去掉，同一句不会记两遍。")
            : L.T("只听电脑放出来的声音，你自己和身边的人说的话不会记下来。"));
        if (model.IsListening) UiText.Set(FromHelp, "Text", () => L.T("改动从下一场会议开始生效。"));
    }

    private List<(string Id, string Name)> mics = [];
    private bool devicesBusy, devicesAgain;

    private async Task RefreshDevices()
    {
        if (devicesBusy) { devicesAgain = true; return; }
        devicesBusy = true;
        try
        {
            do
            {
                devicesAgain = false;
                mics = await Task.Run(DefaultDevices.Mics);
            } while (devicesAgain);
        }
        catch (Exception e) { Log.Debug($"settings: devices {e.GetType().Name}"); }
        finally { devicesBusy = false; }
        FillMics();
    }

    private void FillMics()
    {
        var was = loading;
        loading = true;
        Fill(ListenMicBox, [("", L.T("自动（系统麦克风）")), .. mics], model.Settings.ListenMicId ?? "");
        loading = was;
    }

    // MARK: General & Storage

    private void RenderModels()
    {
        var ready = model.Models.Ready;
        EngineStatusDot.Fill = ready ? Theme.OnAir : Theme.Warn;
        EngineStatusTitle.Text = ready ? "本地引擎就绪 · 离线可用" : "识别模型未下载";
        UiText.Set(ModelState, "Text", () => ready ? "中文 / 英文离线识别与发言人声纹区分已就绪" : L.T("识别模型未下载，首次收听前会自动下载"));
        var (count, bytes) = MeetingAudioStore.StorageStats(model.Store);
        AudioSizeBadgeText.Text = count > 0 ? $"录音 {MeetingAudioStore.FormatSize(bytes)}" : "暂无录音";
        AudioStorageText.Text = count > 0
            ? $"当前已保存 {count} 场会议录音，共占用 {MeetingAudioStore.FormatSize(bytes)}（约 14～18 MB/小时；清理录音不会删除逐字稿和纪要）。"
            : "当前暂无保存的会议录音（开启后，新会议和导入音视频会自动保存轻量 M4A 录音以支持原声回放）。";
        CleanAudioBtn.IsEnabled = count > 0;
    }

    private void CleanAllAudio_Click(object sender, RoutedEventArgs e)
    {
        var (count, bytes) = MeetingAudioStore.StorageStats(model.Store);
        if (count == 0) return;
        if (!Dialog.Confirm(this, "清理全部会议录音？",
                $"将删除 {count} 场会议的原始录音，释放约 {MeetingAudioStore.FormatSize(bytes)} 磁盘空间。\n\n所有逐字稿、发言人标注和会议纪要都会完整保留。",
                "确认清理录音", danger: true)) return;
        var (deleted, freed) = MeetingAudioStore.DeleteAllAudio(model.Store);
        RenderModels();
        model.Say($"已清理 {deleted} 场录音，释放 {MeetingAudioStore.FormatSize(freed)}（纪要与逐字稿已保留）");
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => Native.Open(Paths.Roaming);

    private void OpenLog_Click(object sender, RoutedEventArgs e) => Native.Open(Paths.DebugLog);

    private void Glossary_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenGlossary();

    // MARK: Local CLI Bridge (Visual Cards + Collapsed Advanced Params)

    private IReadOnlyList<CliDetectedItem> detectedClis = [];

    private void RenderCliBridge()
    {
        var was = loading;
        loading = true;
        var s = model.Settings;
        detectedClis = CliBridge.DetectInstalled();
        Fill(CliPresetBox, detectedClis.Select(d =>
        {
            var status = d.Preset.Id == "custom" ? "" : d.Installed ? "（已检测到）" : "（未安装）";
            return (d.Preset.Id, d.Preset.Name + status);
        }).ToArray(), s.CliNotesPreset);
        Fill(CliModeBox,
            [(CliInputMode.Argument.ToString(), "命令行参数（适合 agy 等）"),
             (CliInputMode.Stdin.ToString(), "标准输入 stdin（适合 codex / gemini / claude）"),
             (CliInputMode.PromptFile.ToString(), "临时文件 {prompt_file}（适合自定义脚本）")],
            s.CliNotesInputMode.ToString());
        var preset = CliBridge.FindPreset(s.CliNotesPreset);
        CliCmdBox.Text = string.IsNullOrWhiteSpace(s.CliNotesCommand) ? preset.Executable : s.CliNotesCommand;
        CliArgsBox.Text = s.CliNotesArgs ?? preset.Args;
        CliModelBox.Text = s.CliNotesModel ?? "";
        ExportDirBox.Text = s.DefaultExportDirectory ?? "";
        CliPresetHint.Text = BuildCliHint(preset);
        loading = was;

        RenderCliCards();
        UpdateCliInstallBanner(preset);
        if (string.IsNullOrWhiteSpace(CliTestText.Text))
        {
            var hit = detectedClis.FirstOrDefault(d => d.Preset.Id == preset.Id);
            CliTestText.Foreground = hit?.Installed == true ? Theme.OnAir : Theme.Grey;
            CliTestText.Text = hit?.Installed == true
                ? $"已选定 {preset.Name}（已自动匹配路径），点右侧「测试连通」验证即可"
                : preset.Id == "custom" ? "自定义命令模式：请展开下方「高级参数」填写命令路径"
                : $"{preset.Name} 尚未检测到安装，请参考上方指引安装或切换其他已检测的 CLI";
        }
    }

    private void RenderCliCards()
    {
        CliCardsGrid.Children.Clear();
        var activeId = model.Settings.CliNotesPreset;
        foreach (var item in detectedClis.Where(d => d.Preset.Id != "custom"))
        {
            var selected = string.Equals(item.Preset.Id, activeId, StringComparison.OrdinalIgnoreCase);
            var ok = item.Installed;

            var top = new DockPanel();
            var badge = new Border
            {
                Background = ok ? new SolidColorBrush(Color.FromArgb(0x22, 0x28, 0xCD, 0x41)) : new SolidColorBrush(Color.FromArgb(0x14, 0x16, 0x28, 0x4C)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 1.5, 6, 1.5),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = ok ? "● 已检测到" : "未安装",
                    FontSize = 10,
                    FontWeight = ok ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = ok ? new SolidColorBrush(Color.FromRgb(0x16, 0x7E, 0x45)) : Theme.Grey2,
                },
            };
            DockPanel.SetDock(badge, Dock.Right);
            top.Children.Add(badge);
            top.Children.Add(new TextBlock
            {
                Text = item.Preset.Name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Ink,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var body = new StackPanel();
            body.Children.Add(top);
            body.Children.Add(new TextBlock
            {
                Text = item.Preset.Hint,
                FontSize = 11,
                Foreground = Theme.Grey,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 0),
                LineHeight = 16,
            });

            var card = new Border
            {
                Background = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFE)),
                BorderBrush = selected ? Theme.Primary : new SolidColorBrush(Color.FromArgb(0x18, 0x16, 0x28, 0x4C)),
                BorderThickness = new Thickness(selected ? 1.5 : 1),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(11, 9, 11, 9),
                Margin = new Thickness(0, 0, 8, 8),
                Opacity = ok || selected ? 1.0 : 0.72,
                Cursor = Cursors.Hand,
                Child = body,
            };
            var captured = item;
            card.MouseLeftButtonUp += (_, _) => PickCliCard(captured);
            CliCardsGrid.Children.Add(card);
        }
    }

    private void PickCliCard(CliDetectedItem item)
    {
        var preset = item.Preset;
        var s = model.Settings;
        s.AiMode = AiEngineMode.LocalCli;
        s.CliNotesPreset = preset.Id;
        s.CliNotesCommand = item.ResolvedPath ?? preset.Executable;
        s.CliNotesArgs = preset.Args;
        s.CliNotesInputMode = preset.InputMode;
        s.CliNotesEnabled = item.Installed;
        model.SaveSettings();
        CliTestText.Text = "";
        RenderCliBridge();
    }

    private void UpdateCliInstallBanner(CliBridgePreset preset)
    {
        var hit = detectedClis.FirstOrDefault(d => d.Preset.Id == preset.Id);
        if (preset.Id == "custom" || hit?.Installed == true || string.IsNullOrWhiteSpace(preset.InstallCommand))
        {
            CliInstallBanner.Visibility = Visibility.Collapsed;
            currentInstallCmd = "";
            return;
        }
        CliInstallTitle.Text = $"本机尚未安装 {preset.Name}，在终端执行以下 1 行命令安装并登录后点「重新扫描」：";
        CliInstallCmd.Text = preset.InstallCommand;
        currentInstallCmd = preset.InstallCommand;
        CliCopyInstallBtn.Visibility = preset.InstallCommand.StartsWith("npm ") ? Visibility.Visible : Visibility.Collapsed;
        CliInstallBanner.Visibility = Visibility.Visible;
    }

    private void CliCopyInstall_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(currentInstallCmd)) return;
        try
        {
            Clipboard.SetText(currentInstallCmd);
            CliTestText.Foreground = Theme.OnAir;
            CliTestText.Text = "已复制安装命令到剪贴板，粘贴到 PowerShell 运行并登录即可。";
        }
        catch { }
    }

    private string BuildCliHint(CliBridgePreset preset)
    {
        var found = detectedClis.FirstOrDefault(d => d.Preset.Id == preset.Id)?.ResolvedPath;
        return found != null ? $"{preset.Hint}（本机路径：{found}）" : preset.Hint;
    }

    private void SaveCliSettings()
    {
        var s = model.Settings;
        s.CliNotesPreset = TagOf(CliPresetBox) is { Length: > 0 } p ? p : "custom";
        s.CliNotesCommand = CliCmdBox.Text.Trim();
        s.CliNotesArgs = CliArgsBox.Text.Trim();
        s.CliNotesInputMode = Enum.TryParse<CliInputMode>(TagOf(CliModeBox), out var mode) ? mode : CliInputMode.Stdin;
        s.CliNotesModel = string.IsNullOrWhiteSpace(CliModelBox.Text) ? null : CliModelBox.Text.Trim();
        s.DefaultExportDirectory = string.IsNullOrWhiteSpace(ExportDirBox.Text) ? null : ExportDirBox.Text.Trim();
        s.CliNotesEnabled = !string.IsNullOrWhiteSpace(s.CliNotesCommand);
        model.SaveSettings();
    }

    private void CliChanged(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        SaveCliSettings();
    }

    private void CliText_LostFocus(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        SaveCliSettings();
    }

    private void CliPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        var id = TagOf(CliPresetBox);
        var preset = CliBridge.FindPreset(id);
        var resolved = detectedClis.FirstOrDefault(d => d.Preset.Id == id)?.ResolvedPath;
        var was = loading;
        loading = true;
        CliCmdBox.Text = resolved ?? preset.Executable;
        CliArgsBox.Text = preset.Args;
        CliModeBox.SelectedItem = CliModeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == preset.InputMode.ToString());
        CliPresetHint.Text = BuildCliHint(preset);
        loading = was;
        SaveCliSettings();
        CliTestText.Text = "";
        RenderCliCards();
        UpdateCliInstallBanner(preset);
    }

    private void CliDetect_Click(object sender, RoutedEventArgs e)
    {
        detectedClis = CliBridge.DetectInstalled();
        var installed = detectedClis.Where(d => d.Installed).ToList();
        if (installed.Count > 0)
        {
            var curHit = installed.FirstOrDefault(d => d.Preset.Id == model.Settings.CliNotesPreset) ?? installed[0];
            model.Settings.CliNotesPreset = curHit.Preset.Id;
            model.Settings.CliNotesCommand = curHit.ResolvedPath!;
            model.Settings.CliNotesArgs = curHit.Preset.Args;
            model.Settings.CliNotesInputMode = curHit.Preset.InputMode;
            model.Settings.CliNotesEnabled = true;
            model.SaveSettings();
        }
        RenderCliBridge();
        CliTestText.Foreground = installed.Count > 0 ? Theme.OnAir : Theme.Warn;
        CliTestText.Text = installed.Count > 0
            ? "已扫描到本机 CLI：" + string.Join("、", installed.Select(x => x.Preset.Name))
            : "未检测到常见本地 CLI；可按上方指引安装，或直接切换到「在线云端 API」。";
    }

    private async void CliTest_Click(object sender, RoutedEventArgs e)
    {
        SaveCliSettings();
        CliTestBtn.IsEnabled = false;
        CliTestText.Foreground = Theme.Grey;
        CliTestText.Text = "正在测试本地 CLI 连通性…";
        var config = CliBridge.FromSettings(model.Settings);
        var result = await CliBridge.TestAsync(config);
        CliTestText.Foreground = result.Success ? Theme.OnAir : Theme.Warn;
        CliTestText.Text = result.Message;
        CliTestBtn.IsEnabled = true;
    }
}
