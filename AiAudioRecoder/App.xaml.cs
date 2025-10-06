namespace AiAudioRecoder;

public partial class App : Application
{

    public App()
    {
        try
        {
            Log("App constructor start");
            InitializeComponent();
            Log("App constructor end");
        }
        catch (Exception ex)
        {
            Log($"App constructor exception: {ex}");
            throw;
        }
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        try
        {
            Log("CreateWindow start");
            var win = new Window(new AppShell());
            Log("CreateWindow end");
            return win;
        }
        catch (Exception ex)
        {
            Log($"CreateWindow exception: {ex}");
            throw;
        }
    }

    private void Log(string message)
    {
        try
        {
            var path = System.IO.Path.Combine(FileSystem.Current.AppDataDirectory, "app_log.txt");
            System.IO.File.AppendAllText(path, $"{DateTime.Now:O} {message}\n");
        }
        catch { }
    }
}
