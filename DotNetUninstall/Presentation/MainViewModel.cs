using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Collections.Specialized;
using System.Text.Json;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotNetUninstall.Models;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Uno.Extensions.Navigation;
using Windows.Foundation.Metadata;

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

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private bool hasUninstallTool;

    [ObservableProperty]
    private string? uninstallToolPath;

    [ObservableProperty]
    private string? uninstallToolVersion;

    [ObservableProperty]
    private string? suggestedDownload;

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
        // Load persisted path first (env var takes precedence if present)
        var persisted = LoadPersistedToolPath();
        UninstallToolPath = Environment.GetEnvironmentVariable("DOTNET_UNINSTALL_TOOL")?.Trim();
        if (string.IsNullOrWhiteSpace(UninstallToolPath))
        {
            UninstallToolPath = persisted;
        }
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
            await DetectToolAsync();
            if (HasUninstallTool)
            {
                await UpdateToolVersionAsync();
                if (!string.IsNullOrWhiteSpace(UninstallToolVersion))
                {
                    StatusMessage = $"Using uninstall tool: {Path.GetFileName(UninstallToolPath)} (v{UninstallToolVersion})";
                }
            }
            if (HasUninstallTool)
            {
                await ListFromToolAsync();
            }
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

    private Task DetectToolAsync()
    {
        // Detection strategy (no global tool):
        // 1. Use explicitly configured environment variable DOTNET_UNINSTALL_TOOL
        // 2. Search current working directory and application base directory for a binary named dotnet-core-uninstall or dotnet-core-uninstall.exe
        // 3. Search common locations (/usr/local/bin, /usr/local/share, ~/.dotnet/tools)

        string? path = UninstallToolPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var candidates = new List<string>();
            var fileName = OperatingSystem.IsWindows() ? "dotnet-core-uninstall.exe" : "dotnet-core-uninstall";
            var baseDir = AppContext.BaseDirectory;
            var cwd = Directory.GetCurrentDirectory();
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var potentialDirs = new[]
            {
                cwd,
                baseDir,
                Path.Combine(home, ".dotnet", "tools"),
                "/usr/local/bin",
                "/usr/local/share",
                "/opt/dotnet-core-uninstall"
            };
            foreach (var d in potentialDirs.Distinct())
            {
                var full = Path.Combine(d, fileName);
                if (File.Exists(full)) candidates.Add(full);
            }
            path = candidates.FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            UninstallToolPath = path;
            HasUninstallTool = true;
            StatusMessage = $"Using uninstall tool: {Path.GetFileName(path)}";
            PersistToolPath(path);
            SuggestedDownload = null;
        }
        else
        {
            HasUninstallTool = false;
            StatusMessage = "Uninstall tool not found. Set DOTNET_UNINSTALL_TOOL env var to its path.";
            SuggestedDownload = BuildSuggestedDownload();
        }
        UninstallCommand.NotifyCanExecuteChanged();
        return Task.CompletedTask;
    }

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

    private async Task ListFromToolAsync()
    {
        if (string.IsNullOrWhiteSpace(UninstallToolPath) || !File.Exists(UninstallToolPath)) return;
        var result = await RunProcessAsync(UninstallToolPath!, "list");
        var lines = SplitLines(result.stdout);
        string? section = null; // sdk | runtime
        var sdkHeader = new Regex(@"^\.NET (Core )?SDKs:?", RegexOptions.IgnoreCase);
        var rtHeader = new Regex(@"^\.NET (Core )?Runtimes:?", RegexOptions.IgnoreCase);
        var entryRx = new Regex(@"^(?<ver>[0-9A-Za-z\.-]+)\s+\((?<arch>[^)]+)\)(?:\s+\[(?<reason>[^]]+)\])?\s*$");
        var parsed = new List<DotnetInstallEntry>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (sdkHeader.IsMatch(line)) { section = "sdk"; continue; }
            if (rtHeader.IsMatch(line)) { section = "runtime"; continue; }
            if (line.StartsWith("This tool", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("The versions", StringComparison.OrdinalIgnoreCase)) continue;
            if (section == null) continue;
            var m = entryRx.Match(line);
            if (!m.Success) continue;
            var version = m.Groups["ver"].Value;
            var arch = m.Groups["arch"].Value;
            var reason = m.Groups["reason"].Success ? m.Groups["reason"].Value : null;
            bool canUninstall = string.IsNullOrEmpty(reason) || !reason.Contains("Cannot uninstall", StringComparison.OrdinalIgnoreCase);
            parsed.Add(new DotnetInstallEntry(section, section, version, arch, canUninstall && HasUninstallTool, reason));
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
                var previewKind = DerivePreviewKind(baseEntry.Version);
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
        // Take first two numeric segments; fallback to first segment if only one.
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _)) return parts[0] + "." + parts[1];
        if (parts.Length >= 1) return parts[0];
        return version;
    }

    private static string DerivePreviewKind(string version)
    {
        var v = version.ToLowerInvariant();
        if (v.Contains("-preview.")) return "preview";
        if (v.Contains("-rc.")) return "rc";
        return "ga";
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
        if (!HasUninstallTool)
        {
            ErrorMessage = "dotnet-core-uninstall tool not available.";
            return;
        }
        ErrorMessage = null;
        StatusMessage = $"Uninstalling {entry.Type} {entry.Version}...";
        try
        {
            if (string.IsNullOrWhiteSpace(UninstallToolPath) || !File.Exists(UninstallToolPath))
            {
                ErrorMessage = "Configured uninstall tool path is invalid.";
                return;
            }
            string args = entry.Type == "sdk" ? $"remove --sdk {entry.Version} -y" : $"remove --runtime {entry.Version} -y";
            var result = await RunProcessAsync(UninstallToolPath!, args);
            if (result.exitCode != 0)
            {
                ErrorMessage = (string.IsNullOrWhiteSpace(result.stderr) ? result.stdout : result.stderr) ?? "Uninstall failed.";
            }
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

    private async Task ApplyToolPathAsync()
    {
        if (string.IsNullOrWhiteSpace(UninstallToolPath) || !File.Exists(UninstallToolPath))
        {
            HasUninstallTool = false;
            ErrorMessage = "Specified path invalid.";
            return;
        }
        HasUninstallTool = true;
        ErrorMessage = null;
        StatusMessage = $"Using uninstall tool: {Path.GetFileName(UninstallToolPath)}";
        UninstallCommand.NotifyCanExecuteChanged();
        PersistToolPath(UninstallToolPath!);
        await UpdateToolVersionAsync();
        if (!string.IsNullOrWhiteSpace(UninstallToolVersion))
        {
            StatusMessage = $"Using uninstall tool: {Path.GetFileName(UninstallToolPath)} (v{UninstallToolVersion})";
        }
        SdkItems.Clear();
        RuntimeItems.Clear();
        await ListFromToolAsync();
        BuildGroups();
        StatusMessage = $"Loaded {TotalCount} entries.";
    }

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

    private async Task UpdateToolVersionAsync()
    {
        UninstallToolVersion = null;
        if (string.IsNullOrWhiteSpace(UninstallToolPath) || !File.Exists(UninstallToolPath)) return;
        try
        {
            var result = await RunProcessAsync(UninstallToolPath!, "--version");
            if (result.exitCode == 0)
            {
                var stdout = (result.stdout ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    // Use first non-empty line
                    var line = stdout.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        // Extract version-like token (digits + dots + optional prerelease)
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"[0-9]+(\.[0-9]+){1,3}(-[0-9A-Za-z\.]+)?");
                        UninstallToolVersion = match.Success ? match.Value : line;
                    }
                }
            }
        }
        catch { }
    }
}
