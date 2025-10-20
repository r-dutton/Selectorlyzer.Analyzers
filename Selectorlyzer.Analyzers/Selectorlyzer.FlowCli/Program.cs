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
        if (argsList.Any(a => string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase)))
        {
            PrintHelp();
            return 0;
        }

        string workspaceRoot = Directory.GetCurrentDirectory();
        var explicitSolutions = new List<string>();
        var flowPatterns = new List<string>();
        int? maxDepth = null;
        int concurrency = -1;
        string? dumpGraphPath = null;
        string? outputDirectory = null;

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
                case "--output-dir":
                    if (i + 1 < argsList.Count)
                    {
                        outputDirectory = argsList[++i];
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

        var resolvedOutputDirectory = string.IsNullOrWhiteSpace(outputDirectory)
            ? null
            : ResolvePath(workspaceRoot, outputDirectory);

        RenderFlows(combined, flowPatterns, maxDepth, resolvedOutputDirectory);
        return 0;
    }

    private static void RenderFlows(FlowGraph graph, List<string> patterns, int? maxDepth, string? outputDirectory)
    {
        var nodesById = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var edgesByFrom = graph.Edges
            .GroupBy(e => e.From)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        var controllerNodes = graph.Nodes
            .Where(n => string.Equals(n.Type, "endpoint.controller", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(n => n.Id, StringComparer.Ordinal);

        var actions = graph.Nodes
            .Where(n => string.Equals(n.Type, "endpoint.controller_action", StringComparison.OrdinalIgnoreCase))
            .Select(action => new
            {
                Action = action,
                ControllerId = action.Properties is not null && action.Properties.TryGetValue("controller_id", out var controllerId)
                    ? controllerId?.ToString()
                    : null
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ControllerId))
            .Where(entry => MatchesPatterns(entry.Action, controllerNodes.TryGetValue(entry.ControllerId!, out var controller) ? controller : null, patterns))
            .ToList();

        if (actions.Count == 0)
        {
            Console.WriteLine("No controllers matched the requested flows.");
            return;
        }

        var groupedActions = actions
            .GroupBy(entry => entry.ControllerId!, StringComparer.Ordinal)
            .OrderBy(group => controllerNodes.TryGetValue(group.Key, out var controller)
                ? controller.Fqdn
                : group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (groupedActions.Count == 0)
        {
            Console.WriteLine("No controllers matched the requested flows.");
            return;
        }

        if (outputDirectory is not null)
        {
            Directory.CreateDirectory(outputDirectory);
        }

        foreach (var group in groupedActions)
        {
            controllerNodes.TryGetValue(group.Key, out var controller);
            var controllerHeading = controller is not null
                ? $"controller -> {FormatNode(controller)}"
                : $"controller -> (missing: {group.Key})";

            var actionNodes = group.Select(entry => entry.Action)
                .OrderBy(action => action.Fqdn, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var actionSummaries = actionNodes
                .Select(action => action.Name)
                .ToArray();

            using var controllerWriter = new StringWriter();
            controllerWriter.WriteLine(controllerHeading);
            controllerWriter.WriteLine($"  actions: {string.Join(", ", actionSummaries)}");

            foreach (var action in actionNodes)
            {
                controllerWriter.WriteLine();
                controllerWriter.WriteLine($"action -> {FormatNode(action)}");
                var visited = new HashSet<string>(StringComparer.Ordinal) { action.Id };
                if (!edgesByFrom.TryGetValue(action.Id, out var actionEdges))
                {
                    continue;
                }

                foreach (var edge in actionEdges)
                {
                    if (nodesById.TryGetValue(edge.To, out var target))
                    {
                        RenderNode(nodesById, edgesByFrom, edge, target, 1, maxDepth, visited, controllerWriter);
                    }
                }
            }

            var sectionText = controllerWriter.ToString().TrimEnd();
            if (outputDirectory is null)
            {
                Console.WriteLine(sectionText);
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine($"{controllerHeading} [{string.Join(", ", actionSummaries)}]");
                var controllerName = controller?.Name ?? controller?.Fqdn ?? group.Key;
                var fileName = SanitizeFileName(controllerName) + ".flow.txt";
                var destination = Path.Combine(outputDirectory, fileName);
                File.WriteAllText(destination, sectionText + Environment.NewLine);
            }
        }
    }

    private static void RenderNode(
        Dictionary<string, FlowNode> nodesById,
        Dictionary<string, FlowEdge[]> edgesByFrom,
        FlowEdge edge,
        FlowNode node,
        int depth,
        int? maxDepth,
        HashSet<string> visited,
        TextWriter writer)
    {
        var label = edge.Kind.Equals("remote", StringComparison.OrdinalIgnoreCase) ? "remote" : edge.Kind;
        writer.WriteLine($"{new string(' ', depth * 2)}{label} -> {FormatNode(node)}");

        if (maxDepth.HasValue && depth >= maxDepth.Value)
        {
            writer.WriteLine($"{new string(' ', (depth + 1) * 2)}... (max depth reached)");
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
                RenderNode(nodesById, edgesByFrom, child, target, depth + 1, maxDepth, visited, writer);
            }
        }
    }

    private static bool MatchesPatterns(FlowNode action, FlowNode? controller, List<string> patterns)
    {
        if (patterns.Count == 0)
        {
            return true;
        }

        var normalizedPatterns = patterns
            .Select(Normalize)
            .Where(p => p is not null)
            .Select(p => p!)
            .ToArray();

        if (normalizedPatterns.Length == 0)
        {
            return true;
        }

        var candidates = new HashSet<string>(StringComparer.Ordinal);

        void AddCandidate(string? value)
        {
            var normalized = Normalize(value);
            if (normalized is not null)
            {
                candidates.Add(normalized);
            }
        }

        void AddPropertyValues(IReadOnlyDictionary<string, object?>? properties)
        {
            if (properties is null)
            {
                return;
            }

            foreach (var property in properties)
            {
                switch (property.Value)
                {
                    case null:
                        continue;
                    case string text:
                        AddCandidate(text);
                        break;
                    case IEnumerable<string> textValues:
                        foreach (var text in textValues)
                        {
                            AddCandidate(text);
                        }
                        break;
                    case IEnumerable<object?> objectValues:
                        foreach (var value in objectValues)
                        {
                            AddCandidate(value?.ToString());
                        }
                        break;
                    default:
                        AddCandidate(property.Value.ToString());
                        break;
                }
            }
        }

        AddCandidate(action.Name);
        AddCandidate(action.Fqdn);
        AddPropertyValues(action.Properties);

        if (controller is not null)
        {
            AddCandidate(controller.Name);
            AddCandidate(controller.Fqdn);
            AddPropertyValues(controller.Properties);
        }

        foreach (var pattern in normalizedPatterns)
        {
            if (candidates.Any(candidate => candidate.Contains(pattern, StringComparison.Ordinal)))
            {
                return true;
            }

            var tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length <= 1)
            {
                continue;
            }

            var matchesAllTokens = true;
            foreach (var token in tokens)
            {
                if (!candidates.Any(candidate => candidate.Contains(token, StringComparison.Ordinal)))
                {
                    matchesAllTokens = false;
                    break;
                }
            }

            if (matchesAllTokens)
            {
                return true;
            }
        }

        return false;
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
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

    private static void PrintHelp()
    {
        Console.WriteLine("Selectorlyzer Flow CLI");
        Console.WriteLine();
        Console.WriteLine("Usage: selectorlyzer-flow [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --workspace <path>       Root directory that contains the flow workspace definition.");
        Console.WriteLine("  --solution <path>        Analyze a specific solution file.");
        Console.WriteLine("  --solutions <paths>      Comma-separated list of solution files to analyze.");
        Console.WriteLine("  --flow <pattern>         Filter controllers/actions whose name, FQDN, or metadata contains the pattern.");
        Console.WriteLine("  --flows <patterns>       Comma-separated list of patterns to filter controllers/actions by name, FQDN, or metadata.");
        Console.WriteLine("  --max-depth <number>     Limit traversal depth when rendering flows.");
        Console.WriteLine("  --concurrency <number>   Maximum number of compilations to analyze in parallel.");
        Console.WriteLine("  --dump-graph <path>      Write the composed graph to disk as JSON.");
        Console.WriteLine("  --output-dir <path>      Write each controller flow to <ControllerName>.flow.txt while printing a summary to stdout.");
        Console.WriteLine("  --help, -h               Display this help message.");
    }

    private static string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = name;
        foreach (var invalid in invalidChars)
        {
            sanitized = sanitized.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "controller" : sanitized;
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
