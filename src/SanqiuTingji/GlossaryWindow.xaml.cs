using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SanqiuTingji.Localization;

namespace SanqiuTingji.App;

/// The glossary: 3-pane category directory + term list + detail editor with category-aware import/export.
public partial class GlossaryWindow : Window
{
    private const string AllCategory = "__ALL__";
    private const string GeneralCategory = "";

    private readonly AppModel model;
    private GlossaryTerm current = new();
    private string selectedCategory = AllCategory;
    private bool loading;
    private bool dirty;

    public GlossaryWindow(AppModel model, string? addText = null)
    {
        this.model = model;
        InitializeComponent();
        Native.ApplyLiquidGlass(this, dark: false);
        UiText.Set(this, "Title", () => $"{Brand.Name} · 词库");
        Icon = Icons.Mark(64, false);
        model.Glossary.Changed += OnGlossaryChanged;
        L.Changed += Refresh;
        Closed += (_, _) =>
        {
            model.Glossary.Changed -= OnGlossaryChanged;
            L.Changed -= Refresh;
        };
        var first = model.Glossary.Terms.FirstOrDefault();
        if (addText != null) Show(new GlossaryTerm { Text = addText });
        else if (first != null) Show(first);
        else Show(new GlossaryTerm());
    }

    private void OnGlossaryChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (selectedCategory is not (AllCategory or GeneralCategory) && !model.Glossary.Groups.Contains(selectedCategory))
            selectedCategory = AllCategory;
        FillGroups();
        RenderCategories();
        RenderList();
        RenderTry();
    });

    private void Refresh()
    {
        RenderCategories();
        RenderList();
        RenderTitle();
        RenderTry();
    }

    private bool IsNew => model.Glossary.Terms.All(t => t.Id != current.Id);
    private bool IsCustomCategory => selectedCategory != AllCategory && selectedCategory != GeneralCategory;

    private void Show(GlossaryTerm t)
    {
        current = t.Clone();
        loading = true;
        TextBox.Text = current.Text;
        MisheardBox.Text = string.Join("，", current.Misheard);
        TranslationBox.Text = GlossaryTerm.FormatTranslations(current.Translations);
        NoteBox.Text = current.Note;
        EnabledBox.IsChecked = current.Enabled;
        ExactBox.IsChecked = current.ExactOnly;
        FillGroups();
        GroupBox.Text = current.Group;
        loading = false;
        dirty = false;
        SavedText.Text = "";
        RenderTitle();
        RenderCategories();
        RenderList();
        RenderTry();
        if (current.Text.Length == 0) Dispatcher.BeginInvoke(() => TextBox.Focus());
    }

    private void RenderTitle()
    {
        TitleText.Text = current.Text.Length > 0 ? current.Text : L.T("新词条");
        var cat = (GroupBox.Text ?? "").Trim();
        DetailCategoryText.Text = cat.Length > 0 ? cat : "通用 / 未分类";
        DeleteBtn.Visibility = IsNew ? Visibility.Collapsed : Visibility.Visible;
        SaveBtn.IsEnabled = TextBox.Text.Trim().Length > 0;
    }

    // ── Column 1: Categories ──

    private void RenderCategories()
    {
        if (CategoryHost == null) return;
        var all = model.Glossary.Terms;
        var groups = model.Glossary.Groups;
        CategoryHost.Children.Clear();

        CategoryHost.Children.Add(CategoryRow(AllCategory, "全部词条", "\uE82D", all.Count, isCustom: false));
        CategoryHost.Children.Add(CategoryRow(GeneralCategory, "通用 / 未分类", "\uE774", all.Count(t => t.Group.Length == 0), isCustom: false));

        CategoryHost.Children.Add(new TextBlock
        {
            Text = "自定义分类目录",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Grey2,
            Margin = new Thickness(6, 12, 4, 5),
        });

        if (groups.Count == 0)
        {
            CategoryHost.Children.Add(new TextBlock
            {
                Text = "点右上角「＋ 新建」创建分类，如：电力专业、参会人名",
                FontSize = 11.5,
                Foreground = Theme.Grey2,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(6, 2, 6, 4),
            });
        }
        else
        {
            foreach (var g in groups)
                CategoryHost.Children.Add(CategoryRow(g, g, "\uE8B7", all.Count(t => t.Group == g), isCustom: true));
        }

        // Sync category-aware buttons
        ImportBtn.Content = IsCustomCategory ? $"↑ 导入到「{selectedCategory}」" : "↑ 导入词库";
        AddTermBtn.Content = IsCustomCategory ? $"＋ 在「{selectedCategory}」中添加词条" : "＋ 添加词条";
        CountText.Text = $"共 {all.Count} 个词条 · {groups.Count} 个分类";
    }

    private UIElement CategoryRow(string key, string label, string iconGlyph, int count, bool isCustom)
    {
        var active = selectedCategory == key;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = iconGlyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = active ? Theme.Primary : Theme.Grey2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var title = new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = active ? Theme.Primary : Theme.Ink,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = label,
        };
        var cnt = new TextBlock
        {
            Text = count.ToString(),
            FontSize = 11,
            Foreground = active ? Theme.Primary : Theme.Grey2,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 1);
        Grid.SetColumn(cnt, 2);
        grid.Children.Add(icon);
        grid.Children.Add(title);
        grid.Children.Add(cnt);

        var row = new Border
        {
            Background = active ? new SolidColorBrush(Color.FromArgb(0xEC, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent,
            BorderBrush = active ? new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x7A, 0xFF)) : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 7, 10, 7),
            Margin = new Thickness(0, 1.5, 2, 1.5),
            Cursor = Cursors.Hand,
            Child = grid,
        };
        row.MouseEnter += (_, _) => { if (selectedCategory != key) row.Background = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)); };
        row.MouseLeave += (_, _) => { if (selectedCategory != key) row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, _) => SelectCategory(key);

        if (isCustom)
            row.ContextMenu = BuildCategoryMenu(key);

        return row;
    }

    private void SelectCategory(string category)
    {
        if (selectedCategory == category) return;
        ConfirmLeave();
        selectedCategory = category;
        RenderCategories();
        RenderList();

        // Keep selection inside this category if possible
        var filtered = FilteredTerms();
        if (filtered.Count > 0 && filtered.All(t => t.Id != current.Id))
            Show(filtered[0]);
    }

    private ContextMenu BuildCategoryMenu(string group)
    {
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "重命名分类…" };
        rename.Click += (_, _) => RenameCategory(group);
        menu.Items.Add(rename);

        var scope = new MenuItem { Header = "设置适用会议模板…" };
        scope.Click += (_, _) => EditScope(group);
        menu.Items.Add(scope);

        menu.Items.Add(new Separator());
        var del = new MenuItem { Header = "删除分类（词条保留至通用）", Foreground = Theme.Danger };
        del.Click += (_, _) => DeleteCategory(group);
        menu.Items.Add(del);
        return menu;
    }

    private void ToggleNewCategory_Click(object sender, RoutedEventArgs e)
    {
        NewCategoryBar.Visibility = NewCategoryBar.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (NewCategoryBar.Visibility == Visibility.Visible)
        {
            NewCategoryBox.Text = "";
            NewCategoryBox.Focus();
        }
    }

    private void CancelNewCategory_Click(object sender, RoutedEventArgs e) => NewCategoryBar.Visibility = Visibility.Collapsed;

    private void ConfirmNewCategory_Click(object sender, RoutedEventArgs e) => CreateCategoryFromBar();

    private void NewCategoryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CreateCategoryFromBar(); e.Handled = true; }
        else if (e.Key == Key.Escape) { NewCategoryBar.Visibility = Visibility.Collapsed; e.Handled = true; }
    }

    private void CreateCategoryFromBar()
    {
        var name = NewCategoryBox.Text.Trim();
        if (name.Length == 0) { NewCategoryBox.Focus(); return; }
        model.Glossary.AddGroup(name);
        NewCategoryBar.Visibility = Visibility.Collapsed;
        selectedCategory = name;
        FillGroups();
        RenderCategories();
        RenderList();
        SavedText.Text = $"已创建分类「{name}」";
    }

    private void ManageCategory_Click(object sender, RoutedEventArgs e)
    {
        if (!IsCustomCategory || sender is not Button btn) return;
        var menu = BuildCategoryMenu(selectedCategory);
        menu.PlacementTarget = btn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        btn.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void RenameCategory(string oldName)
    {
        var next = AskCategoryName("重命名分类", $"输入「{oldName}」的新分类名称：", oldName);
        if (string.IsNullOrWhiteSpace(next) || next == oldName) return;
        if (model.Glossary.RenameGroup(oldName, next))
        {
            if (selectedCategory == oldName) selectedCategory = next;
            if (current.Group == oldName)
            {
                current.Group = next;
                loading = true;
                FillGroups();
                GroupBox.Text = next;
                loading = false;
                RenderTitle();
            }
            SavedText.Text = $"分类已重命名为「{next}」";
        }
    }

    private void DeleteCategory(string group)
    {
        var count = model.Glossary.Terms.Count(t => t.Group == group);
        var msg = count > 0
            ? $"分类「{group}」下的 {count} 个词条不会被删除，将自动移回「通用 / 未分类」。"
            : $"确定删除空分类「{group}」？";
        if (!Dialog.Confirm(this, $"删除分类「{group}」？", msg, "删除分类", danger: true)) return;
        model.Glossary.DeleteGroup(group);
        if (selectedCategory == group) selectedCategory = AllCategory;
        if (current.Group == group)
        {
            current.Group = "";
            loading = true;
            FillGroups();
            GroupBox.Text = "";
            loading = false;
            RenderTitle();
        }
        SavedText.Text = $"已删除分类「{group}」";
    }

    private string? AskCategoryName(string title, string prompt, string initial)
    {
        var box = new TextBox { Text = initial, Style = (Style)FindResource("Field"), Margin = new Thickness(0, 10, 0, 0) };
        string? result = null;
        var win = new Window
        {
            Owner = this,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = FontFamily,
            Title = title,
        };
        var ok = new Button { Content = "确定", Style = (Style)FindResource("PrimaryBtn"), Padding = new Thickness(16, 6, 16, 6), IsDefault = true };
        var cancel = new Button { Content = "取消", Style = (Style)FindResource("Btn"), Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        ok.Click += (_, _) => { result = box.Text.Trim(); win.Close(); };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        btns.Children.Add(cancel);
        btns.Children.Add(ok);
        var body = new StackPanel { Width = 320 };
        body.Children.Add(new TextBlock { Text = title, FontSize = 15.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink });
        body.Children.Add(new TextBlock { Text = prompt, FontSize = 12.5, Foreground = Theme.Grey, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(box);
        body.Children.Add(btns);
        win.Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF6, 0xFF, 0xFF, 0xFF)),
            BorderBrush = Theme.GlassRim,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(20, 18, 20, 16),
            Margin = new Thickness(16),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.20, Color = Color.FromRgb(0x0C, 0x19, 0x33) },
            Child = body,
        };
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        win.ShowDialog();
        return result;
    }

    // ── Column 2: Terms list ──

    private List<GlossaryTerm> FilteredTerms()
    {
        var all = model.Glossary.Terms;
        var q = SearchBox?.Text.Trim() ?? "";
        return all
            .Where(t => selectedCategory == AllCategory || t.Group == selectedCategory)
            .Where(t => q.Length == 0
                || t.Text.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.Misheard.Any(m => m.Contains(q, StringComparison.OrdinalIgnoreCase))
                || t.Note.Contains(q, StringComparison.OrdinalIgnoreCase)
                || t.Group.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Text, StringComparer.CurrentCulture)
            .ToList();
    }

    private void RenderList()
    {
        if (ListHost == null) return;
        var shown = FilteredTerms();
        CategoryHeaderText.Text = selectedCategory == AllCategory ? "全部词条"
            : selectedCategory == GeneralCategory ? "通用 / 未分类"
            : selectedCategory;
        CategoryCountText.Text = $"· {shown.Count} 条";
        ManageCategoryBtn.Visibility = IsCustomCategory ? Visibility.Visible : Visibility.Collapsed;
        if (IsCustomCategory)
        {
            CategoryScopeText.Text = "适用范围：" + ScopeText(selectedCategory);
            CategoryScopeText.Visibility = Visibility.Visible;
        }
        else
        {
            CategoryScopeText.Visibility = Visibility.Collapsed;
        }

        ListHost.Children.Clear();
        if (shown.Count == 0)
        {
            ListHost.Children.Add(new TextBlock
            {
                Text = SearchBox.Text.Trim().Length > 0
                    ? "当前分类下没有匹配的词条。"
                    : IsCustomCategory
                        ? $"「{selectedCategory}」分类里还没有词条。点下方「添加词条」或左下角「导入到『{selectedCategory}』」。"
                        : "还没有词条。把常被认错的人名、产品名、专业术语加进来，以后的会议都会自动纠正。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = Theme.Grey2,
                Margin = new Thickness(6, 14, 8, 0),
                LineHeight = 19,
            });
            return;
        }

        foreach (var t in shown) ListHost.Children.Add(Row(t));
    }

    private string ScopeText(string group)
    {
        var ids = model.Glossary.TemplatesOf(group);
        if (ids.Count == 0) return L.T("所有会议");
        var names = ids.Select(id => model.Templates.FirstOrDefault(t => t.Id == id) is { } t ? (t.BuiltIn ? L.T(t.Name) : t.Name) : null)
            .Where(n => n != null).ToList();
        return names.Count == 0 ? L.T("所有会议") : L.F("只在：{0}", string.Join("、", names));
    }

    private void EditScope(string group)
    {
        if (GroupScopeWindow.Ask(this, group, model.Templates, model.Glossary.TemplatesOf(group)) is { } chosen)
            model.Glossary.SetTemplates(group, chosen);
    }

    private UIElement Row(GlossaryTerm t)
    {
        var selected = t.Id == current.Id;
        var body = new StackPanel();
        var top = new DockPanel();
        if (t.Group.Length > 0)
        {
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x18, 0x00, 0x7A, 0xFF)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = t.Group, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Theme.Primary },
            };
            DockPanel.SetDock(badge, Dock.Right);
            top.Children.Add(badge);
        }
        top.Children.Add(new TextBlock
        {
            Text = t.Text,
            FontSize = 13,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = t.Enabled ? Theme.Ink : Theme.Grey2,
        });
        body.Children.Add(top);

        var detail = new List<string>();
        if (!t.Enabled) detail.Add(L.T("已停用"));
        if (t.Misheard.Count > 0) detail.Add(L.F("常被听成 {0}", string.Join("、", t.Misheard)));
        if (t.TranslationFor("en") is { } en) detail.Add(en);
        if (detail.Count > 0)
            body.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", detail),
                FontSize = 11,
                Foreground = Theme.Grey2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 0),
            });

        var row = new Border
        {
            Background = selected ? Theme.PrimarySoft : Brushes.Transparent,
            BorderBrush = selected ? new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x7A, 0xFF)) : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 1.5, 2, 1.5),
            Cursor = Cursors.Hand,
            Child = body,
        };
        row.MouseEnter += (_, _) => { if (t.Id != current.Id) row.Background = new SolidColorBrush(Color.FromArgb(0x70, 0xE8, 0xEE, 0xFA)); };
        row.MouseLeave += (_, _) => { if (t.Id != current.Id) row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, _) => { if (ConfirmLeave()) Show(t); };

        // Right-click term to move quickly between categories
        row.ContextMenu = BuildTermRowMenu(t);
        return row;
    }

    private ContextMenu BuildTermRowMenu(GlossaryTerm term)
    {
        var menu = new ContextMenu();
        var move = new MenuItem { Header = "移动到分类" };

        void AddTarget(string group, string label)
        {
            var mi = new MenuItem { Header = label, IsCheckable = true, IsChecked = term.Group == group };
            mi.Click += (_, _) =>
            {
                model.Glossary.MoveToGroup(term.Id, group);
                if (current.Id == term.Id)
                {
                    current.Group = group;
                    loading = true;
                    FillGroups();
                    GroupBox.Text = group;
                    loading = false;
                    RenderTitle();
                }
                SavedText.Text = group.Length > 0 ? $"已移入分类「{group}」" : "已移入「通用 / 未分类」";
            };
            move.Items.Add(mi);
        }

        AddTarget("", "通用 / 未分类");
        foreach (var g in model.Glossary.Groups) AddTarget(g, g);
        menu.Items.Add(move);

        menu.Items.Add(new Separator());
        var del = new MenuItem { Header = "删除词条", Foreground = Theme.Danger };
        del.Click += (_, _) =>
        {
            if (!Dialog.Confirm(this, L.F("删除词条「{0}」？", term.Text), L.T("之后的会议不再按它纠正和翻译，已经记下的内容不受影响。"), L.T("删除"), danger: true)) return;
            model.Glossary.Remove(term.Id);
            if (current.Id == term.Id)
            {
                dirty = false;
                Show(FilteredTerms().FirstOrDefault() ?? new GlossaryTerm { Group = IsCustomCategory ? selectedCategory : "" });
            }
        };
        menu.Items.Add(del);
        return menu;
    }

    // ── Column 3: Editor ──

    private bool ConfirmLeave()
    {
        if (!dirty || TextBox.Text.Trim().Length == 0) return true;
        Save();
        return true;
    }

    private GlossaryTerm FromFields() => new()
    {
        Id = current.Id,
        Text = TextBox.Text.Trim(),
        Misheard = GlossaryTerm.ParseList(MisheardBox.Text),
        Translations = GlossaryTerm.ParseTranslations(TranslationBox.Text),
        Note = NoteBox.Text.Trim(),
        Enabled = EnabledBox.IsChecked == true,
        ExactOnly = ExactBox.IsChecked == true,
        Group = (GroupBox.Text ?? "").Trim(),
    };

    private void FillGroups()
    {
        var was = loading;
        loading = true;
        var text = GroupBox.Text;
        GroupBox.Items.Clear();
        foreach (var g in model.Glossary.Groups) GroupBox.Items.Add(g);
        GroupBox.Text = text;
        loading = was;
    }

    private void Group_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (loading || GroupBox.SelectedItem is not string g) return;
        GroupBox.Text = g;
        Field_Changed(sender, e);
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || !IsLoaded && sender != TextBox) return;
        dirty = true;
        SavedText.Text = "";
        RenderTitle();
        RenderTry();
    }

    private void Save()
    {
        var t = FromFields();
        if (t.Text.Length == 0) return;
        model.Glossary.Save(t);
        current = model.Glossary.Terms.FirstOrDefault(x => x.Id == t.Id) ?? model.Glossary.Terms.First(x => x.Text == t.Text);
        dirty = false;
        SavedText.Text = current.Group.Length > 0
            ? $"已保存到分类「{current.Group}」，后续识别与纪要自动生效"
            : L.T("已保存，之后识别、翻译和纪要都会用上");
        FillGroups();
        RenderTitle();
        RenderCategories();
        RenderList();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => UiWatch.Time("glossary save", Save);

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmLeave())
            Show(new GlossaryTerm { Group = IsCustomCategory ? selectedCategory : "" });
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (IsNew) return;
        if (!Dialog.Confirm(this, L.F("删除词条「{0}」？", current.Text), L.T("之后的会议不再按它纠正和翻译，已经记下的内容不受影响。"), L.T("删除"), danger: true)) return;
        model.Glossary.Remove(current.Id);
        dirty = false;
        var next = FilteredTerms().FirstOrDefault() ?? model.Glossary.Terms.FirstOrDefault();
        Show(next ?? new GlossaryTerm { Group = IsCustomCategory ? selectedCategory : "" });
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => RenderList();

    // ── Try it ──

    private void Try_Changed(object sender, TextChangedEventArgs e) => RenderTry();

    private void RenderTry()
    {
        if (TryResult == null) return;
        var text = TryBox.Text;
        if (text.Trim().Length == 0)
        {
            TryResult.Text = "";
            TryFixes.Text = "比如输入「下周给专者汇报电力砍价的方案」，看看会不会自动改对。";
            return;
        }
        var terms = model.Glossary.Terms.Where(t => t.Id != current.Id).ToList();
        var edited = FromFields();
        if (edited.Text.Length > 0) terms.Add(edited);
        var (fixedText, fixes) = new TermFixer(terms, model.Glossary.DialectTolerance).Fix(text);
        TryResult.Text = fixedText;
        TryFixes.Text = fixes.Count == 0 ? L.T("没有需要改的。")
            : string.Join("；", fixes.Select(f => L.F("{0} → {1}", f.From, f.To)));
    }

    // ── Built-in Packs & Category-aware Import / Export ──

    private void BuiltIn_Click(object sender, RoutedEventArgs e)
    {
        if (BuiltInGlossaryWindow.Ask(this, model.Glossary) is not { Count: > 0 } chosen) return;
        var (added, updated) = BuiltInGlossaries.Import(model.Glossary, chosen);
        var firstPack = BuiltInGlossaries.All.FirstOrDefault(p => p.Id == chosen[0]);
        if (firstPack != null && (selectedCategory == AllCategory || chosen.Count == 1))
            selectedCategory = firstPack.Category;
        FillGroups();
        RenderCategories();
        RenderList();
        if ((IsNew && TextBox.Text.Trim().Length == 0) || FilteredTerms().All(t => t.Id != current.Id))
        {
            if (FilteredTerms().FirstOrDefault() is { } first) Show(first);
        }
        SavedText.Text = $"已导入内置词包：新增 {added} 个，更新 {updated} 个";
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var targetGroup = IsCustomCategory ? selectedCategory : "";
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = targetGroup.Length > 0 ? $"导入词条到分类「{targetGroup}」" : L.T("导入词库"),
            Filter = L.T("文本文件") + " (*.txt;*.tsv;*.csv)|*.txt;*.tsv;*.csv|" + L.T("所有文件") + " (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var (added, updated) = model.Glossary.Import(System.IO.File.ReadAllText(dialog.FileName), defaultGroup: targetGroup);
            var suffix = targetGroup.Length > 0 ? $"（未指定分类的词条已归入「{targetGroup}」）" : "";
            var msg = $"导入完成：新增 {added} 个，更新 {updated} 个{suffix}";
            if (IsNew && FilteredTerms().FirstOrDefault() is { } first && TextBox.Text.Trim().Length == 0) Show(first);
            SavedText.Text = msg;
        }
        catch (Exception ex)
        {
            Dialog.Confirm(this, L.T("导入失败"), ex.Message, L.T("知道了"));
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (selectedCategory == AllCategory)
        {
            ExportScope(null, $"{Brand.Name}词库_全部.txt");
            return;
        }
        var menu = new ContextMenu { PlacementTarget = btn, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        var curLabel = selectedCategory == GeneralCategory ? "通用 / 未分类" : selectedCategory;
        var one = new MenuItem { Header = $"仅导出当前分类「{curLabel}」" };
        one.Click += (_, _) => ExportScope(selectedCategory, $"{Brand.Name}词库_{curLabel}.txt");
        var all = new MenuItem { Header = "导出全部词库（含所有分类）" };
        all.Click += (_, _) => ExportScope(null, $"{Brand.Name}词库_全部.txt");
        menu.Items.Add(one);
        menu.Items.Add(all);
        btn.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void ExportScope(string? onlyGroup, string defaultName)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safeName = new string(defaultName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = L.T("导出词库"),
            FileName = safeName,
            Filter = L.T("文本文件") + " (*.txt)|*.txt",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            System.IO.File.WriteAllText(dialog.FileName, model.Glossary.Export(onlyGroup), new System.Text.UTF8Encoding(true));
            SavedText.Text = L.F("已导出到 {0}", dialog.FileName);
        }
        catch (Exception ex)
        {
            Dialog.Confirm(this, L.T("导出失败"), ex.Message, L.T("知道了"));
        }
    }
}
