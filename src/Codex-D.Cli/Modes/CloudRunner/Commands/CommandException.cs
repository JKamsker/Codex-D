namespace CodexD.CloudRunner.Commands;

public sealed class CommandException : Exception
{
    public string ErrorCode { get; }
    public object? Details { get; }

    public CommandException(string errorCode, string message, object? details = null) : base(message)
    {
        ErrorCode = errorCode;
        Details = details;
    }
}


