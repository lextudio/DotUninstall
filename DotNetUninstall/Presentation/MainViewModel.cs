using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotNetUninstall.Models;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using NuGet.Versioning;
using Uno.Extensions.Navigation;
using Windows.Foundation.Metadata;
using Mono.Unix.Native; // Mono.Posix for elevation detection

namespace DotNetUninstall.Presentation;

public partial class MainViewModel : ObservableObject
{
    private readonly INavigator _navigator;

    public ObservableCollection<DotnetInstallEntry> SdkItems { get; } = new();
    public ObservableCollection<DotnetInstallEntry> RuntimeItems { get; } = new();
    public ObservableCollection<ChannelGroup> GroupedSdkItems { get; } = new();
    public ObservableCollection<ChannelGroup> GroupedRuntimeItems { get; } = new();
    public int SdkCount => SdkItems.Count;
    public int RuntimeCount => RuntimeItems.Count;
    public int TotalCount => SdkItems.Count + RuntimeItems.Count;

    // Elevation (sudo/root) detection on macOS
    [ObservableProperty]
    private bool isElevated; // True when effective UID == 0 (macOS/Linux)

    [ObservableProperty]
    private string? originalUser; // SUDO_USER or current user when elevated

    [ObservableProperty]
    private bool showElevationWarning; // Controls visibility of the banner

    [ObservableProperty]
    private bool showElevationOffer; // Shown when NOT elevated on macOS

    public bool CanPerformUninstalls
        => !OperatingSystem.IsMacOS() || IsElevated; // On macOS require elevation

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private string? errorMessage;

    // Legacy properties retained for minimal XAML changes but now constant
    [ObservableProperty]
    private bool hasUninstallTool = true; // Always true: using embedded logic

    [ObservableProperty]
    private string? uninstallToolPath; // Unused

    [ObservableProperty]
    private string? uninstallToolVersion; // Unused

    [ObservableProperty]
    private string? suggestedDownload; // Unused

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand<DotnetInstallEntry> UninstallCommand { get; }
    public IAsyncRelayCommand BrowseCommand { get; }

    public string Title { get; }

