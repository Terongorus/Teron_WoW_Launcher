using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
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
using TeronWoWLauncher.Services.Addons;
using TeronWoWLauncher.Services.Core;
using TeronWoWLauncher.Services.Dlls;
using TeronWoWLauncher.Services.Launch;
using TeronWoWLauncher.Services.Patching;
using TeronWoWLauncher.Services.UI;

namespace TeronWoWLauncher;

public partial class MainWindow : Window
{
    private enum PlayButtonState { Play, Install, Update }

    private readonly Logger _log = Logger.Instance;
    private readonly SettingsService _settings = new();
    private readonly DirectorySettingsService _dirSettings = new();
    private readonly LaunchOrchestrator _orchestrator;
    private readonly DllListService _dlls = new();
    private readonly MpqPatchService _mpq = new();
    private readonly RealmlistService _realmlist = new();
    private readonly RealmStatusChecker _realmStatus = new();
    private readonly GameInstallService _install = new();
    private readonly GameProcessService _processCheck = new();
    private readonly GameCacheService _gameCache = new();
    private readonly AddonLibrary _addons = new();
    private readonly LauncherUpdateService _launcherUpdate = new();
    private readonly MarketplaceService _marketplace = new();

    private readonly List<PatchControl> _patchControls = new();
    private readonly ObservableCollection<DllInfo> _dllItems = new();
    private readonly ObservableCollection<DllInfo> _detectedDllItems = new();
    private readonly ObservableCollection<string> _ignoredDllItems = new();
    private readonly ObservableCollection<InstalledAddon> _addonItems = new();
    private readonly ObservableCollection<LogEntry> _logItems = new();
    private readonly ObservableCollection<string> _realmlistHistoryItems = new();
    private readonly ObservableCollection<string> _managedDirectoryItems = new();

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
    private CancellationTokenSource? _downloadCts;
    private bool _downloadSupportsPauseResume;
    private bool _downloadPaused;
    private (string TargetDir, string SuccessMessage, string FailureMessage, Brush Color)? _pausedDownloadParams;
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

        // The configured directory may have been deleted/moved since the last run - fall back to the
        // most-recently-used OTHER managed directory that still exists, rather than leaving the UI
        // pointed at a dead path with no clear next step. Visible (a toast once the window is up), not
        // silent - a directory switching under the user without them noticing is exactly the kind of
        // surprise that caused real problems earlier this project.
        string? deadWowDir = _settings.Current.WowDirectory;
        string? fallbackWowDir = TryResolveFallbackDirectory();
        if (fallbackWowDir is not null)
        {
            _settings.Current.WowDirectory = fallbackWowDir;
            _settings.Save();
        }

        _dirSettings.Load(_settings.ResolveWowDirectory());
        RecordManagedDirectory(_settings.ResolveWowDirectory());
        RestoreWindowPlacement();
        _orchestrator = new LaunchOrchestrator(_settings, _dirSettings);
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
        DirectorySwitchBox.ItemsSource = _managedDirectoryItems;

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
        LoadSettingsIntoUi();
        RefreshDllList();
        RefreshDetectedDlls();
        RefreshIgnoredDllList();
        RefreshMpqList();
        InitAddonsTab();
        _loading = false;

        _lastAppliedWowDir = CurrentWowDir();
        _lastAppliedRealmlist = _dirSettings.Current.Realmlist;
        _lastAppliedClientUrl = CurrentClientUrl();

        if (fallbackWowDir is not null)
        {
            ShowGlobalToast($"'{deadWowDir}' no longer exists — switched to '{fallbackWowDir}'.", ToastSeverity.Warning);
            _log.Warn($"Configured WoW directory '{deadWowDir}' no longer exists; fell back to managed directory '{fallbackWowDir}'.");
        }
        else
        {
            ShowGlobalToast("Ready.", ToastSeverity.Info);
        }

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

        // "What's new" (if the version changed since last run) and the launcher's own update check,
        // in that order - see CheckForLauncherUpdateAsync's own doc comment for why sequential.
        Loaded += (_, _) => _ = CheckForLauncherUpdateAsync();
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

        foreach (PatchDefinition patch in catalog)
        {
            // No DefaultEnabled fallback here - a fresh directory with no recorded selection starts
            // with every tweak unchecked, full stop (a new WoW install shouldn't silently inherit
            // whatever a different installation happened to have enabled).
            bool isChecked = _dirSettings.Current.EnabledPatchIds.Contains(patch.Id);

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
                if (_dirSettings.Current.PatchParameters.TryGetValue(patch.Id, out double stored))
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
        bool signatureRemovalEnabled = _dirSettings.Current.EnabledPatchIds.Contains("signature-removal");
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
        _dirSettings.Save(CurrentWowDir());
        _patchApplyTimer.Stop();
        _patchApplyTimer.Start();
    }

    private async void OnPatchApplyTick(object? sender, EventArgs e)
    {
        _patchApplyTimer.Stop();

        // MainProgressBar/ProgressStatusText are shared with the client download/repair/update flow,
        // the launcher self-update download, and the startup backup-integrity check - all of which
        // set Visibility=Visible synchronously the moment they start. If any of those is already
        // showing the bar, barging in here would show/collapse it independently of whichever of those
        // is still actually running, hiding the bar out from under it while its own progress text and
        // Pause/Cancel buttons (untouched by this method) keep updating - reschedule instead and let
        // whichever operation currently owns the bar finish first.
        if (_applyingPatches || MainProgressBar.Visibility == Visibility.Visible)
        {
            _patchApplyTimer.Start();
            return;
        }

        _applyingPatches = true;
        MainProgressBar.Visibility = Visibility.Visible;
        MainProgressBar.IsIndeterminate = true;
        MainProgressBar.Foreground = PlayColor;
        try
        {
            // Attached directly to the bar (ProgressStatusText), not a toast - same reasoning as
            // the client download's progress narration: this reports repeatedly during one rebuild,
            // and re-showing a toast on every message made a just-closed one reopen moments later.
            await _orchestrator.ApplyPatchesAsync(new Progress<string>(msg => ProgressStatusText.Text = msg));
        }
        finally
        {
            _applyingPatches = false;
            MainProgressBar.Visibility = Visibility.Collapsed;
            MainProgressBar.IsIndeterminate = false;
            ProgressStatusText.Text = string.Empty;
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
            ShowTabToast("Failed to save the DLL list — see the Log tab.", ToastSeverity.Error);
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
            ShowTabToast($"Added {Path.GetFileName(entry)}.", ToastSeverity.Success);
        }
        else
        {
            ShowTabToast($"{Path.GetFileName(entry)} is already tracked.", ToastSeverity.Info);
        }
    }

