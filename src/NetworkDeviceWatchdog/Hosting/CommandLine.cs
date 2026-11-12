namespace NetworkDeviceWatchdog.App.Hosting;

internal enum AppCommand
{
    Run,
    Once,
    TestAlert,
    Validate,
    Init,
    Install,
    Uninstall,
    Help,
    Version,
}

/// <summary>Parsed command line.</summary>
internal sealed record CommandLine(AppCommand Command, string? ConfigPath, string? Language, bool Force)
{
    public const string Usage = """
        Network Device Watchdog - monitors network printers and devices and alerts on failures.

        Usage: NetworkDeviceWatchdog [command] [options]

        Commands:
          (none)          Run the watchdog (Windows: in the notification area).
          --once          Check every device once, print the result and exit.
                          Exit code 0 when all devices are OK, 1 otherwise.
          --test-alert    Send a test alert through every notification channel.
          --validate      Check the configuration file and exit.
          --init          Write a sample configuration file (use --force to overwrite).
          --install       Start the watchdog automatically at logon
                          (Windows: current user; Linux: systemd user service).
          --uninstall     Undo --install and stop the running watchdog.
          --help          Show this help.
          --version       Show the version.

        Options:
          --config <file>     Configuration file. Default: watchdog.json next to the
                              program if present, otherwise the user configuration folder.
          --language <tag>    Override the configured language (en, pt, es, hi, zh, ar...).
          --force             Allow --init to overwrite an existing file.

        On Windows, pipe the output to see it in PowerShell, e.g.:
          .\NetworkDeviceWatchdog.exe --once | Out-String
        """;

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var command = AppCommand.Run;
        string? config = null, language = null;
        var force = false;

        void SetCommand(AppCommand c, string arg)
        {
            if (command != AppCommand.Run)
                throw new ArgumentException($"Only one command can be given; '{arg}' conflicts with an earlier one.");
            command = c;
        }

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string Value() => i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : throw new ArgumentException($"{arg} needs a value.");

            switch (arg.ToLowerInvariant())
            {
                case "--once": SetCommand(AppCommand.Once, arg); break;
                case "--test-alert": SetCommand(AppCommand.TestAlert, arg); break;
                case "--validate": SetCommand(AppCommand.Validate, arg); break;
                case "--init": SetCommand(AppCommand.Init, arg); break;
                case "--install": SetCommand(AppCommand.Install, arg); break;
                case "--uninstall": SetCommand(AppCommand.Uninstall, arg); break;
                case "--help" or "-h" or "/?": SetCommand(AppCommand.Help, arg); break;
                case "--version": SetCommand(AppCommand.Version, arg); break;
                case "--config" or "-c": config = Value(); break;
                case "--language" or "--lang": language = Value(); break;
                case "--force": force = true; break;
                default: throw new ArgumentException($"Unknown argument '{arg}'. Use --help.");
            }
        }
        return new CommandLine(command, config, language, force);
    }
}
