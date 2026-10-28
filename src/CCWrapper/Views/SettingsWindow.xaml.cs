using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CCWrapper.Core;
using CCWrapper.Data;
using CCWrapper.Terminal;

namespace CCWrapper.Views;

public enum FieldKind { Select, Combo, Segments, Text }

/// <summary>One setting row. <paramref name="Values"/> are the JSON values the options write when that isn't the option's
/// own text (a boolean); <paramref name="AnyTrueWins"/>: true in any file wins (disableClaudeAiConnectors), not the most
/// specific file; <paramref name="SessionOnly"/>: one more segment, shown disabled, that Claude Code takes only per session.</summary>
public sealed record FieldDef(string[] Path, string Label, string Description, FieldKind Kind, string[] Options,
    JsonNode?[]? Values = null, string? Placeholder = null, bool AnyTrueWins = false, string? SessionOnly = null)
{
    public string Key => string.Join('.', Path);
    public JsonNode ValueOf(int i) => Values?[i]?.DeepClone() ?? JsonValue.Create(Options[i])!;
}

/// <summary>The account's settings.json files (user, and the project folder's shared and local ones) and its CLAUDE.md
/// files. Edits stay pending until Save, which writes only the files that changed, only if they are unchanged on disk since
/// they were read, after a backup to %LOCALAPPDATA%\CCWrapper\backups.</summary>
public partial class SettingsWindow : Window
{
    internal static readonly string BackupDir = Path.Combine(ClaudeLaunch.AppDataDir, "backups");
    static readonly string[] Effort = ["low", "medium", "high", "xhigh"];
    static readonly string[] HookEvents =
        ["PreToolUse", "PostToolUse", "UserPromptSubmit", "Notification", "Stop", "SubagentStop", "SessionStart", "SessionEnd", "PreCompact", "PostCompact", "ConfigChange", "InstructionsLoaded"];
    static readonly (string Kind, string Title, string Brush, string Sub)[] RuleKinds =
        [("allow", "Allow", "Good", "Runs without asking."), ("ask", "Ask", "Warn", "Always asks first, even in auto mode."), ("deny", "Deny", "Bad", "Never allowed.")];

    readonly Account _account;
    readonly bool _ready;
    readonly List<(FieldDef Def, TextBlock Note, Button Remove, Border Row)> _rows = [];
    readonly ObservableCollection<EnvRowView> _env = [];
    Dictionary<SettingsScope, SettingsFile> _files = [];
    Dictionary<SettingsScope, TextFile> _memory = [];
    string? _cwd;
    DateTime _loadedAt;
    bool _filling, _quiet, _saved;
    string? _jsonError, _envError, _appError; // typed text that does not parse: Save waits until it does
    CompactPrefs _app = CompactPrefs.Load(), _appSaved = CompactPrefs.Load(); // the CCWrapper page: app.json, not a settings file
    bool _browser = AppPrefs.ReadBrowserTool(), _browserSaved = AppPrefs.ReadBrowserTool();

    public SettingsWindow(Account account, string? cwd)
    {
        InitializeComponent();
        _account = account;
        _cwd = cwd;
        Title = $"Settings · {ShellModel.LetterOf(account.Letter)}";
        UserSub.Text = $"Every folder · ~\\{Path.GetFileName(account.ConfigRoot)}\\settings.json";
        EnvList.ItemsSource = _env;
        _ready = true; // the XAML's IsChecked="True" raises Checked before the fields above exist
        Loaded += async (_, _) => await LoadAsync();
    }

    async Task LoadAsync()
    {
        _filling = true;
        _cwd = await ProjectChoice.FillAsync(ProjectBox, _account, _cwd);
        _filling = false;
        await ReloadAsync(keepUser: false);
    }

