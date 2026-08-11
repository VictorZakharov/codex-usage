using CodexUsage.App.UI;

namespace CodexUsage.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (PreviewRenderer.TryRender(args))
        {
            return;
        }

        using var singleInstance = new Mutex(
            initiallyOwned: true,
            name: "Local\\CodexUsage.Tray.SingleInstance",
            createdNew: out var isFirstInstance);

        if (!isFirstInstance)
        {
            return;
        }

        Application.Run(new TrayApplicationContext());
    }
}
