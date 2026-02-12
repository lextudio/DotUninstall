using System;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Uno.Extensions.Navigation;

namespace DotNetUninstall.Presentation;

public sealed partial class MainPage : Page
{
    private readonly DispatcherTimer? _macSystemThemePollTimer;
    private ElementTheme? _lastObservedMacSystemTheme;

    public MainPage()
    {
        this.InitializeComponent();
        this.DataContextChanged += MainPage_DataContextChanged;
        this.Loaded += MainPage_Loaded;
        this.Unloaded += MainPage_Unloaded;

        if (OperatingSystem.IsMacOS())
        {
            _macSystemThemePollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _macSystemThemePollTimer.Tick += (_, _) => ApplyMacSystemThemeIfNeeded();
        }
    }

    private MainViewModel? _vm;

    private void MainPage_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= VmOnPropertyChanged;
        }
        _vm = DataContext as MainViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += VmOnPropertyChanged;
            ApplyRequestedTheme();
            if (_vm.RefreshCommand.CanExecute(null))
            {
                _ = _vm.RefreshCommand.ExecuteAsync(null);
            }
        }
        UpdateUninstallButtons();
    }

    private void VmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsElevated) || e.PropertyName == nameof(MainViewModel.ShowElevationOffer))
        {
            UpdateUninstallButtons();
        }

        if (e.PropertyName == nameof(MainViewModel.RequestedElementTheme))
        {
            ApplyRequestedTheme();
        }
    }

    private void ApplyRequestedTheme()
    {
        if (_vm == null)
        {
            return;
        }

        var requestedTheme = _vm.RequestedElementTheme;
        if (requestedTheme == ElementTheme.Default && OperatingSystem.IsMacOS())
        {
            StartMacSystemThemeSync();
            ApplyMacSystemThemeIfNeeded(force: true);
            return;
        }

        StopMacSystemThemeSync();
        ApplyTheme(this, requestedTheme);

        // Apply at window root too so Shell-level visuals refresh immediately.
        if (DotNetUninstall.App.CurrentMainWindow?.Content is FrameworkElement root && !ReferenceEquals(root, this))
        {
            ApplyTheme(root, requestedTheme);
        }
    }

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_vm?.RequestedElementTheme == ElementTheme.Default && OperatingSystem.IsMacOS())
        {
            StartMacSystemThemeSync();
            ApplyMacSystemThemeIfNeeded(force: true);
        }
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        StopMacSystemThemeSync();
    }

    private void StartMacSystemThemeSync()
    {
        if (_macSystemThemePollTimer is null || _macSystemThemePollTimer.IsEnabled)
        {
            return;
        }

        _macSystemThemePollTimer.Start();
    }

    private void StopMacSystemThemeSync()
    {
        if (_macSystemThemePollTimer is null)
        {
            return;
        }

        if (_macSystemThemePollTimer.IsEnabled)
        {
            _macSystemThemePollTimer.Stop();
        }

        _lastObservedMacSystemTheme = null;
    }

    private void ApplyMacSystemThemeIfNeeded(bool force = false)
    {
        if (!OperatingSystem.IsMacOS() || _vm?.RequestedElementTheme != ElementTheme.Default)
        {
            return;
        }

        var currentSystemTheme = DetectMacSystemTheme();
        if (currentSystemTheme is null)
        {
            return;
        }

        var resolvedTheme = currentSystemTheme.Value;
        if (!force && _lastObservedMacSystemTheme == resolvedTheme)
        {
            return;
        }

        _lastObservedMacSystemTheme = resolvedTheme;
        ApplyTheme(this, resolvedTheme);
        if (DotNetUninstall.App.CurrentMainWindow?.Content is FrameworkElement root && !ReferenceEquals(root, this))
        {
            ApplyTheme(root, resolvedTheme);
        }
    }

    private static ElementTheme? DetectMacSystemTheme()
    {
        try
        {
            var psi = new ProcessStartInfo("defaults", "read -g AppleInterfaceStyle")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return null;
            }

            var output = proc.StandardOutput.ReadToEnd();
            var error = proc.StandardError.ReadToEnd();
            proc.WaitForExit(600);
            if (proc.ExitCode == 0 && output.Trim().Equals("Dark", StringComparison.OrdinalIgnoreCase))
            {
                return ElementTheme.Dark;
            }

            // In light mode macOS reports missing key via non-zero exit code.
            if (proc.ExitCode != 0 &&
                error.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            {
                return ElementTheme.Light;
            }

            if (proc.ExitCode == 0)
            {
                return ElementTheme.Light;
            }
        }
        catch
        {
            // Ignore detection errors to avoid forcing wrong theme.
        }

        return null;
    }

    private static void ApplyTheme(FrameworkElement element, ElementTheme requestedTheme)
    {
        if (requestedTheme == ElementTheme.Default)
        {
            // System mode: clear local value so Uno can track OS theme changes live.
            element.ClearValue(FrameworkElement.RequestedThemeProperty);
            return;
        }

        element.RequestedTheme = requestedTheme;
    }

    private void UpdateUninstallButtons()
    {
        if (!OperatingSystem.IsMacOS()) return; // Only enforce on macOS
        var root = this; // search visual tree for buttons
        DisableUninstallButtons(!_vm?.CanPerformUninstalls ?? false);
    }

    private void DisableUninstallButtons(bool disable)
    {
        // Traverse visual tree when loaded; for simplicity, walk logical children of Pivot
        if (MainPivot == null) return;
        foreach (var item in MainPivot.Items)
        {
            if (item is PivotItem pi && pi.Content is FrameworkElement fe)
            {
                DisableInChildren(fe, disable);
            }
        }
    }

    private void DisableInChildren(FrameworkElement fe, bool disable)
    {
        if (fe is Button btn && (btn.Content as string) == "Uninstall")
        {
            btn.IsEnabled = !disable && btn.IsEnabled; // keep existing false if already false
        }
        int count = VisualTreeHelper.GetChildrenCount(fe);
        for (int i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(fe, i) is FrameworkElement child)
            {
                DisableInChildren(child, disable);
            }
        }
    }

    private void Button_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && OperatingSystem.IsMacOS() && _vm is not null)
        {
            if (!_vm.CanPerformUninstalls)
            {
                b.IsEnabled = false;
            }
        }
    }

    private void OnOpenReleasePage(object sender, RoutedEventArgs e)
    {
        try
        {
            var tag = _vm?.LatestReleaseTag;
            var url = string.IsNullOrWhiteSpace(tag)
                ? "https://github.com/lextudio/DotUninstall/releases/latest"
                : $"https://github.com/lextudio/DotUninstall/releases/tag/{tag}";
            // Cross-platform open
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", url);
            }
        }
        catch { }
    }

    private void OnOpenChannelDownload(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button b && b.Tag is string url && !string.IsNullOrWhiteSpace(url))
            {
                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
                }
                else if (OperatingSystem.IsMacOS())
                {
                    Process.Start("open", url);
                }
                else if (OperatingSystem.IsLinux())
                {
                    Process.Start("xdg-open", url);
                }
            }
        }
        catch { }
    }

}
