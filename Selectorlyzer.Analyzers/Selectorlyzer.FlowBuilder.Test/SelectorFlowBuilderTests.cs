using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Selectorlyzer.FlowBuilder;
using Selectorlyzer.TestUtilities;
using Xunit;

namespace Selectorlyzer.FlowBuilder.Tests
{
    public class SelectorFlowBuilderTests
    {
        [Fact]
        public void Build_ExpandsFlowsAcrossAllConnectedTypes()
        {
            var (compilation, _, queryContext) = FlowBuilderSample.BuildComprehensiveSample();
            var builder = new SelectorFlowBuilder();
            var graph = builder.Build(compilation, queryContext);

            graph.Nodes.Should().NotBeEmpty();
            graph.Edges.Should().NotBeEmpty();

            var controller = graph.Nodes.Single(n => n.Fqdn.EndsWith("UserController"));
            var reachable = CollectReachable(graph, controller.Id)
                .Select(n => n.Fqdn)
                .ToArray();

            reachable.Should().Contain(f => f.Contains("Sample.Services.IUserService"));
            reachable.Should().Contain(f => f.Contains("Sample.Data.IUserRepository"));
            reachable.Should().Contain(f => f.Contains("Sample.Data.UserEntity"));
            reachable.Should().Contain(f => f.Contains("Sample.Contracts.UserDto"));
            reachable.Should().Contain(f => f.Contains("Sample.Configuration.UserSettings"));
            reachable.Should().Contain(f => f.Contains("Sample.IMediator.SendAsync"));
            reachable.Should().Contain(f => f.Contains("Sample.IMemoryCache.TryGetValue"));
            reachable.Should().Contain(f => f.Contains("Sample.IMapper.Map"));
            reachable.Should().Contain(f => f.Contains("Sample.IHttpClientFactory"));
            reachable.Should().Contain(f => f.Contains("Sample.Guard.Null"));
            reachable.Should().Contain(f => f.Contains("Sample.Messaging.GetUserQueryHandler"));
        }

        [Fact]
        public void Build_CapturesHttpCallMetadata()
        {
            var (compilation, _, queryContext) = FlowBuilderSample.BuildComprehensiveSample();
            var builder = new SelectorFlowBuilder();
            var graph = builder.Build(compilation, queryContext);

            var httpCall = graph.Nodes.Should().ContainSingle(n => n.Type == "infra.http_call").Subject;
            httpCall.Properties.Should().NotBeNull();
            httpCall.Properties!.Should().ContainKey("verb", "available keys: {0}", string.Join(",", httpCall.Properties!.Keys));
            httpCall.Properties!.Should().ContainKey("client_type", "available keys: {0}", string.Join(",", httpCall.Properties!.Keys));
            httpCall.Properties!["verb"]?.ToString().Should().Be("GET");
            httpCall.Properties!["client_type"]?.ToString().Should().Contain("HttpClient");
        }

        [Fact]
        public void Build_AppliesCapturedSelectorProperties()
        {
            var syntaxTree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("class DemoService { }");
            var references = new[] { Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Test", new[] { syntaxTree }, references);

            var selectorNodeRuleType = typeof(SelectorFlowBuilder).Assembly.GetType("Selectorlyzer.FlowBuilder.SelectorNodeRule", throwOnError: true)!;
            var createMethod = selectorNodeRuleType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!;
            var rule = createMethod.Invoke(null, new object?[] { "sample.captured", ":class:capture(flow_id, Symbol.Name)", null, true, null });
            var rules = Array.CreateInstance(selectorNodeRuleType, 1);
            rules.SetValue(rule, 0);

            var builder = (SelectorFlowBuilder)Activator.CreateInstance(
                typeof(SelectorFlowBuilder),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: new object?[] { rules },
                culture: null)!;

            var graph = builder.Build(compilation);
            graph.Nodes.Should().ContainSingle();

            var node = graph.Nodes.Single();
            node.Properties.Should().NotBeNull();
            node.Properties!.Should().ContainKey("flow_id");
            node.Properties!["flow_id"]?.ToString().Should().Be("DemoService");
        }

        private static IReadOnlyList<FlowNode> CollectReachable(FlowGraph graph, string startId)
        {
            var nodesById = graph.Nodes.ToDictionary(n => n.Id);
            var adjacency = graph.Edges
                .GroupBy(e => e.From)
                .ToDictionary(g => g.Key, g => g.Select(e => e.To).ToArray());

            var visited = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue(startId);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!visited.Add(current))
                {
                    continue;
                }

                if (!adjacency.TryGetValue(current, out var targets))
                {
                    continue;
                }

                foreach (var target in targets)
                {
                    queue.Enqueue(target);
                }
            }

            return visited
                .Select(id => nodesById[id])
                .ToArray();
        }
    }
}
