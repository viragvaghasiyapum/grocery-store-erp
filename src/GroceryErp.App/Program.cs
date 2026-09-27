using Velopack;

namespace GroceryErp.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        if (args.Contains("--pseudo", StringComparer.OrdinalIgnoreCase))
            Localization.TranslationSource.Instance.PseudoLocalizationEnabled = true;

        var application = new App();
        application.InitializeComponent();
        application.Run();
    }
}