    /// <summary>Reads the files again; <paramref name="keepUser"/> keeps the user files and their pending edits (a folder switch).</summary>
    async Task ReloadAsync(bool keepUser)
    {
        var (account, cwd) = (_account, _cwd);
        try
        {
            var (files, memory) = await Task.Run(() => Load(account, cwd));
            if (keepUser) (files[SettingsScope.User], memory[SettingsScope.User]) = (_files[SettingsScope.User], _memory[SettingsScope.User]);
            (_files, _memory, _loadedAt) = (files, memory, DateTime.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveText.Text = "Could not read the settings files: " + ex.Message;
            return;
        }
        _quiet = true;
        ScopeProject.IsEnabled = ScopeLocal.IsEnabled = MemProject.IsEnabled = MemLocal.IsEnabled = cwd is not null;
        ScopeProject.ToolTip = ScopeLocal.ToolTip = cwd is null ? "Pick a project folder first" : null;
        if (cwd is null) (ScopeUser.IsChecked, MemUser.IsChecked) = (true, true);
        else if (MemUser.IsChecked != true && MemLocal.IsChecked != true) MemProject.IsChecked = true;
        _quiet = false;
        ShowPage();
    }

    static (Dictionary<SettingsScope, SettingsFile>, Dictionary<SettingsScope, TextFile>) Load(Account account, string? cwd)
    {
        var files = new Dictionary<SettingsScope, SettingsFile> { [SettingsScope.User] = SettingsFile.Load(SettingsScope.User, account.Paths.Settings) };
        var memory = new Dictionary<SettingsScope, TextFile> { [SettingsScope.User] = TextFile.Load(account.Paths.ClaudeMd) };
        if (cwd is null) return (files, memory);
        files[SettingsScope.Project] = SettingsFile.Load(SettingsScope.Project, Path.Combine(cwd, ".claude", "settings.json"));
        files[SettingsScope.Local] = SettingsFile.Load(SettingsScope.Local, Path.Combine(cwd, ".claude", "settings.local.json"));
        memory[SettingsScope.Project] = TextFile.Load(Path.Combine(cwd, "CLAUDE.md"));
        memory[SettingsScope.Local] = TextFile.Load(Path.Combine(cwd, "CLAUDE.local.md"));
        return (files, memory);
    }

    // ---- state ----

    SettingsScope Scope => ScopeLocal.IsChecked == true ? SettingsScope.Local : ScopeProject.IsChecked == true ? SettingsScope.Project : SettingsScope.User;
    SettingsScope MemScope => MemLocal.IsChecked == true ? SettingsScope.Local : MemProject.IsChecked == true ? SettingsScope.Project : SettingsScope.User;
    SettingsFile? Current => _files.GetValueOrDefault(Scope);
    SettingsFile? Editable => Current is { Error: null } f ? f : null;
    string Folder => _cwd is null ? "this folder" : Path.GetFileName(_cwd.TrimEnd('\\'));

    string Page => SearchBox.Text.Trim().Length > 0 ? "search"
        : NavPermissions.IsChecked == true ? "permissions" : NavInterface.IsChecked == true ? "interface"
        : NavHooks.IsChecked == true ? "hooks" : NavEnv.IsChecked == true ? "env"
        : NavMemory.IsChecked == true ? "memory" : NavJson.IsChecked == true ? "json" : NavApp.IsChecked == true ? "app"
        : NavAccount.IsChecked == true ? "account" : "general";

    Brush B(string key) => (Brush)FindResource(key);

    // ---- pages ----

    void OnNav(object sender, RoutedEventArgs e)
    {
        if (!_ready || _quiet) return;
        _quiet = true;
        SearchBox.Clear();
        _quiet = false;
        ShowPage();
    }

    void OnSearch(object sender, TextChangedEventArgs e)
    {
        if (_ready && !_quiet) ShowPage();
    }

    void OnScope(object sender, RoutedEventArgs e)
    {
        if (_ready && !_quiet) ShowPage();
    }

    void OnViewJson(object sender, RoutedEventArgs e) => NavJson.IsChecked = true;

    /// <summary>Rebuilds the page from the pending files. Text typed into the JSON or environment page that did not parse is dropped.</summary>
    void ShowPage()
    {
        var page = Page;
        static Visibility V(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        FieldsPage.Visibility = V(page is "general" or "permissions" or "interface" or "search");
        RulesPanel.Visibility = V(page == "permissions");
        HooksPage.Visibility = V(page == "hooks");
        EnvPage.Visibility = V(page == "env");
        MemoryPage.Visibility = V(page == "memory");
        JsonPage.Visibility = V(page == "json");
        AppPage.Visibility = V(page == "app");
        AccountPage.Visibility = V(page == "account");
        ScopeArea.Visibility = V(page is not ("memory" or "app" or "account"));
        MemoryNote.Visibility = V(page == "memory");
        ViewJsonLink.Visibility = V(page != "json");
        var letter = ShellModel.LetterOf(_account.Letter);
        (PageTitle.Text, PageSubtitle.Text) = page switch
        {
            "permissions" => ("Permissions", "What Claude may do without asking, must ask about, or may never do. Rules of every file apply together."),
            "interface" => ("Interface", "The switches of Claude Code's /config, for how sessions look and behave."),
            "hooks" => ("Hooks", "Commands Claude Code runs on its events. Hooks of every file run."),
            "env" => ("Environment", "Variables every session starts with."),
            "memory" => ("CLAUDE.md", "Instructions Claude reads at the start of every session."),
            "json" => ("JSON", "The whole file, for anything the pages above do not cover."),
            "app" => ("CCWrapper", "Settings of this app, not of Claude Code."),
            "account" => ($"Account {letter}", "Who this window is signed in as, and Claude Code's own facts. Nothing here is written."),
            "search" => ($"Settings matching \"{SearchBox.Text.Trim()}\"", "From General, Permissions and Interface."),
            _ => ("General", $"Model, look and updates for Claude Code sessions of account {letter}."),
        };
        if (_files.Count == 0) return; // still loading
        _jsonError = _envError = _appError = null;
        ShowScope();
        switch (page)
        {
            case "hooks": ShowHooks(); break;
            case "env": ShowEnv(); break;
            case "memory": ShowMemory(); break;
            case "json": ShowJson(); break;
            case "app": ShowApp(); break;
            case "account": ShowAccount(); break;
            default:
                BuildRows();
                if (page == "permissions") ShowRules();
                break;
        }
        UpdateCounts();
        UpdateSaveBar();
    }

    /// <summary>After an edit: notes, counts and the save bar follow; <paramref name="rebuild"/> rebuilds the page too.</summary>
    void Changed(bool rebuild = false)
    {
        _saved = false;
        if (rebuild) { ShowPage(); return; }
        RefreshRows();
        ShowScope();
        UpdateCounts();
        UpdateSaveBar();
    }

    void ShowScope()
    {
        string CountOf(SettingsScope s) => !_files.TryGetValue(s, out var f) ? "" : f.Error is not null ? "not valid JSON"
            : f.LoadedText is null && !f.Dirty ? "no file yet" : $"{f.Pending.Count} set";
        (UserCount.Text, ProjectCount.Text, LocalCount.Text) = (CountOf(SettingsScope.User), CountOf(SettingsScope.Project), CountOf(SettingsScope.Local));
        var file = Current;
        FilePath.Text = file?.Path ?? "Pick a project folder";
        (FileState.Text, FileState.Foreground) = file is null ? ("", B("Muted"))
            : file.Error is not null ? ("Not valid JSON", B("Bad"))
            : file.LoadedText is null ? ("Does not exist yet: saving creates it", B("Warn"))
            : ($"Read at {_loadedAt:HH:mm}", B("Muted"));
        BrokenBanner.Visibility = file?.Error is not null && Page != "json" ? Visibility.Visible : Visibility.Collapsed;
        BrokenText.Text = $"This file isn't valid JSON ({file?.Error}). Fix it on the JSON page; until then its settings can't be changed here.";
    }

    void UpdateCounts()
    {
        PermissionsCount.Text = _files.Values.Sum(f => RuleKinds.Sum(k => SettingsData.Rules(f.Pending, k.Kind).Count)) is > 0 and var rules ? rules.ToString() : "";
        HooksCount.Text = _files.Values.Sum(f => SettingsData.Hooks(f.Pending).Count) is > 0 and var hooks ? hooks.ToString() : "";
    }

    // ---- setting rows ----

    IEnumerable<FieldDef> GeneralFields()
    {
        yield return new(["model"], "Model", "What new sessions start with. /model still changes one session.", FieldKind.Combo,
            ["default", "opus", "sonnet", "haiku", "opus[1m]", "sonnet[1m]"]);
        yield return new(["effortLevel"], "Effort", "How hard the model thinks, for models without their own effort below.", FieldKind.Segments, Effort,
            SessionOnly: "max");
        foreach (var id in _files.Values.SelectMany(f => f.Pending["modelSettings"] is JsonObject m ? m.Select(p => p.Key) : []).Distinct().Order())
            yield return new(["modelSettings", id, "effortLevel"], $"Effort · {id}", "This model's own effort: it wins over Effort.", FieldKind.Segments, Effort,
                SessionOnly: "max");
        yield return new(["outputStyle"], "Output style", "How Claude writes its answers. Your own styles work too: type the name.", FieldKind.Combo,
            ["Default", "Explanatory", "Learning"]);
        yield return new(["language"], "Language", "The language Claude answers in. Empty is English.", FieldKind.Text, [], Placeholder: "e.g. italian");
        yield return new(["theme"], "Theme", "The terminal theme inside each session.", FieldKind.Combo,
            ["auto", "dark", "light", "dark-daltonized", "light-daltonized", "dark-ansi", "light-ansi"]);
        yield return new(["timeFormat"], "Time format", "Clock times in the session.", FieldKind.Segments, ["auto", "12-hour", "24-hour"]);
        yield return new(["autoUpdatesChannel"], "Update channel", "latest gets new versions first, stable about a week later, rc gets release candidates.",
            FieldKind.Segments, ["latest", "stable", "rc"]);
        yield return new(["attribution", "commit"], "Commit trailer", "Added to commits Claude makes. Empty adds none.", FieldKind.Text, [],
            Placeholder: "Claude Code's default trailer");
        yield return new(["disableClaudeAiConnectors"], "claude.ai connectors", "Off hides every claude.ai connector from all sessions. Off in any file wins.",
            FieldKind.Segments, ["on", "off"], [false, true], AnyTrueWins: true);
    }

    static IEnumerable<FieldDef> PermissionFields()
    {
        yield return new(["permissions", "defaultMode"], "Default permission mode", "The mode a new session starts in. A Project file can't set auto.",
            FieldKind.Select, ["default", "acceptEdits", "plan", "auto", "dontAsk", "bypassPermissions"]);
    }

    /// <summary>/config's switches. Claude Code reads these from settings.json first (/config writes them to the user file);
    /// .claude.json holds only values older versions saved there.</summary>
    static IEnumerable<FieldDef> InterfaceFields()
    {
        string[] onOff = ["on", "off"];
        JsonNode?[] bools = [true, false];
        FieldDef Switch(string key, string label, string description) => new([key], label, description, FieldKind.Segments, onOff, bools);
        yield return Switch("autoCompactEnabled", "Auto-compact", "Compact the conversation when the context fills up. On by default.");
        yield return Switch("fileCheckpointingEnabled", "Rewind code (checkpoints)", "Snapshot edited files so /rewind can restore them. On by default.");
        yield return Switch("verbose", "Verbose output", "Full tool output instead of summaries. Off by default.");
        yield return Switch("showTurnDuration", "Show turn duration", "How long each answer took, after it. On by default.");
        yield return Switch("spinnerTipsEnabled", "Show tips", "Tips while Claude works. On by default.");
        yield return Switch("prefersReducedMotion", "Reduce motion", "Fewer animations: spinners, shimmer and flashes. Off by default.");
        yield return Switch("terminalProgressBarEnabled", "Terminal progress bar", "Progress shown by terminals that support it. On by default.");
        yield return Switch("respectGitignore", "Respect .gitignore in file picker", "Files .gitignore excludes stay out of @ mentions. On by default.");
        yield return new(["editorMode"], "Editor mode", "Key bindings of the prompt.", FieldKind.Segments, ["normal", "vim"]);
        yield return new(["preferredNotifChannel"], "Notifications", "How a finished task or a permission prompt is signaled.", FieldKind.Select,
            ["auto", "terminal_bell", "notifications_disabled"]);
    }

    void BuildRows()
    {
        _rows.Clear();
        FieldRows.Children.Clear();
        var q = SearchBox.Text.Trim();
        var defs = (Page switch
        {
            "permissions" => PermissionFields(),
            "interface" => InterfaceFields(),
            "search" => GeneralFields().Concat(PermissionFields()).Concat(InterfaceFields()).Where(d => new[] { d.Label, d.Key, d.Description }.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase))),
            _ => GeneralFields(),
        }).ToList();
        foreach (var def in defs) FieldRows.Children.Add(BuildRow(def));
        FieldRowsBox.Visibility = defs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoMatches.Visibility = defs.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshRows();
    }

