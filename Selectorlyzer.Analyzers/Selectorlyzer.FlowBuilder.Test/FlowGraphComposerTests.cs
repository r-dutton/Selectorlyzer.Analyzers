using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Selectorlyzer.FlowBuilder;
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

        var composition = composer.CreateComposition();
        composition.AddGraph(graphA);
        composition.AddGraph(graphB);
        var streamed = composition.Build();

        streamed.Nodes.Should().BeEquivalentTo(combined.Nodes);
        streamed.Edges.Should().BeEquivalentTo(combined.Edges);
    }

    [Fact]
    public void Compose_SkipsRemoteEdgesWhenMetadataIsMissing()
    {
        using var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);

        try
        {
            var workspace = new FlowWorkspaceDefinition(Environment.CurrentDirectory);
            var composer = new FlowGraphComposer(workspace);

            var graph = BuildGraphWithMissingMetadata(actionCount: 250, httpCallCount: 50);
            var combined = composer.Compose(new[] { graph });

            combined.Edges.Should().NotBeEmpty();
            combined.Edges.Should().OnlyContain(e => e.Kind != "remote");
            combined.Edges.Should().Count(e => e.Kind == "flow").Should().Be(50);

            listener.Messages.Should().Contain(message => message.Contains("Skipping remote edge augmentation", StringComparison.Ordinal));
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public void Compose_RemainsApproximatelyLinearWhenMetadataIsMissing()
    {
        var workspace = new FlowWorkspaceDefinition(Environment.CurrentDirectory);
        var composer = new FlowGraphComposer(workspace);

        var smallGraph = BuildGraphWithMissingMetadata(actionCount: 200, httpCallCount: 200);
        var largeGraph = BuildGraphWithMissingMetadata(actionCount: 1000, httpCallCount: 1000);

        composer.Compose(new[] { smallGraph });
        composer.Compose(new[] { largeGraph });

        var smallDuration = Measure(composer, smallGraph, iterations: 8);
        var largeDuration = Measure(composer, largeGraph, iterations: 4);

        var sizeRatio = (double)(largeGraph.Nodes.Length + largeGraph.Edges.Length)
            / Math.Max(1, smallGraph.Nodes.Length + smallGraph.Edges.Length);
        var timeRatio = largeDuration.Ticks / (double)Math.Max(1, smallDuration.Ticks);

        largeDuration.Should().BeGreaterThan(TimeSpan.Zero);
        timeRatio.Should().BeLessThanOrEqualTo(sizeRatio * 3);

        var smallComposed = composer.Compose(new[] { smallGraph });
        var largeComposed = composer.Compose(new[] { largeGraph });

        smallComposed.Edges.Should().OnlyContain(e => e.Kind != "remote");
        largeComposed.Edges.Should().OnlyContain(e => e.Kind != "remote");
    }

    private static FlowGraph BuildGraphWithMissingMetadata(int actionCount, int httpCallCount)
    {
        var nodes = new List<FlowNode>();
        var edges = new List<FlowEdge>();

        for (int i = 0; i < actionCount; i++)
        {
            nodes.Add(new FlowNode
            {
                Id = $"action-{i}",
                Type = "endpoint.controller_action",
                Name = $"Action{i}",
                Fqdn = $"Sample.Controller.Action{i}",
                Assembly = "ServiceAssembly",
                Properties = new Dictionary<string, object?>
                {
                    ["route"] = $"/resource/{i}",
                    ["http_method"] = "GET"
                }
            });
        }

        for (int i = 0; i < httpCallCount; i++)
        {
            var callerId = $"caller-{i}";
            nodes.Add(new FlowNode
            {
                Id = callerId,
                Type = "code.method",
                Name = $"Caller{i}",
                Fqdn = $"Sample.Client.Caller{i}",
                Assembly = "ClientAssembly"
            });

            nodes.Add(new FlowNode
            {
                Id = $"call-{i}",
                Type = "infra.http_call",
                Name = $"Call{i}",
                Fqdn = $"Sample.Client.Call{i}",
                Assembly = "ClientAssembly",
                Properties = new Dictionary<string, object?>
                {
                    ["caller_id"] = callerId
                }
            });
        }

        return new FlowGraph(nodes, edges);
    }

    private static TimeSpan Measure(FlowGraphComposer composer, FlowGraph graph, int iterations)
    {
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            composer.Compose(new[] { graph });
        }

        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private sealed class CapturingTraceListener : TraceListener
    {
        public List<string> Messages { get; } = new();

        public override void TraceEvent(TraceEventCache? eventCache, string? source, TraceEventType eventType, int id, string? message)
        {
            if (message is not null)
            {
                Messages.Add(message);
            }
        }

        public override void Write(string? message)
        {
            if (message is not null)
            {
                Messages.Add(message);
            }
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
            {
                Messages.Add(message);
            }
        }
    }
}
