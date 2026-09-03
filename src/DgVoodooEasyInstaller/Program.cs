namespace DgVoodooEasyInstaller;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.Length >= 2 && args[0] == "--game" ? args[1] : null));
    }
}