    private void OnRemoveDll(object sender, RoutedEventArgs e)
    {
        if (DllList.SelectedItem is DllInfo item)
        {
            _dllItems.Remove(item);
            SaveDllList();
            RefreshDetectedDlls();
            ShowTabToast($"Removed {item.Name}.", ToastSeverity.Success);
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
        foreach (string name in _dlls.ScanForUntrackedDlls(wowDir, _dirSettings.Current.IgnoredDetectedDlls))
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

        List<string> ignored = _dirSettings.Current.IgnoredDetectedDlls;
        foreach (DllInfo item in selected)
        {
            if (!ignored.Contains(item.Name, StringComparer.OrdinalIgnoreCase))
            {
                ignored.Add(item.Name);
            }
        }

        _dirSettings.Save(CurrentWowDir());
        RefreshDetectedDlls();
        RefreshIgnoredDllList();
    }

    // ---------------- Ignored DLLs (Settings tab) ----------------

    private void RefreshIgnoredDllList()
    {
        _ignoredDllItems.Clear();
        foreach (string name in _dirSettings.Current.IgnoredDetectedDlls.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
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
            _dirSettings.Current.IgnoredDetectedDlls.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }

        _dirSettings.Save(CurrentWowDir());
        RefreshIgnoredDllList();
        RefreshDetectedDlls();
    }

    private void OnUnignoreAllDlls(object sender, RoutedEventArgs e)
    {
        if (_dirSettings.Current.IgnoredDetectedDlls.Count == 0)
        {
            return;
        }

        _dirSettings.Current.IgnoredDetectedDlls.Clear();
        _dirSettings.Save(CurrentWowDir());
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
                Background = (Brush)FindResource("RemoveActionBrush"),
                ToolTip = "Remove this patch",
            };
            remove.Click += (_, _) =>
            {
                RunMpqOperation(() => _mpq.Remove(wowDir, patch), $"remove patch {patch.Letter}", $"Removed patch {patch.Letter}.");
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
                RunMpqOperation(() => _mpq.SetEnabled(wowDir, patch, check.IsChecked == true), $"toggle patch {patch.Letter}",
                    $"Patch {patch.Letter} {(check.IsChecked == true ? "enabled" : "disabled")}.");
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

        RunMpqOperation(() => _mpq.Add(CurrentWowDir(), dialog.FileName), "add the MPQ patch", "Added the MPQ patch.");
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
        int count = 0;
        foreach (MpqPatch patch in _mpq.Scan(wowDir))
        {
            // No per-patch successMessage here deliberately - one toast per patch in this loop would
            // flood the 5-slot tab-toast stack; a single summary toast after the loop is enough.
            if (RunMpqOperation(() => _mpq.SetEnabled(wowDir, patch, enabled), $"toggle patch {patch.Letter}"))
            {
                count++;
            }
        }

        RefreshMpqList();
        if (count > 0)
        {
            ShowTabToast($"{(enabled ? "Enabled" : "Disabled")} {count} patch(es).", ToastSeverity.Success);
        }
    }

    /// <summary>
    /// Runs an MPQ file operation (rename/copy/delete under Data\), reporting failure instead of
    /// letting it throw unhandled from a button click — the Data folder can be locked by a running
    /// game or antivirus scan at the exact moment the user clicks. Returns whether it succeeded, and
    /// optionally shows a success toast (omit for callers looping over many patches at once - see
    /// SetAllMpqEnabled, which shows one summary toast instead of one per iteration).
    /// </summary>
    private bool RunMpqOperation(Action action, string failureContext, string? successMessage = null)
    {
        try
        {
            action();
            if (successMessage is not null)
            {
                ShowTabToast(successMessage, ToastSeverity.Success);
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to {failureContext}.", ex);
            ShowTabToast($"Failed to {failureContext} — see the Log tab.", ToastSeverity.Error);
            return false;
        }
    }

    // ---------------- Addons tab ----------------

    private void InitAddonsTab()
    {
        _addons.Load(CurrentWowDir());
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

    // AddAddonAsync is shared by the Installed tab's "Add" dialog and the Browse tab's Install
    // button. Both sub-tabs live under the same top-level Addons tab, so ShowTabToast's per-tab
    // (not per-sub-tab) scoping already routes this correctly regardless of which one triggered it -
    // no separate AddonStatusText/MarketplaceStatusText split needed anymore (issue #13 removes it).
    private async Task AddAddonAsync(string input)
    {
        if (_addonBusy)
        {
            return;
        }

        _addonBusy = true;
        ShowTabToast("Installing…", ToastSeverity.Info, updateKey: "addon-install");
        try
        {
            InstalledAddon addon = await _addons.AddAsync(input, CurrentWowDir());
            RefreshAddonList();
            ShowTabToast($"Installed {WowColorTextParser.StripCodes(addon.Name)}.", ToastSeverity.Success, updateKey: "addon-install");
        }
        catch (Exception ex)
        {
            _log.Error($"Addon install failed for {input}", ex);
            ShowTabToast("Install failed — see the Log tab.", ToastSeverity.Error, updateKey: "addon-install");
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
            ShowTabToast($"Removed {WowColorTextParser.StripCodes(addon.Name)}.", ToastSeverity.Success);
        }
    }

    private async void OnAddonDetailsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InstalledAddon addon })
        {
            return;
        }

        string title = WowColorTextParser.StripCodes(addon.Name);
        ShowTabToast($"Loading details for '{title}'...", ToastSeverity.Info, updateKey: "addon-details");
        try
        {
            string markdown = await _addons.GetDetailsMarkdownAsync(addon, CurrentWowDir(), CancellationToken.None);
            string? repoUrl = addon.SourceKind is AddonSourceKind.GitHub or AddonSourceKind.GitLab ? addon.SourceRef : null;
            string repoLinkLabel = addon.SourceKind == AddonSourceKind.GitLab ? "View on GitLab" : "View on GitHub";
            var dialog = new AddonDetailsDialog(title, markdown, addon.IgnoreUpdates, repoUrl, repoLinkLabel: repoLinkLabel);
            ShowModal(dialog);

            if (dialog.IgnoreUpdates != addon.IgnoreUpdates)
            {
                addon.IgnoreUpdates = dialog.IgnoreUpdates;
                if (addon.IgnoreUpdates)
                {
                    addon.HasUpdateAvailable = false;
                }

                _addons.Save(CurrentWowDir());
                RefreshAddonList();
            }
        }
        catch (Exception ex)
        {
            // Defense in depth: GetDetailsMarkdownAsync's own sub-paths already catch their own
            // errors, but this is an async void handler — anything that slips through uncaught here
            // would crash the whole app, not just fail this one dialog.
            _log.Error($"Could not load details for '{title}'.", ex);
            ShowTabToast("Could not load addon details — see the Log tab.", ToastSeverity.Error);
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
            ShowTabToast("No addon updates available.", ToastSeverity.Info);
            return;
        }

        foreach (InstalledAddon addon in updatable)
        {
            await AddAddonAsync(addon.SourceRef!);
        }

        ShowTabToast($"Updated {updatable.Count} addon(s).", ToastSeverity.Success);
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

            _addons.Load(wowDir);
            _addons.RefreshMetadataFromDisk(wowDir);

            AddonLibrary.LocalAddonSyncResult sync = _addons.SyncLocalAddons(wowDir);
            if (sync.Conflicts.Count > 0)
            {
                var dialog = new LocalAddonsDialog(sync.Conflicts);
                if (ShowModal(dialog) == true)
                {
                    foreach (LocalAddonCandidate candidate in dialog.Selected)
                    {
                        _addons.Adopt(candidate, wowDir);
                    }
                }
            }

            ShowTabToast("Checking for addon updates…", ToastSeverity.Info, updateKey: "addon-refresh");
            await _addons.CheckForUpdatesAsync(wowDir);

            RefreshAddonList();
            ShowTabToast(
                sync.AutoAdopted > 0
                    ? $"Refreshed. {sync.AutoAdopted} local addon(s) added automatically."
                    : "Refreshed.",
                ToastSeverity.Success,
                updateKey: "addon-refresh");
        }
        catch (Exception ex)
        {
            // This also runs automatically on startup (see the constructor) - an uncaught exception
            // here would propagate out of an async void handler and crash the whole app, not just
            // fail this one refresh.
            _log.Error("Addon refresh failed.", ex);
            ShowTabToast("Addon refresh failed — see the Log tab.", ToastSeverity.Error, updateKey: "addon-refresh");
        }
        finally
        {
            _addonBusy = false;
        }
    }

    // ---------------- Addons tab: Browse marketplace (issue #8) ----------------

    private readonly ObservableCollection<MarketplaceRowViewModel> _marketplaceRows = new();
    private readonly Dictionary<string, MarketplaceRowViewModel> _marketplaceRowCache = new(StringComparer.OrdinalIgnoreCase);
    private List<MarketplaceAddonEntry> _marketplaceEntries = new();
    private bool _marketplaceInitialized;
    private bool _marketplaceBusy;
    private string _warperiaSort = "popularity";

    /// <summary>Backing object for one Browse result row — mirrors Teron_Addon_Manager's MarketplaceRow (logic only, not its UI): flips its own Install/Uninstall glyph live via INotifyPropertyChanged instead of requiring a full list rebuild.</summary>
    private sealed class MarketplaceRowViewModel : INotifyPropertyChanged
    {
        public required MarketplaceAddonEntry Entry { get; init; }
        public string AuthorDisplay => Entry.Author is { Length: > 0 } a ? $"by {a}" : string.Empty;
        public bool HasDescription => !string.IsNullOrWhiteSpace(Entry.Description);

        // Guards against re-fetching every time a virtualized container recycles this row back into
        // view (Loaded fires again on each realize) - only the first realize should ever kick off
        // the actual download.
        public bool ThumbnailLoadStarted { get; set; }

        private ImageSource? _thumbnailImage;
        public ImageSource? ThumbnailImage
        {
            get => _thumbnailImage;
            set { _thumbnailImage = value; OnPropertyChanged(nameof(ThumbnailImage)); }
        }

        private bool _isInstalled;
        public bool IsInstalled
        {
            get => _isInstalled;
            set
            {
                if (_isInstalled == value) return;
                _isInstalled = value;
                OnPropertyChanged(nameof(IsInstalled));
                OnPropertyChanged(nameof(InstallGlyph));
                OnPropertyChanged(nameof(InstallToolTip));
                OnPropertyChanged(nameof(InstallBackground));
            }
        }

        // Reuses the same glyphs as the Installed list's own Update ("download") and Remove (trash)
        // buttons - Install and Uninstall are the same action classes, just for a not-yet-tracked addon.
        public string InstallGlyph => IsInstalled ? "" : "";
        public string InstallToolTip => IsInstalled ? "Remove this addon" : "Install this addon";
        // Colors come from App.xaml's shared action-color brushes (issue #14) rather than a locally
        // hardcoded hex value, so this can't drift out of sync with every other Install/Remove button.
        public Brush InstallBackground => (Brush)Application.Current.Resources[IsInstalled ? "RemoveActionBrush" : "InstallActionBrush"];

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void OnAddonsSubTabChanged(object sender, RoutedEventArgs e)
    {
        // AddonsInstalledToggle's XAML-declared IsChecked="True" fires this Checked handler during
        // InitializeComponent() itself, before AddonsBrowseToggle (declared right after it) has been
        // assigned to its field yet - guard against that premature, still-parsing-time call.
        if (AddonsBrowseToggle is null || AddonsBrowseToggle.IsChecked != true || _marketplaceInitialized)
        {
            return;
        }

        _marketplaceInitialized = true;
        MarketplaceList.ItemsSource = _marketplaceRows;

        MarketplaceProviderCombo.Items.Add("Legacy-WoW");
        MarketplaceProviderCombo.Items.Add("Warperia");
        MarketplaceProviderCombo.SelectedIndex = 0; // triggers OnMarketplaceProviderChanged, which does the first load

        UpdateMarketplaceSearchPlaceholder();
    }

    private void OnMarketplaceProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_marketplaceInitialized)
        {
            return;
        }

        bool isLegacyWow = MarketplaceProviderCombo.SelectedIndex == 0;

        MarketplaceCategoryCombo.Items.Clear();
        if (isLegacyWow)
        {
            foreach (LegacyWowCategory cat in MarketplaceService.LegacyWowCategories)
            {
                MarketplaceCategoryCombo.Items.Add(new ComboBoxItem { Content = cat.Name, Tag = cat.Id });
            }
            MarketplaceCategoryCombo.IsEnabled = true;
        }
        else
        {
            // Warperia does have its own category system (checkbox-based, e.g. data-category="class"),
            // but its actual filtering mechanism isn't confirmed working — no per-entry category data
            // on the result cards, and the filter looked AJAX-driven rather than a simple URL param.
            // Left unimplemented rather than guessed at; search still works for narrowing results.
            MarketplaceCategoryCombo.Items.Add(new ComboBoxItem { Content = "All (not supported yet)", Tag = null });
            MarketplaceCategoryCombo.IsEnabled = false;
        }
        MarketplaceCategoryCombo.SelectedIndex = 0;

        MarketplaceSortCombo.Items.Clear();
        MarketplaceSortCombo.Items.Add(new ComboBoxItem { Content = "Name (A-Z)", Tag = "name_asc" });
        MarketplaceSortCombo.Items.Add(new ComboBoxItem { Content = "Name (Z-A)", Tag = "name_desc" });
        if (isLegacyWow)
        {
            MarketplaceSortCombo.Items.Add(new ComboBoxItem { Content = "Most Popular", Tag = "downloads_client" });
        }
        else
        {
            foreach ((string value, string label) in MarketplaceService.WarperiaSortOptions)
            {
                MarketplaceSortCombo.Items.Add(new ComboBoxItem { Content = label, Tag = $"server:{value}" });
            }
        }
        MarketplaceSortCombo.SelectedIndex = 0;

        _ = LoadMarketplaceCatalogAsync(forceRefresh: false);
    }

    private void OnMarketplaceCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_marketplaceInitialized || MarketplaceProviderCombo.SelectedIndex != 0)
        {
            return; // category re-fetch only applies to Legacy-WoW for now
        }

        _ = LoadMarketplaceCatalogAsync(forceRefresh: false);
    }

    private void OnMarketplaceRefreshClick(object sender, RoutedEventArgs e)
    {
        _ = LoadMarketplaceCatalogAsync(forceRefresh: true);
    }

    // Lets the wheel scroll MarketplaceList from anywhere over the Browse tab's body - including the
    // empty background below a short/loading list - not just while hovering the ListBox itself,
    // matching AddonScrollViewer's coverage on the Installed sub-tab. The wrapping Grid this is
    // attached to Stretches to fill the whole Row1/Column0 cell (see MainWindow.xaml), so it receives
    // wheel input over that entire area; this reaches into MarketplaceList's own internal
    // ScrollViewer (rather than an outer wrapper one, since that inner ScrollViewer is what's
    // actually doing the real virtualized scrolling here) the same way OnAddonListPreviewMouseWheel
    // reaches AddonScrollViewer.
    private void OnMarketplacePreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindVisualChild<ScrollViewer>(MarketplaceList) is { } scrollViewer)
        {
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    // MarketplaceList keeps its own internal ScrollViewer (needed for VirtualizingStackPanel to
    // actually virtualize the ~700-entry Legacy-WoW catalog) but hides that ScrollViewer's own
    // scrollbar chrome (see MainWindow.xaml) since it visually broke PageCardBottomStyle's rounded
    // corner. MarketplaceScrollBar is a separate, properly-aligned standalone bar in its own Grid
    // column; this event (raised by the ListBox's internal ScrollViewer, which ScrollChanged bubbles
    // up from) is what keeps that standalone bar's Value/Maximum/ViewportSize in sync with it.
    private void OnMarketplaceListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        MarketplaceScrollBar.Maximum = e.ExtentHeight - e.ViewportHeight;
        MarketplaceScrollBar.ViewportSize = e.ViewportHeight;
        MarketplaceScrollBar.LargeChange = e.ViewportHeight;
        MarketplaceScrollBar.Value = e.VerticalOffset;
    }

    // The reverse direction of the above: dragging/clicking MarketplaceScrollBar only moves its own
    // Value, so this reaches into MarketplaceList's internal ScrollViewer (via VisualTreeHelper -
    // that inner ScrollViewer isn't a named, directly bindable element the way AddonScrollViewer is,
    // since it lives inside ListBox's own default template) and applies the new offset there.
    private void OnMarketplaceScrollBarScroll(object sender, ScrollEventArgs e)
    {
        if (FindVisualChild<ScrollViewer>(MarketplaceList) is { } scrollViewer)
        {
            scrollViewer.ScrollToVerticalOffset(e.NewValue);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    // Warperia's own pagination doesn't expose a reliable "last page" number up front (its
    // page-numbers control only ever renders a handful of nearby pages), so rather than guess a
    // total, this just keeps requesting the next page until one comes back with fewer than a full
    // page's worth of entries (~28 normally, verified against real fetched pages - 20 leaves margin
    // below that without risking mistaking a real full page for the last one). MaxPages is a pure
    // safety net against an unexpected server response looping forever, not an expected ceiling -
    // ~40 pages is already far more than Warperia's real catalog needs.
    private const int WarperiaFullPageThreshold = 20;
    private const int WarperiaMaxPages = 40;

    private async Task LoadMarketplaceCatalogAsync(bool forceRefresh)
    {
        if (_marketplaceBusy)
        {
            return;
        }

        _marketplaceBusy = true;
        ShowTabToast("Loading catalog…", ToastSeverity.Info, updateKey: "marketplace-load");
        try
        {
            bool isLegacyWow = MarketplaceProviderCombo.SelectedIndex == 0;
            List<MarketplaceAddonEntry> fetched;
            if (isLegacyWow)
            {
                int categoryId = MarketplaceCategoryCombo.SelectedItem is ComboBoxItem { Tag: int id } ? id : 137;
                fetched = (await _marketplace.FetchLegacyWowCatalogAsync(categoryId, CancellationToken.None, forceRefresh)).ToList();
            }
            else
            {
                fetched = new List<MarketplaceAddonEntry>();
                for (int page = 1; page <= WarperiaMaxPages; page++)
                {
                    IReadOnlyList<MarketplaceAddonEntry> pageEntries =
                        await _marketplace.FetchWarperiaPageAsync(page, _warperiaSort, CancellationToken.None, forceRefresh);
                    fetched.AddRange(pageEntries);
                    ShowTabToast($"Loading catalog… ({fetched.Count} so far)", ToastSeverity.Info, updateKey: "marketplace-load");
                    if (pageEntries.Count < WarperiaFullPageThreshold)
                    {
                        break;
                    }
                }
            }

            _marketplaceEntries = fetched;
            _marketplaceRowCache.Clear();

            ApplyMarketplaceFilters();
            ShowTabToast($"Showing {_marketplaceRows.Count} of {_marketplaceEntries.Count} addons.", ToastSeverity.Success, updateKey: "marketplace-load");
        }
        catch (Exception ex)
        {
            _log.Error("Marketplace catalog fetch failed", ex);
            ShowTabToast("Could not load the catalog — see the Log tab.", ToastSeverity.Error, updateKey: "marketplace-load");
        }
        finally
        {
            _marketplaceBusy = false;
        }
    }

    private void OnMarketplaceFilterChanged(object sender, RoutedEventArgs e)
    {
        UpdateMarketplaceSearchPlaceholder();
        ApplyMarketplaceFilters();
    }

    private void OnMarketplaceSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_marketplaceInitialized)
        {
            return;
        }

        if (MarketplaceSortCombo.SelectedItem is ComboBoxItem { Tag: string tag } && tag.StartsWith("server:", StringComparison.Ordinal))
        {
            _warperiaSort = tag["server:".Length..];
            _ = LoadMarketplaceCatalogAsync(forceRefresh: false);
            return;
        }

        ApplyMarketplaceFilters();
    }

    private void ApplyMarketplaceFilters()
    {
        IEnumerable<MarketplaceAddonEntry> query = _marketplaceEntries;

        string search = MarketplaceSearchBox.Text.Trim();
        if (search.Length > 0)
        {
            query = query.Where(entry =>
                entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (entry.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (entry.Author?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        string sortTag = MarketplaceSortCombo.SelectedItem is ComboBoxItem { Tag: string t } ? t : "name_asc";
        query = sortTag switch
        {
            "name_desc" => query.OrderByDescending(en => en.Name, StringComparer.OrdinalIgnoreCase),
            "downloads_client" => query.OrderByDescending(en => en.Downloads ?? 0),
            _ => query.OrderBy(en => en.Name, StringComparer.OrdinalIgnoreCase),
        };

        _marketplaceRows.Clear();
        foreach (MarketplaceAddonEntry entry in query)
        {
            if (!_marketplaceRowCache.TryGetValue(entry.DetailUrl, out MarketplaceRowViewModel? row))
            {
                row = new MarketplaceRowViewModel { Entry = entry };
                row.IsInstalled = _addons.Addons.Any(a => string.Equals(a.SourceRef, entry.DetailUrl, StringComparison.OrdinalIgnoreCase));
                _marketplaceRowCache[entry.DetailUrl] = row;
                // Thumbnail loading is deferred to OnMarketplaceRowLoaded (fired when the row's
                // container is actually realized by the virtualizing panel) rather than started
                // here for every filtered entry - with ~700 Legacy-WoW entries (or Warperia's now
                // fully-paged-in catalog), firing a thumbnail fetch for all of them the instant the
                // catalog loads meant hundreds of concurrent requests and UI-thread image-decode
                // callbacks arriving in a burst, which is exactly what caused the reported stutter.
            }

            _marketplaceRows.Add(row);
        }

        // Rebuilding the row collection doesn't reset MarketplaceList's own scroll position - its
        // internal ScrollViewer just clamps whatever offset it already had to the new, possibly
        // much shorter extent. Without this, switching from a long catalog (Legacy-WoW, ~700
        // entries) to a shorter one (Warperia) while scrolled down would land partway through - or
        // at the very bottom of - the new list instead of at its top.
        if (FindVisualChild<ScrollViewer>(MarketplaceList) is { } scrollViewer)
        {
            scrollViewer.ScrollToHome();
        }
    }

    private void OnMarketplaceRowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MarketplaceRowViewModel row } || row.ThumbnailLoadStarted)
        {
            return;
        }

        row.ThumbnailLoadStarted = true;
        _ = LoadMarketplaceThumbnailAsync(row);
    }

    private async Task LoadMarketplaceThumbnailAsync(MarketplaceRowViewModel row)
    {
        string? url = row.Entry.ThumbnailUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            if (string.Equals(row.Entry.Source, "Warperia", StringComparison.OrdinalIgnoreCase))
            {
                // Token-gated - has to go through the same cookie-carrying HttpClient that fetched
                // the listing page, so it can't just be a plain BitmapImage.UriSource load.
                byte[]? bytes = await _marketplace.FetchThumbnailBytesAsync(url, CancellationToken.None);
                if (bytes is null)
                {
                    return;
                }

                var bitmap = new BitmapImage();
                using (var stream = new MemoryStream(bytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }
                bitmap.Freeze();
                row.ThumbnailImage = bitmap;
            }
            else
            {
                // Legacy-WoW thumbnails are plain, unauthenticated URLs - WPF's own imaging pipeline
                // does the async fetch itself, no custom fetch code needed. Deliberately NOT frozen:
                // with CacheOption.OnLoad the download itself still happens asynchronously in the
                // background even after EndInit() returns, and Freeze() throws ("This Freezable
                // cannot be frozen") if called before that download actually finishes. Skipping
                // Freeze() entirely is fine here - it only matters for cross-thread sharing/perf,
                // and this ImageSource is only ever touched from the UI thread.
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(url, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                row.ThumbnailImage = bitmap;
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Marketplace thumbnail failed for {row.Entry.Name}: {ex.Message}");
        }
    }

    private async void OnMarketplaceInstallClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MarketplaceRowViewModel row })
        {
            return;
        }

        if (row.IsInstalled)
        {
            InstalledAddon? installed = _addons.Addons.FirstOrDefault(
                a => string.Equals(a.SourceRef, row.Entry.DetailUrl, StringComparison.OrdinalIgnoreCase));
            if (installed is null)
            {
                return;
            }

            _addons.Remove(installed, CurrentWowDir());
            RefreshAddonList();
            row.IsInstalled = false;
            ShowTabToast($"Removed {WowColorTextParser.StripCodes(installed.Name)}.", ToastSeverity.Success);
            return;
        }

        await AddAddonAsync(row.Entry.DetailUrl);
        row.IsInstalled = _addons.Addons.Any(a => string.Equals(a.SourceRef, row.Entry.DetailUrl, StringComparison.OrdinalIgnoreCase));
    }

    private async void OnMarketplaceDetailsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MarketplaceRowViewModel row })
        {
            return;
        }

        ShowTabToast($"Loading details for '{row.Entry.Name}'...", ToastSeverity.Info, updateKey: "marketplace-details");
        try
        {
            string markdown = await _marketplace.FetchAddonDetailsMarkdownAsync(row.Entry, CancellationToken.None);
            var dialog = new AddonDetailsDialog(row.Entry.Name, markdown, ignoreUpdates: false, showIgnoreUpdates: false);
            ShowModal(dialog);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not load marketplace details for '{row.Entry.Name}'.", ex);
            ShowTabToast("Could not load addon details — see the Log tab.", ToastSeverity.Error, updateKey: "marketplace-details");
        }
    }

    private void OnMarketplaceSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(MarketplaceSearchBox), null);
            UpdateMarketplaceSearchPlaceholder();
            e.Handled = true;
        }
    }

    private void OnMarketplaceSearchFocusChanged(object sender, RoutedEventArgs e) => UpdateMarketplaceSearchPlaceholder();

    private void UpdateMarketplaceSearchPlaceholder()
        => MarketplaceSearchPlaceholder.Visibility = MarketplaceSearchBox.Text.Length == 0 && !MarketplaceSearchBox.IsFocused
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void OnClearMarketplaceSearch(object sender, RoutedEventArgs e)
    {
        MarketplaceSearchBox.Text = string.Empty;
        MarketplaceSearchBox.Focus();
    }

    // ---------------- Home tab (Login & Game) ----------------

    /// <summary>Loads both the global and directory-scoped halves — used at startup. A directory
    /// switch later on only needs <see cref="LoadDirectorySettingsIntoUi"/>, not this.</summary>
    private void LoadSettingsIntoUi()
    {
        LoadGlobalSettingsIntoUi();
        LoadDirectorySettingsIntoUi();
    }

    private void LoadGlobalSettingsIntoUi()
    {
        LauncherSettings s = _settings.Current;
        GameFolderBox.Text = s.WowDirectory ?? string.Empty;
        MinimizeOnLaunchCheck.IsChecked = s.MinimizeOnLaunch;
        RefreshRealmlistHistoryItems();
        RefreshManagedDirectoryItems();
        ClientUrlBox.Text = string.IsNullOrWhiteSpace(s.ClientDownloadUrl)
            ? GameInstallService.DefaultClientUrl
            : s.ClientDownloadUrl;

        UpdateGameFolderHint();
    }

    /// <summary>
    /// The most-recently-used OTHER managed directory that still exists on disk, or null if the
    /// configured directory is unset/still there/has no surviving alternative - called once at
    /// startup, before ManagedDirectories has had a chance to be pruned by RefreshManagedDirectoryItems,
    /// so it re-checks Directory.Exists itself rather than assuming the list is already clean.
    /// </summary>
    private string? TryResolveFallbackDirectory()
    {
        string? configured = _settings.Current.WowDirectory;
        if (string.IsNullOrWhiteSpace(configured) || Directory.Exists(configured))
        {
            return null;
        }

        return _settings.Current.ManagedDirectories.FirstOrDefault(
            d => !string.Equals(d, configured, StringComparison.OrdinalIgnoreCase) && Directory.Exists(d));
    }

    /// <summary>
    /// Adds (or moves to the front of) the managed-directory quick-switch list — called whenever the
    /// launcher confirms a real directory is in use (startup, a successful install/adopt, or the user
    /// browsing/typing a different existing folder), so the dropdown builds itself up without needing
    /// its own separate "add to list" step anywhere.
    /// </summary>
    private void RecordManagedDirectory(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return;
        }

        List<string> dirs = _settings.Current.ManagedDirectories;
        dirs.RemoveAll(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase));
        dirs.Insert(0, dir);
        _settings.Save();
        RefreshManagedDirectoryItems();
    }

    /// <summary>Prunes any managed directory that no longer exists on disk (moved/deleted folder),
    /// then repopulates the dropdown and highlights whichever entry matches the directory currently
    /// in use, if any.</summary>
    private void RefreshManagedDirectoryItems()
    {
        List<string> dirs = _settings.Current.ManagedDirectories;
        int removed = dirs.RemoveAll(d => !Directory.Exists(d));
        if (removed > 0)
        {
            _settings.Save();
        }

        _managedDirectoryItems.Clear();
        foreach (string d in dirs)
        {
            _managedDirectoryItems.Add(d);
        }

        string current = CurrentWowDir();
        DirectorySwitchBox.SelectedItem = _managedDirectoryItems.FirstOrDefault(
            d => string.Equals(d, current, StringComparison.OrdinalIgnoreCase));
    }

    private void OnManagedDirectorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (DirectorySwitchBox.SelectedItem is string selected && !_loading &&
            !string.Equals(selected, GameFolderBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            // Setting .Text fires TextChanged, which schedules the same auto-save/refresh pipeline
            // (ForceStopClientDownloadForDirectoryChange, directory-settings reload, tab rescans) any
            // other way of switching directories already goes through.
            GameFolderBox.Text = selected;
        }
    }

    /// <summary>Populates every UI field scoped to the currently-selected WoW directory (account,
    /// password, realmlist, tweak checkboxes/sliders) from <see cref="_dirSettings"/>.Current — called
    /// at startup and again whenever the selected directory changes, so switching directories actually
    /// repopulates these instead of leaving the previous directory's values showing.</summary>
    private void LoadDirectorySettingsIntoUi()
    {
        DirectorySettings s = _dirSettings.Current;
        AutoLoginCheck.IsChecked = s.AutoLoginEnabled;
        AccountBox.Text = s.Account;
        SavePasswordCheck.IsChecked = s.SavePassword;
        PasswordBoxInput.Password = s.SavePassword ? _dirSettings.GetPassword() : string.Empty;
        DelayBox.Text = s.LoginDelayMs.ToString(CultureInfo.InvariantCulture);
        CleanWdbCheck.IsChecked = s.CleanWdbBeforeLaunch;
        RealmlistBox.Text = s.Realmlist;

        BuildPatchList();

        UpdateRealmlistLabel();
    }

    private void CollectSettingsFromUi()
    {
        LauncherSettings s = _settings.Current;
        s.WowDirectory = string.IsNullOrWhiteSpace(GameFolderBox.Text) ? null : GameFolderBox.Text.Trim();
        s.MinimizeOnLaunch = MinimizeOnLaunchCheck.IsChecked == true;

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

        DirectorySettings ds = _dirSettings.Current;
        ds.AutoLoginEnabled = AutoLoginCheck.IsChecked == true;
        ds.Account = AccountBox.Text.Trim();
        ds.SavePassword = SavePasswordCheck.IsChecked == true;
        ds.LoginDelayMs = int.TryParse(DelayBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d)
            ? Math.Max(0, d)
            : ds.LoginDelayMs;
        ds.CleanWdbBeforeLaunch = CleanWdbCheck.IsChecked == true;
        ds.Realmlist = RealmlistBox.Text.Trim();

        _dirSettings.SetPassword(PasswordBoxInput.Password);

        ds.EnabledPatchIds = new List<string>();
        foreach (PatchControl pc in _patchControls)
        {
            if (pc.Box.IsChecked == true)
            {
                ds.EnabledPatchIds.Add(pc.Def.Id);
            }

            if (pc.Slider is not null && pc.Def.Parameter is PatchParameter p)
            {
                ds.PatchParameters[pc.Def.Id] = p.IsInteger ? Math.Round(pc.Slider.Value) : pc.Slider.Value;
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
        string clientUrl = CurrentClientUrl();

        bool wowDirChanged = !string.Equals(wowDir, _lastAppliedWowDir, StringComparison.OrdinalIgnoreCase);
        bool clientUrlChanged = !string.Equals(clientUrl, _lastAppliedClientUrl, StringComparison.OrdinalIgnoreCase);

        // A queued/in-flight client download is tied to whichever directory it was started against -
        // letting it keep running after the user points the UI at a different directory silently
        // continued downloading into a folder that's no longer even selected.
        if (wowDirChanged)
        {
            ForceStopClientDownloadForDirectoryChange();
        }

        if (wowDirChanged)
        {
            // The directory-scoped fields just collected above (account/password/realmlist/tweaks)
            // still belong to whichever directory was selected before this tick - the UI hasn't been
            // reloaded for the new one yet, so there's nothing real to persist or apply against the
            // new directory from this collection pass. Reload everything scoped to it fresh instead
            // (this is also what actually fixes switching directories showing stale settings).
            _dirSettings.Load(wowDir);
            LoadDirectorySettingsIntoUi();
            RecordManagedDirectory(wowDir);

            RefreshDllList();
            RefreshDetectedDlls();
            RefreshIgnoredDllList();
            RefreshMpqList();
            // Addon tracking (.teronwow-addons.json) lives inside each WoW directory - Load(wowDir)
            // has to run before RefreshAddonList can show anything real for the newly-selected one.
            _addons.Load(wowDir);
            RefreshAddonList();
        }
        else
        {
            _dirSettings.Save(wowDir);

            string realmlist = _dirSettings.Current.Realmlist;
            bool realmlistChanged = !string.Equals(realmlist, _lastAppliedRealmlist, StringComparison.Ordinal);
            if (realmlistChanged && !string.IsNullOrWhiteSpace(realmlist) && Directory.Exists(wowDir))
            {
                try
                {
                    if (!_realmlist.Write(wowDir, realmlist))
                    {
                        ShowGlobalToast("Warning: realmlist.wtf may not have saved correctly — see the Log tab.", ToastSeverity.Warning);
                    }
                }
                catch (Exception ex)
                {
                    _log.Error("Failed to write realmlist.wtf.", ex);
                    ShowGlobalToast("Failed to write realmlist.wtf — see the Log tab.", ToastSeverity.Error);
                }
            }
        }

        UpdateGameFolderHint();
        UpdateRealmlistLabel();

        if (wowDirChanged || clientUrlChanged)
        {
            await RefreshPlayButtonStateAsync();
        }

        _lastAppliedWowDir = wowDir;
        _lastAppliedRealmlist = _dirSettings.Current.Realmlist;
        _lastAppliedClientUrl = clientUrl;

        ShowGlobalToast("Settings saved.", ToastSeverity.Success);
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
        // No "Realmlist:" prefix here - the "Realm" header above this row already provides that
        // context, so the label itself only needs the value (or a placeholder for none set).
        RealmlistLabel.Text = string.IsNullOrEmpty(realm) ? "(not set)" : realm;
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
        bool installed = File.Exists(exe);
        GameFolderHint.Text = installed ? $"✓ WoW.exe found in {dir}" : $"⚠ WoW.exe not found in {dir}";

        // Repair/Delete both only make sense once there's an actual client to act on - previously
        // always enabled, so clicking either with nothing installed just produced a toast saying so
        // instead of the buttons themselves reflecting that up front.
        RepairGameFilesButton.IsEnabled = installed;
        DeleteGameFilesButton.IsEnabled = installed;
    }

    private async void OnRepairGameFiles(object sender, RoutedEventArgs e)
    {
        string wowDir = CurrentWowDir();
        if (!_install.IsInstalled(wowDir))
        {
            ShowGlobalToast("Nothing installed to repair yet.", ToastSeverity.Info);
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

        await RunClientDownloadAsync(wowDir, "Game files repaired.", "Repair failed", InstallColor);
    }

    /// <summary>
    /// Removes exactly what the last install/repair's client archive provided (see
    /// GameInstallService.DeleteInstalledClientFiles) - addons, WTF settings, logs, and realmlist.wtf
    /// are untouched, since none of those come from the client archive itself.
    /// </summary>
    private async void OnDeleteGameFiles(object sender, RoutedEventArgs e)
    {
        string wowDir = CurrentWowDir();
        if (!_install.IsInstalled(wowDir))
        {
            ShowGlobalToast("Nothing installed to delete.", ToastSeverity.Info);
            return;
        }

        if (!_install.HasInstallManifest(wowDir))
        {
            var noManifest = new ConfirmDialog(
                "Can't Delete Safely Yet",
                "No install record exists for this folder yet (it may have been installed by an older " +
                "launcher version, or copied in manually), so there's no way to know exactly which files " +
                "came from the client archive versus anything else in this folder. Run Repair now? It's " +
                "safe, and records what it writes so Delete can target exactly that afterward.",
                confirmText: "Run Repair",
                cancelText: "Cancel");
            if (ShowModal(noManifest) == true)
            {
                await RunClientDownloadAsync(wowDir, "Game files repaired.", "Repair failed", InstallColor);
            }

            return;
        }

        var confirm = new ConfirmDialog(
            "Delete Game Files",
            "This permanently deletes the client files the last install/repair placed in this folder " +
            "(Data\\, WoW.exe, Fonts\\, etc.). Your installed addons, WTF settings/saved variables, and " +
            "logs are not touched. This cannot be undone. Continue?",
            confirmText: "Delete",
            cancelText: "Cancel");
        if (ShowModal(confirm) != true)
        {
            return;
        }

        SetLaunchOperationBusy(true);
        try
        {
            await Task.Run(() => _install.DeleteInstalledClientFiles(wowDir));
            _dirSettings.Current.InstalledClientSignature = null;
            _dirSettings.Save(wowDir);
            ShowGlobalToast("Game files deleted.", ToastSeverity.Success);
            UpdateGameFolderHint();
            await RefreshPlayButtonStateAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Delete game files failed.", ex);
            ShowGlobalToast("Delete failed — see the Log tab.", ToastSeverity.Error);
        }
        finally
        {
            SetLaunchOperationBusy(false);
        }
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
        MainProgressBar.Visibility = Visibility.Visible;
        MainProgressBar.IsIndeterminate = true;
        MainProgressBar.Foreground = PlayColor;
        ProgressStatusText.Text = "Verifying backup integrity...";
        try
        {
            status = await Task.Run(() => _orchestrator.VerifyPristineBackupIntegrity(wowDir));
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not verify the pristine backup's integrity: {ex.Message}");
            return;
        }
        finally
        {
            MainProgressBar.Visibility = Visibility.Collapsed;
            MainProgressBar.IsIndeterminate = false;
            ProgressStatusText.Text = string.Empty;
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
        await RunClientDownloadAsync(wowDir, "Game files repaired.", "Repair failed", InstallColor);
    }

    // ---------------- Launcher self-update / "what's new" ----------------

    /// <summary>
    /// Runs the "what's new" check before the update check, sequentially rather than in parallel —
    /// showing both as separate modal dialogs at once on startup would stack awkwardly. Neither step
    /// can crash startup: each is wrapped in its own try/catch, matching this app's established
    /// "degrade to a warning, never throw from a startup-reachable path" convention.
    /// </summary>
    private async Task CheckForLauncherUpdateAsync()
    {
        await ShowWhatsNewIfNeededAsync();

        try
        {
            LauncherUpdateInfo? update = await _launcherUpdate.CheckForUpdateAsync(AppInfo.Version, CancellationToken.None);
            if (update is null)
            {
                return;
            }

            var confirm = new UpdateConfirmDialog(
                $"Current version: v{AppInfo.Version}\nLatest version: v{update.Version}",
                "A newer version of Teron WoW Launcher is available. Do you wish to update?");
            if (ShowModal(confirm) != true)
            {
                return;
            }

            // Reuses OnInstallProgress as the progress callback (same as the client download flow) -
            // it never sees Extracting=true here since this flow has no extraction phase, so its
            // "Extracting..." branch simply never triggers. Gold, since this is an update action on
            // the launcher itself, not the game client (issue #13). Only CancelDownloadButton is
            // shown here (_downloadSupportsPauseResume left false, so PauseResumeButton stays
            // hidden) - this download isn't resumable (a fresh temp folder every attempt, unlike
            // GameInstallService's fixed-path Range-resume support), and the installer is small
            // enough that a hard cancel is good enough.
            MainProgressBar.Visibility = Visibility.Visible;
            MainProgressBar.IsIndeterminate = false;
            MainProgressBar.Value = 0;
            MainProgressBar.Foreground = UpdateColor;
            _downloadSupportsPauseResume = false;
            CancelDownloadButton.Content = "";
            CancelDownloadButton.ToolTip = "Cancel";
            CancelDownloadButton.Visibility = Visibility.Visible;

            _downloadCts = new CancellationTokenSource();
            string installerPath;
            try
            {
                var progress = new Progress<InstallProgress>(OnInstallProgress);
                installerPath = await _launcherUpdate.DownloadInstallerAsync(update.DownloadUrl, update.Sha256, _downloadCts.Token, progress);
            }
            catch (OperationCanceledException)
            {
                ShowGlobalToast("Update download cancelled.", ToastSeverity.Warning);
                return;
            }
            finally
            {
                MainProgressBar.Visibility = Visibility.Collapsed;
                CancelDownloadButton.Visibility = Visibility.Collapsed;
                ProgressStatusText.Text = string.Empty;
                _downloadCts?.Dispose();
                _downloadCts = null;
            }

            // UseShellExecute triggers the installer's own admin-rights prompt; Close() (not
            // Environment.Exit/Process.Kill) goes through the normal OnWindowClosing/App.OnExit path
            // so the single-instance mutex releases and settings flush before the installer runs.
            Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            _log.Warn($"Launcher update check failed: {ex.Message}");
            ShowGlobalToast("Update check failed — see the Log tab.", ToastSeverity.Error);
        }
    }

    /// <summary>
    /// Shows what changed in this version once, the first time it runs after an update (detected by
    /// comparing against the last version this popup was shown for). Silent on first-ever run — with
    /// no prior version recorded there's nothing to compare against, so it just records the baseline.
    /// </summary>
    private async Task ShowWhatsNewIfNeededAsync()
    {
        string current = AppInfo.Version;
        string? lastSeen = _settings.Current.LastSeenVersion;

        if (lastSeen is not null && !string.Equals(lastSeen, current, StringComparison.Ordinal))
        {
            try
            {
                string changelog = await DocsHttp.GetStringAsync(ChangelogUrl);
                string? section = ExtractChangelogSection(changelog, current);
                if (section is not null)
                {
                    ShowModal(new MarkdownPreviewDialog($"What's new in v{current}", section));
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not load the \"what's new\" changelog section: {ex.Message}");
            }
        }

        _settings.Current.LastSeenVersion = current;
        _settings.Save();
    }

    /// <summary>Slices out one version's own section from the full CHANGELOG.md text — from its
    /// "## [X.Y.Z] - date" header (kept, since it carries the release date the dialog's own title
    /// doesn't) up to (not including) the next "## [" header.</summary>
    private static string? ExtractChangelogSection(string changelog, string version)
    {
        Match match = Regex.Match(
            changelog,
            $@"^## \[{Regex.Escape(version)}\].*?(?=^## \[|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline);
        return match.Success ? match.Value.Trim() : null;
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
            CurrentClientUrl(), _dirSettings.Current.InstalledClientSignature, CancellationToken.None);
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
        // No AppContext.BaseDirectory fallback here - the launcher's own location has nothing to do
        // with any WoW directory (it can be installed anywhere, including Program Files), so an
        // empty default is correct when nothing's configured yet; InstallDialog already requires the
        // user to pick/confirm a folder either way.
        string defaultDir = GameFolderBox.Text.Trim();
        var dialog = new InstallDialog(defaultDir);
        if (ShowModal(dialog) != true)
        {
            return;
        }

        string targetDir = dialog.SelectedFolder;

        // An existing, verified 1.12.1 client already sitting in the chosen folder gets adopted as-is
        // instead of blindly re-downloaded over - the user pointing the launcher at a folder that
        // already has a valid client should verify it, not clobber it.
        bool alreadyValid = _install.IsInstalled(targetDir) && _install.IsUpToDate(targetDir);
        bool ok;
        if (alreadyValid)
        {
            ok = true;
            ShowGlobalToast("Existing 1.12.1 client verified — using it as-is.", ToastSeverity.Success);
        }
        else
        {
            ok = await RunClientDownloadAsync(targetDir, "Client installed.", "Install failed", InstallColor);
        }

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
            // Load/reload directory-scoped state before anything below reads from it - RefreshDetectedDlls
            // and RefreshIgnoredDllList both depend on _dirSettings.Current.IgnoredDetectedDlls, which
            // otherwise would still reflect whichever directory was active before this install.
            _dirSettings.Load(targetDir);
            LoadDirectorySettingsIntoUi();
            RecordManagedDirectory(targetDir);
            RefreshDllList();
            RefreshDetectedDlls();
            RefreshIgnoredDllList();
            RefreshMpqList();
            _addons.Load(targetDir);
            RefreshAddonList();

            // Only needed for the adopt path - RunClientDownloadAsync (the download path, above)
            // already refreshes the Play button state itself on success, so calling it
            // unconditionally here re-did that same remote version check a second time in a row.
            if (alreadyValid)
            {
                await RefreshPlayButtonStateAsync();
            }
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

        await RunClientDownloadAsync(CurrentWowDir(), "Client updated.", "Update failed", UpdateColor);
    }

    // color follows which flow is actually running (issue #13) - previously always green regardless
    // of Install/Update/Repair, which read as though every one of those was the same action.
    //
    // Pause/Resume rather than a hard Cancel: GameInstallService.DownloadAndInstallAsync already
    // resumes a partial download via HTTP Range requests against a fixed temp filename, so
    // cancelling the in-flight request and later re-calling this same method with the same
    // arguments transparently continues from where it left off - no new resume logic needed, just
    // UI that doesn't tear itself down on a "pause".
    private async Task<bool> RunClientDownloadAsync(string targetDir, string successMessage, string failureMessage, Brush color)
    {
        SetLaunchOperationBusy(true);
        MainProgressBar.Visibility = Visibility.Visible;
        MainProgressBar.IsIndeterminate = false;
        MainProgressBar.Foreground = color;
        _downloadSupportsPauseResume = true;
        SetPauseResumeGlyph(paused: false);
        PauseResumeButton.Visibility = Visibility.Visible;
        CancelDownloadButton.Visibility = Visibility.Visible;

        _downloadCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<InstallProgress>(OnInstallProgress);
            string? signature = await _install.DownloadAndInstallAsync(CurrentClientUrl(), targetDir, progress, _downloadCts.Token);
            _dirSettings.Current.InstalledClientSignature = signature;
            _dirSettings.Save(targetDir);
            ShowGlobalToast(successMessage, ToastSeverity.Success);
            await RefreshPlayButtonStateAsync();
            return true;
        }
        catch (OperationCanceledException)
        {
            if (_downloadPaused)
            {
                // Deliberately skip the finally block's teardown (see below) - the bar, its text,
                // and PauseResumeButton (now showing Resume) all stay visible so the user can pick
                // this same download back up. The partial file is deliberately left alone here
                // (unlike a hard Cancel via OnCancelDownloadClick) so Resume can continue from it.
                ProgressStatusText.Text += " — paused";
                _pausedDownloadParams = (targetDir, successMessage, failureMessage, color);
                return false;
            }

            _install.DeletePartialDownload();
            ShowGlobalToast("Download cancelled.", ToastSeverity.Warning);
            return false;
        }
        catch (Exception ex)
        {
            _log.Error($"{failureMessage}.", ex);
            ShowGlobalToast($"{failureMessage} — see the Log tab.", ToastSeverity.Error);
            return false;
        }
        finally
        {
            _downloadCts.Dispose();
            _downloadCts = null;

            if (!_downloadPaused)
            {
                MainProgressBar.Visibility = Visibility.Collapsed;
                PauseResumeButton.Visibility = Visibility.Collapsed;
                CancelDownloadButton.Visibility = Visibility.Collapsed;
                ProgressStatusText.Text = string.Empty;
                SetLaunchOperationBusy(false);
            }
        }
    }

    private void SetPauseResumeGlyph(bool paused)
    {
        PauseResumeButton.Content = paused ? "" : ""; // Play (Resume) : Pause
        PauseResumeButton.ToolTip = paused ? "Resume" : "Pause";
    }

    // Only ever wired up while _downloadSupportsPauseResume is true (the client download flow) -
    // the self-update download hides this button entirely, so there's no "not resumable" branch.
    private void OnPauseResumeClick(object sender, RoutedEventArgs e)
    {
        if (_downloadPaused)
        {
            _downloadPaused = false;
            SetPauseResumeGlyph(paused: false);
            if (_pausedDownloadParams is { } p)
            {
                _pausedDownloadParams = null;
                _ = RunClientDownloadAsync(p.TargetDir, p.SuccessMessage, p.FailureMessage, p.Color);
            }
        }
        else
        {
            _downloadPaused = true;
            SetPauseResumeGlyph(paused: true);
            _downloadCts?.Cancel();
        }
    }

    /// <summary>
    /// Permanently stops the current download and deletes any partial file - unlike Pause, which
    /// deliberately leaves the partial file alone so Resume can continue it. Handles both cases: a
    /// download actively in flight (has a live _downloadCts - cancelling it routes through
    /// RunClientDownloadAsync's/the self-update flow's own catch block, which now sees
    /// _downloadPaused=false and does the delete+teardown there) and one already paused (no active
    /// CTS/Task to catch anything, so this does the teardown directly instead).
    /// </summary>
    private async void OnCancelDownloadClick(object sender, RoutedEventArgs e)
    {
        var confirm = new ConfirmDialog(
            "Cancel Download",
            "This stops the download and deletes the partial file — resuming later will have to start " +
            "over from the beginning instead of picking up where this left off. Continue?",
            confirmText: "Cancel Download",
            cancelText: "Keep Downloading",
            confirmBrush: (Brush)FindResource("RemoveActionBrush"),
            cancelBrush: (Brush)FindResource("AddActionBrush"));
        if (ShowModal(confirm) != true)
        {
            return;
        }

        CancelDownload();
    }

    /// <summary>
    /// Permanently stops the current download and deletes any partial file - unlike Pause, which
    /// deliberately leaves the partial file alone so Resume can continue it. Handles both cases: a
    /// download actively in flight (has a live _downloadCts - cancelling it routes through
    /// RunClientDownloadAsync's/the self-update flow's own catch block, which now sees
    /// _downloadPaused=false and does the delete+teardown there) and one already paused (no active
    /// CTS/Task to catch anything, so this does the teardown directly instead). Deliberately not
    /// where confirmation lives - the button click handler above asks first; this is also called
    /// internally when the WoW directory changes out from under an in-flight download, where a
    /// confirm prompt would be the wrong UX (nothing the user directly asked for).
    /// </summary>
    private void CancelDownload()
    {
        bool wasPaused = _downloadPaused;
        _downloadPaused = false;

        if (_downloadCts is not null)
        {
            _downloadCts.Cancel();
            return;
        }

        if (wasPaused && _pausedDownloadParams is not null)
        {
            _pausedDownloadParams = null;
            _install.DeletePartialDownload();
            MainProgressBar.Visibility = Visibility.Collapsed;
            PauseResumeButton.Visibility = Visibility.Collapsed;
            CancelDownloadButton.Visibility = Visibility.Collapsed;
            ProgressStatusText.Text = string.Empty;
            SetLaunchOperationBusy(false);
            ShowGlobalToast("Download cancelled.", ToastSeverity.Warning);
        }
    }

    /// <summary>
    /// Force-stops whichever client download is active/paused, including deleting its partial file -
    /// called when the WoW directory changes out from under it (switching directories with a
    /// download queued previously did nothing, silently continuing against the directory the UI no
    /// longer points to). Never touches the launcher's own self-update download, which isn't tied to
    /// any WoW directory at all.
    /// </summary>
    private void ForceStopClientDownloadForDirectoryChange()
    {
        if (_downloadSupportsPauseResume && (_downloadCts is not null || _pausedDownloadParams is not null))
        {
            CancelDownload();
        }
    }

    // Reused by both the client download flow above and the launcher self-update download
    // (CheckForLauncherUpdateAsync) - writes to ProgressStatusText (attached directly to
    // MainProgressBar) rather than a toast, since this fires continuously throughout one operation
    // rather than representing a single event.
    private void OnInstallProgress(InstallProgress p)
    {
        MainProgressBar.IsIndeterminate = false;
        double pct = p.Total > 0 ? (double)p.Downloaded / p.Total * 100 : 0;
        MainProgressBar.Value = pct;

        // Speed and ETA both come from the same InstallProgress.BytesPerSecond, so this covers every
        // caller uniformly - client download/repair/update, extraction, and the launcher's own
        // self-update download all report through this one method.
        string[] parts = {
            p.BytesPerSecond > 0 ? $"{FormatBytes((long)p.BytesPerSecond)}/s" : string.Empty,
            FormatEta(p.Total - p.Downloaded, p.BytesPerSecond),
        };
        string suffix = string.Join(", ", Array.FindAll(parts, s => s.Length > 0));
        if (suffix.Length > 0)
        {
            suffix = $" — {suffix}";
        }

        string verb = p.Extracting ? "Extracting" : "Downloading";
        ProgressStatusText.Text = $"{verb} {pct:0}%  ({FormatBytes(p.Downloaded)} / {FormatBytes(p.Total)}){suffix}";
    }

    /// <summary>"2m 15s left" style estimate from the remaining bytes and current rate — empty once
    /// there's nothing meaningful to estimate from (no rate yet, or nothing left).</summary>
    private static string FormatEta(long remainingBytes, double bytesPerSecond)
    {
        if (bytesPerSecond <= 0 || remainingBytes <= 0)
        {
            return string.Empty;
        }

        TimeSpan remaining = TimeSpan.FromSeconds(remainingBytes / bytesPerSecond);
        string text = remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m"
            : remaining.TotalMinutes >= 1
                ? $"{(int)remaining.TotalMinutes}m {remaining.Seconds}s"
                : $"{Math.Max(1, remaining.Seconds)}s";

        return $"{text} left";
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
            ShowGlobalToast("WoW is already running from this game folder — close it first.", ToastSeverity.Warning);
            return;
        }

        CollectSettingsFromUi();
        _settings.Save();
        _dirSettings.Save(CurrentWowDir());

        // Last-chance guard, independent of which tab the user was actually looking at: launching in
        // this state doesn't degrade gracefully, it makes WoW refuse to start entirely (a "corrupted
        // Data folder" error), so this is caught here even if both tab warnings went unnoticed.
        bool signatureRemovalEnabled = _dirSettings.Current.EnabledPatchIds.Contains("signature-removal");
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
                ShowGlobalToast("Launch cancelled — resolve the Signature Removal / MPQ patch conflict first.", ToastSeverity.Warning);
                return;
            }

            PatchControl? signatureControl = _patchControls.FirstOrDefault(pc => pc.Def.Id == "signature-removal");
            if (signatureControl is not null)
            {
                signatureControl.Box.IsChecked = true;
            }

            CollectSettingsFromUi();
            _settings.Save();
            _dirSettings.Save(CurrentWowDir());
            RefreshSignatureMpqWarning();
        }

        if (_dirSettings.Current.CleanWdbBeforeLaunch)
        {
            _gameCache.Clear(CurrentWowDir());
        }

        SetLaunchOperationBusy(true);
        var progress = new Progress<string>(msg => ShowGlobalToast(msg, ToastSeverity.Info));
        try
        {
            PlayResult result = await _orchestrator.PlayAsync(progress, OnGameProcessLaunched);
            ShowGlobalToast(result.Success ? "Launched." : "Launch failed — see the Log tab.", result.Success ? ToastSeverity.Success : ToastSeverity.Error);
        }
        catch (Exception ex)
        {
            _log.Error("Unexpected error during launch.", ex);
            ShowGlobalToast("Launch failed — see the Log tab.", ToastSeverity.Error);
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

    // ---------------- Toasts (issue #13) ----------------

    private static readonly TimeSpan ToastFadeDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan ToastAutoDismissDelay = TimeSpan.FromSeconds(4);

    private Brush SeverityBrush(ToastSeverity severity) => (Brush)FindResource(severity switch
    {
        ToastSeverity.Success => "AddActionBrush",
        ToastSeverity.Warning => "UpdateActionBrush",
        ToastSeverity.Error => "RemoveActionBrush",
        _ => "InstallActionBrush",
    });

    private static void FadeTo(UIElement element, double to, Action? onCompleted = null)
    {
        var animation = new DoubleAnimation(to, ToastFadeDuration);
        if (onCompleted is not null)
        {
            animation.Completed += (_, _) => onCompleted();
        }

        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    // ---- Global toast: single-slot, replaces the footer's old plain StatusText TextBlock ----

    private DispatcherTimer? _globalToastTimer;

    private void ShowGlobalToast(string message, ToastSeverity severity)
    {
        _globalToastTimer?.Stop();

        GlobalToastText.Text = message;
        GlobalToastBorder.BorderBrush = SeverityBrush(severity);
        // Unconditional, not gated on current Visibility: a message arriving while the previous one
        // is still mid fade-out (BeginAnimation replaces the in-flight animation on the same
        // property) would otherwise keep fading toward 0 instead of showing the new one.
        GlobalToastBorder.Visibility = Visibility.Visible;
        FadeTo(GlobalToastBorder, 1);

        if (severity is ToastSeverity.Info or ToastSeverity.Success)
        {
            _globalToastTimer = new DispatcherTimer { Interval = ToastAutoDismissDelay };
            _globalToastTimer.Tick += (_, _) =>
            {
                _globalToastTimer!.Stop();
                DismissGlobalToast();
            };
            _globalToastTimer.Start();
        }
    }

    private void DismissGlobalToast()
    {
        _globalToastTimer?.Stop();
        FadeTo(GlobalToastBorder, 0, () => GlobalToastBorder.Visibility = Visibility.Collapsed);
    }

    private void OnCloseGlobalToast(object sender, RoutedEventArgs e) => DismissGlobalToast();

    // ---- Tab toast: stack of up to 5 cards floating over the active top-level tab ----

    private const int MaxTabToastCards = 5;

    private sealed class TabToastCard
    {
        public required Border Border { get; init; }
        public required TextBlock MessageText { get; init; }
        public required int TabIndex { get; init; }
        public string? UpdateKey { get; init; }
        public DispatcherTimer? Timer { get; set; }
    }

    private readonly List<TabToastCard> _tabToastCards = new();

    /// <summary>
    /// updateKey lets a rapidly-repeating operation (e.g. "Loading catalog… (N so far)" while
    /// paging Warperia) update one card in place instead of flooding the 5-slot stack with
    /// near-duplicate entries - omit it for the common case of one distinct, finished event.
    /// </summary>
    private void ShowTabToast(string message, ToastSeverity severity, string? updateKey = null)
    {
        if (updateKey is not null)
        {
            TabToastCard? existing = _tabToastCards.FirstOrDefault(c => c.UpdateKey == updateKey);
            if (existing is not null)
            {
                existing.MessageText.Text = message;
                existing.Border.BorderBrush = SeverityBrush(severity);
                RestartTabToastTimer(existing, severity);
                return;
            }
        }

        if (_tabToastCards.Count >= MaxTabToastCards)
        {
            RemoveTabToastCard(_tabToastCards[0]);
        }

        TabToastCard card = CreateTabToastCard(message, severity, updateKey);
        _tabToastCards.Add(card);
        TabToastHost.Children.Add(card.Border);
        FadeTo(card.Border, 1);
        RestartTabToastTimer(card, severity);
    }

    private TabToastCard CreateTabToastCard(string message, ToastSeverity severity, string? updateKey)
    {
        var messageText = new TextBlock
        {
            Text = message,
            Style = (Style)FindResource("CaptionText"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
        };

        var closeButton = new Button
        {
            Content = "", // Segoe Fluent Icons: small X ("Cancel"), matches the search-box clear button glyph
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 10,
            Foreground = (Brush)new BrushConverter().ConvertFromString("#9A9AA2")!,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Arrow,
            Width = 16,
            Height = 16,
            Margin = new Thickness(8, 0, 0, 0),
        };
        DockPanel.SetDock(closeButton, Dock.Right);

        var content = new DockPanel();
        content.Children.Add(closeButton);
        content.Children.Add(messageText);

        var border = new Border
        {
            Background = (Brush)new BrushConverter().ConvertFromString("#1B1B1F")!,
            BorderBrush = SeverityBrush(severity),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 0, 0),
            Opacity = 0,
            Child = content,
        };

        var card = new TabToastCard
        {
            Border = border,
            MessageText = messageText,
            TabIndex = MainTabs.SelectedIndex,
            UpdateKey = updateKey,
        };

        closeButton.Click += (_, _) => RemoveTabToastCard(card);
        return card;
    }

    private void RestartTabToastTimer(TabToastCard card, ToastSeverity severity)
    {
        card.Timer?.Stop();
        card.Timer = null;

        if (severity is not (ToastSeverity.Info or ToastSeverity.Success))
        {
            return;
        }

        card.Timer = new DispatcherTimer { Interval = ToastAutoDismissDelay };
        card.Timer.Tick += (_, _) =>
        {
            card.Timer!.Stop();
            RemoveTabToastCard(card);
        };
        card.Timer.Start();
    }

    private void RemoveTabToastCard(TabToastCard card)
    {
        card.Timer?.Stop();
        _tabToastCards.Remove(card);
        FadeTo(card.Border, 0, () => TabToastHost.Children.Remove(card.Border));
    }

    // Tab toasts don't follow the user or persist for later - switching top-level tabs immediately
    // fades out every card that doesn't belong to the newly-selected tab, deliberately, to avoid
    // recreating the "message shown on the wrong tab" bug this whole feature originated from.
    private void OnMainTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs)
        {
            return;
        }

        int activeIndex = MainTabs.SelectedIndex;
        foreach (TabToastCard card in _tabToastCards.Where(c => c.TabIndex != activeIndex).ToList())
        {
            RemoveTabToastCard(card);
        }
    }

    // ---------------- Shared ----------------

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

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        _logItems.Clear();
        ShowTabToast("Log view cleared.", ToastSeverity.Success);
    }

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        string text = string.Join(
            Environment.NewLine,
            _logItems.Select(entry => $"{entry.Timestamp:HH:mm:ss} [{entry.LevelDisplay}] {entry.Message}"));
        try
        {
            Clipboard.SetText(text);
            ShowTabToast("Log copied to clipboard.", ToastSeverity.Success);
        }
        catch
        {
            // clipboard can be transiently locked by another app
            ShowTabToast("Could not copy the log - clipboard may be in use by another app.", ToastSeverity.Error);
        }
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
