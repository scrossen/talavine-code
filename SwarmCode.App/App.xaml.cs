namespace SwarmCode.App;

public partial class App : Application
{
    private const double MainWindowMinimumWidth = 960;
    private const double MainWindowMinimumHeight = 640;

    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage())
        {
            Title = "Talavine Code",
            MinimumWidth = MainWindowMinimumWidth,
            MinimumHeight = MainWindowMinimumHeight,
        };
        window.Destroying += async (_, _) =>
        {
            if (MauiProgram.GetServiceProvider()?.GetService(typeof(Services.MyAppsService)) is Services.MyAppsService apps)
                await apps.Runtime.DisposeAsync();
        };
        return window;
    }
}
