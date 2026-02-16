using System;
using System.Linq;
using System.Reflection;
using CodexD.HttpRunner.Commands;
using CodexD.HttpRunner.Commands.Run;
using Spectre.Console.Cli;
using Xunit;

namespace CodexD.Tests;

public sealed class ReasoningSwitchAliasTests
{
    [Theory]
    [InlineData(typeof(ExecCommand.Settings))]
    [InlineData(typeof(ReviewCommand.Settings))]
    [InlineData(typeof(ResumeCommand.Settings))]
    public void EffortOption_ExposesReasoningAliases(Type settingsType)
    {
        var prop = settingsType.GetProperty("Effort", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(prop);

        var data = prop!.GetCustomAttributesData().SingleOrDefault(a => a.AttributeType == typeof(CommandOptionAttribute));
        Assert.NotNull(data);

        var template = data!.ConstructorArguments.FirstOrDefault().Value as string;
        Assert.False(string.IsNullOrWhiteSpace(template));
        Assert.Contains("-r|--reasoning", template!, StringComparison.Ordinal);
    }
}
