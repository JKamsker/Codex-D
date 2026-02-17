using System;
using System.Linq;
using System.Reflection;
using CodexD.HttpRunner.Commands.Run;
using Spectre.Console.Cli;
using Xunit;

namespace CodexD.Tests;

public sealed class ThinkingFollowSwitchTests
{
    [Fact]
    public void ThinkingCommand_ExposesFollowSwitch()
    {
        var settingsType = typeof(RunThinkingCommand.Settings);
        var prop = settingsType.GetProperty("Follow", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(prop);

        var data = prop!.GetCustomAttributesData().SingleOrDefault(a => a.AttributeType == typeof(CommandOptionAttribute));
        Assert.NotNull(data);

        var template = data!.ConstructorArguments.FirstOrDefault().Value as string;
        Assert.False(string.IsNullOrWhiteSpace(template));
        Assert.Contains("-f|--follow", template!, StringComparison.Ordinal);
    }
}
