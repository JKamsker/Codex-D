using CodexD.HttpRunner.Commands;
using CodexD.HttpRunner.Commands.Run;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Cli;

public static class HttpCli
{
    public static void AddTo(IConfigurator config)
    {
        config.AddBranch("runs", runs =>
        {
            runs.SetDescription("List and inspect runs known by the server.");

            runs.AddCommand<LsCommand>("ls")
                .WithDescription("List runs known by the server.");
        });

        config.AddBranch("run", run =>
        {
            run.SetDescription("Operate on a single run.");

            run.AddCommand<AttachCommand>("attach")
                .WithDescription("Attach to an existing run and stream SSE output.");

            run.AddCommand<InterruptCommand>("interrupt")
                .WithDescription("Interrupt a running run.");

            run.AddCommand<SteerCommand>("steer")
                .WithDescription("Send additional text to an active turn (turn/steer).");

            run.AddCommand<StopCommand>("stop")
                .WithDescription("Pause (stop) a running exec run so it can be resumed.");

            run.AddCommand<ResumeCommand>("resume")
                .WithDescription("Resume a paused (or orphaned) exec run by starting a new turn (default prompt: \"continue\").");

            run.AddCommand<RunMessagesCommand>("messages")
                .WithDescription("Print the last completed agent message(s) for a run.");

            run.AddCommand<RunThinkingCommand>("thinking")
                .WithDescription("Print one-line summaries from thinking blocks (bold **...** headings).");
        });

        config.AddCommand<ServeCommand>("serve")
            .WithDescription("Start the HTTP/SSE runner.");

        config.AddCommand<ExecCommand>("exec")
            .WithDescription("Start a new run via HTTP (optionally detached).");

        config.AddCommand<ReviewCommand>("review")
            .WithDescription("Run a non-interactive `codex review` on the runner and stream output.");

        config.AddCommand<StatusCommand>("status")
            .WithDescription("Show runner discovery + health/info (doctor).");

        config.AddCommand<VersionCommand>("version")
            .WithDescription("Show CLI version and the version of reachable daemon/foreground servers.");
    }
}
