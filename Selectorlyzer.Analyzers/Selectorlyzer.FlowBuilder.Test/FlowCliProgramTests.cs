using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Selectorlyzer.FlowBuilder;
using Selectorlyzer.FlowCli;
using Xunit;

namespace Selectorlyzer.FlowBuilder.Test;

public class FlowCliProgramTests
{
    [Fact]
    public void FormatNode_UsesHttpMethodWhenVerbIsMissing()
    {
        var node = CreateNode(new Dictionary<string, object?>
        {
            ["http_method"] = "GET"
        });

        var formatted = InvokeFormatNode(node);

        formatted.Should().Contain("verb=GET");
        formatted.Should().NotContain("http_method");
    }

    [Fact]
    public void FormatNode_DoesNotDuplicateVerbWhenBothArePresent()
    {
        var node = CreateNode(new Dictionary<string, object?>
        {
            ["verb"] = "POST",
            ["http_method"] = "POST"
        });

        var formatted = InvokeFormatNode(node);

        Regex.Matches(formatted, "verb=POST").Count.Should().Be(1);
        formatted.Should().NotContain("http_method");
    }

    private static FlowNode CreateNode(IReadOnlyDictionary<string, object?>? properties)
    {
        return new FlowNode
        {
            Id = "node-id",
            Type = "endpoint.controller_action",
            Name = "SampleAction",
            Fqdn = "Sample.Controller.SampleAction",
            Assembly = "SampleAssembly",
            Properties = properties
        };
    }

    private static string InvokeFormatNode(FlowNode node)
    {
        var method = typeof(Program).GetMethod("FormatNode", BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        return (string)method!.Invoke(null, new object[] { node })!;
    }
}
