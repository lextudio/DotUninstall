namespace DotNetUninstall.Presentation;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        this.InitializeComponent();
        this.DataContextChanged += MainPage_DataContextChanged;
    }

    private void MainPage_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (DataContext is MainViewModel vm && vm.RefreshCommand.CanExecute(null))
        {
            _ = vm.RefreshCommand.ExecuteAsync(null);
        }
    }
}
