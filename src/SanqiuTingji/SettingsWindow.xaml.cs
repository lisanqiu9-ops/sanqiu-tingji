using SanqiuTingji.Localization;
using System.Windows;
using System.Windows.Controls;
using SanqiuTingji.Audio;

namespace SanqiuTingji.App;

public partial class SettingsWindow : Window
{
    private readonly AppModel model;
    private bool loading = true;

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

        Fill(TargetBox, [("auto", L.T("自动")), .. Languages.Target.Select(l => (l.Code, l.Name))], s.Target);
        ModeNotes.IsChecked = s.Display == DisplayMode.Notes;
        ModeSubs.IsChecked = s.Display == DisplayMode.Subtitles;
        FontSlider.Value = s.SubtitleFontSize;
        UiText.Set(FontText, "Text", () => L.F("悬浮字幕字号  {0:0}", s.SubtitleFontSize));
        TranslateOn.IsChecked = s.TranslationOn;
        VoicesOn.IsChecked = s.SpeakerVoices;
        RedoOn.IsChecked = s.RedoSpeakers;
        SaveAudioOn.IsChecked = s.SaveMeetingAudio;
        LiveNotesOn.IsChecked = s.LiveNotes;
        GlossaryOn.IsChecked = s.GlossaryOn;
        GlossaryAiOn.IsChecked = s.GlossaryAiCheck;
        UiText.Set(GlossaryHelp, "Text", () => L.F("词库里有 {0} 个词条。人名、产品名、术语被认错时会自动改对，翻译和纪要也按词库的写法和译法来。", model.Glossary.Terms.Count));
        model.Glossary.Changed += () => Dispatcher.BeginInvoke(() => UiText.Set(GlossaryHelp, "Text", () => L.F("词库里有 {0} 个词条。人名、产品名、术语被认错时会自动改对，翻译和纪要也按词库的写法和译法来。", model.Glossary.Terms.Count)));
        Capturable.IsChecked = s.HideFromCaptures;
        RenderFrom();

        Fill(PresetBox, ProviderPreset.All.Select(p => (p.Id, L.T(p.Name))).ToArray(), "");
        FillTemplates();
        Fill(NotesLangBox, [("auto", L.T("跟字幕一致")), .. Languages.Target.Select(l => (l.Code, l.Name))], s.NotesLanguage);

        FillMics();

        FollowApps.IsChecked = s.FollowMeetingApps;
        AutoCliEndOn.IsChecked = s.AutoCliNotesOnEnd;
        Fill(SilenceBox, [("Prompt", L.T("先提醒再结束")), ("End", L.T("直接结束")), ("Off", L.T("不处理"))], s.SilenceEnd.ToString());
        Fill(SilenceMinBox, new[] { 5, 10, 15, 30 }.Select(m => (m.ToString(), L.F("{0} 分钟", m))).ToArray(), s.SilenceMinutes.ToString());
        LoginOn.IsChecked = s.LaunchAtLogin;
        TrayStartOn.IsChecked = s.TrayClickStarts;
        UiText.Set(AboutText, "Text", () => L.T("语音识别与发言人区分都在本机完成。开源组件与许可见安装目录里的 NOTICE。"));
        App.Updates.Changed += () => Dispatcher.BeginInvoke(RenderUpdates);
        RenderUpdates();