    public MainViewModel(IStringLocalizer localizer, IOptions<AppConfig> appInfo, INavigator navigator)
    {
        _navigator = navigator;
        Title = $".NET Uninstall Tool UI - {localizer["ApplicationName"]} {appInfo?.Value?.Environment}";
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        UninstallCommand = new AsyncRelayCommand<DotnetInstallEntry>(UninstallAsync, _ => HasUninstallTool);
        BrowseCommand = new AsyncRelayCommand(BrowseAsync);
        SdkItems.CollectionChanged += (_, __) =>
        {
            OnPropertyChanged(nameof(SdkCount));
            OnPropertyChanged(nameof(TotalCount));
        };
        RuntimeItems.CollectionChanged += (_, __) =>
        {
            OnPropertyChanged(nameof(RuntimeCount));
            OnPropertyChanged(nameof(TotalCount));
        };
        // External tool path no longer required.
        DetectElevation();
    }

    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = "Refreshing...";
        SdkItems.Clear();
        RuntimeItems.Clear();
        try
        {
            await ListFromEmbeddedAsync();
            BuildGroups();
            StatusMessage = $"Loaded {TotalCount} entries.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Failed.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private Task DetectToolAsync() => Task.CompletedTask; // No-op retained for compatibility

    private string BuildSuggestedDownload()
    {
        // Tailored to release 1.7.618124 assets (5 assets total: 3 binaries + 2 source archives)
        // Binaries present: Windows MSI, macOS x64 tar.gz, macOS arm64 tar.gz
        // Not present: Linux binaries
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return "Recommended: dotnet-core-uninstall.msi";
            }
            if (OperatingSystem.IsMacOS())
            {
                var arch = RuntimeInformation.ProcessArchitecture;
                return arch == Architecture.Arm64
                    ? "Recommended: dotnet-core-uninstall-macos-arm64.tar.gz"
                    : "Recommended: dotnet-core-uninstall-macos-x64.tar.gz";
            }
            // Linux or other OS
            return "No prebuilt binary for this OS in recent releases like 1.7.618124; see release page for updates or remove manually.";
        }
        catch
        {
            return "See release assets for the correct file name.";
        }
    }

    private async Task ListFromEmbeddedAsync()
    {
        var parsed = new List<DotnetInstallEntry>();
        try
        {
            var list = DotNetUninstall.Tooling.BundleListing.List();
            foreach (var e in list)
            {
                parsed.Add(new DotnetInstallEntry(e.Type, e.Type, e.Version, e.Architecture, e.CanUninstall, e.Reason)
                {
                    UninstallCommand = e.UninstallCommand,
                    DisplayName = e.DisplayName,
                    SubType = e.SubType
                });
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        // Prepare metadata before adding to observable collections
        try
        {
            var neededChannels = new HashSet<string>(parsed.Select(p => DeriveChannel(p.Version)), StringComparer.OrdinalIgnoreCase);
            if (neededChannels.Count > 0)
            {
                await EnsureMetadataAsync(neededChannels);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = (StatusMessage ?? "") + $" (Metadata fetch failed: {ex.Message})";
        }

        int sdkCount = 0, rtCount = 0;
        foreach (var baseEntry in parsed)
        {
            DotnetInstallEntry finalEntry = baseEntry;
            try
            {
                var channel = DeriveChannel(baseEntry.Version);
                ChannelResolved? meta = null;
                if (_channelCache != null)
                {
                    _channelCache.TryGetValue(channel, out meta);
                }
                var (previewKind, previewNum) = DerivePreviewInfo(baseEntry.Version);
                bool isPreview = previewKind != "ga";
                bool outOfSupport = meta != null && (meta.SupportPhase == "eol" || (meta.EolDate.HasValue && meta.EolDate.Value < DateTime.UtcNow.Date));
                bool isSecurity = meta != null && ((baseEntry.Type == "sdk" && meta.SecurityVersions.Contains(baseEntry.Version)) || (baseEntry.Type == "runtime" && meta.SecurityVersions.Contains(baseEntry.Version)));
                finalEntry = baseEntry with
                {
                    Channel = channel,
                    ReleaseType = meta?.ReleaseType?.ToUpperInvariant(),
                    SupportPhase = meta?.SupportPhase,
                    IsOutOfSupport = outOfSupport,
                    PreviewKind = previewKind,
                    PreviewNumber = previewNum,
                    IsPreview = isPreview,
                    IsSecurityUpdate = isSecurity,
                    EolDate = meta?.EolDate
                };
            }
            catch { }

            if (finalEntry.Type == "sdk")
            {
                SdkItems.Add(finalEntry);
                sdkCount++;
            }
            else
            {
                RuntimeItems.Add(finalEntry);
                rtCount++;
            }
        }
        StatusMessage = $"SDKs: {sdkCount}, Runtimes: {rtCount}";
    }

    private static readonly Uri ReleaseMetadataIndex = new("https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json");

    // Real schema (subset) for releases-index.json
    private sealed class ReleasesIndexRoot { public List<ChannelInfo>? ReleasesIndex { get; set; } }
    private sealed class ChannelInfo
    {
        public string? ChannelVersion { get; set; }
        public string? ReleaseType { get; set; }      // lts | sts
        public string? SupportPhase { get; set; }     // preview | go-live | active | maintenance | eol
        public DateTime? EolDate { get; set; }
        public string? ReleasesJson { get; set; }
    }
    // Per-channel releases.json subset
    private sealed class ChannelReleases { public List<ChannelRelease>? Releases { get; set; } }
    private sealed class ChannelRelease
    {
        public string? ReleaseVersion { get; set; }
        public bool? Security { get; set; }
        public SdkRelease? Sdk { get; set; }
        public List<SdkRelease>? Sdks { get; set; }
        public RuntimeRelease? Runtime { get; set; }
    }
    private sealed class SdkRelease { public string? Version { get; set; } }
    private sealed class RuntimeRelease { public string? Version { get; set; } }

    // Simple in-memory cache (lifetime of process)
    private static DateTime _metaCacheTime = DateTime.MinValue;
    private static Dictionary<string, ChannelResolved>? _channelCache; // key = channel
    private sealed class ChannelResolved
    {
        public string? ReleaseType { get; set; }
        public string? SupportPhase { get; set; }
        public DateTime? EolDate { get; set; }
        public HashSet<string> SdkVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RuntimeVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SecurityVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static string DeriveChannel(string version)
    {
        if (NuGet.Versioning.NuGetVersion.TryParse(version, out var nv))
        {
            return nv.Major + "." + nv.Minor;
        }
        // Fallback to previous heuristic
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _)) return parts[0] + "." + parts[1];
        if (parts.Length >= 1) return parts[0];
        return version;
    }

    private static (string kind, int? number) DerivePreviewInfo(string version)
    {
        if (NuGet.Versioning.NuGetVersion.TryParse(version, out var nv))
        {
            if (!nv.IsPrerelease) return ("ga", null);
            // nv.ReleaseLabels is IEnumerable<string>
            var labels = nv.ReleaseLabels?.ToArray() ?? Array.Empty<string>();
            if (labels.Length == 0) return ("ga", null);
            var first = labels[0].ToLowerInvariant();
            if (first == "preview" || first == "rc")
            {
                int? num = null;
                if (labels.Length > 1 && int.TryParse(labels[1], out var n)) num = n;
                return (first, num);
            }
            return ("ga", null);
        }
        // Fallback regex (should rarely be needed once parsing succeeds)
        var v = version.ToLowerInvariant();
        var m = System.Text.RegularExpressions.Regex.Match(v, "-(preview|rc)(?:\\.(?<n>[0-9]+))?");
        if (!m.Success) return ("ga", null);
        int? num2 = null;
        if (m.Groups["n"].Success && int.TryParse(m.Groups["n"].Value, out var parsed2)) num2 = parsed2;
        return (m.Groups[1].Value, num2);
    }

    private async Task EnsureMetadataAsync(HashSet<string> neededChannels)
    {
        // Refresh cache every 12 hours
        if (_channelCache != null && (DateTime.UtcNow - _metaCacheTime) < TimeSpan.FromHours(12))
        {
            // If cache already contains all needed channels, we are done
            if (neededChannels.All(c => _channelCache.ContainsKey(c))) return;
        }

        _channelCache ??= new();
        using var http = new HttpClient();
        ReleasesIndexRoot? indexRoot;
        using (var s = await http.GetStreamAsync(ReleaseMetadataIndex))
        {
            indexRoot = await JsonSerializer.DeserializeAsync<ReleasesIndexRoot>(s, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        if (indexRoot?.ReleasesIndex == null) return;

        var indexLookup = indexRoot.ReleasesIndex
            .Where(c => !string.IsNullOrWhiteSpace(c.ChannelVersion))
            .ToDictionary(c => c.ChannelVersion!, StringComparer.OrdinalIgnoreCase);

        foreach (var ch in neededChannels)
        {
            if (_channelCache.ContainsKey(ch)) continue; // already cached
            if (!indexLookup.TryGetValue(ch, out var ci) || string.IsNullOrWhiteSpace(ci.ReleasesJson)) continue;
            try
            {
                using var rs = await http.GetStreamAsync(ci.ReleasesJson);
                var rels = await JsonSerializer.DeserializeAsync<ChannelReleases>(rs, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                var resolved = new ChannelResolved
                {
                    ReleaseType = ci.ReleaseType?.ToLowerInvariant(),
                    SupportPhase = ci.SupportPhase?.ToLowerInvariant(),
                    EolDate = ci.EolDate?.Date
                };
                if (rels?.Releases != null)
                {
                    foreach (var r in rels.Releases)
                    {
                        bool sec = r.Security == true;
                        void AddSdk(string? v) { if (!string.IsNullOrWhiteSpace(v)) { resolved.SdkVersions.Add(v); if (sec) resolved.SecurityVersions.Add(v); } }
                        void AddRuntime(string? v) { if (!string.IsNullOrWhiteSpace(v)) { resolved.RuntimeVersions.Add(v); if (sec) resolved.SecurityVersions.Add(v); } }
                        if (r.Sdk?.Version != null) AddSdk(r.Sdk.Version);
                        if (r.Sdks != null) foreach (var srel in r.Sdks) AddSdk(srel.Version);
                        if (r.Runtime?.Version != null) AddRuntime(r.Runtime.Version);
                    }
                }
                _channelCache[ch] = resolved;
            }
            catch { /* ignore per-channel errors */ }
        }
        _metaCacheTime = DateTime.UtcNow;
    }

    // TagEntriesAsync removed; tagging now performed during initial list parsing for better Uno binding behavior.

    private async Task UninstallAsync(DotnetInstallEntry? entry)
    {
        if (entry == null) return;
        // Internal logic always available
        ErrorMessage = null;
        StatusMessage = $"Uninstalling {entry.Type} {entry.Version}...";
        try
        {
            if (string.IsNullOrWhiteSpace(entry.UninstallCommand)) { ErrorMessage = "Missing uninstall command."; return; }
            // Simple confirmation: in UI frameworks without dialog support fallback to console; else proceed.
            if (!await ConfirmAsync($"Are you sure you want to uninstall {entry.Type} {entry.Version}?"))
            {
                StatusMessage = "Uninstall canceled.";
                return;
            }
            var (success, err) = DotNetUninstall.Tooling.BundleListing.Uninstall(new DotNetUninstall.Tooling.BundleInfoEntry(entry.Type, entry.Version, entry.Architecture, entry.CanUninstall, entry.Reason, entry.Version, entry.UninstallCommand));
            if (!success) ErrorMessage = err ?? "Uninstall failed.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            await RefreshAsync();
        }
    }

    private Task<bool> ConfirmAsync(string message)
    {
#if WINDOWS
        try
        {
            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title = "Confirm Uninstall",
                Content = message,
                PrimaryButtonText = "Uninstall",
                CloseButtonText = "Cancel"
            };
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Microsoft.UI.Xaml.Window.Current);
            WinRT.Interop.InitializeWithWindow.Initialize(dialog, hwnd);
            var tcs = new TaskCompletionSource<bool>();
            _ = dialog.ShowAsync().AsTask().ContinueWith(t =>
            {
                var result = t.Result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary;
                tcs.TrySetResult(result);
            });
            return tcs.Task;
        }
        catch { }
#endif
        // Fallback immediate approve (could extend with platform-specific dialogs later)
        return Task.FromResult(true);
    }

    private async Task BrowseAsync()
    {
        try
        {
            var pickerAvailable = ApiInformation.IsTypePresent("Windows.Storage.Pickers.FileOpenPicker");
            if (!pickerAvailable)
            {
                ErrorMessage = "File picker API unavailable; paste path manually.";
                return;
            }
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
#if WINDOWS
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Microsoft.UI.Xaml.Window.Current);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }
            catch { }
#endif
            picker.FileTypeFilter.Add("*");
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                UninstallToolPath = file.Path;
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private Task ApplyToolPathAsync() => Task.CompletedTask; // No-op

    private static string[] SplitLines(string text) => text
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task<(int exitCode, string stdout, string stderr)> RunProcessAsync(string fileName, string arguments)
    {
        var tcs = new TaskCompletionSource<(int, string, string)>();
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        string so = string.Empty, se = string.Empty;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) so += e.Data + "\n"; };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) se += e.Data + "\n"; };
        proc.Exited += (_, _) => tcs.TrySetResult((proc.ExitCode, so, se));
        if (!proc.Start()) throw new InvalidOperationException($"Cannot start process {fileName}");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return await tcs.Task.ConfigureAwait(false);
    }

    private void BuildGroups()
    {
        GroupedSdkItems.Clear();
        GroupedRuntimeItems.Clear();
        static (int major, int minor) ParseChannel(string? channel)
        {
            if (string.IsNullOrWhiteSpace(channel)) return (-1, -1);
            var parts = channel.Split('.', StringSplitOptions.RemoveEmptyEntries);
            int major = -1, minor = -1;
            if (parts.Length > 0) int.TryParse(parts[0], out major);
            if (parts.Length > 1) int.TryParse(parts[1], out minor);
            return (major, minor);
        }
        IOrderedEnumerable<IGrouping<string?, DotnetInstallEntry>> OrderGroups(IEnumerable<IGrouping<string?, DotnetInstallEntry>> groups)
            => groups.OrderByDescending(g => ParseChannel(g.Key).major)
                     .ThenByDescending(g => ParseChannel(g.Key).minor);
        if (SdkItems.Count > 0)
        {
            foreach (var grp in OrderGroups(SdkItems.GroupBy(i => i.Channel ?? DeriveChannel(i.Version))))
            {
                var first = grp.FirstOrDefault();
                var rt = first?.ReleaseType?.ToUpperInvariant();
                GroupedSdkItems.Add(new ChannelGroup(grp.Key!, grp, rt, first?.SupportPhase, first?.EolDate));
            }
        }
        if (RuntimeItems.Count > 0)
        {
            foreach (var grp in OrderGroups(RuntimeItems.GroupBy(i => i.Channel ?? DeriveChannel(i.Version))))
            {
                var first = grp.FirstOrDefault();
                var rt2 = first?.ReleaseType?.ToUpperInvariant();
                GroupedRuntimeItems.Add(new ChannelGroup(grp.Key!, grp, rt2, first?.SupportPhase, first?.EolDate));
            }
        }
        OnPropertyChanged(nameof(GroupedSdkItems));
        OnPropertyChanged(nameof(GroupedRuntimeItems));
    }

    private static string GetUserConfigFile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, ".dotnet-uninstall-gui");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "config.json");
    }

    private static void PersistToolPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            var file = GetUserConfigFile();
            var json = JsonSerializer.Serialize(new PersistedConfig { UninstallToolPath = path });
            File.WriteAllText(file, json);
        }
        catch { }
    }

    private static string? LoadPersistedToolPath()
    {
        try
        {
            var file = GetUserConfigFile();
            if (!File.Exists(file)) return null;
            var json = File.ReadAllText(file);
            var cfg = JsonSerializer.Deserialize<PersistedConfig>(json);
            if (cfg?.UninstallToolPath != null && File.Exists(cfg.UninstallToolPath))
                return cfg.UninstallToolPath;
        }
        catch { }
        return null;
    }

    private sealed class PersistedConfig
    {
        public string? UninstallToolPath { get; set; }
    }

    private Task UpdateToolVersionAsync() => Task.CompletedTask; // No-op

    private void DetectElevation()
    {
        try
        {
            if (!OperatingSystem.IsMacOS()) return; // Scope detection to macOS per requirement
            var euid = Syscall.geteuid();
            if (euid == 0)
            {
                IsElevated = true;
                OriginalUser = Environment.GetEnvironmentVariable("SUDO_USER") ?? Environment.UserName;
                ShowElevationWarning = true; // Only show if actually elevated
                OnPropertyChanged(nameof(CanPerformUninstalls));
            }
            else
            {
                // Not elevated – offer to restart with privileges
                ShowElevationOffer = true;
                OnPropertyChanged(nameof(CanPerformUninstalls));
            }
        }
        catch
        {
            // Swallow any unexpected errors; elevation detection is advisory only
        }
    }

    [RelayCommand]
    private void DismissElevationWarning()
    {
        ShowElevationWarning = false;
    }

    [RelayCommand]
    private async Task ElevateAsync()
    {
        try
        {
            if (!OperatingSystem.IsMacOS()) return; // Only implemented for macOS right now
            if (IsElevated) return; // Already elevated

            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;

            // Try to locate enclosing .app bundle root (AppName.app)
            string launchTarget = exe; // fallback
            try
            {
                var exeDir = Path.GetDirectoryName(exe);
                // Expect .../Something.app/Contents/MacOS
                var contentsDir = Directory.GetParent(exeDir ?? string.Empty);
                var appRoot = contentsDir?.Parent?.FullName;
                if (!string.IsNullOrWhiteSpace(appRoot) && appRoot.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(appRoot))
                {
                    launchTarget = appRoot; // Use bundle root
                }
            }
            catch { }

            bool isBundle = launchTarget.EndsWith(".app", StringComparison.OrdinalIgnoreCase);
            string commandPart;
            if (isBundle)
            {
                // Use 'open' with the bundle so macOS treats it as an app launch.
                // Using open under administrator privileges will prompt and then start the app root.
                string escBundle = launchTarget.Replace("'", "'\\''");
                commandPart = $"open '{escBundle}'";
            }
            else
            {
                string escExe = launchTarget.Replace("'", "'\\''");
                commandPart = $"'{escExe}'";
            }

            var appleScript = $"do shell script \"{commandPart}\" with administrator privileges";

            var psi = new ProcessStartInfo
            {
                FileName = "osascript",
                ArgumentList = { "-e", appleScript },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                // Optionally wait a short time to see if launch failed.
                await Task.Delay(1500);
                // Exit current (non-elevated) instance so only one UI remains.
                Environment.Exit(0);
            }
        }
        catch (Exception ex)
        {
            // Surface any failure through status / error messages without crashing.
            ErrorMessage = "Elevation failed: " + ex.Message;
            ShowElevationOffer = true; // keep banner so user can try again
        }
    }

    [RelayCommand]
    private void DismissElevationOffer()
    {
        ShowElevationOffer = false;
    }
}
