using NetworkDeviceWatchdog.App.Hosting;
using NetworkDeviceWatchdog.Configuration;

namespace NetworkDeviceWatchdog.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        CommandLine cli;
        try
        {
            cli = CommandLine.Parse(args);
        }
        catch (ArgumentException e)
        {
            PrepareConsole(interactive: true);
            Console.Error.WriteLine(e.Message);
            return Commands.UsageError;
        }

        PrepareConsole(interactive: cli.Command != AppCommand.Run);
        var output = Console.Out;
        var configPath = AppPaths.ResolveConfig(cli.ConfigPath);

        try
        {
            switch (cli.Command)
            {
                case AppCommand.Help:
                    output.WriteLine(CommandLine.Usage);
                    return Commands.Success;
                case AppCommand.Version:
                    output.WriteLine(Commands.Version);
                    return Commands.Success;
                case AppCommand.Init:
                    return Commands.Init(cli, output);
                case AppCommand.Validate:
                    return Commands.Validate(cli, output);
                case AppCommand.Install:
                    return Commands.Install(cli, output);
                case AppCommand.Uninstall:
                    return Commands.Uninstall(cli, output);
                case AppCommand.Once:
                {
                    using var runtime = WatchdogRuntime.Load(configPath, cli.Language, console: false);
                    return Commands.OnceAsync(runtime, output, CancellationToken.None).GetAwaiter().GetResult();
                }
                case AppCommand.TestAlert:
#if WINDOWS_UI
                    return Windows.WindowsApp.RunTestAlert(cli, configPath);
#else
                {
                    using var runtime = WatchdogRuntime.Load(configPath, cli.Language, console: true);
                    runtime.Service.SendTestAlertAsync(CancellationToken.None).GetAwaiter().GetResult();
                    return Commands.Success;
                }
#endif
                default:
#if WINDOWS_UI
                    return Windows.WindowsApp.Run(cli, configPath);
#else
                    return Commands.RunConsoleAsync(cli, configPath).GetAwaiter().GetResult();
#endif
            }
        }
        catch (FileNotFoundException e) when (e.FileName == configPath)
        {
            return Fail(cli, Commands.EarlyLocalizer(cli.Language).Format("app.configNotFound", ("path", configPath)));
        }
        catch (ConfigException e)
        {
            var writer = new StringWriter();
            Commands.WriteConfigErrors(writer, Commands.EarlyLocalizer(cli.Language), e);
            return Fail(cli, writer.ToString());
        }
    }

    private static int Fail(CommandLine cli, string message)
    {
        Console.Error.WriteLine(message);
#if WINDOWS_UI
        // Started from the Run key or a double-click there is no console: show a dialog.
        if (cli.Command is AppCommand.Run or AppCommand.TestAlert)
            Windows.WindowsApp.ShowStartupError(message);
#else
        _ = cli;
#endif
        return Commands.Problems;
    }

    private static void PrepareConsole(bool interactive)
    {
#if WINDOWS_UI
        // The Windows build is a GUI program (no console window when it runs in the
        // background). For command-line use, write to the console it was started from.
        if (!interactive || !Windows.ConsoleAttach.AttachToParent())
            return;
#else
        _ = interactive;
#endif
        try
        {
            // Hindi, Chinese and Arabic messages need UTF-8 in the terminal.
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
            // No console (output redirected or detached): nothing to configure.
        }
    }
}
