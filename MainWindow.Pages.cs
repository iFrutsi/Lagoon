using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NotchApp.Services;
using Path = System.IO.Path;

namespace NotchApp;

/// <summary>The Clipboard, Widgets and Claude Code tabs. Their content is only built while the
/// tab is on screen and dropped afterwards, so it costs nothing in the background.</summary>
public partial class MainWindow
{
    readonly ClipboardService _clips = new();
    readonly ClaudeAgents _agents = new();
    readonly WeatherService _weather = new();
    ClipKind? _clipFilter;
    bool _clipSnippets;
    int _slowTick;

    static readonly SolidColorBrush ClaudeOrange = new(Color.FromRgb(0xD9, 0x77, 0x57));
    /// <summary>The &lt;/&gt; mark used for Claude Code everywhere (tab, rows, pop-ups): real
    /// characters in a bold code font, since the icon font has no &lt;/&gt; glyph.</summary>
    internal const string CodeGlyph = "</>";
    internal static readonly FontFamily CodeFont = new("Cascadia Code, Consolas");

    static TextBlock CodeMark(double size, Brush fg) => new()
    {
        Text = CodeGlyph, FontFamily = CodeFont, FontWeight = FontWeights.Bold, FontSize = size, Foreground = fg,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    void InitPages()
    {
        // ---- clipboard ----
        foreach (var (label, kind, snippets) in new (string, ClipKind?, bool)[]
                 { ("All", null, false), ("Text", ClipKind.Text, false), ("Links", ClipKind.Link, false),
                   ("Images", ClipKind.Image, false), ("Files", ClipKind.Files, false), ("Snippets", null, true) })
        {
            var chip = new RadioButton { Content = label, GroupName = "clipFilter", Style = (Style)_card.FindResource("FilterChip"), IsChecked = label == "All" };
            chip.Checked += (_, _) => { _clipFilter = kind; _clipSnippets = snippets; BuildClipboard(); };
            _card.ClipFilters.Children.Add(chip);
        }
        _card.ClipClear.Click += (_, _) => _clips.Clear();
        _card.ClipNext.Click += (_, _) => _card.ClipScroll.ScrollToHorizontalOffset(_card.ClipScroll.HorizontalOffset + 320);
        _card.ClipScroll.PreviewMouseWheel += (_, e) =>
        {
            _card.ClipScroll.ScrollToHorizontalOffset(_card.ClipScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        _card.ClipScroll.ScrollChanged += (_, _) => UpdateClipNext();
        Motion.RubberBand(_card.ClipScroll, horizontal: true);
        Motion.RubberBand(_card.AgentScroll);
        _clips.Changed += () => { if (_cardActive && _card.CurrentPage == _card.ClipboardPage) BuildClipboard(); };

        // ---- widgets ----
        _card.WTodoAdd.Click += (_, _) => BeginTodoInput();
        _card.WSaver.MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleSaverFromWidgets(); };
        _card.WTodoInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { AddTodo(_card.WTodoInput.Text); EndTodoInput(); }
            else if (e.Key == Key.Escape) EndTodoInput();
        };
        _card.WTodoInput.LostKeyboardFocus += (_, _) => EndTodoInput();

        // ---- Claude Code ----
        _agents.Changed += () => { if (_cardActive && _card.CurrentPage == _card.AgentsPage) BuildAgents(); };
        _agents.Alert += s =>
        {
            // Alert, not ShowToast: stays out of the way of fullscreen video and games
            if (s.State == AgentState.NeedsYou) Alert(CodeGlyph, $"Claude needs you · {s.Project}", Color.FromRgb(0xFF, 0x9F, 0x0A));
            else Alert(CodeGlyph, $"Claude finished · {s.Project}", Color.FromRgb(0x30, 0xD1, 0x58));
        };
        _card.AgentClear.Click += (_, _) => _agents.ClearFinished();
        _card.AgentConnect.Click += (_, _) =>
        {
            try { ClaudeAgents.Connect(); } catch { }
            BuildAgents();
        };
        _agents.Start();
    }

    /// <summary>Called whenever the visible tab changes or the card opens.</summary>
    void OnPageChanged()
    {
        UpdateWave();
        var p = _card.CurrentPage;
        if (p == _card.ControlsPage) { UpdateClock(); RefreshControls(); }
        if (p == _card.ClipboardPage) BuildClipboard(); else _card.ClipList.Children.Clear();
        if (p == _card.WidgetsPage) UpdateWidgets();
        if (p == _card.TimerPage) { UpdateFocusUi(); UpdateStopwatchUi(); }
        UpdateTickState(); // the open stopwatch ticks faster
        if (p == _card.AgentsPage) BuildAgents(); else _card.AgentList.Children.Clear();
    }

    /// <summary>Card closed: let go of built content (thumbnails etc.).</summary>
    void ReleasePages()
    {
        _card.ClipList.Children.Clear();
        _card.AgentList.Children.Clear();
        EndTodoInput();
    }

    /// <summary>From the 250 ms tick while the card is open.</summary>
    void TickPages()
    {
        var p = _card.CurrentPage;
        if (p == _card.WidgetsPage) UpdateWidgetClocks();
        if (++_slowTick % 4 == 0 && p == _card.AgentsPage) BuildAgents(); // elapsed times, once a second
    }

    // ================================================================== clipboard

    void BuildClipboard()
    {
        var list = _card.ClipList;
        list.Children.Clear();
        IEnumerable<ClipItem> items = _clips.Items;
        if (_clipSnippets) items = items.Where(i => i.Pinned);
        else if (_clipFilter is { } k) items = items.Where(i => i.Kind == k);
        foreach (var item in items.Take(40)) list.Children.Add(ClipCard(item));
        Motion.Stagger(list, horizontal: true);
        _card.ClipEmpty.Visibility = list.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _card.ClipEmpty.Text = _clipSnippets ? "Right-click any item and choose Pin to keep it here." : "Copy text, a link, a picture or files and it shows up here.";
        _card.ClipScroll.ScrollToHorizontalOffset(0);
        Dispatcher.InvokeAsync(UpdateClipNext, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void UpdateClipNext()
    {
        var sv = _card.ClipScroll;
        _card.ClipNext.Visibility = sv.ScrollableWidth > 1 && sv.HorizontalOffset < sv.ScrollableWidth - 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    FrameworkElement ClipCard(ClipItem item)
    {
        (string label, string glyph, Color fg, Color bg) = item.Kind switch
        {
            ClipKind.Files when item.Files!.Length > 1 => ("Files", "", Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0x3D, 0x2B, 0x12)),
            ClipKind.Files => ("File", "", Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0x3D, 0x2B, 0x12)),
            ClipKind.Image => ("Image", "", Color.FromRgb(0x30, 0xD1, 0x58), Color.FromRgb(0x16, 0x33, 0x22)),
            ClipKind.Link => ("Link", "", Color.FromRgb(0xBF, 0x5A, 0xF2), Color.FromRgb(0x2E, 0x1A, 0x3B)),
            _ => ("Text", "", Color.FromRgb(0x64, 0xB5, 0xFF), Color.FromRgb(0x14, 0x28, 0x3A)),
        };
        var accent = new SolidColorBrush(fg);
        var subtle = (Brush)FindResource("Subtle");

        // header strip
        var head = new Grid { Height = 30 };
        var headBg = new Border { CornerRadius = new CornerRadius(16, 16, 0, 0), Background = new SolidColorBrush(bg) };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(Glyph(glyph, 13, accent));
        title.Children.Add(new TextBlock { Text = label, Foreground = accent, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(7, 0, 0, 0) });
        if (item.Pinned) title.Children.Add(Glyph("", 11, accent, new Thickness(6, 1, 0, 0)));
        var when = new TextBlock { Text = Ago(item.Time), FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        head.Children.Add(headBg); head.Children.Add(title); head.Children.Add(when);

        // body
        var body = new Grid { Margin = new Thickness(12, 8, 12, 10) };
        string footer = item.Source ?? "";
        FrameworkElement content;
        switch (item.Kind)
        {
            case ClipKind.Image:
                var thumb = ClipboardService.LoadImage(item, 160);
                footer = $"{item.ImageW} × {item.ImageH}";
                body.Margin = new Thickness(0);
                content = new Border
                {
                    CornerRadius = new CornerRadius(0, 0, 16, 16),
                    Background = thumb != null ? new ImageBrush(thumb) { Stretch = Stretch.UniformToFill } : Brushes.Transparent,
                };
                break;
            case ClipKind.Files:
                var files = item.Files!;
                var tiles = new Grid { Height = 40, HorizontalAlignment = HorizontalAlignment.Left };
                for (int i = 0; i < Math.Min(3, files.Length); i++)
                    tiles.Children.Add(new Border
                    {
                        Width = 36, Height = 36, CornerRadius = new CornerRadius(10), Margin = new Thickness(i * 16, 0, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Background = new SolidColorBrush(i == 0 ? Color.FromRgb(0xE0, 0x3A, 0x4E) : i == 1 ? Color.FromRgb(0xC2, 0x32, 0x5A) : Color.FromRgb(0xB0, 0x7A, 0x2E)),
                        BorderBrush = (Brush)FindResource("Card"), BorderThickness = new Thickness(2),
                        Child = Glyph("", 15, Brushes.White),
                    });
                var stack = new StackPanel();
                stack.Children.Add(tiles);
                stack.Children.Add(new TextBlock { Text = files.Length > 1 ? $"{files.Length} files" : Path.GetFileName(files[0]), FontSize = 14, Margin = new Thickness(0, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
                if (files.Length > 1) stack.Children.Add(new TextBlock { Text = Path.GetFileName(files[0]), FontSize = 13, Foreground = subtle, TextTrimming = TextTrimming.CharacterEllipsis });
                footer = Path.GetFileName(Path.GetDirectoryName(files[0]) ?? "") ?? "";
                content = stack;
                break;
            case ClipKind.Link:
                var uri = new Uri(item.Text!);
                var ls = new StackPanel();
                ls.Children.Add(new TextBlock { Text = uri.Host.Replace("www.", ""), FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                ls.Children.Add(new TextBlock { Text = uri.PathAndQuery, FontSize = 12.5, Foreground = subtle, TextWrapping = TextWrapping.Wrap, MaxHeight = 52, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
                content = ls;
                break;
            default:
                content = new TextBlock { Text = item.Text!.Trim(), FontSize = 13.5, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 72 };
                break;
        }
        body.Children.Add(content);

        var foot = new TextBlock
        {
            Text = footer, FontSize = 13, Margin = new Thickness(12, 0, 12, 10), VerticalAlignment = VerticalAlignment.Bottom,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = item.Kind == ClipKind.Image ? Brushes.White : (Brush)FindResource("FgBrush"),
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(head);
        Grid.SetRow(body, 1); grid.Children.Add(body);
        Grid.SetRow(foot, 1); grid.Children.Add(foot);

        var card = new Border
        {
            Width = 150, CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 10, 0),
            Background = (Brush)FindResource("Card"), Child = grid, Cursor = Cursors.Hand,
            ToolTip = "Click to copy · right-click for more",
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            _clips.CopyBack(item);
            when.Text = "Copied";
            Motion.Pulse(card); // confirm
            e.Handled = true;
        };
        var menu = new ContextMenu();
        var pin = new MenuItem { Header = item.Pinned ? "Unpin" : "Pin as snippet" };
        pin.Click += (_, _) => _clips.TogglePin(item);
        var del = new MenuItem { Header = "Delete" };
        del.Click += (_, _) => _clips.Remove(item);
        menu.Items.Add(pin); menu.Items.Add(del);
        card.ContextMenu = menu;
        return card;
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 60) return "now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h";
        return $"{(int)d.TotalDays}d";
    }

    static TextBlock Glyph(string glyph, double size, Brush fg, Thickness? margin = null) => new()
    {
        Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = size, Foreground = fg,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = margin ?? new Thickness(0),
    };

    // ================================================================== widgets

    void UpdateWidgets()
    {
        UpdateWidgetClocks();

        var (pct, charging) = Battery.Read();
        _card.WBattText.Text = pct is int p ? $"{p}%" : "AC";
        _card.WBattBar.Width = 70 * (pct ?? 100) / 100.0;
        var battColor = new SolidColorBrush(pct is < 20 && !charging ? Color.FromRgb(0xFF, 0x45, 0x3A) : Color.FromRgb(0x30, 0xD1, 0x58));
        _card.WBattBar.Background = battColor;
        _card.WBattText.Foreground = battColor;
        UpdateBatteryDetail();
        _card.WBattIcon.Text = charging ? "" : "";

        // connected Bluetooth headphones, if they report a battery level
        if (BluetoothBattery.Read() is { } buds)
        {
            _card.WBudsName.Text = buds.Name;
            _card.WBudsText.Text = $"{buds.Percent}%";
            _card.WBudsBar.Width = 70 * buds.Percent / 100.0;
            var budsColor = new SolidColorBrush(buds.Percent < 20 ? Color.FromRgb(0xFF, 0x45, 0x3A) : Color.FromRgb(0x30, 0xD1, 0x58));
            _card.WBudsBar.Background = budsColor;
            _card.WBudsText.Foreground = budsColor;
            _card.WBudsRow.Visibility = Visibility.Visible;
        }
        else _card.WBudsRow.Visibility = Visibility.Collapsed;

        BuildTodos();
        _ = UpdateWeather();
    }

    /// <summary>The widgets' second battery line: time to full / time left, and the saver chip.</summary>
    void UpdateBatteryDetail()
    {
        var saver = BatterySaver.IsOn(_settings);
        if (saver == null) { _card.WBattDetailRow.Visibility = Visibility.Collapsed; return; } // desktop PC: no battery
        _card.WBattDetailRow.Visibility = Visibility.Visible;
        _card.WBattDetail.Text = BatteryEstimate.Text() ?? "";
        bool on = saver == true;
        var green = Color.FromRgb(0x30, 0xD1, 0x58);
        _card.WSaver.Background = on ? new SolidColorBrush(Color.FromArgb(0x33, green.R, green.G, green.B)) : (Brush)FindResource("ChipOff");
        _card.WSaverIcon.Foreground = on ? new SolidColorBrush(green) : (Brush)FindResource("Subtle");
        _card.WSaverText.Foreground = on ? new SolidColorBrush(green) : (Brush)FindResource("FgBrush");
        _card.WSaverText.Text = on ? "Saver on" : "Saver off";
    }

    async void ToggleSaverFromWidgets()
    {
        bool on = BatterySaver.IsOn(_settings) != true;
        BatterySaver.Set(on, _settings);
        UpdateBatteryDetail();
        await Task.Delay(1500); // Windows recomputes the estimate a moment later
        if (_card.WidgetsPage.Visibility == Visibility.Visible) UpdateBatteryDetail();
    }

    void UpdateWidgetClocks()
    {
        var now = DateTime.Now;
        (_card.WClock.Text, _card.WAmPm.Text) = TimeFormat.Split(now);
        
        _card.WDay.Text = now.ToString("ddd").ToUpperInvariant();
        _card.WCity.Text = _settings.WorldCity;
        try
        {
            var there = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, _settings.WorldZone);
            _card.WCityTime.Text = TimeFormat.Time(there);
        }
        catch { _card.WCityTime.Text = ""; }
    }

    async Task UpdateWeather()
    {
        if (string.IsNullOrWhiteSpace(_settings.WeatherCity))
        {
            _card.WTemp.Text = "--°";
            _card.WCond.Text = "Set a city in Settings";
            _card.WHiLo.Text = "";
            return;
        }
        var w = await _weather.GetAsync(_settings.WeatherCity);
        if (w == null) { _card.WCond.Text = "Weather unavailable"; return; }
        _card.WTemp.Text = $"{Math.Round(w.Temp)}°";
        _card.WWeatherIcon.Text = WeatherService.Glyph(w.Code);
        _card.WCond.Text = $"{WeatherService.Describe(w.Code)} · {w.City}";
        _card.WHiLo.Text = $"H {Math.Round(w.High)}°  L {Math.Round(w.Low)}°";
    }

    void BuildTodos()
    {
        var list = _card.WTodoList;
        list.Children.Clear();
        var todos = _settings.Todos;
        _card.WTodoCount.Text = todos.Count(t => !t.Done) is int open and > 0 ? open.ToString() : "";
        foreach (var t in todos.OrderBy(t => t.Done).Take(3))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            var circle = new Ellipse { Width = 15, Height = 15, StrokeThickness = 1.6, Stroke = (Brush)FindResource("Subtle"), VerticalAlignment = VerticalAlignment.Center };
            if (t.Done) circle.Fill = (Brush)FindResource("Subtle");
            var text = new TextBlock
            {
                Text = t.Text, FontSize = 14, Margin = new Thickness(9, 0, 0, 0), MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = t.Done ? (Brush)FindResource("Subtle") : (Brush)FindResource("FgBrush"),
                TextDecorations = t.Done ? TextDecorations.Strikethrough : null,
            };
            row.Children.Add(circle); row.Children.Add(text);
            row.MouseLeftButtonUp += (_, _) => { t.Done = !t.Done; _settings.Save(); BuildTodos(); };
            row.ToolTip = "Click to tick off · right-click to delete";
            row.MouseRightButtonUp += (_, e) => { _settings.Todos.Remove(t); _settings.Save(); BuildTodos(); e.Handled = true; };
            list.Children.Add(row);
        }
        if (todos.Count == 0)
            list.Children.Add(new TextBlock { Text = "Nothing to do. Press + to add.", FontSize = 13, Foreground = (Brush)FindResource("Subtle"), Margin = new Thickness(0, 4, 0, 0) });
    }

    bool _typingTodo;

    /// <summary>The card normally never takes focus; it does just while you type a to-do.</summary>
    void BeginTodoInput()
    {
        _typingTodo = true;
        _card.WTodoList.Visibility = Visibility.Collapsed;
        _card.WTodoInput.Text = "";
        _card.WTodoInput.Visibility = Visibility.Visible;
        WindowUtil.SetActivatable(_card, true);
        _card.Activate();
        _card.WTodoInput.Focus();
    }

    void EndTodoInput()
    {
        if (!_typingTodo) return;
        _typingTodo = false;
        _card.WTodoInput.Visibility = Visibility.Collapsed;
        _card.WTodoList.Visibility = Visibility.Visible;
        WindowUtil.SetActivatable(_card, false);
    }

    void AddTodo(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        _settings.Todos.Insert(0, new AppSettings.TodoItem { Text = text });
        _settings.Save();
        BuildTodos();
    }

    // ================================================================== Claude Code

    void BuildAgents()
    {
        var sessions = _agents.Sessions;
        var list = _card.AgentList;
        list.Children.Clear();
        _card.AgentSummary.Children.Clear();

        bool connected = ClaudeAgents.IsConnected;
        _card.AgentEmpty.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _card.AgentConnect.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        _card.AgentEmptyText.Text = connected ? "No Claude Code sessions right now. Start one and it shows up here." : "Connect Claude Code to see your sessions here.";
        _card.AgentClear.Visibility = sessions.Any(s => s.State == AgentState.Done) ? Visibility.Visible : Visibility.Collapsed;
        if (sessions.Count == 0) return;

        void Count(AgentState st, string label, Color c)
        {
            int n = sessions.Count(s => s.State == st);
            if (n == 0) return;
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
            sp.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(c), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = $"{n} {label}", FontSize = 13, Margin = new Thickness(6, 0, 0, 0), Foreground = new SolidColorBrush(c) });
            _card.AgentSummary.Children.Add(sp);
        }
        Count(AgentState.NeedsYou, "needs you", Color.FromRgb(0xFF, 0x9F, 0x0A));
        Count(AgentState.Working, "working", Color.FromRgb(0x64, 0xD2, 0xFF));
        Count(AgentState.Done, "done", Color.FromRgb(0x30, 0xD1, 0x58));

        foreach (var s in sessions.OrderBy(s => s.State == AgentState.NeedsYou ? 0 : s.State == AgentState.Working ? 1 : 2))
            list.Children.Add(AgentRow(s));
    }

    FrameworkElement AgentRow(AgentSession s)
    {
        var orange = Color.FromRgb(0xFF, 0x9F, 0x0A);
        var green = Color.FromRgb(0x30, 0xD1, 0x58);
        Color stateColor = s.State switch { AgentState.NeedsYou => orange, AgentState.Done => green, _ => Color.FromRgb(0x64, 0xD2, 0xFF) };
        var stateBrush = new SolidColorBrush(stateColor);

        // icon square with a badge
        var icon = new Grid { Width = 38, Height = 38, VerticalAlignment = VerticalAlignment.Center };
        icon.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5),
            BorderBrush = s.State == AgentState.Working ? ClaudeOrange : stateBrush,
            Background = new SolidColorBrush(Color.FromArgb(0x33, stateColor.R, stateColor.G, stateColor.B)),
            Child = s.State == AgentState.NeedsYou
                ? CodeMark(12.5, stateBrush)
                : CodeMark(12.5, ClaudeOrange),
        });
        if (s.State != AgentState.Working)
            icon.Children.Add(new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = stateBrush,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -4, -4),
                Child = s.State == AgentState.NeedsYou
                    ? new TextBlock { Text = "!", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                    : Glyph("", 9, Brushes.Black),
            });

        string detailGlyph = s.State switch { AgentState.NeedsYou => "", AgentState.Done => "", _ => "" };
        string detail = s.State == AgentState.Done && (DateTime.Now - s.Since).TotalSeconds < 60 && s.Detail == "Finished" ? "Finished just now" : s.Detail;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        text.Children.Add(new TextBlock { Text = s.Title, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis });
        var dl = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
        dl.Children.Add(Glyph(detailGlyph, 11, stateBrush));
        dl.Children.Add(new TextBlock { Text = detail, FontSize = 13, Foreground = stateBrush, Margin = new Thickness(6, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 });
        text.Children.Add(dl);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(new TextBlock { Text = Elapsed(DateTime.Now - s.Since), FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right });
        var proj = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 1, 0, 0) };
        proj.Children.Add(Glyph("", 11, (Brush)FindResource("Subtle")));
        proj.Children.Add(new TextBlock { Text = s.Project, FontSize = 13, Foreground = (Brush)FindResource("Subtle"), Margin = new Thickness(6, 0, 0, 0) });
        right.Children.Add(proj);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        Grid.SetColumn(right, 2); grid.Children.Add(right);

        var row = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 7, 14, 7), Margin = new Thickness(0, 0, 0, 6), Child = grid,
            Background = s.State == AgentState.NeedsYou ? new SolidColorBrush(Color.FromArgb(0x30, orange.R, orange.G, orange.B)) : (Brush)FindResource("Card"),
            BorderBrush = s.State == AgentState.NeedsYou ? new SolidColorBrush(orange) : null,
            BorderThickness = new Thickness(s.State == AgentState.NeedsYou ? 1.5 : 0),
        };
        var menu = new ContextMenu();
        var dismiss = new MenuItem { Header = "Remove from list" };
        dismiss.Click += (_, _) => _agents.Dismiss(s);
        menu.Items.Add(dismiss);
        row.ContextMenu = menu;
        return row;
    }

    static string Elapsed(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{(int)t.TotalSeconds}s" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}m" : $"{(int)t.TotalHours}h";
}
