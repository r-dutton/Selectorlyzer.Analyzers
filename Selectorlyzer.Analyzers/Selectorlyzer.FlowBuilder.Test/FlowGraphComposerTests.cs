using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Selectorlyzer.Qulaly.Matcher;
using Xunit;

namespace Selectorlyzer.FlowBuilder.Tests;

public class FlowGraphComposerTests
{
    [Fact]
    public void Compose_AddsRemoteEdgesForHttpCalls()
    {
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(HttpClient).Assembly.Location)
        };

        const string solutionACode = @"using System.Net.Http;
using System.Threading.Tasks;

namespace SolutionA;

public abstract class ControllerBase { }

public sealed class ReportsClient
{
    private readonly HttpClient _http;
    public ReportsClient(HttpClient http) => _http = http;
    public Task<string> GetReportsAsync() => _http.GetStringAsync(""/reports"");
}

public sealed class DashboardController : ControllerBase
{
    private readonly ReportsClient _client;
    public DashboardController(ReportsClient client) => _client = client;
    public Task<string> GetAsync() => _client.GetReportsAsync();
}
";

        const string solutionBCode = @"using System;

namespace SolutionB;

public abstract class ControllerBase { }

[Route(""/reports"")]
public sealed class ReportsController : ControllerBase
{
    [HttpGet(""/reports"")]
    public string Get() => ""ok"";
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class RouteAttribute : Attribute
{
    public RouteAttribute(string template) => Template = template;
    public string Template { get; }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class HttpGetAttribute : Attribute
{
    public HttpGetAttribute(string template) => Template = template;
    public string Template { get; }
}
";

        var compilationA = CSharpCompilation.Create(
            "SolutionA",
            new[] { CSharpSyntaxTree.ParseText(solutionACode) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var compilationB = CSharpCompilation.Create(
            "SolutionB",
            new[] { CSharpSyntaxTree.ParseText(solutionBCode) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var builder = new SelectorFlowBuilder();

        var baseContextA = new SelectorQueryContext(metadata: new Dictionary<string, object?>
        {
            ["project"] = "SolutionA",
            ["assembly"] = "SolutionA"
        });
        var baseContextB = new SelectorQueryContext(metadata: new Dictionary<string, object?>
        {
            ["project"] = "SolutionB",
            ["assembly"] = "SolutionB"
        });

        var graphA = builder.Build(compilationA, baseContextA);
        var graphB = builder.Build(compilationB, baseContextB);

        var services = new[]
        {
            new FlowServiceDefinition("SolutionAWeb", null, new[] { "SolutionA" }),
            new FlowServiceDefinition("ReportsApi", null, new[] { "SolutionB" })
        };

        var bindings = new[]
        {
            new FlowServiceBinding("SolutionAWeb", "SolutionA.ReportsClient", "ReportsApi")
        };

        var workspace = new FlowWorkspaceDefinition(
            System.IO.Directory.GetCurrentDirectory(),
            new[] { "SolutionA.sln", "SolutionB.sln" },
            services,
            bindings);

        var composer = new FlowGraphComposer(workspace);
        var combined = composer.Compose(new[] { graphA, graphB });

        combined.Edges.Should().Contain(e => e.Kind == "remote");
        var remoteEdge = combined.Edges.First(e => e.Kind == "remote");
        var remoteTarget = combined.Nodes.Single(n => n.Id == remoteEdge.To);
        remoteTarget.Assembly.Should().Be("SolutionB");

        var callNode = combined.Nodes.Should().ContainSingle(n => n.Type == "infra.http_call").Subject;
        combined.Edges.Should().Contain(e => e.To == callNode.Id && e.Kind == "flow");
    }
}
