using Spectre.Console.Cli;

namespace CodexD.HttpRunner.Commands;

public static class Entrypoint
{
    public static async Task<int> RunAsync(string[] args)
    {
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("codex-d http");

            config.AddCommand<ServeCommand>("serve")
                .WithDescription("Start the HTTP/SSE runner.");

            config.AddCommand<ExecCommand>("exec")
                .WithDescription("Start a new run via HTTP (optionally detached).");

            config.AddCommand<ReviewCommand>("review")
                .WithDescription("Run a non-interactive `codex review` on the runner and stream output.");

            config.AddCommand<AttachCommand>("attach")
                .WithDescription("Attach to an existing run and stream SSE output.");

            config.AddCommand<LsCommand>("ls")
                .WithDescription("List runs known by the server.");
        });

        if (args.Length == 0)
        {
            return await app.RunAsync(["--help"]);
        }

        return await app.RunAsync(args);
    }
}
