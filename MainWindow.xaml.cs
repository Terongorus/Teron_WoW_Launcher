using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Markdig.Wpf;
using Microsoft.Win32;
using TeronWoWLauncher.Dialogs;
using TeronWoWLauncher.Models;
using TeronWoWLauncher.Services;

namespace TeronWoWLauncher;

public partial class MainWindow : Window
{
    private enum PlayButtonState { Play, Install, Update }

    private readonly Logger _log = Logger.Instance;
    private readonly SettingsService _settings = new();
    private readonly LaunchOrchestrator _orchestrator;
    private readonly DllListService _dlls = new();
    private readonly MpqPatchService _mpq = new();
    private readonly RealmlistService _realmlist = new();
    private readonly GameInstallService _install = new();
    private readonly AddonLibrary _addons = new();

    private readonly List<PatchControl> _patchControls = new();
    private readonly ObservableCollection<DllInfo> _dllItems = new();
    private readonly ObservableCollection<DllInfo> _detectedDllItems = new();
    private readonly ObservableCollection<string> _ignoredDllItems = new();
    private readonly ObservableCollection<InstalledAddon> _addonItems = new();

    private readonly DispatcherTimer _patchApplyTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _settingsSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _loading;
    private bool _applyingPatches;
    private bool _savingSettings;
    private bool _addonBusy;

    // Last values a settings-save actually acted on, so retyping the same folder/URL/realmlist
    // doesn't re-trigger the heavier side effects (folder rescans, a network update check) every tick.
    private string _lastAppliedWowDir = string.Empty;
    private string _lastAppliedRealmlist = string.Empty;
    private string _lastAppliedClientUrl = string.Empty;

