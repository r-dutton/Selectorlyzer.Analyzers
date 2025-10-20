using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Selectorlyzer.FlowBuilder;
using Selectorlyzer.Qulaly.Matcher;

namespace Selectorlyzer.FlowCli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        await Task.Yield();

        var argsList = args.ToList();
        string workspaceRoot = Directory.GetCurrentDirectory();
        var explicitSolutions = new List<string>();
        var flowPatterns = new List<string>();
        int? maxDepth = null;
    int concurrency = -1;
    string? dumpGraphPath = null;

        for (int i = 0; i < argsList.Count; i++)
        {
            switch (argsList[i])
            {
                case "--workspace":
                    if (i + 1 < argsList.Count)
                    {
                        workspaceRoot = Path.GetFullPath(argsList[++i]);
                    }
                    break;
                case "--solution":
                    if (i + 1 < argsList.Count)
                    {
                        explicitSolutions.Add(argsList[++i]);
                    }
                    break;
                case "--solutions":
                    if (i + 1 < argsList.Count)
                    {
                        explicitSolutions.AddRange(argsList[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    }
                    break;
                case "--flow":
                case "--flows":
                    if (i + 1 < argsList.Count)
                    {
                        flowPatterns.AddRange(argsList[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    }
                    break;
                case "--max-depth":
                    if (i + 1 < argsList.Count && int.TryParse(argsList[++i], out var depth) && depth >= 0)
                    {
                        maxDepth = depth;
                    }
                    break;
                case "--concurrency":
                    if (i + 1 < argsList.Count && int.TryParse(argsList[++i], out var parsedConcurrency))
                    {
                        concurrency = parsedConcurrency <= 0 ? -1 : parsedConcurrency;
                    }
                    break;
                case "--dump-graph":
                    if (i + 1 < argsList.Count)
                    {
                        dumpGraphPath = argsList[++i];
                    }
                    break;
            }
        }

        if (!Directory.Exists(workspaceRoot))
        {
            throw new DirectoryNotFoundException($"Workspace '{workspaceRoot}' could not be found.");
        }

        var workspaceDefinition = FlowWorkspaceLoader.Load(workspaceRoot);
        var solutionPaths = explicitSolutions.Count > 0
            ? explicitSolutions.Select(path => ResolvePath(workspaceRoot, path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : workspaceDefinition.SolutionPaths.ToList();

        if (solutionPaths.Count == 0)
        {
            Console.WriteLine("No solutions discovered. Provide --solution or add a flow.workspace.json file.");
            return 0;
        }

        var builder = new SelectorFlowBuilder();
        var composer = new FlowGraphComposer(workspaceDefinition);
        var composition = composer.CreateComposition();
        int graphCount = 0;

        foreach (var solutionPath in solutionPaths)
        {
            Console.WriteLine($"[flow] Loading solution {solutionPath}");
            var solutionModel = SolutionProjectLoader.Load(solutionPath);
            Console.WriteLine($"[flow] Loaded {solutionModel.Projects.Count} projects");

            var factory = new ProjectCompilationFactory(solutionModel, concurrency);
            factory.BuildAll(CancellationToken.None);

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency <= 0 ? -1 : concurrency
            };

            Parallel.ForEach(
               solutionModel.Projects.Where(p => !p.ProjectName.Contains(".Tests", StringComparison.OrdinalIgnoreCase)), parallelOptions, project =>
            {
                Console.WriteLine($"[flow] Analyzing project {project.ProjectName}");
                var compilation = factory.GetCompilation(project);
                if (compilation.SyntaxTrees.Length == 0)
                {
                    return;
                }

                var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["project"] = project.ProjectName,
                    ["assembly"] = project.AssemblyName,
                    ["solution"] = solutionModel.SolutionPath
                };

                var baseContext = new SelectorQueryContext(compilation: compilation, metadata: metadata);
                var graph = builder.Build(compilation, baseContext);
                if (graph.Nodes.Length == 0 && graph.Edges.Length == 0)
                {
                    return;
                }

                composition.AddGraph(graph);
                Interlocked.Increment(ref graphCount);
            });
        }

        if (graphCount == 0)
        {
            Console.WriteLine("No compilations produced any graph data.");
            return 0;
        }

        var combined = composition.Build();

        if (!string.IsNullOrWhiteSpace(dumpGraphPath))
        {
            DumpGraph(combined, ResolvePath(workspaceRoot, dumpGraphPath!));
        }

        RenderFlows(combined, flowPatterns, maxDepth);
        return 0;
    }

    private static void RenderFlows(FlowGraph graph, List<string> patterns, int? maxDepth)
    {
        var nodesById = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var edgesByFrom = graph.Edges
            .GroupBy(e => e.From)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        var controllers = graph.Nodes
            .Where(n => string.Equals(n.Type, "endpoint.controller", StringComparison.OrdinalIgnoreCase))
            .Where(n => patterns.Count == 0 || patterns.Any(p => n.Fqdn.Contains(p, StringComparison.OrdinalIgnoreCase) || n.Name.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => n.Fqdn, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (controllers.Count == 0)
        {
            Console.WriteLine("No controllers matched the requested flows.");
            return;
        }

        foreach (var controller in controllers)
        {
            Console.WriteLine($"controller -> {FormatNode(controller)}");
            var visited = new HashSet<string>(StringComparer.Ordinal) { controller.Id };
            if (edgesByFrom.TryGetValue(controller.Id, out var controllerEdges))
            {
                foreach (var edge in controllerEdges)
                {
                    if (nodesById.TryGetValue(edge.To, out var target))
                    {
                        RenderNode(nodesById, edgesByFrom, edge, target, 1, maxDepth, visited);
                    }
                }
            }

            Console.WriteLine();
        }
    }

    private static void RenderNode(
        Dictionary<string, FlowNode> nodesById,
        Dictionary<string, FlowEdge[]> edgesByFrom,
        FlowEdge edge,
        FlowNode node,
        int depth,
        int? maxDepth,
        HashSet<string> visited)
    {
        var label = edge.Kind.Equals("remote", StringComparison.OrdinalIgnoreCase) ? "remote" : edge.Kind;
        Console.WriteLine($"{new string(' ', depth * 2)}{label} -> {FormatNode(node)}");

        if (maxDepth.HasValue && depth >= maxDepth.Value)
        {
            Console.WriteLine($"{new string(' ', (depth + 1) * 2)}... (max depth reached)");
            return;
        }

        if (!visited.Add(node.Id))
        {
            return;
        }

        if (!edgesByFrom.TryGetValue(node.Id, out var childEdges))
        {
            return;
        }

        foreach (var child in childEdges)
        {
            if (nodesById.TryGetValue(child.To, out var target))
            {
                RenderNode(nodesById, edgesByFrom, child, target, depth + 1, maxDepth, visited);
            }
        }
    }

    private static string FormatNode(FlowNode node)
    {
        var assembly = string.IsNullOrWhiteSpace(node.Assembly) ? string.Empty : $" [{node.Assembly}]";
        var details = new List<string>();
        if (node.Properties is { Count: > 0 })
        {
            if (node.Properties.TryGetValue("verb", out var verb) && verb is not null)
            {
                details.Add($"verb={verb}");
            }

            if (node.Properties.TryGetValue("full_route", out var fullRoute) && fullRoute is not null)
            {
                details.Add($"route={fullRoute}");
            }
            else if (node.Properties.TryGetValue("route", out var route) && route is not null)
            {
                details.Add($"route={route}");
            }
        }

        var detailText = details.Count > 0 ? " (" + string.Join(", ", details) + ")" : string.Empty;
        return $"{node.Type}: {node.Fqdn}{assembly}{detailText}";
    }

    private static string ResolvePath(string workspaceRoot, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return workspaceRoot;
        }

        return Path.GetFullPath(Path.IsPathRooted(candidate) ? candidate : Path.Combine(workspaceRoot, candidate));
    }

    private static void DumpGraph(FlowGraph graph, string destination)
    {
        var nodes = graph.Nodes.Select(node => new
        {
            node.Id,
            node.Type,
            node.Name,
            node.Fqdn,
            node.Assembly,
            node.Project,
            Span = node.Span is null ? null : new { node.Span.StartLine, node.Span.EndLine },
            node.SymbolId,
            Tags = node.Tags,
            Properties = node.Properties?.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value?.ToString(),
                StringComparer.OrdinalIgnoreCase)
        }).ToArray();

        var edges = graph.Edges.Select(edge => new
        {
            edge.From,
            edge.To,
            edge.Kind,
            edge.Source,
            edge.Confidence,
            Evidence = edge.Evidence is null
                ? null
                : edge.Evidence.Files.Select(file => new
                {
                    file.Path,
                    file.StartLine,
                    file.EndLine
                }).ToArray()
        }).ToArray();

        var payload = new { nodes, edges };
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        var json = JsonSerializer.Serialize(payload, options);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? Directory.GetCurrentDirectory());
        File.WriteAllText(destination, json);
        Console.WriteLine($"[flow] Graph dump written to {destination}");
    }
}