        model.Ai.Changed += () => Dispatcher.BeginInvoke(RenderKey);
        // The translation switch also lives in the panel and the tray menu.
        model.StateChanged += () =>
        {
            // Templates may have changed in the template center.
            if (!TemplateDefaultBox.IsDropDownOpen) FillTemplates();
            var was = loading;
            loading = true;
            TranslateOn.IsChecked = model.Settings.TranslationOn;
            loading = was;
        };
        Activated += (_, _) => { _ = RefreshDevices(); RenderModels(); };
        RenderProviders();
        RenderKey();
        RenderCliBridge();
        _ = RefreshDevices();
        RenderModels();
        loading = false;
    }

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
        Fill(TargetBox, [("auto", L.T("自动")), .. Languages.Target.Select(l => (l.Code, l.Name))], model.Settings.Target);
        Fill(NotesLangBox, [("auto", L.T("跟字幕一致")), .. Languages.Target.Select(l => (l.Code, l.Name))], model.Settings.NotesLanguage);
        Fill(SilenceBox, [("Prompt", L.T("先提醒再结束")), ("End", L.T("直接结束")), ("Off", L.T("不处理"))], model.Settings.SilenceEnd.ToString());
        Fill(SilenceMinBox, new[] { 5, 10, 15, 30 }.Select(m => (m.ToString(), L.F("{0} 分钟", m))).ToArray(), model.Settings.SilenceMinutes.ToString());
        FillMics();
        FillTemplates();
        var preset = TagOf(PresetBox);
        Fill(PresetBox, ProviderPreset.All.Select(p => (p.Id, L.T(p.Name))).ToArray(), preset);
        RenderUpdates();
        RenderKey();
        loading = wasLoading;
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        // Applied first: saving raises StateChanged, which puts the switch back to the stored value.
        model.SetTranslation(TranslateOn.IsChecked == true);
        var s = model.Settings;
        var oldDisplay = s.Display;
        var oldCapturable = s.HideFromCaptures;
        s.Target = TagOf(TargetBox);
        s.Display = ModeSubs.IsChecked == true ? DisplayMode.Subtitles : DisplayMode.Notes;
        s.SpeakerVoices = VoicesOn.IsChecked == true;
        s.RedoSpeakers = RedoOn.IsChecked == true;
        s.SaveMeetingAudio = SaveAudioOn.IsChecked == true;
        s.LiveNotes = LiveNotesOn.IsChecked == true;
        s.GlossaryOn = GlossaryOn.IsChecked == true;
        s.GlossaryAiCheck = GlossaryAiOn.IsChecked == true;
        s.HideFromCaptures = Capturable.IsChecked == true;
        s.NotesLanguage = TagOf(NotesLangBox);
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
        if (oldDisplay != s.Display || oldCapturable != s.HideFromCaptures) ((App)Application.Current).SwitchDisplay();
    }

    private void Font_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FontText == null) return;
        UiText.Set(FontText, "Text", () => L.F("悬浮字幕字号  {0:0}", FontSlider.Value));
        if (loading) return;
        model.Settings.SubtitleFontSize = FontSlider.Value;
        model.SaveSettings();
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

    // MARK: AI services

    /// The service being edited: always the one in use (switching the active one switches the editor).
    private AiProvider? editing;

    private void RenderProviders()
    {
        var s = model.Settings;
        var was = loading;
        loading = true;
        Fill(ActiveBox, s.Providers.Select(p => (p.Id, $"{p.Name} · {p.Model}")).ToArray(), s.ActiveProvider()?.Id ?? "");
        if (s.Providers.Count == 0) ActiveBox.Items.Add(UiText.Bind(new ComboBoxItem { Content = L.T("还没有添加 AI 服务"), Tag = "", IsEnabled = false }, ("Content", () => L.T("还没有添加 AI 服务"))));
        if (s.Providers.Count == 0) ActiveBox.SelectedIndex = 0;
        loading = was;
        LoadEditor(s.ActiveProvider());
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
        UiText.Set(KeyHint, "Text", () => info.KeyOptional ? L.T("这个服务不需要 key") : saved != null ? L.F("已保存 {0}，留空则不修改", AIKey.Masked(saved)) : L.T("还没有填写"));
        UiText.Set(KeyLink, "Content", () => info.KeyOptional ? L.T("打开服务官网 ↗") : L.T("去申请 key ↗"));
        KeyLink.Visibility = info.KeyUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UiText.Set(ModelHint, "Text", () => info.Models.Length > 0 ? L.T("可以从列表里选，也可以直接输入模型名") : L.T("直接输入模型名，或先填 key 再获取列表"));
    }

    private void RenderKey()
    {
        var ai = model.Ai;
        UiText.Set(KeyState, "Text", () => ai.Current == null ? L.T("未设置：添加一个 AI 服务后即可生成纪要") : ai.State switch
        {
            AiClient.KeyState.None => L.F("{0}：还没有填写 API key", ai.Current.Name),
            AiClient.KeyState.Checking => L.F("正在验证 {0}…", ai.Describe),
            AiClient.KeyState.Ok => L.F("✓ {0} 可用 · 响应 {1:0.0} 秒", ai.Describe, ai.CheckSeconds),
            AiClient.KeyState.Invalid => L.F("{0}：key 不可用，请检查后重新填写", ai.Current.Name),
            AiClient.KeyState.Unreachable => $"{ai.Current.Name}：{ai.CheckError ?? "暂时连不上，请检查网络或代理"}",
            _ => L.F("{0}（尚未验证）", ai.Describe),
        });
        KeyState.Foreground = ai.State is AiClient.KeyState.Invalid or AiClient.KeyState.Unreachable ? Theme.Warn
            : ai.State == AiClient.KeyState.Ok ? Theme.OnAir : Theme.Ink;
    }

    private void Active_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || TagOf(ActiveBox) is not { Length: > 0 } id) return;
        model.Settings.ActiveProviderId = id;
        model.SaveSettings();
        model.Ai.Refresh();
        _ = model.Ai.Check();
        LoadEditor(model.Settings.ActiveProvider());
    }

    private void AddProvider_Click(object sender, RoutedEventArgs e)
    {
        // A new service starts as the aggregator (one key, many models); the platform is one click away.
        var p = ProviderPreset.Find("ucloud").Create();
        if (model.Settings.Providers.Any(x => x.Name == p.Name)) p.Name += $" {model.Settings.Providers.Count + 1}";
        model.Settings.Providers.Add(p);
        model.Settings.ActiveProviderId = p.Id;
        model.SaveSettings();
        model.Ai.Refresh();
        RenderProviders();
        RenderKey();
        PresetBox.Focus();
    }

    /// Picking a platform fills in its address and a model; the name follows unless the user set one.
    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || editing == null) return;
        var info = ProviderPreset.Find(TagOf(PresetBox));
        var oldInfo = editing.Info;
        if (NameBox.Text.Trim().Length == 0 || NameBox.Text.StartsWith(oldInfo.Name)) NameBox.Text = info.Name;
        if (info.BaseUrl.Length > 0 || oldInfo.Id != "custom") UrlBox.Text = info.BaseUrl;
        SetModels(info.Models, info.Models.FirstOrDefault() ?? "");
        RenderPresetHints(info, editing.Id);
    }

    /// The editor's values into the service (the key only when one was typed).
    private bool ApplyEditor()
    {
        if (editing == null) return false;
        var info = ProviderPreset.Find(TagOf(PresetBox));
        var url = info.NormalizeBaseUrl(UrlBox.Text);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
        {
            UiText.Set(ModelHint, "Text", () => L.T("Base URL 不对，应该像 https://api.example.com/v1"));
            ModelHint.Foreground = Theme.Warn;
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
            UiText.Set(ModelHint, "Text", () => L.T("请先选择或输入一个模型"));
            ModelHint.Foreground = Theme.Warn;
            return;
        }
        model.Settings.ActiveProviderId = editing.Id;
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
        UiText.Set(ModelHint, "Text", () => L.T("正在获取…"));
        try
        {
            var list = await AiClient.ListModels(editing);
            SetModels(list, editing.Model);
            UiText.Set(ModelHint, "Text", () => list.Count == 0 ? L.T("这个服务没有返回模型，请直接输入模型名") : L.F("共 {0} 个模型，点右侧箭头选择", list.Count));
            if (list.Count > 0) ModelBox.IsDropDownOpen = true;
        }
        catch (AiException ex) when (ex.Failure == AiFailure.InvalidKey)
        {
            UiText.Set(ModelHint, "Text", () => L.T("key 不对或没有权限，请检查"));
        }
        catch (Exception)
        {
            UiText.Set(ModelHint, "Text", () => L.T("这个服务不支持获取模型列表，请直接输入模型名"));
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
        if (devicesBusy)
        {
            devicesAgain = true;
            return;
        }
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

    // MARK: Updates

    private void RenderUpdates()
    {
        var u = App.Updates;
        VersionText.Text = $"{Brand.Name} {u.Version}";
        var found = u.AvailableVersion;
        UiText.Set(UpdateBtn, "Content", () => found != null ? L.F("查看新版本 {0}", found) : L.T("检查更新"));
        UpdateBtn.Style = (Style)FindResource(found != null ? "PrimaryBtn" : "Btn");
        UiText.Set(UpdateText, "Text", () => u.Status.Length > 0 ? u.Status
            : u.Installed ? L.T("每 6 小时自动检查一次新版本，发现后会问你要不要更新。") : L.T("当前不是安装版（开发版），不能自动更新。"));
    }

    /// Checks GitHub; a newer version opens the 发现新版本 box, where the user decides.
    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateBtn.IsEnabled = false;
        var found = App.Updates.AvailableVersion != null || await App.Updates.Check();
        UpdateBtn.IsEnabled = true;
        if (found) UpdateWindow.ShowFor(model, App.Updates);
    }

    // MARK: General

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

    // MARK: Local CLI Bridge

    private IReadOnlyList<CliDetectedItem> detectedClis = [];

    private void RenderCliBridge()
    {
        var was = loading;
        loading = true;
        var s = model.Settings;
        detectedClis = CliBridge.DetectInstalled();
        CliEnabledOn.IsChecked = s.CliNotesEnabled;
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
    }

    private string BuildCliHint(CliBridgePreset preset)
    {
        var found = detectedClis.FirstOrDefault(d => d.Preset.Id == preset.Id)?.ResolvedPath;
        return found != null ? $"{preset.Hint}（本机路径：{found}）" : preset.Hint;
    }

    private void SaveCliSettings()
    {
        var s = model.Settings;
        s.CliNotesEnabled = CliEnabledOn.IsChecked == true;
        s.CliNotesPreset = TagOf(CliPresetBox) is { Length: > 0 } p ? p : "custom";
        s.CliNotesCommand = CliCmdBox.Text.Trim();
        s.CliNotesArgs = CliArgsBox.Text.Trim();
        s.CliNotesInputMode = Enum.TryParse<CliInputMode>(TagOf(CliModeBox), out var mode) ? mode : CliInputMode.Stdin;
        s.CliNotesModel = string.IsNullOrWhiteSpace(CliModelBox.Text) ? null : CliModelBox.Text.Trim();
        s.DefaultExportDirectory = string.IsNullOrWhiteSpace(ExportDirBox.Text) ? null : ExportDirBox.Text.Trim();
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
    }

    private void CliDetect_Click(object sender, RoutedEventArgs e)
    {
        detectedClis = CliBridge.DetectInstalled();
        var installed = detectedClis.Where(d => d.Installed).ToList();
        if (installed.Count > 0 && string.IsNullOrWhiteSpace(CliCmdBox.Text))
        {
            var first = installed[0];
            model.Settings.CliNotesPreset = first.Preset.Id;
            model.Settings.CliNotesCommand = first.ResolvedPath!;
            model.Settings.CliNotesArgs = first.Preset.Args;
            model.Settings.CliNotesInputMode = first.Preset.InputMode;
            model.Settings.CliNotesEnabled = true;
            model.SaveSettings();
        }
        RenderCliBridge();
        CliTestText.Foreground = installed.Count > 0 ? Theme.OnAir : Theme.Warn;
        CliTestText.Text = installed.Count > 0
            ? "已检测到本机 CLI：" + string.Join("、", installed.Select(x => x.Preset.Name))
            : "未在默认路径检测到常见 CLI，可手动填写可执行文件路径。";
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
