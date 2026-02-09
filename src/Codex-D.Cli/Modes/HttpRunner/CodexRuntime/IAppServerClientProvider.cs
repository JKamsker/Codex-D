using JKToolKit.CodexSDK.AppServer;

namespace CodexD.HttpRunner.CodexRuntime;

public interface IAppServerClientProvider
{
    ValueTask<CodexAppServerClient> GetClientAsync(CancellationToken ct = default);
    CodexAppServerClient? TryGetClient();
}
