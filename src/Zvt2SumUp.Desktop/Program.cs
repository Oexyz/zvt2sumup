using Zvt2SumUp.Core;
using Zvt2SumUp.Service;

namespace Zvt2SumUp.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--service", StringComparer.OrdinalIgnoreCase) || args.Contains("--service-smoke-test", StringComparer.OrdinalIgnoreCase))
            return GatewayServiceHost.RunAsync(args).GetAwaiter().GetResult();
        ApplicationConfiguration.Initialize();
        try
        {
            Directory.CreateDirectory(AppPaths.Root); Directory.CreateDirectory(AppPaths.Logs);
            if (args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
            {
                using MainForm smoke = new(true); smoke.Shown += (_, _) => smoke.BeginInvoke(smoke.Close); Application.Run(smoke); return 0;
            }
            if (args.Contains("--layout-smoke-test", StringComparer.OrdinalIgnoreCase))
            {
                int result = 1;
                using MainForm smoke = new(true);
                smoke.Shown += (_, _) => smoke.BeginInvoke(() =>
                {
                    string? error = smoke.ValidateMinimumLayout();
                    if (error is null) result = 0; else Console.Error.WriteLine(error);
                    smoke.Close();
                });
                Application.Run(smoke);
                return result;
            }
            Application.Run(new MainForm()); return 0;
        }
        catch (Exception exception)
        { MessageBox.Show(SensitiveDataRedactor.Redact(exception.ToString()), "ZVT2SumUp - Startfehler", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
    }
}
