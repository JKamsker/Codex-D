using CodexD.Shared.Output;
using Xunit;

namespace CodexD.Tests;

public sealed class OutputFormatTests
{
    [Theory]
    [InlineData("human", OutputFormat.Human)]
    [InlineData("text", OutputFormat.Human)]
    [InlineData("pretty", OutputFormat.Human)]
    [InlineData("json", OutputFormat.Json)]
    [InlineData("jsonl", OutputFormat.Jsonl)]
    [InlineData("ndjson", OutputFormat.Jsonl)]
    [InlineData("JSONL", OutputFormat.Jsonl)]
    public void Parse_KnownValues(string value, OutputFormat expected)
    {
        Assert.Equal(expected, OutputFormatParser.Parse(value));
    }

    [Fact]
    public void Resolve_Default_IsHuman()
    {
        Assert.Equal(OutputFormat.Human, OutputFormatParser.Resolve(null, jsonFlag: false, OutputFormatUsage.Single));
    }

    [Fact]
    public void Resolve_JsonFlag_Single_IsJson()
    {
        Assert.Equal(OutputFormat.Json, OutputFormatParser.Resolve(null, jsonFlag: true, OutputFormatUsage.Single));
    }

    [Fact]
    public void Resolve_JsonFlag_Streaming_IsJsonl()
    {
        Assert.Equal(OutputFormat.Jsonl, OutputFormatParser.Resolve(null, jsonFlag: true, OutputFormatUsage.Streaming));
    }

    [Fact]
    public void Resolve_OutputFormatJson_Streaming_IsJsonl()
    {
        Assert.Equal(OutputFormat.Jsonl, OutputFormatParser.Resolve("json", jsonFlag: false, OutputFormatUsage.Streaming));
    }

    [Fact]
    public void Resolve_OutputFormat_WinsOverJsonFlag()
    {
        Assert.Equal(OutputFormat.Human, OutputFormatParser.Resolve("human", jsonFlag: true, OutputFormatUsage.Streaming));
    }

    [Fact]
    public void Parse_Invalid_Throws()
    {
        Assert.Throws<ArgumentException>(() => OutputFormatParser.Parse("nope"));
    }
}

