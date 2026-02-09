namespace CodexD.HttpRunner.Client;

public sealed class RunnerResolutionFailure : Exception
{
    public RunnerResolutionFailure(string userMessage) : base(userMessage)
    {
        UserMessage = userMessage;
    }

    public string UserMessage { get; }
}