    Border BuildRow(FieldDef def)
    {
        var file = Editable;
        var note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var remove = new Button { Style = (Style)FindResource("Link"), Content = "Remove from this file", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        remove.Click += (_, _) => { file?.Set(def.Path, null); Changed(rebuild: true); };

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = def.Label, FontWeight = FontWeights.SemiBold });
        head.Children.Add(new TextBlock { Text = def.Key, Style = (Style)FindResource("Mono"), FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var noteLine = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        noteLine.Children.Add(note);
        noteLine.Children.Add(remove);
        var left = new StackPanel();
        left.Children.Add(head);
        left.Children.Add(new TextBlock { Text = def.Description, Style = (Style)FindResource("Dim"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
        left.Children.Add(noteLine);

        var control = Control(def, file);
        control.IsEnabled = file is not null;
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(20, 0, 0, 0);
        AutomationPropertiesName(control, def.Label);
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        grid.Children.Add(left);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        var row = new Border { BorderBrush = B("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(16, 12, 16, 12), Child = grid };
        _rows.Add((def, note, remove, row));
        return row;
    }

    static void AutomationPropertiesName(DependencyObject element, string name) => element.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, name);

    /// <summary>The editor of one setting. Its handlers write straight into the pending file.</summary>
    FrameworkElement Control(FieldDef def, SettingsFile? file)
    {
        var value = file?.Get(def.Path);
        var index = value is null ? -1 : Enumerable.Range(0, def.Options.Length).FirstOrDefault(i => JsonNode.DeepEquals(def.ValueOf(i), value), -1);
        static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString();
        switch (def.Kind)
        {
            case FieldKind.Segments:
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal };
                var group = Guid.NewGuid().ToString();
                for (var i = 0; i < def.Options.Length; i++)
                {
                    var option = i;
                    var choice = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = group, Content = def.Options[i], IsChecked = i == index };
                    choice.Checked += (_, _) => { file?.Set(def.Path, def.ValueOf(option)); Changed(); };
                    panel.Children.Add(choice);
                }
                if (def.SessionOnly is { } only)
                {
                    // Claude Code drops it from settings.json without a word: offering it would save nothing.
                    var off = new RadioButton { Style = (Style)FindResource("Segment"), Content = only, IsEnabled = false,
                        ToolTip = $"Claude Code doesn't save {only} in settings. Use /effort {only} in a session." };
                    ToolTipService.SetShowOnDisabled(off, true);
                    panel.Children.Add(off);
                }
                return new Border { Background = B("Bg"), BorderBrush = B("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = panel };
            }
            case FieldKind.Select:
            {
                var box = new ComboBox { MinWidth = 200 };
                box.Items.Add(new ComboBoxItem { Content = "Not set" });
                foreach (var o in def.Options) box.Items.Add(new ComboBoxItem { Content = o });
                if (value is not null && index < 0) box.Items.Add(new ComboBoxItem { Content = Str(value) }); // a value this list doesn't know stays pickable
                box.SelectedIndex = value is null ? 0 : index >= 0 ? index + 1 : box.Items.Count - 1;
                box.SelectionChanged += (_, _) =>
                {
                    if (box.SelectedIndex > def.Options.Length) return; // the unknown value: unchanged
                    file?.Set(def.Path, box.SelectedIndex <= 0 ? null : def.ValueOf(box.SelectedIndex - 1));
                    Changed();
                };
                return box;
            }
            case FieldKind.Combo:
            {
                var combo = new ComboBox { IsEditable = true, MinWidth = 200, ItemsSource = def.Options, Text = Str(value) ?? "" };
                combo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
                {
                    var text = combo.Text.Trim();
                    if (JsonNode.DeepEquals(file?.Get(def.Path), text.Length == 0 ? null : JsonValue.Create(text))) return;
                    file?.Set(def.Path, text.Length == 0 ? null : JsonValue.Create(text));
                    Changed();
                }));
                return combo;
            }
            default:
            {
                var wasSet = value is not null; // then empty is a value ("" = no trailer); else empty means not set
                var box = new TextBox { Width = 240, Text = Str(value) ?? "" };
                var hint = new TextBlock { Text = def.Placeholder, Foreground = B("Muted"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false, Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed };
                box.TextChanged += (_, _) =>
                {
                    hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                    file?.Set(def.Path, box.Text.Length == 0 && !wasSet ? null : JsonValue.Create(box.Text));
                    Changed();
                };
                var grid = new Grid();
                grid.Children.Add(box);
                grid.Children.Add(hint);
                return grid;
            }
        }
    }

    void RefreshRows()
    {
        var file = Current;
        foreach (var (def, note, remove, row) in _rows)
        {
            (note.Text, note.Foreground) = NoteOf(def);
            remove.Visibility = file is { Error: null } && file.Get(def.Path) is not null ? Visibility.Visible : Visibility.Collapsed;
            row.Background = file is not null && !JsonNode.DeepEquals(file.Get(def.Path), file.GetSaved(def.Path)) ? B("ChangedFill") : Brushes.Transparent;
        }
    }

    (string, Brush) NoteOf(FieldDef def)
    {
        if (def.AnyTrueWins && SettingsData.Order.Where(s => s != Scope && _files.TryGetValue(s, out var f) && f.Get(def.Path) is JsonValue v
                && v.TryGetValue<bool>(out var on) && on).Select(s => (SettingsScope?)s).FirstOrDefault() is { } off)
            return ($"The {off} file turns this off, and off in any file wins", B("Warn"));
        var (kind, from, value) = SettingsData.Source(_files, Scope, def.Path);
        string Show() => def.Values is { } values && Array.FindIndex(values, x => JsonNode.DeepEquals(x, value)) is >= 0 and var i ? def.Options[i] : SettingsData.Show(value);
        return kind switch
        {
            SourceKind.Overridden => ($"{from} overrides this in {Folder}: {Show()}", B("Warn")),
            SourceKind.SetHere => ("Set in this file", B("Good")),
            SourceKind.Inherited => ($"Not set here · uses {Show()} from {from}", B("Muted")),
            _ => ("Not set · Claude Code's default", B("Muted")),
        };
    }

    // ---- permission rules ----

    void ShowRules()
    {
        var file = Editable;
        RuleLists.ItemsSource = RuleKinds.Select(k =>
        {
            var rules = file is null ? [] : SettingsData.Rules(file.Pending, k.Kind);
            var elsewhere = SettingsData.Order.Where(s => s != Scope && _files.ContainsKey(s))
                .Select(s => (Scope: s, Count: SettingsData.Rules(_files[s].Pending, k.Kind).Count)).Where(x => x.Count > 0)
                .Select(x => $"{x.Scope} adds {x.Count}").ToList();
            return new RuleListView(k.Title, B(k.Brush), k.Sub + (rules.Count > 0 ? $" · {rules.Count} rule{(rules.Count == 1 ? "" : "s")}" : ""),
                "None in this file." + (elsewhere.Count > 0 ? $" {string.Join(", ", elsewhere)} in {Folder}." : ""),
                rules.Select(r => new RuleChip(k.Kind, r)).ToList());
        }).ToList();
    }

    void OnRuleTyped(object sender, TextChangedEventArgs e) => RuleError.Visibility = Visibility.Collapsed;

    void OnAddRule(object sender, RoutedEventArgs e)
    {
        if (Editable is not { } file) return;
        var text = RuleBox.Text.Trim();
        if (!Regex.IsMatch(text, @"^[A-Za-z_][\w.-]*(\(.+\))?$"))
        {
            RuleError.Text = "A rule is a tool name, with a pattern in brackets if needed: Bash(npm test:*), Read(~/.ssh/**), mcp__server__tool.";
            RuleError.Visibility = Visibility.Visible;
            return;
        }
        var kind = (RuleKind.SelectedItem as ComboBoxItem)?.Tag as string ?? "allow";
        SettingsData.SetRules(file, kind, SettingsData.Rules(file.Pending, kind).Append(text));
        RuleBox.Clear();
        Changed(rebuild: true);
    }

    void OnRemoveRule(object sender, RoutedEventArgs e)
    {
        if (Editable is not { } file || (sender as FrameworkElement)?.DataContext is not RuleChip chip) return;
        SettingsData.SetRules(file, chip.Kind, SettingsData.Rules(file.Pending, chip.Kind).Where(r => r != chip.Text));
        Changed(rebuild: true);
    }

    // ---- hooks ----

    void ShowHooks()
    {
        var file = Editable;
        var rows = file is null ? [] : SettingsData.Hooks(file.Pending);
        HookList.ItemsSource = rows.Select(r => new HookRowView(r)).ToList();
        var elsewhere = SettingsData.Order.Where(s => s != Scope && _files.ContainsKey(s))
            .Select(s => (Scope: s, Count: SettingsData.Hooks(_files[s].Pending).Count)).Where(x => x.Count > 0).ToList();
        var verb = elsewhere is [{ Count: 1 }] ? "runs" : "run";
        NoHooks.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoHooksText.Text = "No hooks in this file." + (elsewhere.Count > 0
            ? $" {string.Join(" and ", elsewhere.Select(x => $"The {x.Scope} file's {x.Count} hook{(x.Count == 1 ? "" : "s")}"))} still {verb} in {Folder}." : "");
        AddHookButton.IsEnabled = file is not null;
    }

    void OnAddHook(object sender, RoutedEventArgs e)
    {
        if (Editable is not { } file) return;
        var dialog = new HookDialog(HookEvents, "PreToolUse", null, null, null) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        SettingsData.AddHook(file, dialog.Event, dialog.Matcher, dialog.Command, dialog.Timeout);
        Changed(rebuild: true);
    }

    void OnEditHook(object sender, RoutedEventArgs e)
    {
        if (Editable is not { } file || (sender as FrameworkElement)?.DataContext is not HookRowView { Row: var row }) return;
        var dialog = new HookDialog(HookEvents, row.Event, row.Matcher, row.Command, row.Timeout) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        SettingsData.RemoveHook(file, row);
        SettingsData.AddHook(file, dialog.Event, dialog.Matcher, dialog.Command, dialog.Timeout);
        Changed(rebuild: true);
    }

    void OnRemoveHook(object sender, RoutedEventArgs e)
    {
        if (Editable is not { } file || (sender as FrameworkElement)?.DataContext is not HookRowView { Row: var row }) return;
        SettingsData.RemoveHook(file, row);
        Changed(rebuild: true);
    }

    // ---- environment ----

    void ShowEnv()
    {
        _env.Clear();
        if (Editable is { } file)
            foreach (var (name, value) in SettingsData.Env(file.Pending)) _env.Add(new EnvRowView(name, value));
        AddEnvButton.IsEnabled = Editable is not null;
        EnvError.Visibility = Visibility.Collapsed;
    }

    void OnEnvTyped(object sender, TextChangedEventArgs e)
    {
        if (!_ready || sender is not TextBox { DataContext: EnvRowView row, Tag: string field } box) return;
        if (field == "Name") row.Name = box.Text; else row.Value = box.Text;
        ApplyEnv();
    }

    void OnRemoveEnv(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EnvRowView row) return;
        _env.Remove(row);
        ApplyEnv();
    }

    void OnAddEnv(object sender, RoutedEventArgs e) => _env.Add(new EnvRowView("", ""));

    void ApplyEnv()
    {
        if (Editable is not { } file) return;
        _envError = SettingsData.SetEnv(file, _env.Select(r => (r.Name, r.Value)).ToList());
        EnvError.Text = _envError ?? "";
        EnvError.Visibility = _envError is null ? Visibility.Collapsed : Visibility.Visible;
        Changed();
    }

    // ---- CLAUDE.md ----

    void OnMemoryTab(object sender, RoutedEventArgs e)
    {
        if (_ready && !_quiet && _memory.Count > 0) ShowMemory();
    }

    void ShowMemory()
    {
        var has = _memory.TryGetValue(MemScope, out var m);
        _quiet = true;
        MemoryBox.Text = m?.Pending ?? "";
        _quiet = false;
        MemoryBox.IsEnabled = has;
        MemoryPath.Text = m?.Path ?? "Pick a project folder";
        MemoryState.Text = m is null ? "" : (MemScope switch
        {
            SettingsScope.User => "Every folder",
            SettingsScope.Project => "Shared with the repo",
            _ => "Just you, this folder",
        }) + (m.LoadedText is null ? " · does not exist yet: saving creates it" : "");
    }

    void OnMemoryTyped(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _quiet || !_memory.TryGetValue(MemScope, out var m)) return;
        m.Pending = MemoryBox.Text;
        _saved = false;
        UpdateSaveBar();
    }

    // ---- Account (read-only) ----

    Task<string?>? _version; // claude --version once per window: it starts a process

    async void ShowAccount()
    {
        var account = _account;
        var (info, facts) = await Task.Run(() => (AccountReader.Read(account), AccountReader.ReadFacts(account)));
        _version ??= ClaudeCli.VersionAsync(account);
        if (Page != "account") return;
        AccountRows.Children.Clear();
        void Add(string label, string? value) => AccountRows.Children.Add(FactRow(label, value));
        Add("Name", info.DisplayName);
        Add("Email", info.Email);
        Add("Organization", info.OrganizationName);
        Add("Organization type", info.OrganizationType);
        Add("Billing type", info.BillingType);
        Add("Account ID", facts.AccountUuid);
        Add("Organization ID", facts.OrganizationUuid);
        Add("Profile", ShellModel.LetterOf(account.Letter));
        Add("Config folder", account.ConfigRoot);
        Add("Install method", facts.InstallMethod);
        Add("Auto-update", facts.AutoUpdates);
        var versionRow = FactRow("Claude Code", "…");
        AccountRows.Children.Add(versionRow);
        AccountButtons.Children.Clear();
        AccountButtons.Children.Add(LinkButton("Open folder", () => Process.Start(new ProcessStartInfo(account.ConfigRoot) { UseShellExecute = true })));
        foreach (var (label, url) in AccountLinks.For(info))
            AccountButtons.Children.Add(LinkButton(label, () => OpenUrl(url)));
        OtherRows.Children.Clear();
        foreach (var row in facts.Other) OtherRows.Children.Add(FactRow(row.Key, row.Value, row.Source));
        if (facts.Other.Count == 0) OtherRows.Children.Add(FactRow("—", null));
        ((TextBlock)versionRow.Children[1]).Text = await _version ?? "—";
    }

    static void OpenUrl(string url)
    {
        // Only http(s) leave for the shell: the URLs are ours, but ShellExecute would run anything.
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    Button LinkButton(string text, Action open)
    {
        var button = new Button { Content = text, Style = (Style)FindResource("Outlined"), Height = 32, Padding = new(14, 0, 14, 0), Margin = new(0, 0, 8, 0) };
        button.Click += (_, _) =>
        {
            try { open(); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { SaveText.Text = "Could not open it: " + ex.Message; }
        };
        return button;
    }

    Grid FactRow(string label, string? value, string? source = null)
    {
        var grid = new Grid { Margin = new(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new() { Width = new(190) });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var name = new TextBlock { Text = label, Style = (Style)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Top, ToolTip = source };
        var text = new TextBlock { Text = string.IsNullOrEmpty(value) ? "—" : value, TextWrapping = TextWrapping.Wrap, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12.5 };
        Grid.SetColumn(text, 1);
        grid.Children.Add(name);
        grid.Children.Add(text);
        return grid;
    }

    // ---- CCWrapper ----

    void ShowApp()
    {
        _quiet = true;
        BrowserOn.IsChecked = _browser;
        (CompactOn.IsChecked, CompactThreshold.Text, CompactPrompt.Text) = (_app.Enabled, _app.Threshold.ToString(), _app.Prompt);
        _quiet = false;
    }

    void OnAppEdited(object sender, RoutedEventArgs e)
    {
        if (!_ready || _quiet) return;
        _browser = BrowserOn.IsChecked == true;
        _appError = !int.TryParse(CompactThreshold.Text, out var threshold) || !CompactPrefs.ValidThreshold(threshold) ? "The threshold must be a whole number from 1 to 99."
            : CompactPrompt.Text.Trim().Length == 0 ? "The prompt can't be empty." : null;
        if (_appError is null) _app = new(CompactOn.IsChecked == true, threshold, CompactPrompt.Text.Trim());
        _saved = false;
        UpdateSaveBar();
    }

    // ---- JSON ----

    void ShowJson()
    {
        var file = Current;
        _quiet = true;
        JsonBox.Text = file is null ? "" : file.Error is null ? file.Text : file.LoadedText ?? "";
        _quiet = false;
        JsonBox.IsEnabled = file is not null;
        SetJsonState();
    }

    void OnJsonTyped(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _quiet || Current is not { } file) return;
        _jsonError = file.ReplaceText(JsonBox.Text) is { } error ? "The JSON is not valid: " + error : null;
        _saved = false;
        SetJsonState();
        ShowScope();
        UpdateSaveBar();
    }

    void SetJsonState()
    {
        var error = _jsonError ?? (Current?.Error is { } e ? "Not valid JSON: " + e : null);
        (JsonIcon.Text, JsonIcon.Foreground) = error is null ? ("", B("Good")) : ("", B("Bad"));
        (JsonState.Text, JsonState.Foreground) = error is null ? ("Valid JSON", B("Good")) : (error, B("Bad"));
    }

    // ---- save ----

    List<string> DirtyNames() =>
        _files.Values.Where(f => f.Dirty).Select(f => $"{f.Scope} settings")
            .Concat(_memory.Where(m => m.Value.Dirty).Select(m => m.Key == SettingsScope.Local ? "CLAUDE.local.md" : $"{m.Key} CLAUDE.md"))
            .Concat(_app != _appSaved || _browser != _browserSaved ? new[] { "CCWrapper settings" } : [])
            .ToList();

    void UpdateSaveBar()
    {
        var dirty = DirtyNames();
        var invalid = _jsonError ?? _envError ?? _appError;
        SaveButton.IsEnabled = dirty.Count > 0 && invalid is null;
        RevertButton.IsEnabled = dirty.Count > 0 || invalid is not null;
        (SaveText.Text, SaveDot.Fill) = invalid is not null ? (invalid + " Fix it to save.", B("Bad"))
            : dirty.Count > 0 ? ($"Unsaved: {string.Join(", ", dirty)}. Save writes only these files and keeps every key this window doesn't show.", B("Warn"))
            : _saved ? ("Saved. Open sessions pick up settings changes; CLAUDE.md is read when a session starts.", B("Good"))
            : ("No unsaved changes", B("SwitchOff"));
    }

    void OnSave(object sender, RoutedEventArgs e) => SaveAll();

    bool SaveAll()
    {
        var failed = _files.Values.Select(f => (f.Path, Error: f.Save(BackupDir)))
            .Concat(_memory.Values.Select(m => (m.Path, Error: m.Save(BackupDir))))
            .Concat(SaveApp())
            .Where(r => r.Error is not null).ToList();
        _saved = failed.Count == 0;
        ShowPage();
        if (failed.Count > 0)
            Dialog.Show(this, "Not saved:\n\n" + string.Join("\n\n", failed.Select(f => $"{f.Path}\n{f.Error}")), "Settings", MessageBoxButton.OK, MessageBoxImage.Error);
        return failed.Count == 0;
    }

    IEnumerable<(string Path, string? Error)> SaveApp()
    {
        if (_browser != _browserSaved)
        {
            var browserError = AppPrefs.WriteBrowserTool(_browser);
            if (browserError is null) _browserSaved = _browser;
            yield return (AppPrefs.DefaultPath, browserError);
        }
        if (_app == _appSaved) yield break;
        var error = _app.Save();
        if (error is null) _appSaved = _app;
        yield return (CompactPrefs.DefaultPath, error);
    }

    void OnRevert(object sender, RoutedEventArgs e)
    {
        (_app, _browser) = (_appSaved, _browserSaved);
        foreach (var f in _files.Values) f.Revert();
        foreach (var m in _memory.Values) m.Revert();
        _saved = false;
        ShowPage();
    }

    async void OnProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _filling) return;
        var unsaved = _files.Any(f => f.Key != SettingsScope.User && f.Value.Dirty) || _memory.Any(m => m.Key != SettingsScope.User && m.Value.Dirty);
        if (unsaved && Dialog.Show(this, $"Changes to {Folder}'s files are not saved. Switch folders and lose them?", "Unsaved changes",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            _filling = true;
            ProjectBox.SelectedItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
            _filling = false;
            return;
        }
        _cwd = (ProjectBox.SelectedItem as ProjectChoice)?.Path;
        await ReloadAsync(keepUser: true);
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if ((_jsonError ?? _envError ?? _appError) is not null)
        {
            e.Cancel = Dialog.Show(this, "Some changes are not valid and can't be saved. Close and lose all unsaved changes?", "Settings",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK;
            return;
        }
        if (DirtyNames().Count == 0) return;
        var answer = Dialog.Show(this, "Save your changes before closing?", "Settings", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        e.Cancel = answer == MessageBoxResult.Cancel || answer == MessageBoxResult.Yes && !SaveAll();
    }
}

public sealed record RuleChip(string Kind, string Text)
{
    public string RemoveLabel => "Remove " + Text;
}

public sealed record RuleListView(string Title, Brush Color, string Sub, string EmptyText, IReadOnlyList<RuleChip> Rules)
{
    public Visibility EmptyVisibility => Rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
}

public sealed record HookRowView(HookRow Row)
{
    static readonly HashSet<string> ToolEvents = ["PreToolUse", "PostToolUse", "PostToolUseFailure", "PermissionRequest"];

    public string MatcherText => Row.Matcher is { } m ? $"matcher: {m}" : ToolEvents.Contains(Row.Event) ? "every tool" : "";
    public string TimeoutText => Row.Timeout is { } t ? $"timeout {t:0.#} s" : "";
    public string CommandText => Row.Command ?? $"{Row.Type} hook";
    public bool CanEdit => Row.Type == "command";
    public string? EditTip => CanEdit ? null : "Only command hooks are edited here: change this one on the JSON page";
}

/// <summary>One variable of the Environment page. <see cref="Secret"/> is fixed when the row is made.</summary>
public sealed class EnvRowView(string name, string value)
{
    public string Name { get; set; } = name;
    public string Value { get; set; } = value;
    public bool Secret { get; } = SettingsData.LooksSecret(name);
}
