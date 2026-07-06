using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TeronWoWLauncher.Models;
using TeronWoWLauncher.Services;

namespace TeronWoWLauncher;

public partial class MainWindow : Window
{
    private readonly Logger _log = Logger.Instance;
    private readonly SettingsService _settings = new();
    private readonly LaunchOrchestrator _orchestrator;
    private readonly DllListService _dlls = new();
    private readonly MpqPatchService _mpq = new();
    private readonly RealmlistService _realmlist = new();
    private readonly GameInstallService _install = new();
    private CancellationTokenSource? _installCts;
    private readonly AddonLibrary _addons = new();
    private readonly ObservableCollection<InstalledAddon> _addonItems = new();
    private bool _addonBusy;

    private readonly List<PatchControl> _patchControls = new();
    private readonly ObservableCollection<string> _dllItems = new();
    private readonly DispatcherTimer _patchApplyTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _loading;
    private bool _applyingPatches;

    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x92));

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

        _settings.Load();
        _orchestrator = new LaunchOrchestrator(_settings);
        _log.MessageLogged += OnMessageLogged;
        _patchApplyTimer.Tick += OnPatchApplyTick;

        DllList.ItemsSource = _dllItems;

        _loading = true;
        BuildPatchList();
        LoadSettingsIntoUi();
        RefreshDllList();
        RefreshMpqList();
        InitInstallTab();
        InitAddonsTab();
        _loading = false;

        UpdateStatus("Ready.");
        _log.Info("Launcher UI initialized.");
    }

    // ---------------- Patches tab ----------------

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
                double value = _settings.Current.PatchParameters.TryGetValue(patch.Id, out double stored)
                    ? stored
                    : p.Default;
                value = Math.Clamp(value, p.Min, p.Max);

                slider = new Slider
                {
                    Minimum = p.Min,
                    Maximum = p.Max,
                    Value = value,
                    Width = 260,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsSnapToTickEnabled = p.IsInteger,
                    TickFrequency = p.IsInteger ? 1 : 0.01,
                    IsEnabled = isChecked,
                };

                var valueLabel = new TextBlock
                {
                    Width = 70,
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                void UpdateValueLabel() => valueLabel.Text = FormatValue(slider.Value, p);
                slider.ValueChanged += (_, _) =>
                {
                    UpdateValueLabel();
                    SchedulePatchApply();
                };
                UpdateValueLabel();

                Slider capturedSlider = slider;
                box.Checked += (_, _) => capturedSlider.IsEnabled = true;
                box.Unchecked += (_, _) => capturedSlider.IsEnabled = false;

                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 0, 0, 8) };
                row.Children.Add(new TextBlock
                {
                    Text = $"{p.Label}:",
                    Width = 150,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                row.Children.Add(slider);
                row.Children.Add(valueLabel);
                PatchesPanel.Children.Add(row);
            }

            _patchControls.Add(new PatchControl { Def = patch, Box = box, Slider = slider });
        }

        if (catalog.Count == 0)
        {
            PatchesPanel.Children.Add(new TextBlock { Text = "No executable patches available yet.", Foreground = MutedBrush });
        }
    }

    private static string FormatValue(double value, PatchParameter p)
    {
        string unit = string.IsNullOrEmpty(p.Unit) ? string.Empty : " " + p.Unit;
        return p.IsInteger
            ? ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture) + unit
            : value.ToString("0.###", CultureInfo.InvariantCulture) + unit;
    }

    // Apply the current patch selection to WoW.exe shortly after a change (debounced so a slider
    // drag coalesces into a single rebuild). Never applies during initial UI load.
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
        _dllItems.Clear();
        foreach (string name in _dlls.ReadActiveNames(CurrentWowDir()))
        {
            _dllItems.Add(name);
        }
    }

    private void SaveDllList() => _dlls.WriteActiveNames(CurrentWowDir(), new List<string>(_dllItems));

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
        // Store just the file name when it lives in the game folder; otherwise the full path.
        string parent = Path.GetDirectoryName(chosen) ?? string.Empty;
        string entry = string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar),
            wowDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(chosen)
            : chosen;

        if (!_dllItems.Contains(entry))
        {
            _dllItems.Add(entry);
            SaveDllList();
        }
    }

    private void OnRemoveDll(object sender, RoutedEventArgs e)
    {
        if (DllList.SelectedItem is string item)
        {
            _dllItems.Remove(item);
            SaveDllList();
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
                Margin = new Thickness(0, 6, 0, 0),
            });
            return;
        }

        foreach (MpqPatch patch in patches)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };

            var remove = new Button { Content = "Remove", Padding = new Thickness(8, 3, 8, 3) };
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

    // ---------------- Addons tab ----------------

    private void InitAddonsTab()
    {
        _addons.Load();
        AddonList.ItemsSource = _addonItems;
        RefreshAddonList();
    }

    private void RefreshAddonList()
    {
        _addonItems.Clear();
        foreach (InstalledAddon addon in _addons.Addons)
        {
            _addonItems.Add(addon);
        }
    }

    private async void OnAddAddonFromUrl(object sender, RoutedEventArgs e)
    {
        string input = AddonUrlBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            AddonStatusText.Text = "Enter a GitHub repo or .zip URL.";
            return;
        }

        await AddAddonAsync(input);
    }

    private async void OnAddAddonFromFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select an addon archive",
            Filter = "Addon archives (*.zip;*.rar;*.7z)|*.zip;*.rar;*.7z|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true)
        {
            await AddAddonAsync(dialog.FileName);
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
            AddonUrlBox.Text = string.Empty;
            RefreshAddonList();
            AddonStatusText.Text = $"Installed {addon.Name}.";
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

    private void OnRemoveAddon(object sender, RoutedEventArgs e)
    {
        if (AddonList.SelectedItem is InstalledAddon addon)
        {
            _addons.Remove(addon, CurrentWowDir());
            RefreshAddonList();
            AddonStatusText.Text = $"Removed {addon.Name}.";
        }
    }

    private async void OnUpdateAddon(object sender, RoutedEventArgs e)
    {
        // Re-fetching from the same source reinstalls the latest and updates the tracked entry in place.
        if (AddonList.SelectedItem is InstalledAddon addon && !string.IsNullOrWhiteSpace(addon.SourceRef))
        {
            await AddAddonAsync(addon.SourceRef);
        }
    }

    private void OnRefreshAddons(object sender, RoutedEventArgs e)
    {
        _addons.Load();
        RefreshAddonList();
    }

    // ---------------- Login & Game tab ----------------

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
        UpdateGameFolderHint();
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

    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        CollectSettingsFromUi();
        _settings.Save();

        // Write realmlist.wtf immediately on save (not on every launch).
        string wowDir = CurrentWowDir();
        if (!string.IsNullOrWhiteSpace(_settings.Current.Realmlist) && Directory.Exists(wowDir))
        {
            try
            {
                _realmlist.Write(wowDir, _settings.Current.Realmlist);
            }
            catch (Exception ex)
            {
                _log.Error("Failed to write realmlist.wtf.", ex);
            }
        }

        UpdateGameFolderHint();
        RefreshDllList();
        RefreshMpqList();
        UpdateStatus("Settings saved.");
    }

    private void OnBrowseGameFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the World of Warcraft folder (contains WoW.exe)" };
        if (!string.IsNullOrWhiteSpace(GameFolderBox.Text) && Directory.Exists(GameFolderBox.Text))
        {
            dialog.InitialDirectory = GameFolderBox.Text;
        }

        if (dialog.ShowDialog() == true)
        {
            GameFolderBox.Text = dialog.FolderName;
            UpdateGameFolderHint();
            RefreshDllList();
            RefreshMpqList();
        }
    }

    private void UpdateGameFolderHint()
    {
        string dir = CurrentWowDir();
        string exe = Path.Combine(dir, "WoW.exe");
        GameFolderHint.Text = File.Exists(exe) ? $"✓ WoW.exe found in {dir}" : $"⚠ WoW.exe not found in {dir}";
    }

    private string CurrentWowDir()
        => !string.IsNullOrWhiteSpace(GameFolderBox.Text) && Directory.Exists(GameFolderBox.Text)
            ? GameFolderBox.Text.Trim()
            : _settings.ResolveWowDirectory();

    // ---------------- Install / Update tab ----------------

    private void InitInstallTab()
    {
        InstallFolderBox.Text = CurrentWowDir();
        ClientUrlBox.Text = string.IsNullOrWhiteSpace(_settings.Current.ClientDownloadUrl)
            ? GameInstallService.DefaultClientUrl
            : _settings.Current.ClientDownloadUrl;
        UpdateInstallStatus();
    }

    private void UpdateInstallStatus()
    {
        string dir = InstallFolderBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dir))
        {
            InstallStatusText.Text = "Choose an install folder.";
            return;
        }

        string? version = _install.GetClientVersion(dir);
        InstallStatusText.Text = version is null
            ? "No client installed here yet."
            : version == GameInstallService.ExpectedVersion
                ? $"✓ Client installed: {version} (up to date)."
                : $"⚠ Client version {version} found (expected {GameInstallService.ExpectedVersion}).";
    }

    private void OnBrowseInstallFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select where to install the client" };
        if (!string.IsNullOrWhiteSpace(InstallFolderBox.Text) && Directory.Exists(InstallFolderBox.Text))
        {
            dialog.InitialDirectory = InstallFolderBox.Text;
        }

        if (dialog.ShowDialog() == true)
        {
            InstallFolderBox.Text = dialog.FolderName;
            UpdateInstallStatus();
        }
    }

    private async void OnInstallClicked(object sender, RoutedEventArgs e)
    {
        string dir = InstallFolderBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dir))
        {
            UpdateStatus("Choose an install folder first.");
            return;
        }

        string url = string.IsNullOrWhiteSpace(ClientUrlBox.Text)
            ? GameInstallService.DefaultClientUrl
            : ClientUrlBox.Text.Trim();

        // Persist a non-default URL so it survives restarts.
        _settings.Current.ClientDownloadUrl = url == GameInstallService.DefaultClientUrl ? null : url;
        _settings.Save();

        InstallButton.IsEnabled = false;
        CancelInstallButton.IsEnabled = true;
        _installCts = new CancellationTokenSource();
        var progress = new Progress<InstallProgress>(OnInstallProgress);

        try
        {
            _log.Info($"Installing client from {url} to {dir}");
            await _install.DownloadAndInstallAsync(url, dir, progress, _installCts.Token);

            // The install folder is now the game home.
            GameFolderBox.Text = dir;
            _settings.Current.WowDirectory = dir;
            _settings.Save();
            UpdateGameFolderHint();
            RefreshDllList();
            RefreshMpqList();
            UpdateInstallStatus();
            InstallProgressBar.IsIndeterminate = false;
            InstallProgressBar.Value = 100;
            UpdateStatus("Client installed.");
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("Download cancelled.");
        }
        catch (Exception ex)
        {
            _log.Error("Client install failed.", ex);
            UpdateStatus("Install failed — see the Log tab.");
        }
        finally
        {
            InstallProgressBar.IsIndeterminate = false;
            InstallButton.IsEnabled = true;
            CancelInstallButton.IsEnabled = false;
            _installCts?.Dispose();
            _installCts = null;
        }
    }

    private void OnCancelInstall(object sender, RoutedEventArgs e) => _installCts?.Cancel();

    private void OnInstallProgress(InstallProgress p)
    {
        if (p.Extracting)
        {
            InstallProgressBar.IsIndeterminate = true;
            UpdateStatus("Extracting client...");
            return;
        }

        InstallProgressBar.IsIndeterminate = false;
        double pct = p.Total > 0 ? (double)p.Downloaded / p.Total * 100 : 0;
        InstallProgressBar.Value = pct;
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

    // ---------------- Play ----------------

    private async void OnPlayClicked(object sender, RoutedEventArgs e)
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

    private void UpdateStatus(string message) => StatusText.Text = message;

    private void OnMessageLogged(object? sender, LogEntry entry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText($"{entry.Timestamp:HH:mm:ss} [{entry.Level}] {entry.Message}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        });
    }
}
