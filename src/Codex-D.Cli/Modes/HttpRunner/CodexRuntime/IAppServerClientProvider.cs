using JKToolKit.CodexSDK.AppServer.Resiliency;

namespace CodexD.HttpRunner.CodexRuntime;

public interface IAppServerClientProvider
{
    ValueTask<ResilientCodexAppServerClient> GetClientAsync(CancellationToken ct = default);
    ResilientCodexAppServerClient? TryGetClient();
}
