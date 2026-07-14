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
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markdig.Wpf;
using Microsoft.Win32;
using TeronWoWLauncher.Dialogs;
using TeronWoWLauncher.Models;
using TeronWoWLauncher.Native;
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
    private readonly RealmStatusChecker _realmStatus = new();
    private readonly GameInstallService _install = new();
    private readonly GameProcessService _processCheck = new();
    private readonly GameCacheService _gameCache = new();
    private readonly AddonLibrary _addons = new();

    private readonly List<PatchControl> _patchControls = new();
    private readonly ObservableCollection<DllInfo> _dllItems = new();
    private readonly ObservableCollection<DllInfo> _detectedDllItems = new();
    private readonly ObservableCollection<string> _ignoredDllItems = new();
    private readonly ObservableCollection<InstalledAddon> _addonItems = new();
    private readonly ObservableCollection<LogEntry> _logItems = new();
    private readonly ObservableCollection<string> _realmlistHistoryItems = new();

    private readonly DispatcherTimer _patchApplyTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _settingsSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _realmStatusTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly DispatcherTimer _gameRunningTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private int _realmStatusCheckInFlight;
    private bool _loading;
    private bool _applyingPatches;
    private bool _savingSettings;
    private bool _addonBusy;
    private bool _launchOperationBusy;
    private bool _wowAlreadyRunning;
    private int? _launchedGameProcessId;

    /// <summary>Set only when we minimized the window ourselves for a launch (see MinimizeOnLaunch),
    /// so restoring it when the game exits puts it back exactly how the user left it (Normal or
    /// Maximized) instead of always forcing Normal.</summary>
    private WindowState? _windowStateBeforeMinimize;

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
    private static readonly Brush DisabledPlayColor = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x54));
    private static readonly Brush RealmOnlineBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x7D, 0x3B));
    private static readonly Brush RealmOfflineBrush = new SolidColorBrush(Color.FromRgb(0xB3, 0x3A, 0x3A));
    private static readonly Brush RealmUnknownBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x72));
    private static readonly Brush RealmNetworkUnavailableBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0x92, 0x2B));

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
        LoadRandomBodyBackground();
        WindowChromeHelper.FixMaximizedBounds(this);
        StateChanged += OnWindowStateChanged;
        Closing += OnWindowClosing;

        _settings.Load();
        RestoreWindowPlacement();
        _orchestrator = new LaunchOrchestrator(_settings);
        _log.MessageLogged += OnMessageLogged;
        _patchApplyTimer.Tick += OnPatchApplyTick;
        _settingsSaveTimer.Tick += OnSettingsSaveTick;
        _realmStatusTimer.Tick += OnRealmStatusTick;
        _realmStatusTimer.Start();
        _gameRunningTimer.Tick += OnGameRunningTick;
        _gameRunningTimer.Start();
        OnGameRunningTick(null, EventArgs.Empty); // reflect an already-running game immediately, don't wait for the first tick

        DllList.ItemsSource = _dllItems;
        DetectedDllList.ItemsSource = _detectedDllItems;
        IgnoredDllList.ItemsSource = _ignoredDllItems;
        LogList.ItemsSource = _logItems;
        RealmlistBox.ItemsSource = _realmlistHistoryItems;

        // Save on any change instead of a Save button — CollectSettingsFromUi() validates each field
        // (bad numbers/URLs keep the last good value) before anything is persisted.
        AutoLoginCheck.Checked += (_, _) => ScheduleSettingsSave();
        AutoLoginCheck.Unchecked += (_, _) => ScheduleSettingsSave();
        AccountBox.TextChanged += (_, _) => ScheduleSettingsSave();
        PasswordBoxInput.PasswordChanged += (_, _) => ScheduleSettingsSave();
        SavePasswordCheck.Checked += (_, _) => ScheduleSettingsSave();
        SavePasswordCheck.Unchecked += (_, _) => ScheduleSettingsSave();
        GameFolderBox.TextChanged += (_, _) => ScheduleSettingsSave();
        // ComboBox has no TextChanged event of its own — IsEditable routes typed input through its
        // internal PART_EditableTextBox, whose TextChanged bubbles up as the TextBoxBase attached
        // event, which AddHandler picks up here the same way XAML's "TextBoxBase.TextChanged=..."
        // syntax would.
        RealmlistBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ScheduleSettingsSave()));
        RealmlistBox.LostFocus += (_, _) => UpdateRealmlistLabel();
        ClientUrlBox.TextChanged += (_, _) => ScheduleSettingsSave();
        DelayBox.TextChanged += (_, _) => ScheduleSettingsSave();
        CleanWdbCheck.Checked += (_, _) => ScheduleSettingsSave();
        CleanWdbCheck.Unchecked += (_, _) => ScheduleSettingsSave();
        MinimizeOnLaunchCheck.Checked += (_, _) => ScheduleSettingsSave();
        MinimizeOnLaunchCheck.Unchecked += (_, _) => ScheduleSettingsSave();

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

        // Deferred to Loaded rather than run here directly: showing a modal ConfirmDialog/dialog needs
        // its Owner (this window) to have already been shown, which hasn't happened yet mid-constructor.
        Loaded += (_, _) => _ = VerifyPristineBackupIntegrityAsync();

        // Same startup re-scan the Addons tab's own "Refresh" button does (local-folder adoption +
        // update check), so a newer addon version or a manually-dropped-in folder surfaces without the
        // user having to remember to click it. DLL "detected" and MPQ patch scans already happen above
        // (RefreshDetectedDlls/RefreshMpqList), since re-scanning those doesn't need a shown Owner.
        Loaded += (_, _) => _ = RefreshAddonsAsync();
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

    /// <summary>Picks one of the bundled background paintings at random for this launch (see Artwork/, embedded via the csproj).</summary>
    private const int BodyBackgroundImageCount = 11;

    private void LoadRandomBodyBackground()
    {
        int pick = Random.Shared.Next(1, BodyBackgroundImageCount + 1);
        var uri = new Uri($"pack://application:,,,/Artwork/background_{pick}.jpg", UriKind.Absolute);
        BodyBackgroundImage.Source = new BitmapImage(uri);
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

        // ComboBox has no SelectAll() of its own; reach into its editable-mode template part instead.
        RealmlistBox.ApplyTemplate();
        if (RealmlistBox.Template.FindName("PART_EditableTextBox", RealmlistBox) is TextBox editableText)
        {
            editableText.SelectAll();
        }

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
            box.Click += (_, _) => { SchedulePatchApply(); RefreshSignatureMpqWarning(); };
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

        RefreshSignatureMpqWarning();
    }

    /// <summary>
    /// Signature Removal disables the file-integrity check that would otherwise reject custom MPQ
    /// patches — without it, WoW refuses to launch at all while one is active, reporting a corrupted
    /// Data folder rather than just ignoring the patch. Shown on both the Tweaks and MPQ Patches tabs
    /// (whichever the user happens to be on) whenever that combination is currently in effect.
    /// </summary>
    private void RefreshSignatureMpqWarning()
    {
        bool signatureRemovalEnabled = _settings.Current.EnabledPatchIds.Contains("signature-removal");
        int activeMpqCount = _mpq.Scan(CurrentWowDir()).Count(p => p.Enabled);
        bool atRisk = !signatureRemovalEnabled && activeMpqCount > 0;

        SignatureMpqWarningBanner.Visibility = atRisk ? Visibility.Visible : Visibility.Collapsed;
        MpqSignatureWarningBanner.Visibility = atRisk ? Visibility.Visible : Visibility.Collapsed;

        if (atRisk)
        {
            string patchWord = activeMpqCount == 1 ? "patch" : "patches";
            SignatureMpqWarningText.Text =
                $"⚠ {activeMpqCount} active custom MPQ {patchWord} — with Signature Removal off, WoW will refuse " +
                "to launch and report a corrupted Data folder instead of just ignoring them.";
            MpqSignatureWarningText.Text =
                "⚠ Signature Removal is disabled (Tweaks tab). WoW will refuse to launch while any patch above " +
                "is active, reporting a corrupted Data folder — enable it or disable these patches first.";
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

    private void SaveDllList()
    {
        try
        {
            _dlls.WriteActiveNames(CurrentWowDir(), _dllItems.Select(d => d.Name).ToList());
        }
        catch (Exception ex)
        {
            _log.Error("Failed to save dlls.txt.", ex);
            UpdateStatus("Failed to save the DLL list — see the Log tab.");
        }
    }

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
                RunMpqOperation(() => _mpq.Remove(wowDir, patch), $"remove patch {patch.Letter}");
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
                RunMpqOperation(() => _mpq.SetEnabled(wowDir, patch, check.IsChecked == true), $"toggle patch {patch.Letter}");
                RefreshMpqList();
            };
            row.Children.Add(check);

            MpqPanel.Children.Add(row);
        }

        RefreshSignatureMpqWarning();
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

        RunMpqOperation(() => _mpq.Add(CurrentWowDir(), dialog.FileName), "add the MPQ patch");
        RefreshMpqList();
    }

    private void OnRefreshMpq(object sender, RoutedEventArgs e) => RefreshMpqList();

    // The MPQ Patches tab's visible scrollbar is a standalone ScrollBar (see MainWindow.xaml) rather
    // than MpqScrollViewer's own — that keeps it in a dedicated Grid column shared with the pinned
    // header, so the two never drift out of alignment. Dragging/clicking it only moves its own Value;
    // this is what actually applies that to the ScrollViewer it's bound to.
    private void OnMpqScrollBarScroll(object sender, ScrollEventArgs e) => MpqScrollViewer.ScrollToVerticalOffset(e.NewValue);

    private void OnSelectAllMpq(object sender, RoutedEventArgs e) => SetAllMpqEnabled(true);

    private void OnDeselectAllMpq(object sender, RoutedEventArgs e) => SetAllMpqEnabled(false);

    private void SetAllMpqEnabled(bool enabled)
    {
        string wowDir = CurrentWowDir();
        foreach (MpqPatch patch in _mpq.Scan(wowDir))
        {
            RunMpqOperation(() => _mpq.SetEnabled(wowDir, patch, enabled), $"toggle patch {patch.Letter}");
        }

        RefreshMpqList();
    }

    /// <summary>
    /// Runs an MPQ file operation (rename/copy/delete under Data\), reporting failure instead of
    /// letting it throw unhandled from a button click — the Data folder can be locked by a running
    /// game or antivirus scan at the exact moment the user clicks.
    /// </summary>
    private void RunMpqOperation(Action action, string failureContext)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to {failureContext}.", ex);
            UpdateStatus($"Failed to {failureContext} — see the Log tab.");
        }
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

        // InstalledAddon doesn't raise property-change notifications, so nothing binds to this
        // directly — recomputed here, the one place the addon list is (re)built from, instead of at
        // every call site that might change HasUpdateAvailable.
        UpdateAllAddonsButton.IsEnabled = _addonItems.Any(a => a.HasUpdateAvailable);
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
            string? repoUrl = addon.SourceKind == AddonSourceKind.GitHub ? addon.SourceRef : null;
            var dialog = new AddonDetailsDialog(title, markdown, addon.IgnoreUpdates, repoUrl);
            ShowModal(dialog);

            if (dialog.IgnoreUpdates != addon.IgnoreUpdates)
            {
                addon.IgnoreUpdates = dialog.IgnoreUpdates;
                if (addon.IgnoreUpdates)
                {
                    addon.HasUpdateAvailable = false;
                }

                _addons.Save();
                RefreshAddonList();
            }

            UpdateStatus("Ready.");
        }
        catch (Exception ex)
        {
            // Defense in depth: GetDetailsMarkdownAsync's own sub-paths already catch their own
            // errors, but this is an async void handler — anything that slips through uncaught here
            // would crash the whole app, not just fail this one dialog.
            _log.Error($"Could not load details for '{title}'.", ex);
            UpdateStatus("Could not load addon details — see the Log tab.");
        }
    }

    private async void OnUpdateAddonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InstalledAddon addon } && !string.IsNullOrWhiteSpace(addon.SourceRef))
        {
            await AddAddonAsync(addon.SourceRef);
        }
    }

    /// <summary>Updates every addon currently flagged as updatable, one at a time, reusing the same
    /// install/update path as the per-row Update button (including its single-flight guard).</summary>
    private async void OnUpdateAllAddonsClick(object sender, RoutedEventArgs e)
    {
        List<InstalledAddon> updatable = _addons.Addons
            .Where(a => a.HasUpdateAvailable && !string.IsNullOrWhiteSpace(a.SourceRef))
            .ToList();

        if (updatable.Count == 0)
        {
            AddonStatusText.Text = "No addon updates available.";
            return;
        }

        foreach (InstalledAddon addon in updatable)
        {
            await AddAddonAsync(addon.SourceRef!);
        }

        AddonStatusText.Text = $"Updated {updatable.Count} addon(s).";
    }

    // The Addons tab's visible scrollbar is a standalone ScrollBar (see MainWindow.xaml) rather than
    // AddonScrollViewer's own — that keeps it in a dedicated Grid column shared with the pinned header,
    // so the two never drift out of alignment. Dragging/clicking it only moves its own Value; this is
    // what actually applies that to the ScrollViewer it's bound to.
    private void OnAddonScrollBarScroll(object sender, ScrollEventArgs e) => AddonScrollViewer.ScrollToVerticalOffset(e.NewValue);

    // AddonList is a ListBox, which always carries its own internal ScrollViewer even with its
    // scrollbar visibility set to Disabled — that inner ScrollViewer still claims (marks Handled) any
    // mouse wheel input over the list, so it never reaches AddonScrollViewer above it. Intercepting the
    // wheel here, before the ListBox's own handler runs, and driving AddonScrollViewer directly is the
    // standard workaround for this nested-ScrollViewer wheel-eating behavior.
    private void OnAddonListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        AddonScrollViewer.ScrollToVerticalOffset(AddonScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>
    /// Refresh now does everything at once: reload from disk, re-read each tracked addon's own .toc
    /// (in case it changed on disk), auto-adopt any untracked local folder that doesn't collide with an
    /// existing addon's name, and check every remote-tracked addon for an update. A conflict dialog only
    /// appears when a local folder's name actually collides with something already tracked.
    /// </summary>
    private async void OnRefreshAddons(object sender, RoutedEventArgs e) => await RefreshAddonsAsync();

    /// <summary>
    /// Reloads addons.json, re-reads each tracked addon's own .toc, adopts/flags untracked local
    /// AddOns folders, and checks every remote-tracked addon for updates — everything the Addons
    /// tab's "Refresh" button does. Also run once automatically on startup (see the constructor),
    /// so new updates/local folders surface without the user having to remember to click Refresh.
    ///
    /// Shares <see cref="_addonBusy"/> with <see cref="AddAddonAsync"/>: without this, the startup
    /// auto-refresh could overlap with a near-simultaneous Add/Update click (both read-modify-write
    /// the same in-memory addon list and addons.json), which wasn't reachable before this ran
    /// automatically — previously only two rapid manual Refresh clicks could race.
    /// </summary>
    private async Task RefreshAddonsAsync()
    {
        if (_addonBusy)
        {
            return;
        }

        _addonBusy = true;
        try
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
        finally
        {
            _addonBusy = false;
        }
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
        CleanWdbCheck.IsChecked = s.CleanWdbBeforeLaunch;
        MinimizeOnLaunchCheck.IsChecked = s.MinimizeOnLaunch;
        RealmlistBox.Text = s.Realmlist;
        RefreshRealmlistHistoryItems();
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
        s.CleanWdbBeforeLaunch = CleanWdbCheck.IsChecked == true;
        s.MinimizeOnLaunch = MinimizeOnLaunchCheck.IsChecked == true;
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
                if (!_realmlist.Write(wowDir, realmlist))
                {
                    UpdateStatus("Warning: realmlist.wtf may not have saved correctly — see the Log tab.");
                }
            }
            catch (Exception ex)
            {
                _log.Error("Failed to write realmlist.wtf.", ex);
                UpdateStatus("Failed to write realmlist.wtf — see the Log tab.");
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
        _ = RefreshRealmStatusAsync();
    }

    private async void OnRealmStatusTick(object? sender, EventArgs e) => await RefreshRealmStatusAsync();

    /// <summary>
    /// Probes the current realmlist's auth port and colors <see cref="RealmlistStatusDot"/> accordingly.
    /// Guarded against overlap: if a slow/timing-out check from the previous timer tick is still in
    /// flight, this tick is skipped rather than piling up a second concurrent probe.
    /// </summary>
    private async Task RefreshRealmStatusAsync()
    {
        if (Interlocked.CompareExchange(ref _realmStatusCheckInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            string realm = RealmlistBox.Text?.Trim() ?? string.Empty;
            RealmStatus status = await _realmStatus.CheckAsync(realm);

            RealmlistStatusDot.Fill = status switch
            {
                RealmStatus.Online => RealmOnlineBrush,
                RealmStatus.Offline => RealmOfflineBrush,
                RealmStatus.NetworkUnavailable => RealmNetworkUnavailableBrush,
                _ => RealmUnknownBrush,
            };
            RealmlistStatusDot.ToolTip = status switch
            {
                RealmStatus.Online => "Realm is up — auth server accepted a connection.",
                RealmStatus.Offline => "Realm appears to be down — auth server refused the connection or timed out.",
                RealmStatus.NetworkUnavailable => "No network connection detected — can't check realm status right now.",
                _ when string.IsNullOrEmpty(realm) => "No realmlist set.",
                _ => "Realm address doesn't resolve — check the realmlist for typos.",
            };

            // Only cache addresses that actually resolve (Online or Offline both mean "a real host");
            // Unknown means DNS itself gave a real "no such host" answer, so it's a typo'd/fake
            // address that would just pollute the dropdown with junk the user never meant to keep. A
            // real server that's just temporarily down still gets cached — the whole point is being
            // able to switch back to it once it's back up. The mirror case: this same periodic check
            // also catches a realm that used to resolve but no longer does (e.g. decommissioned) and
            // prunes it back out, but only for whichever realm is currently active — a
            // cached-but-unvisited entry elsewhere in the dropdown isn't re-checked until the user
            // actually selects it.
            //
            // NetworkUnavailable deliberately touches history neither way: it means this machine
            // couldn't even attempt the check (no local network path, or the DNS server itself was
            // unreachable), which says nothing about whether the realm address is real — pruning it
            // here is exactly the bug this status exists to avoid.
            switch (status)
            {
                case RealmStatus.Online:
                case RealmStatus.Offline:
                    RecordRealmlistHistory(realm);
                    break;
                case RealmStatus.Unknown:
                    RemoveRealmlistHistory(realm);
                    break;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _realmStatusCheckInFlight, 0);
        }
    }

    /// <summary>
    /// Adds a confirmed-resolvable realm to the front of the history dropdown (most-recent-first,
    /// deduplicated, capped), persisting only when something actually changed.
    /// </summary>
    private void RecordRealmlistHistory(string realm)
    {
        if (string.IsNullOrWhiteSpace(realm))
        {
            return;
        }

        List<string> history = _settings.Current.RealmlistHistory;
        if (history.Count > 0 && string.Equals(history[0], realm, StringComparison.OrdinalIgnoreCase))
        {
            return; // already the most-recent entry; nothing to change
        }

        history.RemoveAll(h => string.Equals(h, realm, StringComparison.OrdinalIgnoreCase));
        history.Insert(0, realm);
        const int maxHistory = 10;
        if (history.Count > maxHistory)
        {
            history.RemoveRange(maxHistory, history.Count - maxHistory);
        }

        _settings.Save();
        RefreshRealmlistHistoryItems();
    }

    /// <summary>Prunes a realm that no longer resolves at all — the reverse of <see cref="RecordRealmlistHistory"/>.</summary>
    private void RemoveRealmlistHistory(string realm)
    {
        if (string.IsNullOrWhiteSpace(realm))
        {
            return;
        }

        int removed = _settings.Current.RealmlistHistory.RemoveAll(h => string.Equals(h, realm, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            _settings.Save();
            RefreshRealmlistHistoryItems();
        }
    }

    private void RefreshRealmlistHistoryItems()
    {
        string current = RealmlistBox.Text;
        _realmlistHistoryItems.Clear();
        foreach (string h in _settings.Current.RealmlistHistory)
        {
            _realmlistHistoryItems.Add(h);
        }

        // Refreshing ItemsSource can otherwise disturb an editable ComboBox's own in-progress text.
        RealmlistBox.Text = current;
    }

    private void OnRealmlistHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (RealmlistBox.SelectedItem is string selected && !_loading)
        {
            RealmlistBox.Text = selected;
            UpdateRealmlistLabel();
            ScheduleSettingsSave();
        }
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

    /// <summary>
    /// One-time-per-launch check: WoW.exe.backup is the pristine source every future patch rebuild is
    /// built from, so if it's changed since it was created (disk corruption, an interrupted write from
    /// before the atomic-rename fix, or external tampering), that corruption would otherwise carry
    /// silently into every patch from here on. Offers a repair (which also re-establishes a fresh,
    /// verified backup) rather than just logging it where it'd likely go unnoticed.
    /// </summary>
    private async Task VerifyPristineBackupIntegrityAsync()
    {
        string wowDir = CurrentWowDir();
        BackupIntegrityStatus status;
        try
        {
            status = await Task.Run(() => _orchestrator.VerifyPristineBackupIntegrity(wowDir));
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not verify the pristine backup's integrity: {ex.Message}");
            return;
        }

        if (status != BackupIntegrityStatus.Mismatch)
        {
            return;
        }

        _log.Warn("WoW.exe.backup no longer matches the hash recorded when it was created — it may be corrupted or was modified outside the launcher.");

        var confirm = new ConfirmDialog(
            "Backup integrity check failed",
            "Your saved pristine WoW.exe backup appears to have changed unexpectedly since it was " +
            "created — it may be corrupted, or modified outside the launcher. Every executable patch " +
            "is rebuilt from this file, so if it's no longer truly clean, that corruption would carry " +
            "into every future patch.\n\n" +
            "Repair Game Files now to get a guaranteed-clean client? This also re-establishes a fresh, verified backup.",
            confirmText: "Repair Now",
            cancelText: "Later");
        if (ShowModal(confirm) != true)
        {
            return;
        }

        _orchestrator.DiscardCorruptBackup(wowDir);
        await RunClientDownloadAsync(wowDir, "Game files repaired.", "Repair failed");
    }

    // ---------------- Play / Install / Update ----------------

    /// <summary>
    /// Multi-instance guard: a second WoW.exe from the same game folder sharing one Data\/WTF\ folder
    /// risks save/cache write conflicts, and it was previously possible to spawn one anyway since only
    /// the executable-patch step (not the actual launch) checked whether the game was already running.
    ///
    /// Checks two things, either of which counts as "running": (1) the specific process we ourselves
    /// last launched, tracked by PID and checked via Process.GetProcessById/.HasExited — reliable even
    /// right after injection, when it's still settling; (2) GameProcessService's folder-based scan,
    /// for a WoW.exe the user started some other way (double-clicking it directly) that we never
    /// launched and so have no PID for. (1) exists specifically because (2) alone isn't reliable
    /// enough right after our own launch: it matches by reading the process's MainModule path, which
    /// can throw (and get silently treated as "not running") for a process still settling from
    /// CreateSuspended+inject+Resume — the exact same access-denied failure mode already seen from
    /// AutoLoginService querying the same freshly-launched process's I/O counters.
    /// </summary>
    private void OnGameRunningTick(object? sender, EventArgs e)
    {
        bool running = IsTrackedLaunchStillRunning() || _processCheck.IsRunning(CurrentWowDir());
        if (running == _wowAlreadyRunning)
        {
            return;
        }

        _wowAlreadyRunning = running;
        UpdatePlayButtonEnabled();

        if (!running)
        {
            FocusLauncherWindow();
        }
    }

    /// <summary>
    /// Brings the launcher back to the foreground once the game exits, so the user doesn't have to
    /// manually alt-tab back to it. Always active, regardless of MinimizeOnLaunch — restores from
    /// whatever state we minimized it to (Normal or Maximized), or just re-activates it if it was
    /// never minimized (e.g. the user alt-tabbed away rather than us hiding it).
    /// </summary>
    private void FocusLauncherWindow()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = _windowStateBeforeMinimize ?? WindowState.Normal;
        }

        _windowStateBeforeMinimize = null;

        Show();
        Activate();

        // Activate() alone can be silently ignored by Windows' foreground-lock restrictions when
        // another app currently owns focus (the exact same unreliability AutoLoginService already
        // works around for the game window) — the direct Win32 call is a more reliable fallback.
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            User32.SetForegroundWindow(hwnd);
        }
    }

    private bool IsTrackedLaunchStillRunning()
    {
        if (_launchedGameProcessId is not int pid)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No process with this ID any more - it has exited (or the ID was recycled to something
            // else, in which case treating it as "gone" is still the correct, safe answer).
            _launchedGameProcessId = null;
            return false;
        }
        catch (Win32Exception)
        {
            // The process exists but denied even this minimal query — the same access-denied wall
            // AutoLoginService already hit querying I/O counters on this same freshly-injected
            // process. Since we can't tell either way, fail toward "still running": that just keeps
            // Play disabled a little longer than strictly necessary, whereas assuming "not running"
            // risks re-enabling it and letting a second instance through — the one thing this guard
            // exists to prevent. Not a permanent stuck state either way: the folder-based fallback
            // check (GameProcessService.IsRunning, OR'd in by the caller) doesn't depend on this PID
            // at all, so it still correctly detects the moment this process actually exits.
            return true;
        }
    }

    /// <summary>
    /// PlayButton.IsEnabled has two independent reasons to be false — an install/update/launch flow
    /// already in progress, or the game already running — so every place that used to toggle it
    /// directly goes through here instead, or one flag finishing would incorrectly re-enable the
    /// button while the other reason still applies.
    /// </summary>
    private void SetLaunchOperationBusy(bool busy)
    {
        _launchOperationBusy = busy;
        UpdatePlayButtonEnabled();
    }

    private void UpdatePlayButtonEnabled()
    {
        PlayButton.IsEnabled = !_launchOperationBusy && !_wowAlreadyRunning;
        PlayButton.ToolTip = _wowAlreadyRunning
            ? "WoW is already running from this game folder — close it first."
            : null;
        UpdatePlayButtonAppearance();
    }

    private void SetPlayState(PlayButtonState state)
    {
        _playState = state;
        PlayButton.Content = state switch
        {
            PlayButtonState.Install => "INSTALL",
            PlayButtonState.Update => "UPDATE",
            _ => "PLAY",
        };
        UpdatePlayButtonAppearance();
    }

    /// <summary>
    /// The Play button's background follows its state color (Play/Install/Update) normally, but
    /// turns flat gray whenever it's actually disabled — the app-wide Button style's default
    /// IsEnabled=False behavior only dims content to 45% opacity, which still reads as "colored but
    /// faded" rather than clearly inactive. Called after either half of the button's appearance
    /// (enabled/disabled, or which state it's in) changes, since both feed into this.
    /// </summary>
    private void UpdatePlayButtonAppearance()
    {
        PlayButton.Background = !PlayButton.IsEnabled
            ? DisabledPlayColor
            : _playState switch
            {
                PlayButtonState.Install => InstallColor,
                PlayButtonState.Update => UpdateColor,
                _ => PlayColor,
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
        SetLaunchOperationBusy(true);
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
            SetLaunchOperationBusy(false);
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
        // Belt-and-suspenders against the 2-second poll's own race window: re-check right at the
        // moment of the click rather than trusting whatever OnGameRunningTick last observed.
        if (IsTrackedLaunchStillRunning() || _processCheck.IsRunning(CurrentWowDir()))
        {
            _wowAlreadyRunning = true;
            UpdatePlayButtonEnabled();
            UpdateStatus("WoW is already running from this game folder — close it first.");
            return;
        }

        CollectSettingsFromUi();
        _settings.Save();

        // Last-chance guard, independent of which tab the user was actually looking at: launching in
        // this state doesn't degrade gracefully, it makes WoW refuse to start entirely (a "corrupted
        // Data folder" error), so this is caught here even if both tab warnings went unnoticed.
        bool signatureRemovalEnabled = _settings.Current.EnabledPatchIds.Contains("signature-removal");
        int activeMpqCount = _mpq.Scan(CurrentWowDir()).Count(p => p.Enabled);
        if (!signatureRemovalEnabled && activeMpqCount > 0)
        {
            string patchWord = activeMpqCount == 1 ? "patch" : "patches";
            var confirm = new ConfirmDialog(
                "Launch will fail",
                $"Signature Removal is off, but {activeMpqCount} custom MPQ {patchWord} are still active.\n\n" +
                "WoW will refuse to launch and report a corrupted Data folder instead of just ignoring them.\n\n" +
                "Enable Signature Removal now and continue?",
                confirmText: "Enable & Continue",
                cancelText: "Cancel");
            if (ShowModal(confirm) != true)
            {
                UpdateStatus("Launch cancelled — resolve the Signature Removal / MPQ patch conflict first.");
                return;
            }

            PatchControl? signatureControl = _patchControls.FirstOrDefault(pc => pc.Def.Id == "signature-removal");
            if (signatureControl is not null)
            {
                signatureControl.Box.IsChecked = true;
            }

            CollectSettingsFromUi();
            _settings.Save();
            RefreshSignatureMpqWarning();
        }

        if (_settings.Current.CleanWdbBeforeLaunch)
        {
            _gameCache.Clear(CurrentWowDir());
        }

        SetLaunchOperationBusy(true);
        var progress = new Progress<string>(UpdateStatus);
        try
        {
            PlayResult result = await _orchestrator.PlayAsync(progress, OnGameProcessLaunched);
            UpdateStatus(result.Success ? "Launched." : "Launch failed — see the Log tab.");
        }
        catch (Exception ex)
        {
            _log.Error("Unexpected error during launch.", ex);
            UpdateStatus("Launch failed — see the Log tab.");
        }
        finally
        {
            if (!_wowAlreadyRunning)
            {
                _wowAlreadyRunning = IsTrackedLaunchStillRunning() || _processCheck.IsRunning(CurrentWowDir());
            }

            SetLaunchOperationBusy(false);
        }
    }

    /// <summary>
    /// Called by LaunchOrchestrator.PlayAsync the moment the game process exists and every DLL is
    /// injected — well before auto-login finishes (which can take several more seconds waiting for
    /// the window/loading screen). Runs on the UI thread, same as the rest of RunPlayFlowAsync (no
    /// ConfigureAwait(false) anywhere in this codebase, so the await chain never leaves it).
    ///
    /// Tracking the PID this early (rather than only after the whole flow returns) matters for the
    /// multi-instance guard specifically: without it, a click on Play during that several-second
    /// auto-login window would fall back to the folder-based process scan — the exact fragile,
    /// module-path-matching check that can throw/misreport "not running" for a process still
    /// settling right after CreateSuspended+inject+Resume, which is what let a second instance slip
    /// through in the first place.
    /// </summary>
    private void OnGameProcessLaunched(int pid)
    {
        _launchedGameProcessId = pid;
        _wowAlreadyRunning = true;
        UpdatePlayButtonEnabled();

        if (_settings.Current.MinimizeOnLaunch && WindowState != WindowState.Minimized)
        {
            _log.Info("Minimizing launcher window (game started).");
            _windowStateBeforeMinimize = WindowState;
            WindowState = WindowState.Minimized;
        }
    }

    // ---------------- Shared ----------------

    private void UpdateStatus(string message) => StatusText.Text = message;

    // Caps _logItems so a long play session can't grow it without bound — Logger's own per-day log
    // file on disk is a separate, complete record and is unaffected by this trim.
    private const int MaxLogItems = 2000;

    private void OnMessageLogged(object? sender, LogEntry entry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _logItems.Add(entry);
            while (_logItems.Count > MaxLogItems)
            {
                _logItems.RemoveAt(0);
            }

            LogScrollViewer.ScrollToEnd();
        });
    }

    private void OnClearLog(object sender, RoutedEventArgs e) => _logItems.Clear();

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        string text = string.Join(
            Environment.NewLine,
            _logItems.Select(entry => $"{entry.Timestamp:HH:mm:ss} [{entry.LevelDisplay}] {entry.Message}"));
        try { Clipboard.SetText(text); } catch { /* clipboard can be transiently locked by another app */ }
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_log.LogFilePath);
            if (dir is not null)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not open log folder: {ex.Message}");
        }
    }

    // Same standalone-ScrollBar-drives-a-hidden-ScrollViewer pattern as MPQ Patches/Addons (see
    // OnAddonScrollBarScroll's own comment for the alignment rationale).
    private void OnLogScrollBarScroll(object sender, ScrollEventArgs e) => LogScrollViewer.ScrollToVerticalOffset(e.NewValue);

    // LogList is a ListBox, which always carries its own internal ScrollViewer even with its scrollbar
    // visibility set to Disabled — that inner ScrollViewer still claims (marks Handled) any mouse wheel
    // input over the list, so it never reaches LogScrollViewer above it. Intercepting the wheel here,
    // before the ListBox's own handler runs, and driving LogScrollViewer directly is the standard
    // workaround for this nested-ScrollViewer wheel-eating behavior.
    private void OnLogListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        LogScrollViewer.ScrollToVerticalOffset(LogScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void RadioButton_Checked(object sender, RoutedEventArgs e)
    {

    }
}
