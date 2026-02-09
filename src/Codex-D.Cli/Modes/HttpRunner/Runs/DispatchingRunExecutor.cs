using CodexD.HttpRunner.Contracts;

namespace CodexD.HttpRunner.Runs;

public sealed class DispatchingRunExecutor : IRunExecutor
{
    private readonly IRunExecutor _exec;
    private readonly IRunExecutor _review;

    public DispatchingRunExecutor(
        CodexAppServerRunExecutor exec,
        CodexReviewRunExecutor review)
    {
        _exec = exec;
        _review = review;
    }

    public Task<RunExecutionResult> ExecuteAsync(RunExecutionContext context, CancellationToken ct)
    {
        var kind = RunKinds.Normalize(context.Kind);
        return kind == RunKinds.Review
            ? _review.ExecuteAsync(context, ct)
            : _exec.ExecuteAsync(context, ct);
    }
}

