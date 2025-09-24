namespace DotNetUninstall.Presentation;

public sealed partial class MainPage : Page
{
    private bool _autoRefreshed;
    public MainPage()
    {
        this.InitializeComponent();
        this.DataContextChanged += MainPage_DataContextChanged;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        TryAutoRefresh();
    }

    private void MainPage_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        TryAutoRefresh();
    }

    private void TryAutoRefresh()
    {
        if (_autoRefreshed) return;
        if (DataContext is MainViewModel vm && vm.RefreshCommand.CanExecute(null))
        {
            _autoRefreshed = true;
            _ = vm.RefreshCommand.ExecuteAsync(null);
        }
    }
}
