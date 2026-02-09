using JKToolKit.CodexSDK.AppServer;

namespace CodexWebUi.Runner.HttpRunner.CodexRuntime;

public interface IAppServerClientProvider
{
    ValueTask<CodexAppServerClient> GetClientAsync(CancellationToken ct = default);
    CodexAppServerClient? TryGetClient();
}
