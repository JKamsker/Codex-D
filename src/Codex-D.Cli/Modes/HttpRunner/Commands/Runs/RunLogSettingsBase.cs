using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands.Runs;

public abstract class RunLogSettingsBase : CommandSettings
{
    [CommandOption("--file <PATH>")]
    public string? FilePath { get; init; }

    [CommandOption("--run <RUN_ID>")]
    public Guid? RunId { get; init; }

    [CommandOption("--state-dir <DIR>")]
    public string? StateDir { get; init; }

    [CommandOption("--tail-lines <N>")]
    public int? TailLines { get; init; }
}

