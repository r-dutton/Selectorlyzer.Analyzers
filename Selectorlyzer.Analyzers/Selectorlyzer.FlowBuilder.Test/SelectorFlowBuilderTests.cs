using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Selectorlyzer.FlowBuilder;
using Selectorlyzer.Qulaly.Matcher;
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

        [Fact]
        public void Build_AddsMissingSyntaxTreesBeforeAnalyzingSymbols()
        {
            var primaryTree = CSharpSyntaxTree.ParseText("namespace Sample { class Entry { } }");
            var externalTree = CSharpSyntaxTree.ParseText("namespace External { public class Extra { } }");
            var references = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };

            var compilation = CSharpCompilation.Create("Primary", new[] { primaryTree }, references);
            var externalCompilation = CSharpCompilation.Create("External", new[] { externalTree }, references);
            var extraSymbol = externalCompilation.GetTypeByMetadataName("External.Extra")!;

            var selectorNodeRuleType = typeof(SelectorFlowBuilder).Assembly.GetType("Selectorlyzer.FlowBuilder.SelectorNodeRule", throwOnError: true)!;
            var createMethod = selectorNodeRuleType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!;
            var rule = createMethod.Invoke(null, new object?[] { "external.type", ":class", null, true, null });
            var rules = Array.CreateInstance(selectorNodeRuleType, 1);
            rules.SetValue(rule, 0);

            var builder = (SelectorFlowBuilder)Activator.CreateInstance(
                typeof(SelectorFlowBuilder),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: new object?[] { rules },
                culture: null)!;

            var queryContext = new SelectorQueryContext(
                compilation: null,
                metadata: null,
                symbolResolver: (_, _) => extraSymbol);

            var graph = builder.Build(compilation, queryContext);

            graph.Nodes.Should().Contain(node => node.Fqdn == "External.Extra");
        }

        [Fact]
        public void Build_ScalesLinearly_ForLargeTypeGraph()
        {
            var smallCompilation = CreateLargeTypeGraph(150);
            var largeCompilation = CreateLargeTypeGraph(300);

            var smallBuilder = CreateCatchAllBuilder();
            smallBuilder.Build(smallCompilation);

            var stopwatch = Stopwatch.StartNew();
            smallBuilder.Build(smallCompilation);
            stopwatch.Stop();
            var smallDuration = stopwatch.Elapsed;
            smallDuration.Should().BeGreaterThan(TimeSpan.Zero);

            var largeBuilder = CreateCatchAllBuilder();
            largeBuilder.Build(largeCompilation);

            stopwatch.Restart();
            largeBuilder.Build(largeCompilation);
            stopwatch.Stop();
            var largeDuration = stopwatch.Elapsed;

            var ratio = largeDuration.TotalMilliseconds / Math.Max(smallDuration.TotalMilliseconds, 1d);
            ratio.Should().BeLessThan(6d);
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

        private static SelectorFlowBuilder CreateCatchAllBuilder()
        {
            var selectorNodeRuleType = typeof(SelectorFlowBuilder).Assembly.GetType("Selectorlyzer.FlowBuilder.SelectorNodeRule", throwOnError: true)!;
            var createMethod = selectorNodeRuleType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!;
            var rule = createMethod.Invoke(null, new object?[] { "perf.all", ":class", null, true, null });
            var rules = Array.CreateInstance(selectorNodeRuleType, 1);
            rules.SetValue(rule, 0);

            return (SelectorFlowBuilder)Activator.CreateInstance(
                typeof(SelectorFlowBuilder),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: new object?[] { rules },
                culture: null)!;
        }

        private static Compilation CreateLargeTypeGraph(int size)
        {
            var source = new StringBuilder();
            source.AppendLine("namespace LargeGraph");
            source.AppendLine("{");
            source.AppendLine("    public interface IRequest { }");
            source.AppendLine("    public interface INotification { }");
            source.AppendLine("    public interface IRequestHandler<TRequest> where TRequest : IRequest { void Handle(TRequest request); }");
            source.AppendLine("    public interface IRequestProcessor<TRequest> where TRequest : IRequest { void Process(TRequest request); }");
            source.AppendLine("    public interface IPipelineBehavior<TRequest> where TRequest : IRequest { void Process(TRequest request); }");
            source.AppendLine("    public interface INotificationHandler<TNotification> where TNotification : INotification { void Handle(TNotification notification); }");
            source.AppendLine("    public interface IServiceBase { void Execute(); }");
            source.AppendLine("    public class BaseType0 : IServiceBase { public virtual void Execute() { } }");
            for (var i = 1; i <= size; i++)
            {
                source.AppendLine($"    public class BaseType{i} : BaseType{i - 1} {{ public override void Execute() {{ base.Execute(); }} }}");
            }

            for (var i = 0; i < size; i++)
            {
                source.AppendLine($"    public interface IService{i} : IServiceBase {{ void Run{i}(); }}");
                source.AppendLine($"    public class Service{i} : BaseType{i + 1}, IService{i} {{ public override void Execute() {{ base.Execute(); }} public void Run{i}() {{ }} }}");
                source.AppendLine($"    public class Request{i} : IRequest {{ }}");
                source.AppendLine($"    public class Request{i}Handler : IRequestHandler<Request{i}>, IRequestProcessor<Request{i}>, IPipelineBehavior<Request{i}> {{ public void Handle(Request{i} request) {{ }} public void Process(Request{i} request) {{ }} }}");
                source.AppendLine($"    public class Notification{i} : INotification {{ }}");
                source.AppendLine($"    public class Notification{i}Handler : INotificationHandler<Notification{i}> {{ public void Handle(Notification{i} notification) {{ }} }}");
            }

            source.AppendLine("}");

            var syntaxTree = CSharpSyntaxTree.ParseText(source.ToString());
            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
            };

            return CSharpCompilation.Create("LargeGraph", new[] { syntaxTree }, references);
        }
    }
}
