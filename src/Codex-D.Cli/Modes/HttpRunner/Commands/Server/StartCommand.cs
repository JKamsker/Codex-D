using CodexD.Shared.Output;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands.Server;

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

        if (settings.Daemon || settings.DaemonChild)
        {
            if (format != OutputFormat.Human)
            {
                CliOutput.WriteJsonError("invalid_args", "Use `codex-d daemon start` for daemon mode.");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Use `codex-d daemon start` for daemon mode.[/]");
            }
            return 2;
        }

        return await ServeCommand.RunForegroundAsync(settings, format, cancellationToken);
    }
}

