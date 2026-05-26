namespace PreviousPractice;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UserAppTheme = AppTheme.Light;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new NavigationPage(new MainPage()));
    }

    public static Page? CurrentPage => Current?.Windows.FirstOrDefault()?.Page;
}
