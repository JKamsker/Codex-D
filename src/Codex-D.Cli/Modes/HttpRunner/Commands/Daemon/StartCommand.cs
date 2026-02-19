using System.ComponentModel;
using CodexD.Shared.Output;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands.Daemon;

public sealed class StartCommand : AsyncCommand<ServeCommand.Settings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ServeCommand.Settings settings, CancellationToken cancellationToken)
    {
        OutputFormat format;
        try
        {
            format = OutputFormatParser.Resolve(settings.OutputFormat, settings.Json, OutputFormatUsage.Streaming);
        }
        catch (ArgumentException ex)
        {
            if (settings.Json || !string.IsNullOrWhiteSpace(settings.OutputFormat))
            {
                CliOutput.WriteJsonError("invalid_outputformat", ex.Message);
                return 2;
            }

            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        if (!OperatingSystem.IsWindows())
        {
            if (format != OutputFormat.Human)
            {
                CliOutput.WriteJsonError("unsupported", "Daemon mode is currently supported only on Windows.");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Daemon mode is currently supported only on Windows.[/]");
            }
            return 2;
        }

        if (settings.DaemonChild)
        {
            return await ServeCommand.RunDaemonChildAsync(settings, format, cancellationToken);
        }

        return await ServeCommand.RunDaemonParentAsync(settings, format, cancellationToken);
    }
}

