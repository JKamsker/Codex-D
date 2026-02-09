using Spectre.Console.Cli;

namespace CodexD.CloudRunner.Cli;

public static class CloudCli
{
    public static void AddTo(IConfigurator config)
    {
        config.AddBranch("cloud", cloud =>
        {
            cloud.SetDescription("Runner that connects to CodexWebUi.Api via SignalR.");

            cloud.AddCommand<ServeCommand>("serve")
                .WithDescription("Run the Cloud runner (connects to CodexWebUi.Api via SignalR).");
        });
    }
}

