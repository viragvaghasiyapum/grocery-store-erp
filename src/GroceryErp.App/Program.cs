using System.Diagnostics;
using Velopack;

namespace GroceryErp.App;

public static class Program
{
    internal static long LaunchTimestamp { get; private set; }
    [STAThread]
    public static void Main(string[] args)
    {
        LaunchTimestamp = Stopwatch.GetTimestamp();
        VelopackApp.Build().Run();
        if (args.Contains("--pseudo", StringComparer.OrdinalIgnoreCase))
            Localization.TranslationSource.Instance.PseudoLocalizationEnabled = true;

        var application = new App();
        application.InitializeComponent();
        application.Run();
    }
}
