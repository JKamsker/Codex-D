namespace CodexD.Shared.Paths;

public sealed class PathPolicyException : Exception
{
    public string ErrorCode { get; }

    public PathPolicyException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}

