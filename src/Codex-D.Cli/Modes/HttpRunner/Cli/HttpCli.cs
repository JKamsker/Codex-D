using CodexD.HttpRunner.Commands;
using CodexD.HttpRunner.Commands.Run;
using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Cli;

public static class HttpCli
{
    public static void AddTo(IConfigurator config)
    {
        config.AddBranch("http", http =>
        {
            http.SetDescription("Standalone HTTP + SSE runner.");

            http.AddBranch("runs", runs =>
            {
                runs.SetDescription("List and inspect runs known by the server.");

                runs.AddCommand<LsCommand>("ls")
                    .WithDescription("List runs known by the server.");
            });

            http.AddBranch("run", run =>
            {
                run.SetDescription("Operate on a single run.");

                run.AddCommand<AttachCommand>("attach")
                    .WithDescription("Attach to an existing run and stream SSE output.");

                run.AddCommand<InterruptCommand>("interrupt")
                    .WithDescription("Interrupt a running run.");

                run.AddCommand<StopCommand>("stop")
                    .WithDescription("Pause (stop) a running exec run so it can be resumed.");

                run.AddCommand<ResumeCommand>("resume")
                    .WithDescription("Resume a paused (or orphaned) exec run by starting a new turn (default prompt: \"continue\").");

                run.AddCommand<RunMessagesCommand>("messages")
                    .WithDescription("Print the last completed agent message(s) for a run.");

                run.AddCommand<RunThinkingCommand>("thinking")
                    .WithDescription("Print one-line summaries from thinking blocks (bold **...** headings).");
            });

            http.AddCommand<ServeCommand>("serve")
                .WithDescription("Start the HTTP/SSE runner.");

            http.AddCommand<ExecCommand>("exec")
                .WithDescription("Start a new run via HTTP (optionally detached).");

            http.AddCommand<ReviewCommand>("review")
                .WithDescription("Run a non-interactive `codex review` on the runner and stream output.");

            http.AddCommand<StatusCommand>("status")
                .WithDescription("Show runner discovery + health/info (doctor).");
        });
    }
}
