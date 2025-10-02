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
    public MainPage()
    {
        this.InitializeComponent();
        this.DataContextChanged += MainPage_DataContextChanged;
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
