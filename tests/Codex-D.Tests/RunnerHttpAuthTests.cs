using CodexWebUi.Runner.HttpRunner.Server;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CodexWebUi.Runner.Tests;

public sealed class AuthTests
{
    [Fact]
    public void IsAuthorized_ReturnsFalse_WhenHeaderMissing()
    {
        var ctx = new DefaultHttpContext();
        Assert.False(Auth.IsAuthorized(ctx.Request, "t"));
    }

    [Fact]
    public void IsAuthorized_ReturnsFalse_WhenSchemeIsNotBearer()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = "Basic abc";
        Assert.False(Auth.IsAuthorized(ctx.Request, "abc"));
    }

    [Fact]
    public void IsAuthorized_ReturnsTrue_WhenTokenMatches()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = "Bearer  secret-token ";
        Assert.True(Auth.IsAuthorized(ctx.Request, "secret-token"));
    }

    [Fact]
    public void IsAuthorized_IsCaseInsensitiveForBearer()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = "bEaReR secret-token";
        Assert.True(Auth.IsAuthorized(ctx.Request, "secret-token"));
    }
}
