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
    private readonly DllMetadataService _dllMetadata = new();
    private readonly MpqPatchService _mpq = new();
    private readonly MpqPatchMetadataService _mpqMetadata = new();
    private readonly RealmlistService _realmlist = new();
    private readonly ConfigWtfService _configWtf = new();
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
    private readonly DispatcherTimer _addonFolderWatchTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private FileSystemWatcher? _addonFolderWatcher;
    private int _realmStatusCheckInFlight;
    private bool _loading;
    private bool _applyingPatches;
    private bool _savingSettings;
    private bool _addonBusy;
    private bool _mpqMetadataBusy;
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
    private bool _lastAppliedConfigWtfEnabled;

    private PlayButtonState _playState = PlayButtonState.Play;
    private UpdateCheckResult? _pendingUpdateCheck;

    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x92));
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
        MainProgressBar.Loaded += OnMainProgressBarLoaded;

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
        _mpqMetadata.Load(_settings.ResolveWowDirectory());
        _dllMetadata.Load(_settings.ResolveWowDirectory());
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

        // Save on any change instead of a Save button — CollectSettingsFromUi() validates each field
        // (bad numbers/URLs keep the last good value) before anything is persisted.
        AutoLoginCheck.Checked += (_, _) => ScheduleSettingsSave();
        AutoLoginCheck.Unchecked += (_, _) => ScheduleSettingsSave();
        AccountBox.TextChanged += (_, _) => ScheduleSettingsSave();
        PasswordBoxInput.PasswordChanged += (_, _) => ScheduleSettingsSave();
        SavePasswordCheck.Checked += (_, _) => ScheduleSettingsSave();
        SavePasswordCheck.Unchecked += (_, _) => ScheduleSettingsSave();
        GameFolderBox.TextChanged += (_, _) => ScheduleSettingsSave();
        ProfileNameBox.TextChanged += (_, _) => { ScheduleSettingsSave(); BuildProfileList(); };
        // ComboBox has no TextChanged event of its own — IsEditable routes typed input through its
        // internal PART_EditableTextBox, whose TextChanged bubbles up as the TextBoxBase attached
        // event, which AddHandler picks up here the same way XAML's "TextBoxBase.TextChanged=..."
        // syntax would.
        RealmlistBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ScheduleSettingsSave()));
        RealmlistBox.LostFocus += (_, _) => UpdateRealmlistLabel();
        DelayBox.TextChanged += (_, _) => ScheduleSettingsSave();
        CleanWdbCheck.Checked += (_, _) => ScheduleSettingsSave();
        CleanWdbCheck.Unchecked += (_, _) => ScheduleSettingsSave();
        ConfigWtfCheck.Checked += (_, _) => ScheduleSettingsSave();
        ConfigWtfCheck.Unchecked += (_, _) => ScheduleSettingsSave();
        MinimizeOnLaunchCheck.Checked += (_, _) => ScheduleSettingsSave();
        MinimizeOnLaunchCheck.Unchecked += (_, _) => ScheduleSettingsSave();

        _loading = true;
        LoadSettingsIntoUi();
        InitDllsTab();
        RefreshIgnoredDllList();
        RefreshMpqList();
        InitAddonsTab();
        _loading = false;

        _lastAppliedWowDir = CurrentWowDir();
        _lastAppliedRealmlist = _dirSettings.Current.Realmlist;
        _lastAppliedClientUrl = CurrentClientUrl();
        _lastAppliedConfigWtfEnabled = _dirSettings.Current.ConfigWtfRewriteEnabled;

        if (fallbackWowDir is not null)
        {
            ShowToast("Launcher", $"'{deadWowDir}' no longer exists — switched to '{fallbackWowDir}'.", ToastSeverity.Warning);
            _log.Warn($"Configured WoW directory '{deadWowDir}' no longer exists; fell back to managed directory '{fallbackWowDir}'.");
        }
        else
        {
            ShowToast("Launcher", "Ready.", ToastSeverity.Info);
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

        // One-time-per-patch best-effort metadata extraction for any custom MPQ patch that predates
        // this feature (or was dropped in while the app was closed) — see MpqPatchMetadataService's
        // AutoExtractAttempted flag for why this never re-scans an already-attempted patch.
        Loaded += (_, _) => _ = RunRetroactiveMpqExtractionAsync();

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

        _addonFolderWatchTimer.Stop();
        _addonFolderWatcher?.Dispose();
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

    /// <summary>
    /// One-time, per-directory: pre-checks any patch marked <see cref="PatchDefinition.DefaultEnabled"/>
    /// (currently the TurtleWoW/OctoWoW tweaks that already ship baked into those clients) so a fresh
    /// directory's first patch sync is a no-op against what the client already ships, instead of
    /// looking like an unrequested revert. Guarded by <see cref="DirectorySettings.PatchDefaultsSeeded"/>
    /// so it never re-applies over a later, deliberate "everything off" choice.
    /// </summary>
    private void SeedDefaultPatchIdsIfNeeded(IReadOnlyList<PatchDefinition> catalog)
    {
        DirectorySettings ds = _dirSettings.Current;
        if (ds.PatchDefaultsSeeded)
        {
            return;
        }

        bool changed = false;
        foreach (PatchDefinition patch in catalog)
        {
            if (patch.DefaultEnabled && !ds.EnabledPatchIds.Contains(patch.Id))
            {
                ds.EnabledPatchIds.Add(patch.Id);
                changed = true;
            }
        }

        ds.PatchDefaultsSeeded = true;
        if (changed && !_loading)
        {
            _dirSettings.Save(CurrentWowDir());
        }
    }

    private void BuildPatchList()
    {
        PatchesPanel.Children.Clear();
        MandatoryPatchesPanel.Children.Clear();
        _patchControls.Clear();

        // CurrentClientIdentifierId() (live combo selection), not _dirSettings.Current.ClientProfileId -
        // that field only updates on the 700ms debounced save tick (see ScheduleSettingsSave/
        // PersistSettingsFromUiAsync), so reading it here showed a stale "no identifier assigned"
        // message for up to 700ms right after picking one, including whenever OnClientIdentifierSelected
        // calls this synchronously before the debounce has had a chance to fire.
        ClientProfile? profile = ResolveClientProfile(CurrentClientIdentifierId());
        if (profile is null)
        {
            // Same condition the Home tab's Client identifier warning + Play/Install button gating
            // already surface (see RefreshClientIdentifierWarning/UpdatePlayButtonEnabled) - this is
            // just the Tweaks tab's own reflection of it, no separate interruption needed here.
            PatchesPanel.Children.Add(new TextBlock
            {
                Text = "No client identifier assigned to this profile — pick one in Profile settings on Home.",
                Foreground = MutedBrush,
                FontSize = BodyFontSize,
                TextWrapping = TextWrapping.Wrap,
            });
            RefreshSignatureMpqWarning();
            return;
        }

        IReadOnlyList<PatchDefinition> catalog = PatchCatalog.For(profile.Category, profile.Id);
        SeedDefaultPatchIdsIfNeeded(catalog);

        foreach (PatchDefinition patch in catalog)
        {
            // Purely a UI grouping concern (see PatchDefinition.MandatoryForMpq) - Signature Removal
            // and LAA render into the "Mandatory for custom MPQ patches" section, everything else
            // into "Optional tweaks". Doesn't affect apply order, which is still Category-driven.
            StackPanel target = patch.MandatoryForMpq ? MandatoryPatchesPanel : PatchesPanel;

            // No DefaultEnabled fallback here - a fresh directory with no recorded selection starts
            // with every tweak unchecked, full stop (a new WoW install shouldn't silently inherit
            // whatever a different installation happened to have enabled).
            bool isChecked = _dirSettings.Current.EnabledPatchIds.Contains(patch.Id);

            var box = new CheckBox { Content = patch.Name, IsChecked = isChecked };
            box.Click += (_, _) => { SchedulePatchApply(); RefreshSignatureMpqWarning(); };
            target.Children.Add(box);
            target.Children.Add(new TextBlock
            {
                Text = patch.Description,
                Foreground = MutedBrush,
                FontSize = 12,
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
                target.Children.Add(row);
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
        catch (ClientIdentifierMismatchException ex)
        {
            // A dialog, not a toast, specifically for this one recoverable case - the user has to
            // actually go fix the identifier before tweaks can apply at all, so this offers to take
            // them straight there rather than leaving them to notice/interpret a toast on their own.
            _log.Error("Patch apply failed: client identifier mismatch.", ex);
            OfferToFixClientIdentifier(ex.ProfileName, ex.Category);
        }
        catch (Exception ex)
        {
            // This is an async void timer tick - an unhandled exception here can't be caught by any
            // caller, only crash the whole app via the global DispatcherUnhandledException backstop.
            // Every method this currently calls already catches its own I/O exceptions internally,
            // but that's exactly the kind of downstream guarantee a future edit could quietly break,
            // so this tick guards itself too rather than relying on it forever.
            _log.Error("Patch apply tick failed unexpectedly.", ex);
            ShowToast("Tweaks", $"Failed to apply tweaks: {ex.Message}", ToastSeverity.Error);
        }
        finally
        {
            _applyingPatches = false;
            MainProgressBar.Visibility = Visibility.Collapsed;
            MainProgressBar.IsIndeterminate = false;
            ProgressStatusText.Text = string.Empty;
        }
    }

    /// <summary>Offers to jump the user straight to Profile settings' Client identifier combo when a
    /// tweak apply failed because the assigned identifier doesn't actually match this directory's
    /// WoW.exe. Accepting switches to Home and flashes the combo; Cancel does nothing - the tweak
    /// selection itself is untouched either way (nothing was written to disk on this failure path).</summary>
    private void OfferToFixClientIdentifier(string profileName, ClientCategory category)
    {
        string categoryLabel = category == ClientCategory.VanillaPlus ? "Vanilla+" : "Vanilla";
        var confirm = new ConfirmDialog(
            "Client Identifier Mismatch",
            $"WoW.exe doesn't match a pristine baseline for '{profileName}' ({categoryLabel}). " +
            "The Client identifier assigned to this profile needs to be set or corrected before " +
            "tweaks can apply.\n\nOpen Profile settings and fix it now?",
            confirmText: "Fix It",
            cancelText: "Cancel");
        if (ShowModal(confirm) != true)
        {
            return;
        }

        SelectTab(0);
        ClientIdentifierCombo.BringIntoView();
        FlashHighlight(ClientIdentifierCombo);
    }

    // ---------------- DLLs tab ----------------

    /// <summary>Wires ItemsSource/search filtering once, then does the first populate — same split as
    /// InitAddonsTab. DllList (tracked) and DetectedDllList (untracked) stay two separate ListBoxes/
    /// collections rather than one merged data source: that's what makes Move Up/Down naturally a
    /// no-op on an untracked row (DllList.SelectedIndex can only ever be a tracked entry, since
    /// DetectedDllList is a different control entirely) without needing an extra guard, and keeps
    /// tracked-load-order vs detected-alphabetical ordering simple - each list just sorts itself the
    /// way it already did before this was one visual card.</summary>
    private void InitDllsTab()
    {
        DllList.ItemsSource = _dllItems;
        DetectedDllList.ItemsSource = _detectedDllItems;
        CollectionViewSource.GetDefaultView(_dllItems).Filter = FilterDllRow;
        CollectionViewSource.GetDefaultView(_detectedDllItems).Filter = FilterDllRow;
        RefreshDllList();
        RefreshDetectedDlls();
        UpdateDllSearchPlaceholder();
    }

    private bool FilterDllRow(object item)
    {
        string query = DllSearchBox.Text.Trim();
        if (query.Length == 0)
        {
            return true;
        }

        var dll = (DllInfo)item;
        return dll.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void OnDllSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        CollectionViewSource.GetDefaultView(_dllItems).Refresh();
        CollectionViewSource.GetDefaultView(_detectedDllItems).Refresh();
        UpdateDllSearchPlaceholder();
    }

    private void OnDllSearchFocusChanged(object sender, RoutedEventArgs e) => UpdateDllSearchPlaceholder();

    // Driven explicitly from TextChanged/GotFocus/LostFocus instead of an XAML trigger bound to
    // IsFocused - see UpdateAddonSearchPlaceholder's own comment for why (logical vs keyboard focus).
    private void UpdateDllSearchPlaceholder()
        => DllSearchPlaceholder.Visibility = DllSearchBox.Text.Length == 0 && !DllSearchBox.IsFocused
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void OnDllSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(DllSearchBox), null);
            UpdateDllSearchPlaceholder();
            e.Handled = true;
        }
    }

    private void OnClearDllSearch(object sender, RoutedEventArgs e)
    {
        DllSearchBox.Text = string.Empty;
        DllSearchBox.Focus();
    }

    // DllList/DetectedDllList are ListBoxes, which always carry their own internal ScrollViewer even
    // with scrollbar visibility set to Disabled - that inner ScrollViewer still claims (marks
    // Handled) any mouse wheel input, so it never reaches DllScrollViewer above it. Intercepting the
    // wheel here, before the ListBox's own handler runs, and driving DllScrollViewer directly is the
    // standard workaround (see OnAddonListPreviewMouseWheel's identical fix).
    private void OnDllListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        DllScrollViewer.ScrollToVerticalOffset(DllScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void RefreshDllList()
    {
        string wowDir = CurrentWowDir();

        // dlls.txt.cache's path is otherwise only resolved at actual Play time
        // (LaunchOrchestrator -> DllListService.UpdateCache) - touching it here too means it migrates
        // into PerDirectoryDataFolder's ".teronwow" subfolder right away, alongside every other
        // per-directory data file, instead of trickling in only on the user's next launch.
        _dlls.ReadCache(wowDir);

        _dllItems.Clear();
        foreach (string name in _dlls.ReadActiveNames(wowDir))
        {
            _dllItems.Add(_dllMetadata.Merge(DllMetadataReader.Read(name, _dlls.ResolvePath(wowDir, name)), isTracked: true));
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
            ShowToast("DLLs", $"Failed to save the DLL list: {ex.Message}", ToastSeverity.Error);
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
            _dllItems.Add(_dllMetadata.Merge(DllMetadataReader.Read(entry, chosen), isTracked: true));
            SaveDllList();
            RefreshDetectedDlls();
            ShowToast("DLLs", $"Added {Path.GetFileName(entry)}.", ToastSeverity.Success);
        }
        else
        {
            ShowToast("DLLs", $"{Path.GetFileName(entry)} is already tracked.", ToastSeverity.Info);
        }
    }

    /// <summary>No longer wired to any button (the per-row track/untrack toggle covers this now -
    /// see OnToggleDllTrackedClick) - kept in case a standalone Remove ever comes back.</summary>
    private void OnRemoveDll(object sender, RoutedEventArgs e)
    {
        if (DllList.SelectedItem is DllInfo item)
        {
            _dllItems.Remove(item);
            SaveDllList();
            RefreshDetectedDlls();
            ShowToast("DLLs", $"Removed {item.Name}.", ToastSeverity.Success);
        }
    }

    private void OnDllInfoClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DllInfo item)
        {
            return;
        }

        string wowDir = CurrentWowDir();
        // Re-read fresh (not the already-merged row item) so the dialog can tell which fields the
        // file itself defines (locked) apart from whatever's only in the manual sidecar.
        DllInfo extracted = DllMetadataReader.Read(item.Name, _dlls.ResolvePath(wowDir, item.Name));
        var dialog = new DllMetadataDialog(extracted, _dllMetadata.Get(item.Name));
        if (ShowModal(dialog) == true)
        {
            _dllMetadata.SaveManualEdit(
                wowDir, item.Name,
                dialog.Version, dialog.VersionIsUserEditable,
                dialog.Author, dialog.AuthorIsUserEditable,
                dialog.Description, dialog.DescriptionIsUserEditable);
            // The edited DLL could be in either list depending on its current tracked state.
            RefreshDllList();
            RefreshDetectedDlls();
            ShowToast("DLLs", $"Updated info for {item.Name}.", ToastSeverity.Success);
        }
    }

    /// <summary>The single per-row action that replaces both the old standalone Add-selected and
    /// Remove buttons: tracking a DLL is exactly "add it to dlls.txt", untracking is exactly "remove
    /// it from dlls.txt" (the file itself is never touched either way).</summary>
    private void OnToggleDllTrackedClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DllInfo item)
        {
            return;
        }

        if (item.IsTracked)
        {
            _dllItems.Remove(item);
            SaveDllList();
            RefreshDetectedDlls();
            ShowToast("DLLs", $"Stopped tracking {item.Name}.", ToastSeverity.Success);
        }
        else
        {
            string wowDir = CurrentWowDir();
            if (!_dllItems.Any(d => string.Equals(d.Name, item.Name, StringComparison.OrdinalIgnoreCase)))
            {
                _dllItems.Add(_dllMetadata.Merge(DllMetadataReader.Read(item.Name, _dlls.ResolvePath(wowDir, item.Name)), isTracked: true));
            }

            SaveDllList();
            RefreshDetectedDlls();
            ShowToast("DLLs", $"Tracking {item.Name}.", ToastSeverity.Success);
        }
    }

    private void OnIgnoreDllClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DllInfo item)
        {
            return;
        }

        List<string> ignored = _dirSettings.Current.IgnoredDetectedDlls;
        if (!ignored.Contains(item.Name, StringComparer.OrdinalIgnoreCase))
        {
            ignored.Add(item.Name);
        }

        _dirSettings.Save(CurrentWowDir());
        RefreshDetectedDlls();
        RefreshIgnoredDllList();
        ShowToast("DLLs", $"Ignored {item.Name}.", ToastSeverity.Success);
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
            _detectedDllItems.Add(_dllMetadata.Merge(DllMetadataReader.Read(name, Path.Combine(wowDir, name)), isTracked: false));
        }
    }

    private void OnRefreshDlls(object sender, RoutedEventArgs e)
    {
        RefreshDllList();
        RefreshDetectedDlls();
    }

    private void OnDllScrollBarScroll(object sender, ScrollEventArgs e) => DllScrollViewer.ScrollToVerticalOffset(e.NewValue);

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
        ShowToast("Settings", selected.Count == 1 ? $"Un-ignored {selected[0]}." : $"Un-ignored {selected.Count} DLLs.", ToastSeverity.Success);
    }

    private void OnUnignoreAllDlls(object sender, RoutedEventArgs e)
    {
        if (_dirSettings.Current.IgnoredDetectedDlls.Count == 0)
        {
            return;
        }

        int count = _dirSettings.Current.IgnoredDetectedDlls.Count;
        _dirSettings.Current.IgnoredDetectedDlls.Clear();
        _dirSettings.Save(CurrentWowDir());
        RefreshIgnoredDllList();
        RefreshDetectedDlls();
        ShowToast("Settings", $"Un-ignored {count} DLL(s).", ToastSeverity.Success);
    }

    // ---------------- MPQ tab ----------------

    private void RefreshMpqList()
    {
        MpqPanel.Children.Clear();
        string wowDir = CurrentWowDir();
        List<MpqPatch> patches = _mpq.Scan(wowDir);

        string query = MpqSearchBox.Text.Trim();
        List<MpqPatch> visible = query.Length == 0
            ? patches
            : patches.Where(p => MatchesMpqSearch(p, query)).ToList();

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
        else if (visible.Count == 0)
        {
            MpqPanel.Children.Add(new TextBlock
            {
                Text = "No patches match your search.",
                Foreground = MutedBrush,
                FontSize = BodyFontSize,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }

        foreach (MpqPatch patch in visible)
        {
            MpqPatchMetadata? meta = _mpqMetadata.Get(patch.Letter);
            (string title, string? caption) = MpqRowDisplay(patch, meta);

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
                RunMpqOperation(() =>
                {
                    _mpq.Remove(wowDir, patch);
                    _mpqMetadata.Remove(wowDir, patch.Letter);
                }, $"remove patch {patch.Letter}", $"Removed patch {patch.Letter}.");
                RefreshMpqList();
            };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);

            var details = new Button
            {
                Style = (Style)FindResource("GhostIconButtonStyle"),
                Content = "", // Segoe Fluent Icons: Info - same glyph as the Addons list's "view details" button
                Tag = patch,
                Width = 28,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "View / edit patch info",
            };
            details.Click += (_, _) =>
            {
                var dialog = new MpqPatchMetadataDialog(patch.FileName, _mpqMetadata.Get(patch.Letter));
                if (ShowModal(dialog) == true)
                {
                    _mpqMetadata.SaveManualEdit(
                        wowDir, patch.Letter,
                        dialog.PatchTitle, dialog.TitleIsUserEditable,
                        dialog.Author, dialog.AuthorIsUserEditable,
                        dialog.Description, dialog.DescriptionIsUserEditable,
                        dialog.Version, dialog.VersionIsUserEditable);
                    RefreshMpqList();
                }
            };
            DockPanel.SetDock(details, Dock.Left);
            row.Children.Add(details);

            var check = new CheckBox
            {
                Content = title,
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

            if (caption is null)
            {
                row.Children.Add(check);
            }
            else
            {
                // Inline caption, same "Name  Caption" single-line shape as the Addons/DLLs lists -
                // the checkbox docks left instead of filling, so the caption can sit right after it
                // and fill the remaining width.
                DockPanel.SetDock(check, Dock.Left);
                row.Children.Add(check);
                row.Children.Add(new TextBlock
                {
                    Text = caption,
                    Style = (Style)FindResource("CaptionText"),
                    Foreground = MutedBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(8, 0, 0, 0),
                });
            }

            MpqPanel.Children.Add(row);
        }

        RefreshSignatureMpqWarning();
    }

    /// <summary>Matches against the same title MpqRowDisplay actually shows (friendly title if set,
    /// else the raw slot filename) plus the raw slot filename itself always - so searching "patch-a"
    /// still finds a patch even once it's been given a friendly title that no longer mentions it.</summary>
    private bool MatchesMpqSearch(MpqPatch patch, string query)
    {
        string slotName = $"patch-{patch.Letter}.mpq";
        string? title = _mpqMetadata.Get(patch.Letter)?.Title;
        return slotName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(title) && title.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    // MpqPanel is hand-built rows (not an ItemsSource-bound list), so unlike DllList/AddonList's
    // CollectionViewSource.Filter, filtering here just re-runs RefreshMpqList with the current query -
    // same search-box UX (placeholder, clear button, Escape-clears-focus) as Addons/DLLs otherwise.
    private void OnMpqSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshMpqList();
        UpdateMpqSearchPlaceholder();
    }

    private void OnMpqSearchFocusChanged(object sender, RoutedEventArgs e) => UpdateMpqSearchPlaceholder();

    private void UpdateMpqSearchPlaceholder()
        => MpqSearchPlaceholder.Visibility = MpqSearchBox.Text.Length == 0 && !MpqSearchBox.IsFocused
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void OnMpqSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(MpqSearchBox), null);
            UpdateMpqSearchPlaceholder();
            e.Handled = true;
        }
    }

    private void OnClearMpqSearch(object sender, RoutedEventArgs e)
    {
        MpqSearchBox.Text = string.Empty;
        MpqSearchBox.Focus();
    }

    /// <summary>Merges an MpqPatch's scanned state with its (possibly null) metadata into the row's
    /// display title and an optional Author/Description caption line (same omit-if-absent "  •  "
    /// shape as DllInfo.Summary). When a custom title is set, the canonical patch-&lt;letter&gt;.mpq
    /// slot name is appended in parentheses right after it - a friendly title shouldn't hide which
    /// physical file/load-order slot it actually is. Falls back to the plain slot name alone when no
    /// title is set.</summary>
    private static (string Title, string? Caption) MpqRowDisplay(MpqPatch patch, MpqPatchMetadata? meta)
    {
        string slotName = $"patch-{patch.Letter}.mpq";
        string title = !string.IsNullOrWhiteSpace(meta?.Title) ? $"{meta!.Title} ({slotName})" : slotName;
        if (!patch.Enabled)
        {
            title += "  (disabled)";
        }

        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(meta?.Version))
        {
            parts.Add($"v{meta!.Version}");
        }

        if (!string.IsNullOrWhiteSpace(meta?.Author))
        {
            parts.Add(meta!.Author!);
        }

        if (!string.IsNullOrWhiteSpace(meta?.Description))
        {
            parts.Add(meta!.Description!);
        }

        if (!string.IsNullOrWhiteSpace(meta?.Website))
        {
            parts.Add(meta!.Website!);
        }

        return (title, parts.Count > 0 ? string.Join("  •  ", parts) : null);
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

        string wowDir = CurrentWowDir();
        MpqPatch? added = RunMpqOperation(() => _mpq.Add(wowDir, dialog.FileName), "add the MPQ patch", "Added the MPQ patch.");
        RefreshMpqList();
        if (added is not null)
        {
            _ = ExtractMpqMetadataAsync(wowDir, added);
        }
    }

    /// <summary>Backgrounds a best-effort metadata extraction attempt for one newly-added patch, then
    /// applies whatever was found (silently - no toast either way, see MpqPatchMetadataExtractor's own
    /// doc comment) and refreshes the row in place, but only if the user hasn't since switched to a
    /// different WoW directory.</summary>
    private async Task ExtractMpqMetadataAsync(string wowDir, MpqPatch patch)
    {
        string path = Path.Combine(_mpq.DataDirectory(wowDir), patch.FileName);
        MpqExtractedInfo? info = await Task.Run(() => MpqPatchMetadataExtractor.TryExtract(path));
        _mpqMetadata.ApplyExtracted(wowDir, patch.Letter, path, info?.Title, info?.Author, info?.Description, info?.Version, info?.Website);

        if (string.Equals(CurrentWowDir(), wowDir, StringComparison.OrdinalIgnoreCase))
        {
            RefreshMpqList();
        }
    }

    /// <summary>One-time-per-patch background sweep for any custom MPQ patch that's never had an
    /// extraction attempt recorded (pre-existing patches from before this feature shipped, or ones
    /// added while the app was closed) - guarded by _mpqMetadataBusy the same way RefreshAddonsAsync's
    /// startup rescan guards against overlapping runs. AutoExtractAttempted means a given patch is only
    /// ever opened once, not on every tab visit/refresh.</summary>
    private async Task RunRetroactiveMpqExtractionAsync()
    {
        if (_mpqMetadataBusy)
        {
            return;
        }

        _mpqMetadataBusy = true;
        try
        {
            string wowDir = CurrentWowDir();
            string dataDir = _mpq.DataDirectory(wowDir);
            List<MpqPatch> pending = _mpq.Scan(wowDir)
                .Where(p => _mpqMetadata.NeedsExtraction(p.Letter, Path.Combine(dataDir, p.FileName)))
                .ToList();
            if (pending.Count == 0)
            {
                return;
            }

            foreach (MpqPatch patch in pending)
            {
                string path = Path.Combine(dataDir, patch.FileName);
                MpqExtractedInfo? info = await Task.Run(() => MpqPatchMetadataExtractor.TryExtract(path));
                _mpqMetadata.ApplyExtracted(wowDir, patch.Letter, path, info?.Title, info?.Author, info?.Description, info?.Version, info?.Website);
            }

            if (string.Equals(CurrentWowDir(), wowDir, StringComparison.OrdinalIgnoreCase))
            {
                RefreshMpqList();
            }
        }
        catch (Exception ex)
        {
            // Defense in depth only - TryExtract already swallows its own per-file failures. No
            // user-facing toast: this is a silent background maintenance pass, not user-initiated.
            _log.Warn($"Retroactive MPQ metadata extraction pass failed: {ex.Message}");
        }
        finally
        {
            _mpqMetadataBusy = false;
        }
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
            // flood the toast stack; a single summary toast after the loop is enough.
            if (RunMpqOperation(() => _mpq.SetEnabled(wowDir, patch, enabled), $"toggle patch {patch.Letter}"))
            {
                count++;
            }
        }

        RefreshMpqList();
        if (count > 0)
        {
            ShowToast("MPQ Patches", $"{(enabled ? "Enabled" : "Disabled")} {count} patch(es).", ToastSeverity.Success);
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
        => RunMpqOperation(() => { action(); return true; }, failureContext, successMessage);

    private T? RunMpqOperation<T>(Func<T> action, string failureContext, string? successMessage = null)
    {
        try
        {
            T result = action();
            if (successMessage is not null)
            {
                ShowToast("MPQ Patches", successMessage, ToastSeverity.Success);
            }

            return result;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to {failureContext}.", ex);
            ShowToast("MPQ Patches", $"Failed to {failureContext}: {ex.Message}", ToastSeverity.Error);
            return default;
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

        _addonFolderWatchTimer.Tick += OnAddonFolderWatchTimerTick;
        StartAddonFolderWatcher(CurrentWowDir());
    }

    /// <summary>
    /// Watches the currently-selected installation's Interface\AddOns folder for folders appearing/
    /// disappearing on disk (added/removed by hand, or by something other than this launcher), so the
    /// Addons tab can pick that up live instead of only on the next manual Refresh. Re-created every
    /// time the selected directory changes (see <see cref="PersistSettingsFromUiAsync"/>) - a stale
    /// watcher left pointed at a directory the user has since moved/deleted away from would otherwise
    /// keep a handle open on it and never fire for the newly-selected one.
    /// </summary>
    private void StartAddonFolderWatcher(string wowDir)
    {
        _addonFolderWatcher?.Dispose();
        _addonFolderWatcher = null;

        string addonsDir = AddonPaths.AddOnsDir(wowDir);
        if (!Directory.Exists(addonsDir))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(addonsDir)
            {
                NotifyFilter = NotifyFilters.DirectoryName,
                IncludeSubdirectories = false,
            };

            // Raised on a background thread — only ever touches the DispatcherTimer's Start/Stop,
            // which are themselves safe to call off the UI thread, so no explicit Dispatcher hop is
            // needed here; the timer's own Tick still runs on the UI thread as usual.
            watcher.Created += (_, _) => ScheduleAddonFolderRescan();
            watcher.Deleted += (_, _) => ScheduleAddonFolderRescan();
            watcher.Renamed += (_, _) => ScheduleAddonFolderRescan();
            watcher.EnableRaisingEvents = true;

            _addonFolderWatcher = watcher;
        }
        catch (Exception ex)
        {
            // Best-effort - the manual Refresh button still works if the watcher itself can't be
            // created (e.g. a transient permissions/handle issue), so this degrades quietly.
            _log.Warn($"Could not watch {addonsDir} for addon folder changes: {ex.Message}");
        }
    }

    // Restarts the debounce window on every raw event - a folder copy fires many Created events in
    // quick succession, and this waits for them to go quiet before actually rescanning once.
    private void ScheduleAddonFolderRescan()
    {
        _addonFolderWatchTimer.Stop();
        _addonFolderWatchTimer.Start();
    }

    private async void OnAddonFolderWatchTimerTick(object? sender, EventArgs e)
    {
        _addonFolderWatchTimer.Stop();
        await RefreshAddonsLocalOnlyAsync();
    }

    /// <summary>
    /// The local-only half of <see cref="RefreshAddonsAsync"/>: re-reads on-disk .toc metadata and
    /// adopts/merges untracked folders, but deliberately skips <see cref="AddonLibrary
    /// .CheckForUpdatesAsync"/> - a local file-system change should never trigger a GitHub round-trip
    /// on its own. The manual Refresh button still runs the full flow, including update checks.
    /// </summary>
    private async Task RefreshAddonsLocalOnlyAsync()
    {
        if (_addonBusy)
        {
            return;
        }

        _addonBusy = true;
        try
        {
            string wowDir = CurrentWowDir();
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

            // Local disk read only (opens an existing .git folder, no network) - safe here despite
            // this being the local-only refresh path.
            _addons.LinkManualAddonsWithGitRemotes(wowDir);

            RefreshAddonList();
        }
        catch (Exception ex)
        {
            _log.Error("Local addon folder rescan failed.", ex);
        }
        finally
        {
            _addonBusy = false;
        }

        await Task.CompletedTask;
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
    // button - both pass the fixed "Addons" source to ShowToast, so this routes correctly
    // regardless of which one triggered it, no separate AddonStatusText/MarketplaceStatusText
    // split needed anymore (issue #13 removes it).
    private async Task AddAddonAsync(string input)
    {
        if (_addonBusy)
        {
            return;
        }

        _addonBusy = true;
        ShowToast("Addons", "Installing…", ToastSeverity.Info, updateKey: "addon-install");
        try
        {
            InstalledAddon addon = await _addons.AddAsync(input, CurrentWowDir());
            RefreshAddonList();
            ShowToast("Addons", $"Installed {WowColorTextParser.StripCodes(addon.Name)}.", ToastSeverity.Success, updateKey: "addon-install");
        }
        catch (Exception ex)
        {
            _log.Error($"Addon install failed for {input}", ex);
            ShowToast("Addons", $"Install failed: {ex.Message}", ToastSeverity.Error, updateKey: "addon-install");
        }
        finally
        {
            _addonBusy = false;
        }
    }

    private async void OnRemoveAddonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InstalledAddon addon })
        {
            await _addons.RemoveAsync(addon, CurrentWowDir());
            RefreshAddonList();
            ShowToast("Addons", $"Removed {WowColorTextParser.StripCodes(addon.Name)}.", ToastSeverity.Success);
        }
    }

    private void OnRenameAddonFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InstalledAddon addon } || addon.Folders.Count == 0)
        {
            return;
        }

        var dialog = new RenameAddonFolderDialog(addon.Folders);
        if (ShowModal(dialog) != true)
        {
            return;
        }

        string oldFolder = dialog.SelectedFolder;
        string newName = dialog.NewName;
        AddonLibrary.RenameFolderOutcome outcome = _addons.RenameFolder(addon, oldFolder, newName, CurrentWowDir());

        switch (outcome)
        {
            case AddonLibrary.RenameFolderOutcome.Success:
                RefreshAddonList();
                ShowToast("Addons", $"Renamed '{oldFolder}' to '{newName}'.", ToastSeverity.Success);
                break;
            case AddonLibrary.RenameFolderOutcome.Collision:
                ShowToast("Addons", $"A folder named '{newName}' already exists.", ToastSeverity.Error);
                break;
            case AddonLibrary.RenameFolderOutcome.InvalidName:
                ShowToast("Addons", "Enter a valid folder name.", ToastSeverity.Error);
                break;
            case AddonLibrary.RenameFolderOutcome.MoveFailed:
                ShowToast("Addons", $"Could not rename '{oldFolder}' — check the Log tab for details.", ToastSeverity.Error);
                break;
        }
    }

    private async void OnAddonDetailsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InstalledAddon addon })
        {
            return;
        }

        string title = WowColorTextParser.StripCodes(addon.Name);
        ShowToast("Addons", $"Loading details for '{title}'...", ToastSeverity.Info, updateKey: "addon-details");
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
            ShowToast("Addons", $"Could not load addon details: {ex.Message}", ToastSeverity.Error);
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
            ShowToast("Addons", "No addon updates available.", ToastSeverity.Info);
            return;
        }

        foreach (InstalledAddon addon in updatable)
        {
            await AddAddonAsync(addon.SourceRef!);
        }

        ShowToast("Addons", $"Updated {updatable.Count} addon(s).", ToastSeverity.Success);
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

            _addons.LinkManualAddonsWithGitRemotes(wowDir);

            ShowToast("Addons", "Checking for addon updates…", ToastSeverity.Info, updateKey: "addon-refresh");
            await _addons.CheckForUpdatesAsync(wowDir);

            RefreshAddonList();
            string syncSuffix = (sync.AutoAdopted, sync.Merged) switch
            {
                (0, 0) => string.Empty,
                (var a, 0) => $" {a} local addon(s) added automatically.",
                (0, var m) => $" {m} local folder(s) merged into existing addons.",
                (var a, var m) => $" {a} local addon(s) added, {m} folder(s) merged.",
            };
            ShowToast("Addons", $"Refreshed.{syncSuffix}", ToastSeverity.Success, updateKey: "addon-refresh");
        }
        catch (Exception ex)
        {
            // This also runs automatically on startup (see the constructor) - an uncaught exception
            // here would propagate out of an async void handler and crash the whole app, not just
            // fail this one refresh.
            _log.Error("Addon refresh failed.", ex);
            ShowToast("Addons", $"Addon refresh failed: {ex.Message}", ToastSeverity.Error, updateKey: "addon-refresh");
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
        ShowToast("Addons", "Loading catalog…", ToastSeverity.Info, updateKey: "marketplace-load");
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
                    ShowToast("Addons", $"Loading catalog… ({fetched.Count} so far)", ToastSeverity.Info, updateKey: "marketplace-load");
                    if (pageEntries.Count < WarperiaFullPageThreshold)
                    {
                        break;
                    }
                }
            }

            _marketplaceEntries = fetched;
            _marketplaceRowCache.Clear();

            ApplyMarketplaceFilters();
            ShowToast("Addons", $"Showing {_marketplaceRows.Count} of {_marketplaceEntries.Count} addons.", ToastSeverity.Success, updateKey: "marketplace-load");
        }
        catch (Exception ex)
        {
            _log.Error("Marketplace catalog fetch failed", ex);
            ShowToast("Addons", $"Could not load the catalog: {ex.Message}", ToastSeverity.Error, updateKey: "marketplace-load");
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

            await _addons.RemoveAsync(installed, CurrentWowDir());
            RefreshAddonList();
            row.IsInstalled = false;
            ShowToast("Addons", $"Removed {WowColorTextParser.StripCodes(installed.Name)}.", ToastSeverity.Success);
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

        ShowToast("Addons", $"Loading details for '{row.Entry.Name}'...", ToastSeverity.Info, updateKey: "marketplace-details");
        try
        {
            string markdown = await _marketplace.FetchAddonDetailsMarkdownAsync(row.Entry, CancellationToken.None);
            string repoLinkLabel = row.Entry.Source == "Warperia" ? "View on Warperia" : "View on Legacy-WoW";
            var dialog = new AddonDetailsDialog(row.Entry.Name, markdown, ignoreUpdates: false, row.Entry.DetailUrl,
                showIgnoreUpdates: false, repoLinkLabel: repoLinkLabel);
            ShowModal(dialog);
        }
        catch (Exception ex)
        {
            _log.Error($"Could not load marketplace details for '{row.Entry.Name}'.", ex);
            ShowToast("Addons", $"Could not load addon details: {ex.Message}", ToastSeverity.Error, updateKey: "marketplace-details");
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
        SeedDefaultProfileIfNeeded();
        RefreshManagedDirectoryItems();

        SeedDefaultClientProfilesIfNeeded();
        BuildClientProfileList();
        RefreshClientIdentifierCombo();

        UpdateGameFolderHint();
    }

    /// <summary>
    /// One-time, at startup: if no profile (tracked directory) exists yet, seed one instead of
    /// leaving the Home tab's Profile list empty until the user notices and clicks Add Profile
    /// themselves. Prefers whatever the pre-Profiles global WowDirectory already pointed at, if it
    /// still exists on disk - that's real prior configuration (an upgrade from before this feature
    /// existed), not something to discard - and only falls back to the launcher's own folder (the
    /// same default OnAddProfile uses) for a genuinely fresh install with no prior state at all.
    /// </summary>
    private void SeedDefaultProfileIfNeeded()
    {
        if (_settings.Current.ManagedDirectories.Count > 0)
        {
            return;
        }

        string existing = GameFolderBox.Text.Trim();
        string defaultDir = !string.IsNullOrWhiteSpace(existing) && Directory.Exists(existing)
            ? existing
            : AppContext.BaseDirectory.TrimEnd('\\', '/');

        RecordManagedDirectory(defaultDir);
        if (string.IsNullOrWhiteSpace(GameFolderBox.Text))
        {
            GameFolderBox.Text = defaultDir;

            // GameFolderBox.Text's own TextChanged→ScheduleSettingsSave path no-ops while _loading
            // is true (see ScheduleSettingsSave), which is exactly the state this only ever runs
            // in (called from LoadGlobalSettingsIntoUi, itself only called from the constructor's
            // _loading-guarded startup block) - so nothing else reloads _dirSettings for the newly-
            // seeded directory the way a real directory switch normally would. Load it explicitly
            // here so LoadDirectorySettingsIntoUi (which runs moments later in the same startup
            // sequence) reflects this directory's own settings, not whatever was loaded at
            // construction for the pre-seeding (possibly empty) WowDirectory.
            _dirSettings.Load(defaultDir);
        }
    }

    // ---------------- Client Identifiers (global table of known servers/clients) ----------------

    /// <summary>
    /// One-time, global: if the profile table has never been touched (empty), seed it with a few
    /// known-good starting points so a fresh install isn't staring at an empty table. Kronos and
    /// Twinstar serve the same vanilla 1.12.1 zip (only their realmlist differs), so the Kronos seed
    /// reuses that same confirmed-working URL. TurtleWoW's own server has shut down (no fresh download
    /// exists), but its byte-verified baseline is still worth offering for an already-installed copy.
    /// Never re-seeds afterward - the user can freely edit/delete any of these, including all three.
    /// </summary>
    private void SeedDefaultClientProfilesIfNeeded()
    {
        if (_settings.Current.ClientProfiles.Count > 0)
        {
            return;
        }

        _settings.Current.ClientProfiles.Add(new ClientProfile
        {
            Id = ClientProfile.KronosSeedId,
            Name = "Kronos WoW",
            DownloadUrl = GameInstallService.DefaultClientUrl,
            Category = ClientCategory.Vanilla,
        });
        _settings.Current.ClientProfiles.Add(new ClientProfile
        {
            Id = ClientProfile.OctoWowSeedId,
            Name = "OctoWoW",
            DownloadUrl = GameInstallService.DefaultOctoWowClientUrl,
            Category = ClientCategory.VanillaPlus,
        });
        _settings.Current.ClientProfiles.Add(new ClientProfile
        {
            Id = ClientProfile.TurtleWowSeedId,
            Name = "Turtle WoW",
            DownloadUrl = GameInstallService.DefaultTurtleWowClientUrl,
            Category = ClientCategory.VanillaPlus,
        });
        _settings.Save();
    }

    /// <summary>Rebuilds the global Client Identifiers table (Settings tab) from
    /// <see cref="LauncherSettings.ClientProfiles"/> - one editable row per profile, plus an Add
    /// button already in XAML. Each row's controls write straight back into the same ClientProfile
    /// instance (reference type, shared with the list), so no separate collect step is needed for
    /// this table - which profile a directory uses is picked in ProfileDialog instead.</summary>
    private void BuildClientProfileList()
    {
        ClientProfilesPanel.Children.Clear();

        foreach (ClientProfile profile in _settings.Current.ClientProfiles)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameBox = new TextBox { Text = profile.Name, Margin = new Thickness(0, 0, 6, 0) };
            nameBox.LostFocus += (_, _) =>
            {
                string trimmed = nameBox.Text.Trim();
                if (trimmed.Length > 0 && trimmed != profile.Name)
                {
                    profile.Name = trimmed;
                    _settings.Save();
                    RefreshClientIdentifierCombo();
                }
                else
                {
                    nameBox.Text = profile.Name;
                }
            };
            Grid.SetColumn(nameBox, 0);
            row.Children.Add(nameBox);

            var categoryCombo = new ComboBox { Margin = new Thickness(0, 0, 6, 0) };
            categoryCombo.Items.Add(new ComboBoxItem { Content = "Vanilla", Tag = ClientCategory.Vanilla });
            categoryCombo.Items.Add(new ComboBoxItem { Content = "Vanilla+", Tag = ClientCategory.VanillaPlus });
            categoryCombo.SelectedIndex = profile.Category == ClientCategory.VanillaPlus ? 1 : 0;
            categoryCombo.SelectionChanged += (_, _) =>
            {
                if (categoryCombo.SelectedItem is ComboBoxItem { Tag: ClientCategory selected })
                {
                    profile.Category = selected;
                    _settings.Save();
                    // The currently-viewed directory's Tweaks tab depends on category when it's the
                    // one using this exact profile - refresh so an edit here is reflected immediately.
                    BuildPatchList();
                }
            };
            Grid.SetColumn(categoryCombo, 1);
            row.Children.Add(categoryCombo);

            var urlBox = new TextBox
            {
                Text = profile.DownloadUrl ?? string.Empty,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Leave empty if this server has no downloadable client (e.g. an already-installed copy).",
            };
            urlBox.LostFocus += (_, _) =>
            {
                string trimmed = urlBox.Text.Trim();
                profile.DownloadUrl = trimmed.Length == 0 ? null : trimmed;
                _settings.Save();
            };
            Grid.SetColumn(urlBox, 2);
            row.Children.Add(urlBox);

            var deleteButton = new Button
            {
                Content = "Delete",
                Background = (Brush)FindResource("RemoveActionBrush"),
            };
            deleteButton.Click += (_, _) =>
            {
                _settings.Current.ClientProfiles.Remove(profile);
                _settings.Save();
                BuildClientProfileList();
                RefreshClientIdentifierCombo();
                // A directory that was pointing at the now-deleted profile becomes unassigned again -
                // reflected immediately by re-resolving the combo/patch list for whichever directory
                // is currently shown, same as any other profile change above.
                SetClientIdentifierCombo(_dirSettings.Current.ClientProfileId);
                BuildPatchList();
            };
            Grid.SetColumn(deleteButton, 3);
            row.Children.Add(deleteButton);

            ClientProfilesPanel.Children.Add(row);
        }
    }

    private void OnAddClientProfile(object sender, RoutedEventArgs e)
    {
        _settings.Current.ClientProfiles.Add(new ClientProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "New Profile",
            DownloadUrl = null,
            Category = ClientCategory.Vanilla,
        });
        _settings.Save();
        BuildClientProfileList();
        RefreshClientIdentifierCombo();
    }

    /// <summary>Repopulates ClientIdentifierCombo (Home, Profile settings) from the current global
    /// list, preserving whichever profile is currently selected if it still exists. Display text is
    /// "Name (Category)" per-item, so the category is visible without needing the global table open.</summary>
    private void RefreshClientIdentifierCombo()
    {
        string? currentId = CurrentClientIdentifierId();
        ClientIdentifierCombo.Items.Clear();
        foreach (ClientProfile profile in _settings.Current.ClientProfiles)
        {
            string category = profile.Category == ClientCategory.VanillaPlus ? "Vanilla+" : "Vanilla";
            ClientIdentifierCombo.Items.Add(new ComboBoxItem { Content = $"{profile.Name} ({category})", Tag = profile.Id });
        }

        SetClientIdentifierCombo(currentId);
    }

    private void OnClientIdentifierSelected(object sender, SelectionChangedEventArgs e)
    {
        RefreshClientIdentifierWarning();
        if (_loading)
        {
            return;
        }

        BuildPatchList();
        UpdatePlayButtonEnabled(); // Play/Install may need to (un)gate on client-identifier presence.
        ScheduleSettingsSave();
    }

    private string? CurrentClientIdentifierId()
        => ClientIdentifierCombo.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : null;

    private void SetClientIdentifierCombo(string? profileId)
    {
        foreach (ComboBoxItem item in ClientIdentifierCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && tag == profileId)
            {
                ClientIdentifierCombo.SelectedItem = item;
                RefreshClientIdentifierWarning();
                return;
            }
        }

        ClientIdentifierCombo.SelectedItem = null;
        RefreshClientIdentifierWarning();
    }

    /// <summary>Shows the "pick a client identifier" warning under the combo whenever nothing's
    /// selected - the Play/Install button is separately force-disabled for the same reason
    /// (see UpdatePlayButtonEnabled), so this is the visible explanation for why.</summary>
    private void RefreshClientIdentifierWarning()
    {
        ClientIdentifierWarningText.Visibility =
            CurrentClientIdentifierId() is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private ClientProfile? ResolveClientProfile(string? profileId)
        => profileId is null
            ? null
            : _settings.Current.ClientProfiles.FirstOrDefault(p => p.Id == profileId);

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
    /// then repopulates <see cref="_managedDirectoryItems"/> and rebuilds the Profile list to match.</summary>
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

        BuildProfileList();
    }

    /// <summary>
    /// Resolves what a profile row should actually show: its own <see cref="DirectorySettings.Name"/>
    /// if one was set, otherwise the directory path - except for the launcher's own root folder (the
    /// directory a freshly-added profile defaults to, see OnAddProfile), which falls back to "Default"
    /// instead of an unhelpful raw path. Reads the CURRENTLY ACTIVE directory's name straight out of
    /// ProfileNameBox itself, not _dirSettings.Current.Name - that field only updates on the 700ms
    /// debounced save tick (see ScheduleSettingsSave/PersistSettingsFromUiAsync), so reading it here
    /// would show a stale name until the debounce fires or the app restarts. Every other directory's
    /// name is read fresh off its own settings file, since only the active one has live UI to read from.
    /// </summary>
    private string GetProfileDisplayName(string dir)
    {
        string? name = string.Equals(dir, CurrentWowDir(), StringComparison.OrdinalIgnoreCase)
            ? ProfileNameBox.Text
            : new DirectorySettingsService().Load(dir).Name;

        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        string baseDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        return string.Equals(dir, baseDir, StringComparison.OrdinalIgnoreCase) ? "Default" : dir;
    }

    /// <summary>
    /// Rebuilds the Home "Profiles" list (<see cref="ManagedDirectories"/>, one row per directory) -
    /// clicking a row's directory text switches to it (same mechanism the old DirectorySwitchBox
    /// selection used: setting GameFolderBox.Text fires the existing TextChanged → ScheduleSettingsSave
    /// → PersistSettingsFromUiAsync wowDirChanged pipeline), a small delete button untracks it (not a
    /// destructive file-system action - see OnRemoveProfile). The row matching CurrentWowDir() is
    /// visually highlighted so it's clear which profile is active.
    /// </summary>
    private void BuildProfileList()
    {
        ProfileListPanel.Children.Clear();
        string current = CurrentWowDir();

        if (_managedDirectoryItems.Count == 0)
        {
            ProfileListPanel.Children.Add(new TextBlock
            {
                Text = "No profiles yet — click Add Profile below to get started.",
                Foreground = MutedBrush,
                FontSize = BodyFontSize,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        foreach (string dir in _managedDirectoryItems)
        {
            bool isActive = string.Equals(dir, current, StringComparison.OrdinalIgnoreCase);

            // Active row: full gold border+fill (the app's one consistent "selected" accent -
            // checkbox fills, focus borders, the nav tab's selected label all use the same
            // #E6C067) instead of the previous blue-border/gold-fill mismatch, with a thicker
            // border so it actually reads as selected at a glance rather than blending into the
            // inactive rows around it.
            var row = new Border
            {
                BorderThickness = new Thickness(isActive ? 2 : 1),
                BorderBrush = isActive
                    ? new SolidColorBrush(Color.FromRgb(0xE6, 0xC0, 0x67))
                    : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)),
                // Solid background even when inactive - a fully transparent row was unreadable
                // against the faint background art showing through the card behind it.
                Background = isActive
                    ? new SolidColorBrush(Color.FromArgb(0x48, 0xE6, 0xC0, 0x67))
                    : new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x29)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 6, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = Cursors.Hand,
            };

            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Explicit Foreground/FontSize - a bare TextBlock has no app-wide default style to fall
            // back on (App.xaml has no global TextBlock style, only named ones like BodyText), so
            // this was rendering at WPF's own default (black, ~12px) and was nearly invisible
            // against the dark card.
            var dirText = new TextBlock
            {
                Text = GetProfileDisplayName(dir),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = isActive ? FontWeights.Bold : FontWeights.Normal,
                FontSize = 15,
                Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xED)),
                ToolTip = dir,
            };
            Grid.SetColumn(dirText, 0);
            rowGrid.Children.Add(dirText);

            // Same trash-icon/RemoveActionBrush treatment as the DLL "untrack" and Addon "Remove"
            // row buttons, instead of the previous unstyled ghost button that didn't match the
            // rest of the app's icon-button vocabulary.
            var deleteButton = new Button
            {
                Style = (Style)FindResource("IconButtonStyle"),
                Content = "",
                Background = (Brush)FindResource("RemoveActionBrush"),
                Width = 28,
                Height = 28,
                ToolTip = "Remove this profile (untracks the directory only - files on disk are untouched)",
                VerticalAlignment = VerticalAlignment.Center,
            };
            deleteButton.Click += (_, e) =>
            {
                e.Handled = true; // don't also trigger the row's own MouseLeftButtonUp switch-to below
                OnRemoveProfile(dir);
            };
            Grid.SetColumn(deleteButton, 1);
            rowGrid.Children.Add(deleteButton);

            row.Child = rowGrid;
            row.MouseLeftButtonUp += (_, _) => OnSelectProfile(dir);

            ProfileListPanel.Children.Add(row);
        }
    }

    /// <summary>Switches to a profile the same way the old DirectorySwitchBox selection did - setting
    /// GameFolderBox.Text fires TextChanged → ScheduleSettingsSave → PersistSettingsFromUiAsync's
    /// wowDirChanged branch, which reloads every directory-scoped tab.</summary>
    private void OnSelectProfile(string dir)
    {
        if (!_loading && !string.Equals(dir, GameFolderBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            GameFolderBox.Text = dir;
        }
    }

    /// <summary>Untracks a profile from the list. Does not touch anything on disk - "Delete Game
    /// Files" (Settings) is the destructive action for that. Falls back to another managed directory
    /// if the removed one was active, same as the dead-directory fallback at startup.</summary>
    private void OnRemoveProfile(string dir)
    {
        _settings.Current.ManagedDirectories.RemoveAll(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase));
        _settings.Save();

        bool wasActive = string.Equals(dir, CurrentWowDir(), StringComparison.OrdinalIgnoreCase);
        RefreshManagedDirectoryItems(); // also rebuilds the list

        if (wasActive)
        {
            string? fallback = _settings.Current.ManagedDirectories.FirstOrDefault(Directory.Exists);
            GameFolderBox.Text = fallback ?? string.Empty;
        }
    }

    private void OnAddProfile(object sender, RoutedEventArgs e)
    {
        // Defaults to the launcher's own folder rather than leaving the field empty - a fresh
        // profile still needs SOME directory to exist for DirectorySettingsService to key off of,
        // and the user can immediately Browse elsewhere if this isn't where they want the client.
        string defaultDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        string dir = _settings.Current.ManagedDirectories.FirstOrDefault(
            d => string.Equals(d, defaultDir, StringComparison.OrdinalIgnoreCase)) ?? defaultDir;

        RecordManagedDirectory(dir);
        GameFolderBox.Text = dir; // triggers the existing directory-switch pipeline if it's a real change
        if (string.Equals(dir, CurrentWowDir(), StringComparison.OrdinalIgnoreCase))
        {
            // Already the active directory (re-adding a profile that's already selected) - the
            // .Text setter above won't have fired TextChanged, so nothing else will refresh the UI.
            BuildProfileList();
        }

        FlashHighlight(GameFolderBox);
    }

    /// <summary>Populates every UI field scoped to the currently-selected WoW directory (account,
    /// password, realmlist, tweak checkboxes/sliders) from <see cref="_dirSettings"/>.Current — called
    /// at startup and again whenever the selected directory changes, so switching directories actually
    /// repopulates these instead of leaving the previous directory's values showing.</summary>
    private void LoadDirectorySettingsIntoUi()
    {
        DirectorySettings s = _dirSettings.Current;
        ProfileNameBox.Text = s.Name ?? string.Empty;
        AutoLoginCheck.IsChecked = s.AutoLoginEnabled;
        AccountBox.Text = s.Account;
        SavePasswordCheck.IsChecked = s.SavePassword;
        PasswordBoxInput.Password = s.SavePassword ? _dirSettings.GetPassword() : string.Empty;
        DelayBox.Text = s.LoginDelayMs.ToString(CultureInfo.InvariantCulture);
        CleanWdbCheck.IsChecked = s.CleanWdbBeforeLaunch;
        ConfigWtfCheck.IsChecked = s.ConfigWtfRewriteEnabled;
        RealmlistBox.Text = s.Realmlist;
        SetClientIdentifierCombo(s.ClientProfileId);

        BuildPatchList();

        UpdateRealmlistLabel();
    }

    private void CollectSettingsFromUi()
    {
        LauncherSettings s = _settings.Current;
        s.WowDirectory = string.IsNullOrWhiteSpace(GameFolderBox.Text) ? null : GameFolderBox.Text.Trim();
        s.MinimizeOnLaunch = MinimizeOnLaunchCheck.IsChecked == true;

        DirectorySettings ds = _dirSettings.Current;
        ds.Name = string.IsNullOrWhiteSpace(ProfileNameBox.Text) ? null : ProfileNameBox.Text.Trim();
        ds.ClientProfileId = CurrentClientIdentifierId();

        ds.AutoLoginEnabled = AutoLoginCheck.IsChecked == true;
        ds.Account = AccountBox.Text.Trim();
        ds.SavePassword = SavePasswordCheck.IsChecked == true;
        ds.LoginDelayMs = int.TryParse(DelayBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d)
            ? Math.Max(0, d)
            : ds.LoginDelayMs;
        ds.CleanWdbBeforeLaunch = CleanWdbCheck.IsChecked == true;
        ds.ConfigWtfRewriteEnabled = ConfigWtfCheck.IsChecked == true;
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
        catch (Exception ex)
        {
            // Same reasoning as OnPatchApplyTick's own catch - an async void timer tick has no
            // caller that could catch this otherwise, only the global crash backstop.
            _log.Error("Settings save tick failed unexpectedly.", ex);
            ShowToast("Settings", $"Failed to save settings: {ex.Message}", ToastSeverity.Error);
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
            _mpqMetadata.Load(wowDir);
            _dllMetadata.Load(wowDir);
            // Guarded so the bound field assignments inside don't each schedule their own redundant
            // settings-save tick (ScheduleSettingsSave no-ops while _loading is true) - harmless
            // either way since a follow-up save would just re-persist the same values, but wasteful.
            _loading = true;
            LoadDirectorySettingsIntoUi();
            _loading = false;
            RecordManagedDirectory(wowDir); // re-highlights the now-active row via RefreshManagedDirectoryItems

            RefreshDllList();
            RefreshDetectedDlls();
            RefreshIgnoredDllList();
            RefreshMpqList();
            _ = RunRetroactiveMpqExtractionAsync();
            // Addon tracking (.teronwow-addons.json) lives inside each WoW directory - Load(wowDir)
            // has to run before RefreshAddonList can show anything real for the newly-selected one.
            _addons.Load(wowDir);
            RefreshAddonList();
            StartAddonFolderWatcher(wowDir);

            ShowToast("Launcher", $"Switched to '{wowDir}'.", ToastSeverity.Success);
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
                        ShowToast("Launcher", "Warning: realmlist.wtf may not have saved correctly — see the Log tab.", ToastSeverity.Warning);
                    }
                }
                catch (Exception ex)
                {
                    _log.Error("Failed to write realmlist.wtf.", ex);
                    ShowToast("Launcher", $"Failed to write realmlist.wtf: {ex.Message}", ToastSeverity.Error);
                }
            }

            SyncConfigWtfToggle(wowDir);
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
        _lastAppliedConfigWtfEnabled = _dirSettings.Current.ConfigWtfRewriteEnabled;

        ShowToast("Launcher", "Settings saved.", ToastSeverity.Success);
    }

    /// <summary>
    /// Applies or reverts the Config.WTF prerequisite toggle the moment it changes. Turning it on
    /// snapshots each required key's current value first (so turning it off later restores exactly
    /// that, not a stale snapshot from an earlier on/off cycle); turning it off restores from that
    /// snapshot and clears it. Skipped (with a soft heads-up, not an error) while the game is running,
    /// the same way executable-patch changes are — Config.WTF is re-applied fresh at the start of
    /// every Play anyway (see LaunchOrchestrator.PlayAsync), so nothing is lost by waiting.
    /// </summary>
    private void SyncConfigWtfToggle(string wowDir)
    {
        bool enabled = _dirSettings.Current.ConfigWtfRewriteEnabled;
        if (enabled == _lastAppliedConfigWtfEnabled || !Directory.Exists(wowDir))
        {
            return;
        }

        if (_processCheck.IsRunning(wowDir))
        {
            ShowToast("Launcher",
                "Close the game to apply the Config.WTF change now — it will also be applied automatically on your next Play.",
                ToastSeverity.Info);
            return;
        }

        try
        {
            if (enabled)
            {
                _dirSettings.Current.ConfigWtfOriginalValues =
                    _configWtf.ReadAll(wowDir, ConfigWtfService.RequiredSettings.Select(s => s.Key));
                _configWtf.ApplyRequiredSettings(wowDir);
            }
            else
            {
                _configWtf.RestoreValues(wowDir, _dirSettings.Current.ConfigWtfOriginalValues);
                _dirSettings.Current.ConfigWtfOriginalValues.Clear();
            }

            _dirSettings.Save(wowDir);
        }
        catch (Exception ex)
        {
            _log.Error("Failed to update Config.WTF.", ex);
            ShowToast("Launcher", $"Failed to update Config.WTF: {ex.Message}", ToastSeverity.Error);
        }
    }

    private string CurrentWowDir()
        => !string.IsNullOrWhiteSpace(GameFolderBox.Text) && Directory.Exists(GameFolderBox.Text)
            ? GameFolderBox.Text.Trim()
            : _settings.ResolveWowDirectory();

    private string CurrentClientUrl()
        => ResolveClientProfile(CurrentClientIdentifierId())?.DownloadUrl ?? string.Empty;

    private void UpdateRealmlistLabel() => _ = RefreshRealmStatusAsync();

    private async void OnRealmStatusTick(object? sender, EventArgs e)
    {
        try
        {
            await RefreshRealmStatusAsync();
        }
        catch (Exception ex)
        {
            // Same reasoning as OnPatchApplyTick/OnSettingsSaveTick's own catch - this fires
            // unattended on a 60s timer with no caller that could catch it otherwise.
            _log.Error("Realm status tick failed unexpectedly.", ex);
        }
    }

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
            ShowToast("Settings", "Nothing installed to repair yet.", ToastSeverity.Info);
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
            ShowToast("Settings", "Nothing installed to delete.", ToastSeverity.Info);
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
            ShowToast("Settings", "Game files deleted.", ToastSeverity.Success);
            UpdateGameFolderHint();
            await RefreshPlayButtonStateAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Delete game files failed.", ex);
            ShowToast("Settings", $"Delete failed: {ex.Message}", ToastSeverity.Error);
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
                ShowToast("Launcher", "Update download cancelled.", ToastSeverity.Warning);
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
            ShowToast("Launcher", $"Update check failed: {ex.Message}", ToastSeverity.Error);
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
        // Installing needs a client identifier to know the download URL/patch catalog - gated here
        // (rather than only warning) so there's no way to kick off an install that would immediately
        // fail. Play/Update aren't gated the same way - RunPlayFlowAsync has its own check for the
        // already-installed-but-identifier-since-deleted edge case this can't see.
        bool needsClientIdentifier = _playState == PlayButtonState.Install && CurrentClientIdentifierId() is null;
        PlayButton.IsEnabled = !_launchOperationBusy && !_wowAlreadyRunning && !needsClientIdentifier;
        PlayButton.ToolTip = _wowAlreadyRunning
            ? "WoW is already running from this game folder — close it first."
            : needsClientIdentifier
                ? "Pick a client identifier in Profile settings before installing."
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
        UpdatePlayButtonEnabled(); // re-evaluates the Install-state client-identifier gate above too
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

        // An existing, verified client already sitting in the chosen folder gets adopted as-is
        // instead of blindly re-downloaded over - the user pointing the launcher at a folder that
        // already has a valid client should verify it, not clobber it. Peeked directly rather than
        // through _dirSettings (which still holds whichever directory was selected before this one
        // until the full Load(targetDir) further below) so the right profile's check applies even on
        // the very first look at a freshly-adopted folder. A folder with no profile assigned yet
        // (brand new, or never configured through this launcher) has no category to check the version
        // against - IsInstalled alone decides "already valid" there; the Client identifier warning +
        // Play/Install gating (via LoadDirectorySettingsIntoUi's chain) prompts for one afterward either way.
        string? targetProfileId = new DirectorySettingsService().Load(targetDir).ClientProfileId;
        ClientProfile? targetProfile = _settings.Current.ClientProfiles.FirstOrDefault(p => p.Id == targetProfileId);
        bool alreadyValid = _install.IsInstalled(targetDir) &&
            (targetProfile is null || _install.IsUpToDate(targetDir, targetProfile.Category));
        bool ok;
        if (alreadyValid)
        {
            ok = true;
            ShowToast("Launcher", "Existing client verified — using it as-is.", ToastSeverity.Success);
        }
        else
        {
            ok = await RunClientDownloadAsync(targetDir, "Client installed.", "Install failed", InstallColor);
        }

        if (ok)
        {
            _settings.Current.WowDirectory = targetDir;
            _settings.Save();

            // Guarded so setting .Text below (and the bound fields inside LoadDirectorySettingsIntoUi)
            // don't each schedule their own redundant settings-save tick - ScheduleSettingsSave
            // no-ops entirely while _loading is true, which is more reliable than the previous
            // "stop the timer first" attempt (ScheduleSettingsSave restarts it right back up unless
            // _loading is actually set).
            _loading = true;
            _settingsSaveTimer.Stop();
            GameFolderBox.Text = targetDir;
            _lastAppliedWowDir = targetDir;

            UpdateGameFolderHint();
            // Load/reload directory-scoped state before anything below reads from it - RefreshDetectedDlls
            // and RefreshIgnoredDllList both depend on _dirSettings.Current.IgnoredDetectedDlls, which
            // otherwise would still reflect whichever directory was active before this install.
            _dirSettings.Load(targetDir);
            _mpqMetadata.Load(targetDir);
            _dllMetadata.Load(targetDir);
            LoadDirectorySettingsIntoUi();
            _loading = false;
            RecordManagedDirectory(targetDir); // re-highlights the now-active row via RefreshManagedDirectoryItems
            RefreshDllList();
            RefreshDetectedDlls();
            RefreshIgnoredDllList();
            RefreshMpqList();
            _ = RunRetroactiveMpqExtractionAsync();
            _addons.Load(targetDir);
            RefreshAddonList();
            StartAddonFolderWatcher(targetDir);

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
            ShowToast("Launcher", successMessage, ToastSeverity.Success);
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
                //
                // Guarded rather than a plain +=: OnInstallProgress overwrites this text wholesale
                // (Text = ...) on each real progress tick, which normally clears a prior " — paused"
                // suffix - but if the user pauses, resumes, and pauses again before any fresh tick
                // actually arrives to do that overwrite, a plain += kept stacking another " — paused"
                // onto whatever was already there ("— paused — paused — paused...", confirmed via
                // screenshots showing 2 and then 3 stacked suffixes, 2026-07-19).
                if (!ProgressStatusText.Text.EndsWith(" — paused", StringComparison.Ordinal))
                {
                    ProgressStatusText.Text += " — paused";
                }
                _pausedDownloadParams = (targetDir, successMessage, failureMessage, color);
                return false;
            }

            _install.DeletePartialDownload();
            ShowToast("Launcher", "Download cancelled.", ToastSeverity.Warning);
            return false;
        }
        catch (Exception ex)
        {
            _log.Error($"{failureMessage}.", ex);
            ShowToast("Launcher", $"{failureMessage}: {ex.Message}", ToastSeverity.Error);
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
            ResumeProgressBarStripeAnimation();
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
            PauseProgressBarStripeAnimation();
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
            // Cancelling while paused otherwise leaves the stripe animation's Clock paused
            // indefinitely - it wouldn't resume on its own for whatever future operation next shows
            // the bar, since only OnPauseResumeClick's own Resume branch normally undoes this.
            ResumeProgressBarStripeAnimation();
            MainProgressBar.Visibility = Visibility.Collapsed;
            PauseResumeButton.Visibility = Visibility.Collapsed;
            CancelDownloadButton.Visibility = Visibility.Collapsed;
            ProgressStatusText.Text = string.Empty;
            SetLaunchOperationBusy(false);
            ShowToast("Launcher", "Download cancelled.", ToastSeverity.Warning);
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

    private bool _progressBarStripeAnimationStarted;
    private AnimationClock? _progressBarStripeClock;
    private System.Windows.Shapes.Rectangle? _progressBarStripeOverlay;

    // Tile pitch (must match BuildStripeGeometry's own spacing) and how far right to pre-build
    // stripes - generously beyond any realistic window/bar width, built once rather than rebuilt
    // per resize, since regenerating ~150 simple parallelograms is cheap enough to just over-provision.
    private const double ProgressStripeTilePitch = 20;
    private const double ProgressStripeMaxWidth = 3000;

    // Exact extent of the geometry BuildStripeGeometry produces, used to pin the DrawingBrush's
    // Viewbox/Viewport to an absolute identity mapping (see OnMainProgressBarLoaded for why that
    // pinning is load-bearing). Left starts two tile-pitches before 0 so that even at the scroll
    // animation's maximum displacement (+1 pitch) the leftmost painted stripe is still fully off-view
    // left of the bar's border. Right: the loop's last figure starts at ProgressStripeMaxWidth and
    // its top edge extends one more pitch. Height 40 covers the bar's actual content height.
    private const double ProgressStripeGeometryLeft = -2 * ProgressStripeTilePitch;
    private const double ProgressStripeGeometryRight = ProgressStripeMaxWidth + ProgressStripeTilePitch;
    private const double ProgressStripeGeometryHeight = 40;

    /// <summary>
    /// Builds PART_StripeOverlay's fill by hand-constructing one continuous, non-tiled geometry
    /// (many explicit repeated parallelograms) and animating the whole brush via
    /// ApplyAnimationClock, rather than a DrawingBrush TileMode="Tile" - a real, reproducing mid-bar
    /// seam persisted (confirmed via screenshot, 2026-07-19) even after removing the per-tick resize
    /// that was the first suspected cause, pointing at WPF's own tiled-brush rasterization as the
    /// actual source of the seam rather than anything about resizing. A single continuous
    /// hand-built geometry has no tile boundary for that to happen across. Also why this builds and
    /// assigns the brush fresh in code rather than reading one declared in the template: a
    /// template-declared Freezable is frozen by default (shared across every instance of the styled
    /// control), which is what "Cannot animate... because the object is sealed or frozen" was about
    /// the first time this was tried - a freshly code-constructed brush was never frozen to begin
    /// with, since it's never part of any shared Style/Template resource.
    ///
    /// Loaded can fire more than once (observed re-firing on a window resize's Loaded re-broadcast),
    /// so this guards against rebuilding/restarting the animation clock redundantly on every refire.
    /// </summary>
    private void OnMainProgressBarLoaded(object sender, RoutedEventArgs e)
    {
        if (_progressBarStripeAnimationStarted)
        {
            return;
        }

        MainProgressBar.ApplyTemplate();
        if (MainProgressBar.Template.FindName("PART_StripeOverlay", MainProgressBar) is not System.Windows.Shapes.Rectangle overlay)
        {
            return;
        }

        var translate = new TranslateTransform(0, 0);

        // Viewbox and Viewport are BOTH pinned to the geometry's exact absolute extent, making the
        // brush a strict identity mapping: geometry coordinate x paints at overlay coordinate x,
        // period. Without this, both default to RelativeToBoundingBox, which auto-fits the DRAWING'S
        // BOUNDING BOX onto the overlay rectangle - pinning the drawing's left boundary to the bar's
        // left border no matter how much off-view margin the geometry builds in (the margin is part
        // of the bounding box, so the auto-fit swallows it). The scroll animation then dragged that
        // boundary rightward by one full tile-pitch every cycle before snapping back, sweeping a
        // 0-to-20px UNPAINTED void into view at the bar's start once per loop. That void - solid
        // indicator color with no stripes, growing and collapsing in sync with the animation - was
        // the long-standing "pooling at the start" issue (root-caused 2026-07-19 via a solid
        // red-indicator/lime-stripes diagnostic build: the start showed pure red for most of each
        // cycle, and the pattern's left edge visibly oscillated between the border and one stripe
        // length in). It also explains why an added leading-edge fade did nothing: opacity masks
        // can't paint content into a region the brush never painted. With the identity mapping, the
        // geometry's two-tile-pitch left margin genuinely sits off-view, so the painted region still
        // covers the whole bar even at maximum scroll displacement.
        var stripeBounds = new Rect(
            ProgressStripeGeometryLeft, 0,
            ProgressStripeGeometryRight - ProgressStripeGeometryLeft, ProgressStripeGeometryHeight);
        var brush = new DrawingBrush(new GeometryDrawing(
            new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), null, BuildStripeGeometry()))
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = stripeBounds,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = stripeBounds,
            Stretch = Stretch.Fill,
            Transform = translate,
        };
        overlay.Fill = brush;
        _progressBarStripeOverlay = overlay;

        // Created as an explicit Clock (not the simple BeginAnimation(dp, animation) overload) so
        // OnPauseResumeClick can actually pause/resume it later - the simple overload doesn't hand
        // back anything controllable, which is why pausing a download previously left the stripe
        // still visibly scrolling for the duration of the pause (reported 2026-07-19).
        _progressBarStripeAnimationStarted = true;
        var animation = new DoubleAnimation(0, ProgressStripeTilePitch, TimeSpan.FromSeconds(0.6)) { RepeatBehavior = RepeatBehavior.Forever };
        _progressBarStripeClock = animation.CreateClock();
        translate.ApplyAnimationClock(TranslateTransform.XProperty, _progressBarStripeClock);
    }

    /// <summary>
    /// One continuous StreamGeometry containing many explicit parallelogram stripes at a fixed 20px
    /// pitch, spanning ProgressStripeGeometryLeft..ProgressStripeGeometryRight - the brush that
    /// paints it maps these coordinates 1:1 onto the overlay (see OnMainProgressBarLoaded), which is
    /// what makes the off-view left margin real rather than swallowed by bounding-box auto-fitting.
    /// Tall enough to cover MainProgressBar's actual content height in a single row, rather than
    /// relying on any vertical tiling.
    ///
    /// Each figure is a true parallelogram: bottom edge [x, x+stripeWidth], top edge shifted right
    /// by one full pitch, so both slanted sides share the same slope and the horizontal
    /// cross-section is a constant stripeWidth at every height. Note the closing side's slope is
    /// (pitch - stripeWidth) over the height while the drawn side's is stripeWidth over the height -
    /// these only match when stripeWidth is exactly half the pitch, so changing the stripe/gap ratio
    /// requires reshaping the figure, not just widening stripeWidth (tried 2026-07-19 with 0.8x
    /// pitch: the shapes warped into near-vertical trapezoids and lost their slant entirely).
    /// </summary>
    private static StreamGeometry BuildStripeGeometry()
    {
        const double stripeWidth = ProgressStripeTilePitch / 2;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            for (double x = ProgressStripeGeometryLeft; x <= ProgressStripeMaxWidth; x += ProgressStripeTilePitch)
            {
                ctx.BeginFigure(new Point(x, ProgressStripeGeometryHeight), true, true);
                ctx.LineTo(new Point(x + stripeWidth, ProgressStripeGeometryHeight), false, false);
                ctx.LineTo(new Point(x + ProgressStripeTilePitch, 0), false, false);
                ctx.LineTo(new Point(x + stripeWidth, 0), false, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private void PauseProgressBarStripeAnimation() => _progressBarStripeClock?.Controller?.Pause();

    private void ResumeProgressBarStripeAnimation() => _progressBarStripeClock?.Controller?.Resume();

    /// <summary>
    /// Fades out the last ~20px (one tile pitch) of the currently-revealed stripe region. The clip
    /// that reveals the stripe pattern sweeps left-to-right with a straight vertical edge, but the
    /// stripes themselves are diagonal - the newest stripe sitting right at that boundary only has a
    /// thin partial cross-section visible until the sweep has moved far enough past it, which read as
    /// the pattern "pooling" before resolving into a clean stripe (reported 2026-07-19). Every stripe
    /// further back is already fully revealed and unaffected - only the one currently transitioning
    /// needs hiding until it's swept far enough into view to look complete.
    ///
    /// There's no equivalent fade at the fixed left border - fading opacity can only ever remove
    /// coverage, never add it, so it can't help the periodic gap that lands there as the pattern
    /// scrolls past a fixed clip edge (a moving periodic pattern behind a fixed window must cycle
    /// through every phase, confirmed via screenshots 2026-07-19). That's addressed instead in
    /// BuildStripeGeometry by narrowing the gap relative to the stripe width.
    /// </summary>
    private void UpdateProgressBarStripeFadeMask(double fillWidthPx)
    {
        if (_progressBarStripeOverlay is null)
        {
            return;
        }

        const double fadeWidth = ProgressStripeTilePitch;
        _progressBarStripeOverlay.OpacityMask = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(Math.Max(0, fillWidthPx - fadeWidth), 0),
            EndPoint = new Point(fillWidthPx, 0),
            GradientStops =
            {
                new GradientStop(Colors.White, 0),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
            },
        };
    }

    // Reused by both the client download flow above and the launcher self-update download
    // (CheckForLauncherUpdateAsync) - writes to ProgressStatusText (attached directly to
    // MainProgressBar) rather than a toast, since this fires continuously throughout one operation
    // rather than representing a single event.
    private void OnInstallProgress(InstallProgress p)
    {
        MainProgressBar.IsIndeterminate = false;
        double pct = p.Total > 0 ? (double)p.Downloaded / p.Total * 100 : 0;

        // Animated rather than a direct Value set - snapping straight to each raw byte-count
        // percentage made the visible fill jump/jitter with every download-speed spike (reported
        // 2026-07-19). BeginAnimation's default HandoffBehavior (SnapshotAndReplace) smoothly
        // redirects from wherever the fill currently is toward each new target, even when updates
        // arrive faster than this animation's own duration, rather than restarting from 0 each time.
        var valueAnimation = new DoubleAnimation(pct, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        MainProgressBar.BeginAnimation(RangeBase.ValueProperty, valueAnimation);
        UpdateProgressBarStripeFadeMask(MainProgressBar.ActualWidth * pct / 100);

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
            ShowToast("Launcher", "WoW is already running from this game folder — close it first.", ToastSeverity.Warning);
            return;
        }

        // The Install-state Play button is already force-disabled without a client identifier (see
        // UpdatePlayButtonEnabled), but an already-installed directory whose assigned identifier was
        // since deleted from the Client Identifiers table isn't caught by that gate (it's in Play/
        // Update state, not Install) - covered here instead of a modal, matching the same inline
        // warning already shown in Profile settings.
        // CurrentClientIdentifierId(), not _dirSettings.Current.ClientProfileId - same staleness gap as
        // BuildPatchList (see its own comment): this field only updates on the debounced save tick, so
        // reading it here right after picking an identifier could incorrectly block a legitimate Play.
        if (ResolveClientProfile(CurrentClientIdentifierId()) is null)
        {
            ShowToast("Launcher", "No client identifier assigned to this profile — pick one in Profile settings.", ToastSeverity.Warning);
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
                ShowToast("Launcher", "Launch cancelled — resolve the Signature Removal / MPQ patch conflict first.", ToastSeverity.Warning);
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
            // CurrentWowDir() reads GameFolderBox.Text, a UI element - must be resolved on this
            // (the UI) thread before handing off to Task.Run, not called from inside the background
            // lambda (which would throw "the calling thread cannot access this object").
            string wowDirForCacheClear = CurrentWowDir();

            // Off the calling thread - WDB can accumulate hundreds to low-thousands of small files
            // on a long-lived install, and this runs on every single Play click when the toggle is
            // on, directly on the UI thread otherwise.
            await Task.Run(() => _gameCache.Clear(wowDirForCacheClear));
        }

        SetLaunchOperationBusy(true);
        var progress = new Progress<string>(msg => ShowToast("Launcher", msg, ToastSeverity.Info));
        try
        {
            PlayResult result = await _orchestrator.PlayAsync(progress, OnGameProcessLaunched);
            ShowToast("Launcher", result.Success ? "Launched." : "Launch failed — see the Log tab.", result.Success ? ToastSeverity.Success : ToastSeverity.Error);
        }
        catch (Exception ex)
        {
            _log.Error("Unexpected error during launch.", ex);
            ShowToast("Launcher", $"Launch failed: {ex.Message}", ToastSeverity.Error);
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
    private static readonly TimeSpan ToastAutoDismissDelay = TimeSpan.FromSeconds(10);

    // Once a card has been hovered at least once, every dismiss countdown after that point (i.e.
    // from when the mouse leaves) is capped at this shorter delay instead of the full
    // ToastAutoDismissDelay - hovering pauses the countdown entirely while the mouse is over it (so
    // it never disappears mid-read), but shouldn't let a toast linger indefinitely once you've
    // actually had a look at it.
    private static readonly TimeSpan ToastHoverDismissDelay = TimeSpan.FromSeconds(5);

    private Brush SeverityBrush(ToastSeverity severity) => (Brush)FindResource(severity switch
    {
        ToastSeverity.Success => "AddActionBrush",
        ToastSeverity.Warning => "UpdateActionBrush",
        ToastSeverity.Error => "RemoveActionBrush",
        _ => "InstallActionBrush",
    });

    // Segoe Fluent Icons glyphs: checkmark-circle, info-circle, warning triangle, error-circle.
    private static string SeverityIconGlyph(ToastSeverity severity) => severity switch
    {
        ToastSeverity.Success => "",
        ToastSeverity.Warning => "",
        ToastSeverity.Error => "",
        _ => "",
    };

    private static void FadeTo(UIElement element, double to, Action? onCompleted = null)
    {
        var animation = new DoubleAnimation(to, ToastFadeDuration);
        if (onCompleted is not null)
        {
            animation.Completed += (_, _) => onCompleted();
        }

        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    // ---- Unified toast stack: up to 10 cards floating top-right, above every tab. Every call site
    // passes its own fixed source label explicitly (never derived from whichever tab happens to be
    // selected) - a card's title can never end up wrong just because the user switched tabs while an
    // async operation was still in flight, and cards no longer need clearing on tab switch either. ----

    private const int MaxToastCards = 10;

    // Maps a toast's source label to the nav tab it should jump to on click - matches the
    // RadioButton Tag order in MainWindow.xaml. "Launcher" has no entry: whole-launcher messages
    // aren't scoped to any single tab, so they aren't clickable.
    private static readonly Dictionary<string, int> ToastSourceTabIndex = new()
    {
        ["Home"] = 0,
        ["Tweaks"] = 1,
        ["DLLs"] = 2,
        ["MPQ Patches"] = 3,
        ["Addons"] = 4,
        ["Settings"] = 5,
        ["Log"] = 6,
    };

    private sealed class ToastCard
    {
        public required Border Border { get; init; }
        public required TextBlock MessageText { get; init; }
        public required TextBlock Icon { get; init; }
        public required ToastSeverity Severity { get; set; }
        public bool HasBeenHovered { get; set; }
        public string? UpdateKey { get; init; }
        public DispatcherTimer? Timer { get; set; }
    }

    private readonly List<ToastCard> _toastCards = new();

    /// <summary>
    /// updateKey lets a rapidly-repeating operation (e.g. "Loading catalog… (N so far)" while
    /// paging Warperia) update one card in place instead of flooding the toast stack with
    /// near-duplicate entries - omit it for the common case of one distinct, finished event.
    /// </summary>
    private void ShowToast(string source, string message, ToastSeverity severity, string? updateKey = null)
    {
        if (updateKey is not null)
        {
            ToastCard? existing = _toastCards.FirstOrDefault(c => c.UpdateKey == updateKey);
            if (existing is not null)
            {
                existing.MessageText.Text = message;
                existing.Icon.Text = SeverityIconGlyph(severity);
                existing.Icon.Foreground = SeverityBrush(severity);
                existing.Border.BorderBrush = SeverityBrush(severity);
                existing.Severity = severity;
                RestartToastTimer(existing, severity);
                return;
            }
        }

        if (_toastCards.Count >= MaxToastCards)
        {
            // Prefer evicting the oldest Info/Success card - Warning/Error are supposed to persist
            // until manually dismissed, so the cap shouldn't silently break that guarantee under
            // normal load. Only fall back to strict oldest-first if every slot is Warning/Error.
            ToastCard toEvict = _toastCards.FirstOrDefault(c => c.Severity is ToastSeverity.Info or ToastSeverity.Success)
                ?? _toastCards[0];
            RemoveToastCard(toEvict);
        }

        ToastCard card = CreateToastCard(source, message, severity, updateKey);
        _toastCards.Add(card);
        ToastHost.Children.Add(card.Border);
        FadeTo(card.Border, 1);
        RestartToastTimer(card, severity);
    }

    private ToastCard CreateToastCard(string source, string message, ToastSeverity severity, string? updateKey)
    {
        var titleText = new TextBlock
        {
            Text = $"{source}:",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = (Brush)new BrushConverter().ConvertFromString("#E6C067")!,
            Margin = new Thickness(0, 0, 0, 2),
        };

        var messageText = new TextBlock
        {
            Text = message,
            Style = (Style)FindResource("CaptionText"),
            TextWrapping = TextWrapping.Wrap,
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
            VerticalAlignment = VerticalAlignment.Top,
        };
        DockPanel.SetDock(closeButton, Dock.Right);

        var icon = new TextBlock
        {
            Text = SeverityIconGlyph(severity),
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 20,
            Foreground = SeverityBrush(severity),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            // Segoe Fluent Icons glyphs sit in the upper part of their line box, so centering the
            // TextBlock itself (which centers the full line box, not the glyph's visible ink) reads
            // as slightly-too-high - this nudges it down to compensate.
            Margin = new Thickness(0, 3, 10, 0),
        };

        var textStack = new StackPanel();
        textStack.Children.Add(titleText);
        textStack.Children.Add(messageText);

        var textArea = new DockPanel();
        textArea.Children.Add(closeButton);
        textArea.Children.Add(textStack);

        // Two columns: icon sized to itself (centered both ways within that column), text area takes
        // the rest - both driven by the same Auto row height as the rest of the card, so the icon
        // stays vertically centered regardless of how many lines the message wraps to.
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(textArea, 1);
        layout.Children.Add(icon);
        layout.Children.Add(textArea);

        var border = new Border
        {
            Width = 340,
            Background = (Brush)new BrushConverter().ConvertFromString("#1B1B1F")!,
            BorderBrush = SeverityBrush(severity),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 0, 0),
            Opacity = 0,
            Child = layout,
        };

        var card = new ToastCard
        {
            Border = border,
            MessageText = messageText,
            Icon = icon,
            Severity = severity,
            UpdateKey = updateKey,
        };

        closeButton.Click += (_, _) => RemoveToastCard(card);

        // Hovering pauses the dismiss countdown entirely (so it never vanishes mid-read); once the
        // mouse leaves, every future countdown for this card is capped at ToastHoverDismissDelay
        // instead of the full ToastAutoDismissDelay - use card.Severity, not the closure-captured
        // severity parameter, since ShowToast's updateKey merge path can change a card's severity
        // after creation and this must always reflect its current one.
        border.MouseEnter += (_, _) =>
        {
            card.HasBeenHovered = true;
            card.Timer?.Stop();
        };
        border.MouseLeave += (_, _) => RestartToastTimer(card, card.Severity);

        // Click-to-view-source: jump to the tab this toast came from, for any source that actually
        // maps to one ("Launcher" messages aren't scoped to a single tab, so they stay non-clickable).
        // Guarded against the close button specifically so dismissing a toast never also navigates.
        if (ToastSourceTabIndex.TryGetValue(source, out int tabIndex))
        {
            border.Cursor = System.Windows.Input.Cursors.Hand;
            border.MouseLeftButtonUp += (_, e) =>
            {
                if (ReferenceEquals(e.OriginalSource, closeButton))
                {
                    return;
                }

                MainTabs.SelectedIndex = tabIndex;
            };
        }

        return card;
    }

    private void RestartToastTimer(ToastCard card, ToastSeverity severity)
    {
        card.Timer?.Stop();
        card.Timer = null;

        if (severity is not (ToastSeverity.Info or ToastSeverity.Success))
        {
            return;
        }

        card.Timer = new DispatcherTimer { Interval = card.HasBeenHovered ? ToastHoverDismissDelay : ToastAutoDismissDelay };
        card.Timer.Tick += (_, _) =>
        {
            card.Timer!.Stop();
            RemoveToastCard(card);
        };
        card.Timer.Start();
    }

    private void RemoveToastCard(ToastCard card)
    {
        card.Timer?.Stop();
        _toastCards.Remove(card);
        FadeTo(card.Border, 0, () => ToastHost.Children.Remove(card.Border));
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
        ShowToast("Log", "Log view cleared.", ToastSeverity.Success);
    }

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        string text = string.Join(
            Environment.NewLine,
            _logItems.Select(entry => $"{entry.Timestamp:HH:mm:ss} [{entry.LevelDisplay}] {entry.Message}"));
        try
        {
            Clipboard.SetText(text);
            ShowToast("Log", "Log copied to clipboard.", ToastSeverity.Success);
        }
        catch
        {
            // clipboard can be transiently locked by another app
            ShowToast("Log", "Could not copy the log - clipboard may be in use by another app.", ToastSeverity.Error);
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