    private PlayButtonState _playState = PlayButtonState.Play;
    private UpdateCheckResult? _pendingUpdateCheck;

    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x92));
    private static readonly Brush BodyBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xD0));
    private const double BodyFontSize = 14;
    private static readonly Brush PlayColor = new SolidColorBrush(Color.FromRgb(0x3B, 0x7D, 0x3B));
    private static readonly Brush InstallColor = new SolidColorBrush(Color.FromRgb(0x2E, 0x6D, 0xA4));
    private static readonly Brush UpdateColor = new SolidColorBrush(Color.FromRgb(0xC9, 0x92, 0x2B));

    // "HEAD" resolves to whichever branch GitHub reports as the repo's default, so this always
    // reflects what's actually published there rather than a hardcoded branch name.
    private const string ReadmeUrl = "https://raw.githubusercontent.com/Terongorus/Teron_WoW_Launcher/HEAD/README.md";
    private const string ChangelogUrl = "https://raw.githubusercontent.com/Terongorus/Teron_WoW_Launcher/HEAD/CHANGELOG.md";
    private static readonly HttpClient DocsHttp = CreateDocsHttpClient();

    private static HttpClient CreateDocsHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TeronWoWLauncher");
        return http;
    }

    private sealed class PatchControl
    {
        public required PatchDefinition Def { get; init; }
        public required CheckBox Box { get; init; }
        public Slider? Slider { get; init; }
    }

    public MainWindow()
    {
        InitializeComponent();

        Title = AppInfo.DisplayNameWithVersion;
        HeaderText.Text = AppInfo.DisplayName;
        VersionLabel.Text = $"v{AppInfo.Version}";
        WindowChromeHelper.FixMaximizedBounds(this);
        StateChanged += OnWindowStateChanged;
        Closing += OnWindowClosing;

        _settings.Load();
        RestoreWindowPlacement();
        _orchestrator = new LaunchOrchestrator(_settings);
        _log.MessageLogged += OnMessageLogged;
        _patchApplyTimer.Tick += OnPatchApplyTick;
        _settingsSaveTimer.Tick += OnSettingsSaveTick;

        DllList.ItemsSource = _dllItems;
        DetectedDllList.ItemsSource = _detectedDllItems;
        IgnoredDllList.ItemsSource = _ignoredDllItems;

        // Save on any change instead of a Save button — CollectSettingsFromUi() validates each field
        // (bad numbers/URLs keep the last good value) before anything is persisted.
        AutoLoginCheck.Checked += (_, _) => ScheduleSettingsSave();
        AutoLoginCheck.Unchecked += (_, _) => ScheduleSettingsSave();
        AccountBox.TextChanged += (_, _) => ScheduleSettingsSave();
        PasswordBoxInput.PasswordChanged += (_, _) => ScheduleSettingsSave();
        SavePasswordCheck.Checked += (_, _) => ScheduleSettingsSave();
        SavePasswordCheck.Unchecked += (_, _) => ScheduleSettingsSave();
        GameFolderBox.TextChanged += (_, _) => ScheduleSettingsSave();
        RealmlistBox.TextChanged += (_, _) => ScheduleSettingsSave();
        ClientUrlBox.TextChanged += (_, _) => ScheduleSettingsSave();
        DelayBox.TextChanged += (_, _) => ScheduleSettingsSave();

        _loading = true;
        BuildPatchList();
        LoadSettingsIntoUi();
        RefreshDllList();
        RefreshDetectedDlls();
        RefreshIgnoredDllList();
        RefreshMpqList();
        InitAddonsTab();
        _loading = false;

        _lastAppliedWowDir = CurrentWowDir();
        _lastAppliedRealmlist = _settings.Current.Realmlist;
        _lastAppliedClientUrl = CurrentClientUrl();

        UpdateStatus("Ready.");
        _log.Info("Launcher UI initialized.");

        _ = RefreshPlayButtonStateAsync();
        _ = LoadDocsFromGitHubAsync();
    }

    // ---------------- Custom title bar ----------------

    private void OnMinimizeClicked(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClicked(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeRestoreButton.Content = maximized ? "" : ""; // ChromeRestore / ChromeMaximize
        MaximizeRestoreButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    /// <summary>Applies the saved size/position/state, if any and still sane, before the window is shown.</summary>
    private void RestoreWindowPlacement()
    {
        LauncherSettings s = _settings.Current;

        if (s.WindowWidth is double w && s.WindowHeight is double h && w > 0 && h > 0)
        {
            Width = w;
            Height = h;
        }

        // Only trust a saved position if the window would still land on a currently-connected
        // monitor -- otherwise a since-removed second monitor could strand it off-screen forever.
        if (s.WindowLeft is double l && s.WindowTop is double t && IsOnVirtualScreen(l, t, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
        }

        if (Enum.TryParse(s.SavedWindowState, out WindowState savedState))
        {
            WindowState = savedState;
        }
    }

    private static bool IsOnVirtualScreen(double left, double top, double width, double height)
    {
        double screenLeft = SystemParameters.VirtualScreenLeft;
        double screenTop = SystemParameters.VirtualScreenTop;
        double screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        double screenBottom = screenTop + SystemParameters.VirtualScreenHeight;
        return left < screenRight && left + width > screenLeft && top < screenBottom && top + height > screenTop;
    }

    /// <summary>
    /// Saves size/position/state for next launch. Uses RestoreBounds (the Normal-state bounds)
    /// rather than the current Width/Height/Left/Top whenever the window is not Normal right now,
    /// so maximizing or minimizing does not overwrite the size it should restore back to.
    /// </summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        LauncherSettings s = _settings.Current;
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

        if (bounds.Width > 0 && bounds.Height > 0)
        {
            s.WindowLeft = bounds.Left;
            s.WindowTop = bounds.Top;
            s.WindowWidth = bounds.Width;
            s.WindowHeight = bounds.Height;
        }

        s.SavedWindowState = WindowState.ToString();
        _settings.Save();
    }

    // ---------------- Home tab: How to use (README/CHANGELOG from GitHub, rendered as Markdown) ----------------

    private async Task LoadDocsFromGitHubAsync()
    {
        await Task.WhenAll(
            LoadDocAsync(ReadmeUrl, ReadmeViewer),
            LoadDocAsync(ChangelogUrl, ChangelogViewer));
    }

    private async Task LoadDocAsync(string url, MarkdownViewer target)
    {
        target.Markdown = "Loading from GitHub…";
        try
        {
            target.Markdown = await DocsHttp.GetStringAsync(url);
        }
        catch (Exception ex)
        {
            target.Markdown = "Could not load this from GitHub right now.";
            _log.Warn($"Failed to load {url}: {ex.Message}");
        }
    }

    private void OnMarkdownHyperlink(object sender, ExecutedRoutedEventArgs e)
    {
        string? url = e.Parameter?.ToString();
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to open link {url}: {ex.Message}");
        }
    }

    /// <summary>
    /// The Home tab's Readme/Changelog boxes are compact previews with no internal scrollbar; clicking
    /// anywhere on one (the MarkdownViewer itself is hit-test-invisible so the Border always catches it)
    /// opens the same content full-size, with normal scrolling, in its own dialog.
    /// </summary>
    private void OnExpandMarkdown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string kind })
        {
            return;
        }

        (string title, string markdown) = kind switch
        {
            "Readme" => ("README", ReadmeViewer.Markdown ?? string.Empty),
            "Changelog" => ("CHANGELOG", ChangelogViewer.Markdown ?? string.Empty),
            _ => (kind, string.Empty),
        };

        ShowModal(new MarkdownPreviewDialog(title, markdown));
    }

    /// <summary>
    /// Shows one of our own dialogs modally, fogging the launcher behind it for the duration. Not
    /// used for native OS dialogs (OpenFileDialog etc.) — those already have their own modal chrome.
    /// </summary>
    private bool? ShowModal(Window dialog)
    {
        dialog.Owner = this;
        ModalOverlay.Visibility = Visibility.Visible;
        try
        {
            return dialog.ShowDialog();
        }
        finally
        {
            ModalOverlay.Visibility = Visibility.Collapsed;
        }
    }

    // ---------------- Navigation ----------------

    private void OnNavClicked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out int index))
        {
            MainTabs.SelectedIndex = index;
        }
    }

    private void SelectTab(int index)
    {
        MainTabs.SelectedIndex = index;
        if (NavPanel.Children.Count > index && NavPanel.Children[index] is RadioButton rb)
        {
            rb.IsChecked = true;
        }
    }

    private void OnRealmlistLabelClicked(object sender, MouseButtonEventArgs e)
    {
        SelectTab(5); // Settings
        RealmlistBox.Focus();
        RealmlistBox.SelectAll();
        FlashHighlight(RealmlistBox);
    }

    /// <summary>Briefly flashes a control's border gold then fades it back, to draw the eye to it.</summary>
    private static void FlashHighlight(Control control)
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42));
        control.BorderBrush = brush; // a local instance, so the animation doesn't touch the shared Style brush

        var animation = new ColorAnimation
        {
            From = Color.FromRgb(0xE6, 0xC0, 0x67),
            To = Color.FromRgb(0x3A, 0x3A, 0x42),
            Duration = TimeSpan.FromMilliseconds(900),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    // ---------------- Tweaks tab ----------------

    private void BuildPatchList()
    {
        PatchesPanel.Children.Clear();
        _patchControls.Clear();

        IReadOnlyList<PatchDefinition> catalog = PatchCatalog.All;
        bool firstRun = _settings.Current.EnabledPatchIds.Count == 0;

        foreach (PatchDefinition patch in catalog)
        {
            bool isChecked = firstRun
                ? patch.DefaultEnabled
                : _settings.Current.EnabledPatchIds.Contains(patch.Id);

            var box = new CheckBox { Content = patch.Name, IsChecked = isChecked };
            box.Click += (_, _) => SchedulePatchApply();
            PatchesPanel.Children.Add(box);
            PatchesPanel.Children.Add(new TextBlock
            {
                Text = patch.Description,
                Foreground = MutedBrush,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24, 2, 0, 4),
            });

            Slider? slider = null;
            if (patch.Parameter is PatchParameter p)
            {
                double value = p.Default;
                if (_settings.Current.PatchParameters.TryGetValue(patch.Id, out double stored))
                {
                    // One-time migration: FoV used to be stored (and edited) in radians; a leftover
                    // radians value will always be well under the new degrees-based minimum.
                    value = patch.Id == "fov" && stored < p.Min
                        ? stored * 180.0 / Math.PI
                        : stored;
                }
                value = Math.Clamp(value, p.Min, p.Max);

                slider = new Slider
                {
                    Minimum = p.Min,
                    Maximum = p.Max,
                    Value = value,
                    Width = 380,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsSnapToTickEnabled = p.IsInteger,
                    TickFrequency = p.IsInteger ? 1 : 0.01,
                    IsEnabled = isChecked,
                };

                var valueBox = new TextBox
                {
                    Width = 65,
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    IsEnabled = isChecked,
                };

                Slider capturedSlider = slider;
                TextBox capturedBox = valueBox;

                void UpdateValueBox() => capturedBox.Text = FormatNumericValue(capturedSlider.Value, p);

                void CommitValueBox()
                {
                    if (double.TryParse(capturedBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                    {
                        capturedSlider.Value = Math.Clamp(parsed, p.Min, p.Max);
                    }

                    UpdateValueBox(); // resync display even if parsing failed or clamping produced no change
                }

                slider.ValueChanged += (_, _) =>
                {
                    UpdateValueBox();
                    SchedulePatchApply();
                };
                valueBox.LostFocus += (_, _) => CommitValueBox();
                valueBox.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        CommitValueBox();
                        e.Handled = true;
                    }
                };
                UpdateValueBox();

                box.Checked += (_, _) => { capturedSlider.IsEnabled = true; capturedBox.IsEnabled = true; };
                box.Unchecked += (_, _) => { capturedSlider.IsEnabled = false; capturedBox.IsEnabled = false; };

                // No left-side label here — it would just repeat the CheckBox's own Name/Description
                // directly above, which already identifies this slider.
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 0, 0, 8) };
                row.Children.Add(slider);
                row.Children.Add(valueBox);
                if (!string.IsNullOrEmpty(p.Unit))
                {
                    row.Children.Add(new TextBlock
                    {
                        Text = p.Unit,
                        Foreground = MutedBrush,
                        FontSize = BodyFontSize,
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
                PatchesPanel.Children.Add(row);
            }

            _patchControls.Add(new PatchControl { Def = patch, Box = box, Slider = slider });
        }

        if (catalog.Count == 0)
        {
            PatchesPanel.Children.Add(new TextBlock { Text = "No executable tweaks available yet.", Foreground = MutedBrush, FontSize = BodyFontSize });
        }
    }

    private static string FormatNumericValue(double value, PatchParameter p)
        => p.IsInteger
            ? ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.###", CultureInfo.InvariantCulture);

    private void SchedulePatchApply()
    {
        if (_loading)
        {
            return;
        }

        CollectSettingsFromUi();
        _settings.Save();
        _patchApplyTimer.Stop();
        _patchApplyTimer.Start();
    }

    private async void OnPatchApplyTick(object? sender, EventArgs e)
    {
        _patchApplyTimer.Stop();
        if (_applyingPatches)
        {
            _patchApplyTimer.Start();
            return;
        }

        _applyingPatches = true;
        try
        {
            await _orchestrator.ApplyPatchesAsync(new Progress<string>(UpdateStatus));
        }
        finally
        {
            _applyingPatches = false;
        }
    }

    // ---------------- DLLs tab ----------------

    private void RefreshDllList()
    {
        string wowDir = CurrentWowDir();
        _dllItems.Clear();
        foreach (string name in _dlls.ReadActiveNames(wowDir))
        {
            _dllItems.Add(DllMetadataReader.Read(name, _dlls.ResolvePath(wowDir, name)));
        }
    }

    private void SaveDllList() => _dlls.WriteActiveNames(CurrentWowDir(), _dllItems.Select(d => d.Name).ToList());

    private void OnAddDll(object sender, RoutedEventArgs e)
    {
        string wowDir = CurrentWowDir();
        var dialog = new OpenFileDialog
        {
            Title = "Select a DLL to inject",
            Filter = "DLL files (*.dll)|*.dll|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(wowDir) ? wowDir : null,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string chosen = dialog.FileName;
        string parent = Path.GetDirectoryName(chosen) ?? string.Empty;
        string entry = string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar),
            wowDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(chosen)
            : chosen;

        if (!_dllItems.Any(d => string.Equals(d.Name, entry, StringComparison.OrdinalIgnoreCase)))
        {
            _dllItems.Add(DllMetadataReader.Read(entry, chosen));
            SaveDllList();
            RefreshDetectedDlls();
        }
    }

    private void OnRemoveDll(object sender, RoutedEventArgs e)
    {
        if (DllList.SelectedItem is DllInfo item)
        {
            _dllItems.Remove(item);
            SaveDllList();
            RefreshDetectedDlls();
        }
    }

    private void OnMoveDllUp(object sender, RoutedEventArgs e) => MoveDll(-1);

    private void OnMoveDllDown(object sender, RoutedEventArgs e) => MoveDll(1);

    private void MoveDll(int delta)
    {
        int i = DllList.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _dllItems.Count)
        {
            return;
        }

        _dllItems.Move(i, j);
        DllList.SelectedIndex = j;
        SaveDllList();
    }

    private void RefreshDetectedDlls()
    {
        string wowDir = CurrentWowDir();
        _detectedDllItems.Clear();
        foreach (string name in _dlls.ScanForUntrackedDlls(wowDir, _settings.Current.IgnoredDetectedDlls))
        {
            _detectedDllItems.Add(DllMetadataReader.Read(name, Path.Combine(wowDir, name)));
        }
    }

    private void OnRefreshDetectedDlls(object sender, RoutedEventArgs e) => RefreshDetectedDlls();

    private void OnAddDetectedDlls(object sender, RoutedEventArgs e)
    {
        List<DllInfo> selected = DetectedDllList.SelectedItems.Cast<DllInfo>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        foreach (DllInfo item in selected)
        {
            if (!_dllItems.Any(d => string.Equals(d.Name, item.Name, StringComparison.OrdinalIgnoreCase)))
            {
                _dllItems.Add(item);
            }
        }

        SaveDllList();
        RefreshDetectedDlls();
    }

    private void OnIgnoreDetectedDlls(object sender, RoutedEventArgs e)
    {
        List<DllInfo> selected = DetectedDllList.SelectedItems.Cast<DllInfo>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        List<string> ignored = _settings.Current.IgnoredDetectedDlls;
        foreach (DllInfo item in selected)
        {
            if (!ignored.Contains(item.Name, StringComparer.OrdinalIgnoreCase))
            {
                ignored.Add(item.Name);
            }
        }

        _settings.Save();
        RefreshDetectedDlls();
        RefreshIgnoredDllList();
    }

    // ---------------- Ignored DLLs (Settings tab) ----------------

    private void RefreshIgnoredDllList()
    {
        _ignoredDllItems.Clear();
        foreach (string name in _settings.Current.IgnoredDetectedDlls.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            _ignoredDllItems.Add(name);
        }
    }

    private void OnUnignoreDlls(object sender, RoutedEventArgs e)
    {
        List<string> selected = IgnoredDllList.SelectedItems.Cast<string>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        foreach (string name in selected)
        {
            _settings.Current.IgnoredDetectedDlls.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }

        _settings.Save();
        RefreshIgnoredDllList();
        RefreshDetectedDlls();
    }

    private void OnUnignoreAllDlls(object sender, RoutedEventArgs e)
    {
        if (_settings.Current.IgnoredDetectedDlls.Count == 0)
        {
            return;
        }

        _settings.Current.IgnoredDetectedDlls.Clear();
        _settings.Save();
        RefreshIgnoredDllList();
        RefreshDetectedDlls();
    }

    // ---------------- MPQ tab ----------------

    private void RefreshMpqList()
    {
        MpqPanel.Children.Clear();
        string wowDir = CurrentWowDir();
        List<MpqPatch> patches = _mpq.Scan(wowDir);

        if (patches.Count == 0)
        {
            MpqPanel.Children.Add(new TextBlock
            {
                Text = "No custom MPQ patches found in Data\\.",
                Foreground = MutedBrush,
                FontSize = BodyFontSize,
                Margin = new Thickness(0, 6, 0, 0),
            });
            return;
        }

        foreach (MpqPatch patch in patches)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };

            var remove = new Button
            {
                Style = (Style)FindResource("IconButtonStyle"),
                Content = "", // Segoe Fluent Icons: Delete
                Width = 28,
                Height = 28,
                ToolTip = "Remove this patch",
            };
            remove.Click += (_, _) =>
            {
                _mpq.Remove(wowDir, patch);
                RefreshMpqList();
            };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);

            var check = new CheckBox
            {
                Content = $"patch-{patch.Letter}.mpq" + (patch.Enabled ? string.Empty : "  (disabled)"),
                IsChecked = patch.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0),
            };
            check.Click += (_, _) =>
            {
                _mpq.SetEnabled(wowDir, patch, check.IsChecked == true);
                RefreshMpqList();
            };
            row.Children.Add(check);

            MpqPanel.Children.Add(row);
        }
    }

    private void OnAddMpq(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select an MPQ patch to add",
            Filter = "MPQ archives (*.mpq)|*.mpq|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _mpq.Add(CurrentWowDir(), dialog.FileName);
        RefreshMpqList();
    }

    private void OnRefreshMpq(object sender, RoutedEventArgs e) => RefreshMpqList();

    private void OnSelectAllMpq(object sender, RoutedEventArgs e) => SetAllMpqEnabled(true);

    private void OnDeselectAllMpq(object sender, RoutedEventArgs e) => SetAllMpqEnabled(false);

    private void SetAllMpqEnabled(bool enabled)
    {
        string wowDir = CurrentWowDir();
        foreach (MpqPatch patch in _mpq.Scan(wowDir))
        {
            _mpq.SetEnabled(wowDir, patch, enabled);
        }

        RefreshMpqList();
    }

    // ---------------- Addons tab ----------------

    private void InitAddonsTab()
    {
        _addons.Load();
        AddonList.ItemsSource = _addonItems;
        CollectionViewSource.GetDefaultView(_addonItems).Filter = FilterAddonRow;
        RefreshAddonList();
        UpdateAddonSearchPlaceholder();
    }

    private bool FilterAddonRow(object item)
    {
        string query = AddonSearchBox.Text.Trim();
        if (query.Length == 0)
        {
            return true;
        }

        var addon = (InstalledAddon)item;
        return WowColorTextParser.StripCodes(addon.Name).Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void OnAddonSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        CollectionViewSource.GetDefaultView(_addonItems).Refresh();
        UpdateAddonSearchPlaceholder();
    }

    private void OnAddonSearchFocusChanged(object sender, RoutedEventArgs e) => UpdateAddonSearchPlaceholder();

    // Driven explicitly from TextChanged/GotFocus/LostFocus instead of an XAML trigger bound to
    // IsFocused: IsFocused reflects logical focus (FocusManager), not keyboard focus, and
    // Keyboard.ClearFocus() (used below for Escape) only clears the latter — a trigger watching
    // IsFocused could end up never seeing it flip back to false. Setting Visibility directly here
    // has no such ambiguity.
    private void UpdateAddonSearchPlaceholder()
        => AddonSearchPlaceholder.Visibility = AddonSearchBox.Text.Length == 0 && !AddonSearchBox.IsFocused
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void OnAddonSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(AddonSearchBox), null);
            UpdateAddonSearchPlaceholder();
            e.Handled = true;
        }
    }

    private void OnClearAddonSearch(object sender, RoutedEventArgs e)
    {
        AddonSearchBox.Text = string.Empty;
        AddonSearchBox.Focus();
    }

    private void RefreshAddonList()
    {
        _addonItems.Clear();
        foreach (InstalledAddon addon in _addons.Addons)
        {
            _addonItems.Add(addon);
        }
    }

    private async void OnOpenAddAddonDialog(object sender, RoutedEventArgs e)
    {
        var dialog = new AddAddonDialog();
        if (ShowModal(dialog) == true && !string.IsNullOrWhiteSpace(dialog.Input))
        {
            await AddAddonAsync(dialog.Input);
        }
    }

    private async Task AddAddonAsync(string input)
    {
        if (_addonBusy)
        {
            return;
        }

        _addonBusy = true;
        AddonStatusText.Text = "Installing…";
        try
        {
            InstalledAddon addon = await _addons.AddAsync(input, CurrentWowDir());
            RefreshAddonList();
            AddonStatusText.Text = $"Installed {WowColorTextParser.StripCodes(addon.Name)}.";
        }
        catch (Exception ex)
        {
            _log.Error($"Addon install failed for {input}", ex);
            AddonStatusText.Text = "Install failed — see the Log tab.";
        }
        finally
        {
            _addonBusy = false;
        }
    }

    private void OnRemoveAddonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InstalledAddon addon })
        {
            _addons.Remove(addon, CurrentWowDir());
            RefreshAddonList();
            AddonStatusText.Text = $"Removed {WowColorTextParser.StripCodes(addon.Name)}.";
        }
    }

    private async void OnAddonDetailsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InstalledAddon addon })
        {
            return;
        }

        string title = WowColorTextParser.StripCodes(addon.Name);
        UpdateStatus($"Loading details for '{title}'...");
        try
        {
            string markdown = await _addons.GetDetailsMarkdownAsync(addon, CurrentWowDir(), CancellationToken.None);
            ShowModal(new MarkdownPreviewDialog(title, markdown));
        }
        finally
        {
            UpdateStatus("Ready.");
        }
    }

    private async void OnUpdateAddonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InstalledAddon addon } && !string.IsNullOrWhiteSpace(addon.SourceRef))
        {
            await AddAddonAsync(addon.SourceRef);
        }
    }

    /// <summary>
    /// Refresh now does everything at once: reload from disk, re-read each tracked addon's own .toc
    /// (in case it changed on disk), auto-adopt any untracked local folder that doesn't collide with an
    /// existing addon's name, and check every remote-tracked addon for an update. A conflict dialog only
    /// appears when a local folder's name actually collides with something already tracked.
    /// </summary>
    private async void OnRefreshAddons(object sender, RoutedEventArgs e)
    {
        string wowDir = CurrentWowDir();

        _addons.Load();
        _addons.RefreshMetadataFromDisk(wowDir);

        AddonLibrary.LocalAddonSyncResult sync = _addons.SyncLocalAddons(wowDir);
        if (sync.Conflicts.Count > 0)
        {
            var dialog = new LocalAddonsDialog(sync.Conflicts);
            if (ShowModal(dialog) == true)
            {
                foreach (LocalAddonCandidate candidate in dialog.Selected)
                {
                    _addons.Adopt(candidate);
                }
            }
        }

        AddonStatusText.Text = "Checking for addon updates…";
        await _addons.CheckForUpdatesAsync();

        RefreshAddonList();
        AddonStatusText.Text = sync.AutoAdopted > 0
            ? $"Refreshed. {sync.AutoAdopted} local addon(s) added automatically."
            : "Refreshed.";
    }

    // ---------------- Home tab (Login & Game) ----------------

    private void LoadSettingsIntoUi()
    {
        LauncherSettings s = _settings.Current;
        GameFolderBox.Text = s.WowDirectory ?? string.Empty;
        AutoLoginCheck.IsChecked = s.AutoLoginEnabled;
        AccountBox.Text = s.Account;
        SavePasswordCheck.IsChecked = s.SavePassword;
        PasswordBoxInput.Password = s.SavePassword ? _settings.GetPassword() : string.Empty;
        DelayBox.Text = s.LoginDelayMs.ToString(CultureInfo.InvariantCulture);
        RealmlistBox.Text = s.Realmlist;
        ClientUrlBox.Text = string.IsNullOrWhiteSpace(s.ClientDownloadUrl)
            ? GameInstallService.DefaultClientUrl
            : s.ClientDownloadUrl;

        UpdateGameFolderHint();
        UpdateRealmlistLabel();
    }

    private void CollectSettingsFromUi()
    {
        LauncherSettings s = _settings.Current;
        s.WowDirectory = string.IsNullOrWhiteSpace(GameFolderBox.Text) ? null : GameFolderBox.Text.Trim();
        s.AutoLoginEnabled = AutoLoginCheck.IsChecked == true;
        s.Account = AccountBox.Text.Trim();
        s.SavePassword = SavePasswordCheck.IsChecked == true;
        s.LoginDelayMs = int.TryParse(DelayBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d)
            ? Math.Max(0, d)
            : s.LoginDelayMs;
        s.Realmlist = RealmlistBox.Text.Trim();

        // Only accept a well-formed absolute http(s) URL; otherwise keep whatever was last valid
        // (a URL drives an actual HTTP request, so a malformed one must never silently take effect).
        string url = ClientUrlBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url) || url == GameInstallService.DefaultClientUrl)
        {
            s.ClientDownloadUrl = null;
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out Uri? parsedUrl) &&
                 (parsedUrl.Scheme == Uri.UriSchemeHttp || parsedUrl.Scheme == Uri.UriSchemeHttps))
        {
            s.ClientDownloadUrl = url;
        }

        _settings.SetPassword(PasswordBoxInput.Password);

        s.EnabledPatchIds = new List<string>();
        foreach (PatchControl pc in _patchControls)
        {
            if (pc.Box.IsChecked == true)
            {
                s.EnabledPatchIds.Add(pc.Def.Id);
            }

            if (pc.Slider is not null && pc.Def.Parameter is PatchParameter p)
            {
                s.PatchParameters[pc.Def.Id] = p.IsInteger ? Math.Round(pc.Slider.Value) : pc.Slider.Value;
            }
        }
    }

    /// <summary>Debounced so a burst of keystrokes settles into one save, not one per character.</summary>
    private void ScheduleSettingsSave()
    {
        if (_loading)
        {
            return;
        }

        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private async void OnSettingsSaveTick(object? sender, EventArgs e)
    {
        _settingsSaveTimer.Stop();
        if (_savingSettings)
        {
            _settingsSaveTimer.Start();
            return;
        }

        _savingSettings = true;
        try
        {
            await PersistSettingsFromUiAsync();
        }
        finally
        {
            _savingSettings = false;
        }
    }

    /// <summary>
    /// Saves every field, then only runs the heavier side effects (realmlist.wtf, folder rescans, a
    /// network update check) for whichever of WoW directory / realmlist / client URL actually changed.
    /// </summary>
    private async Task PersistSettingsFromUiAsync()
    {
        CollectSettingsFromUi();
        _settings.Save();

        string wowDir = CurrentWowDir();
        string realmlist = _settings.Current.Realmlist;
        string clientUrl = CurrentClientUrl();

        bool wowDirChanged = !string.Equals(wowDir, _lastAppliedWowDir, StringComparison.OrdinalIgnoreCase);
        bool realmlistChanged = !string.Equals(realmlist, _lastAppliedRealmlist, StringComparison.Ordinal);
        bool clientUrlChanged = !string.Equals(clientUrl, _lastAppliedClientUrl, StringComparison.OrdinalIgnoreCase);

        if (realmlistChanged && !string.IsNullOrWhiteSpace(realmlist) && Directory.Exists(wowDir))
        {
            try
            {
                _realmlist.Write(wowDir, realmlist);
            }
            catch (Exception ex)
            {
                _log.Error("Failed to write realmlist.wtf.", ex);
            }
        }

        UpdateGameFolderHint();
        UpdateRealmlistLabel();

        if (wowDirChanged)
        {
            RefreshDllList();
            RefreshDetectedDlls();
            RefreshMpqList();
            RefreshAddonList();
        }

        if (wowDirChanged || clientUrlChanged)
        {
            await RefreshPlayButtonStateAsync();
        }

        _lastAppliedWowDir = wowDir;
        _lastAppliedRealmlist = realmlist;
        _lastAppliedClientUrl = clientUrl;

        UpdateStatus("Settings saved.");
    }

    private string CurrentWowDir()
        => !string.IsNullOrWhiteSpace(GameFolderBox.Text) && Directory.Exists(GameFolderBox.Text)
            ? GameFolderBox.Text.Trim()
            : _settings.ResolveWowDirectory();

    private string CurrentClientUrl()
    {
        string url = ClientUrlBox.Text?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(url) ? GameInstallService.DefaultClientUrl : url;
    }

    private void UpdateRealmlistLabel()
    {
        string realm = RealmlistBox.Text?.Trim() ?? string.Empty;
        RealmlistLabel.Text = $"Realmlist: {(string.IsNullOrEmpty(realm) ? "(not set)" : realm)}";
    }

    // ---------------- Settings tab ----------------

    private void OnBrowseGameFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the World of Warcraft folder (contains WoW.exe)" };
        if (!string.IsNullOrWhiteSpace(GameFolderBox.Text) && Directory.Exists(GameFolderBox.Text))
        {
            dialog.InitialDirectory = GameFolderBox.Text;
        }

        if (dialog.ShowDialog() == true)
        {
            // Setting .Text fires TextChanged, which schedules the same auto-save/refresh pipeline.
            GameFolderBox.Text = dialog.FolderName;
        }
    }

    private void UpdateGameFolderHint()
    {
        string dir = CurrentWowDir();
        string exe = Path.Combine(dir, "WoW.exe");
        GameFolderHint.Text = File.Exists(exe) ? $"✓ WoW.exe found in {dir}" : $"⚠ WoW.exe not found in {dir}";
    }

    private async void OnRepairGameFiles(object sender, RoutedEventArgs e)
    {
        string wowDir = CurrentWowDir();
        if (!_install.IsInstalled(wowDir))
        {
            UpdateStatus("Nothing installed to repair yet.");
            return;
        }

        var confirm = new ConfirmDialog(
            "Repair Game Files",
            "This will re-download and reinstall all game files from the source URL, overwriting anything " +
            "that differs locally. There is no per-file update list for this client, so the whole archive " +
            "is refreshed. Continue?",
            confirmText: "Repair",
            cancelText: "Cancel");
        if (ShowModal(confirm) != true)
        {
            return;
        }

        await RunClientDownloadAsync(wowDir, "Game files repaired.", "Repair failed");
    }

    // ---------------- Play / Install / Update ----------------

    private void SetPlayState(PlayButtonState state)
    {
        _playState = state;
        (PlayButton.Content, PlayButton.Background) = state switch
        {
            PlayButtonState.Install => ("INSTALL", InstallColor),
            PlayButtonState.Update => ("UPDATE", UpdateColor),
            _ => ("PLAY", PlayColor),
        };
    }

    private async Task RefreshPlayButtonStateAsync()
    {
        string wowDir = CurrentWowDir();
        if (!_install.IsInstalled(wowDir))
        {
            SetPlayState(PlayButtonState.Install);
            return;
        }

        UpdateCheckResult check = await _install.CheckForUpdateAsync(
            CurrentClientUrl(), _settings.Current.InstalledClientSignature, CancellationToken.None);
        _pendingUpdateCheck = check;
        SetPlayState(check.UpdateAvailable ? PlayButtonState.Update : PlayButtonState.Play);
    }

    private async void OnPlayClicked(object sender, RoutedEventArgs e)
    {
        switch (_playState)
        {
            case PlayButtonState.Install:
                await RunInstallFlowAsync();
                break;
            case PlayButtonState.Update:
                await RunUpdateFlowAsync();
                break;
            default:
                await RunPlayFlowAsync();
                break;
        }
    }

    private async Task RunInstallFlowAsync()
    {
        string defaultDir = !string.IsNullOrWhiteSpace(GameFolderBox.Text) ? GameFolderBox.Text.Trim() : AppContext.BaseDirectory;
        var dialog = new InstallDialog(defaultDir);
        if (ShowModal(dialog) != true)
        {
            return;
        }

        string targetDir = dialog.SelectedFolder;
        bool ok = await RunClientDownloadAsync(targetDir, "Client installed.", "Install failed");
        if (ok)
        {
            _settings.Current.WowDirectory = targetDir;
            _settings.Save();

            // Stop any pending debounce first: setting .Text below fires TextChanged, and without
            // this it would schedule a redundant duplicate of the refresh/recheck we do right here.
            _settingsSaveTimer.Stop();
            GameFolderBox.Text = targetDir;
            _lastAppliedWowDir = targetDir;

            UpdateGameFolderHint();
            RefreshDllList();
            RefreshDetectedDlls();
            RefreshMpqList();
            RefreshAddonList();
        }
    }

    private async Task RunUpdateFlowAsync()
    {
        if (_pendingUpdateCheck is not { UpdateAvailable: true })
        {
            await RefreshPlayButtonStateAsync();
            return;
        }

        var dialog = new UpdateConfirmDialog(_pendingUpdateCheck.Detail);
        if (ShowModal(dialog) != true)
        {
            return;
        }

        await RunClientDownloadAsync(CurrentWowDir(), "Client updated.", "Update failed");
    }

    private async Task<bool> RunClientDownloadAsync(string targetDir, string successMessage, string failureMessage)
    {
        PlayButton.IsEnabled = false;
        MainProgressBar.Visibility = Visibility.Visible;
        MainProgressBar.IsIndeterminate = false;
        MainProgressBar.Value = 0;

        try
        {
            var progress = new Progress<InstallProgress>(OnInstallProgress);
            string? signature = await _install.DownloadAndInstallAsync(CurrentClientUrl(), targetDir, progress, CancellationToken.None);
            _settings.Current.InstalledClientSignature = signature;
            _settings.Save();
            UpdateStatus(successMessage);
            await RefreshPlayButtonStateAsync();
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"{failureMessage}.", ex);
            UpdateStatus($"{failureMessage} — see the Log tab.");
            return false;
        }
        finally
        {
            MainProgressBar.Visibility = Visibility.Collapsed;
            PlayButton.IsEnabled = true;
        }
    }

    private void OnInstallProgress(InstallProgress p)
    {
        if (p.Extracting)
        {
            MainProgressBar.IsIndeterminate = true;
            UpdateStatus("Extracting client...");
            return;
        }

        MainProgressBar.IsIndeterminate = false;
        double pct = p.Total > 0 ? (double)p.Downloaded / p.Total * 100 : 0;
        MainProgressBar.Value = pct;
        string speed = p.BytesPerSecond > 0 ? $" — {FormatBytes((long)p.BytesPerSecond)}/s" : string.Empty;
        UpdateStatus($"Downloading {pct:0}%  ({FormatBytes(p.Downloaded)} / {FormatBytes(p.Total)}){speed}");
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }

        return $"{value:0.#} {units[i]}";
    }

    private async Task RunPlayFlowAsync()
    {
        CollectSettingsFromUi();
        _settings.Save();

        PlayButton.IsEnabled = false;
        var progress = new Progress<string>(UpdateStatus);
        try
        {
            bool ok = await _orchestrator.PlayAsync(progress);
            UpdateStatus(ok ? "Launched." : "Launch failed — see the Log tab.");
        }
        catch (Exception ex)
        {
            _log.Error("Unexpected error during launch.", ex);
            UpdateStatus("Launch failed — see the Log tab.");
        }
        finally
        {
            PlayButton.IsEnabled = true;
        }
    }

    // ---------------- Shared ----------------

    private void UpdateStatus(string message) => StatusText.Text = message;

    private void OnMessageLogged(object? sender, LogEntry entry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText($"{entry.Timestamp:HH:mm:ss} [{entry.Level}] {entry.Message}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        });
    }

    private void RadioButton_Checked(object sender, RoutedEventArgs e)
    {

    }
}
