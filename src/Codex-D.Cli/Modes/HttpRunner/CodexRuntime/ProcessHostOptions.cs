namespace CodexWebUi.Runner.HttpRunner.CodexRuntime;

public sealed class ProcessHostOptions
{
    public TimeSpan RestartDelay { get; set; } = TimeSpan.FromSeconds(5);
}
