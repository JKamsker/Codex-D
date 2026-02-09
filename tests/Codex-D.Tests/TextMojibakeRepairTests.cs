using CodexD.Utils;
using Xunit;

namespace CodexD.Tests;

public sealed class TextMojibakeRepairTests
{
    [Fact]
    public void Fix_Cp437Mojibake_RewritesToUnicode()
    {
        var repaired = TextMojibakeRepair.Fix("YouÔÇÖre");
        Assert.Equal("You’re", repaired);
    }

    [Fact]
    public void Fix_Cp1252Mojibake_RewritesToUnicode()
    {
        var repaired = TextMojibakeRepair.Fix("Youâ€™re");
        Assert.Equal("You’re", repaired);
    }

    [Fact]
    public void Fix_NormalText_IsUnchanged()
    {
        var input = "You're";
        var repaired = TextMojibakeRepair.Fix(input);
        Assert.Equal(input, repaired);
    }
}

