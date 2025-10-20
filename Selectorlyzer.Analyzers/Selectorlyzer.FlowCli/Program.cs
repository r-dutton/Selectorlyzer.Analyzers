using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        string? graphViewerPath = null;
        FlowOutputFormat outputFormat = FlowOutputFormat.Text;

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
                case "--output-format":
                    if (i + 1 < argsList.Count)
                    {
                        outputFormat = ParseOutputFormat(argsList[++i]);
                    }
                    break;
                case "--write-graph-viewer":
                    if (i + 1 < argsList.Count)
                    {
                        graphViewerPath = argsList[++i];
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

        if (!string.IsNullOrWhiteSpace(graphViewerPath))
        {
            WriteGraphViewer(ResolvePath(workspaceRoot, graphViewerPath!));
        }

        var resolvedOutputDirectory = string.IsNullOrWhiteSpace(outputDirectory)
            ? null
            : ResolvePath(workspaceRoot, outputDirectory);

        RenderFlows(combined, flowPatterns, maxDepth, resolvedOutputDirectory, outputFormat);
        return 0;
    }


    private static bool MatchesPatterns(FlowNode action, FlowNode? controller, List<string> patterns)
    {
        if (patterns.Count == 0)
        {
            return true;
        }

        return patterns.Any(pattern =>
            action.Fqdn.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
            action.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
            (controller is not null &&
                (controller.Fqdn.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                 controller.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))));
    }

    private static string FormatNode(FlowNode node)
    {
        var assembly = string.IsNullOrWhiteSpace(node.Assembly) ? string.Empty : $" [{node.Assembly}]";
        var details = new List<string>();
        if (node.Properties is { Count: > 0 })
        {
            string? verbText = null;
            if (node.Properties.TryGetValue("verb", out var verb) && verb is not null)
            {
                verbText = verb.ToString();
            }
            else if (node.Properties.TryGetValue("http_method", out var httpMethod) && httpMethod is not null)
            {
                verbText = httpMethod.ToString();
            }

            if (!string.IsNullOrWhiteSpace(verbText))
            {
                details.Add($"verb={verbText}");
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

    private static FlowActionFlow BuildActionFlow(
        FlowNode action,
        Dictionary<string, FlowNode> nodesById,
        Dictionary<string, FlowEdge[]> edgesByFrom,
        int? maxDepth)
    {
        var traversal = new FlowTraversal(action);
        var visited = new HashSet<string>(StringComparer.Ordinal) { action.Id };

        Traverse(action, 0);
        return new FlowActionFlow(action, traversal);

        void Traverse(FlowNode current, int depth)
        {
            if (!edgesByFrom.TryGetValue(current.Id, out var edges))
            {
                return;
            }

            foreach (var edge in edges)
            {
                if (!nodesById.TryGetValue(edge.To, out var target))
                {
                    continue;
                }

                var nextDepth = depth + 1;
                var reachedMaxDepth = maxDepth.HasValue && depth >= maxDepth.Value;
                var leadsToVisited = !visited.Add(target.Id);

                traversal.AddEdge(current.Id, new FlowTraversalEdge(edge, target, nextDepth, reachedMaxDepth, leadsToVisited));

                if (reachedMaxDepth || leadsToVisited)
                {
                    continue;
                }

                Traverse(target, nextDepth);
            }
        }
    }

    private static FlowOutputFormat ParseOutputFormat(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return FlowOutputFormat.Text;
        }

        if (Enum.TryParse(candidate, ignoreCase: true, out FlowOutputFormat parsed))
        {
            return parsed;
        }

        throw new ArgumentException($"Unsupported output format '{candidate}'. Supported values: {string.Join(", ", Enum.GetNames(typeof(FlowOutputFormat)))}.");
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
        Console.WriteLine("  --flow <pattern>         Filter controllers/actions whose name or FQDN contains the pattern.");
        Console.WriteLine("  --flows <patterns>       Comma-separated list of patterns to filter controllers/actions.");
        Console.WriteLine("  --max-depth <number>     Limit traversal depth when rendering flows.");
        Console.WriteLine("  --concurrency <number>   Maximum number of compilations to analyze in parallel.");
        Console.WriteLine("  --dump-graph <path>      Write the composed graph to disk as JSON.");
        Console.WriteLine("  --output-dir <path>      Write each controller flow to <ControllerName>.<format> while printing a summary to stdout.");
        Console.WriteLine("  --output-format <name>   Choose renderer: Text, Mermaid, Dot, PlantUml. Defaults to Text.");
        Console.WriteLine("  --write-graph-viewer <path>  Write an interactive HTML viewer for JSON graph dumps.");
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

    private static void WriteGraphViewer(string destination)
    {
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(destination, GraphViewerHtml);
        Console.WriteLine($"[flow] Graph viewer written to {destination}");
    }

    private static string GetEdgeLabel(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return "flow";
        }

        return kind.Equals("remote", StringComparison.OrdinalIgnoreCase) ? "remote" : kind;
    }

    private static readonly string GraphViewerHtml = """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <title>Selectorlyzer Flow Graph Viewer</title>
    <style>
        :root {
            color-scheme: light dark;
            font-family: system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
            background-color: var(--page-bg, #101418);
            color: var(--page-fg, #f5f7fa);
        }

        body {
            margin: 0;
            padding: 1.5rem;
            max-width: 1200px;
            margin-inline: auto;
        }

        h1 {
            margin-top: 0;
        }

        .controls {
            display: flex;
            flex-wrap: wrap;
            gap: 1rem;
            align-items: center;
            margin-bottom: 1.5rem;
        }

        .summary {
            margin-bottom: 1rem;
        }

        .panel {
            border: 1px solid rgba(128, 128, 128, 0.35);
            border-radius: 0.5rem;
            padding: 1rem;
            margin-bottom: 1rem;
        }

        .panel h2 {
            margin-top: 0;
            font-size: 1.15rem;
        }

        details summary {
            cursor: pointer;
        }

        .node-list {
            display: grid;
            grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
            gap: 0.75rem;
        }

        .node-card {
            border: 1px solid rgba(128, 128, 128, 0.25);
            border-radius: 0.4rem;
            padding: 0.75rem;
            background: rgba(128, 128, 128, 0.1);
        }

        .node-card h3 {
            margin: 0 0 0.5rem 0;
            font-size: 1rem;
        }

        .node-meta {
            font-size: 0.85rem;
            opacity: 0.8;
        }

        .edge-list {
            font-family: "Fira Mono", "SFMono-Regular", Consolas, monospace;
            font-size: 0.9rem;
            white-space: pre-wrap;
            background: rgba(128, 128, 128, 0.12);
            border-radius: 0.5rem;
            padding: 0.75rem;
        }

        .search-box {
            min-width: 220px;
            padding: 0.45rem 0.6rem;
            border-radius: 0.4rem;
            border: 1px solid rgba(128, 128, 128, 0.4);
            font-size: 1rem;
            background: transparent;
            color: inherit;
        }

        .badge {
            display: inline-block;
            padding: 0.2rem 0.5rem;
            border-radius: 999px;
            font-size: 0.75rem;
            background: rgba(128, 128, 128, 0.25);
            margin-inline-end: 0.5rem;
        }

        .footer {
            margin-top: 2rem;
            font-size: 0.85rem;
            opacity: 0.7;
        }
    </style>
</head>
<body>
    <h1>Selectorlyzer Flow Graph Viewer</h1>
    <p>Load a <code>.json</code> graph dump produced by <code>--dump-graph</code> to explore controllers, actions, and their flows.</p>

    <div class="controls">
        <label>
            <strong>Graph JSON:</strong>
            <input type="file" accept="application/json" id="fileInput">
        </label>
        <input type="search" placeholder="Filter by name, type, or route" id="search" class="search-box" disabled>
        <span id="status" class="badge">No graph loaded</span>
    </div>

    <div id="content"></div>

    <div class="footer">
        <p>This viewer runs entirely in your browser—no data ever leaves your machine.</p>
    </div>

    <script>
        const fileInput = document.getElementById('fileInput');
        const searchBox = document.getElementById('search');
        const status = document.getElementById('status');
        const content = document.getElementById('content');

        let graph = null;

        fileInput.addEventListener('change', async (event) => {
            const file = event.target.files?.[0];
            if (!file) {
                return;
            }

            try {
                const text = await file.text();
                graph = JSON.parse(text);
                status.textContent = `Loaded ${graph.nodes?.length ?? 0} nodes`;
                searchBox.disabled = false;
                render(graph, '');
            } catch (err) {
                status.textContent = 'Failed to parse graph';
                searchBox.disabled = true;
                content.innerHTML = '';
                console.error(err);
            }
        });

        searchBox.addEventListener('input', () => {
            if (!graph) {
                return;
            }
            render(graph, searchBox.value || '');
        });

        function render(graph, filter) {
            const query = filter.trim().toLowerCase();
            const nodes = Array.isArray(graph.nodes) ? graph.nodes : [];
            const edges = Array.isArray(graph.edges) ? graph.edges : [];

            const nodesById = new Map(nodes.map(node => [node.id ?? node.Id, node]));
            const edgesByFrom = new Map();
            for (const edge of edges) {
                const from = edge.from ?? edge.From;
                if (!from) continue;
                if (!edgesByFrom.has(from)) {
                    edgesByFrom.set(from, []);
                }
                edgesByFrom.get(from).push(edge);
            }

            const controllers = nodes.filter(node => (node.type ?? node.Type) === 'endpoint.controller');
            const filteredControllers = controllers.filter(ctrl => matchesQuery(ctrl, query));

            const fragment = document.createDocumentFragment();

            if (filteredControllers.length === 0) {
                const panel = document.createElement('div');
                panel.className = 'panel';
                panel.innerHTML = `<p>No controllers matched the filter <strong>${escapeHtml(filter)}</strong>.</p>`;
                fragment.append(panel);
            }

            for (const controller of filteredControllers) {
                const panel = document.createElement('div');
                panel.className = 'panel';

                const title = document.createElement('h2');
                title.textContent = controller.fqdn ?? controller.Fqdn ?? controller.name ?? controller.Name ?? 'Controller';
                panel.append(title);

                const meta = document.createElement('div');
                meta.className = 'summary';
                const assembly = controller.assembly ?? controller.Assembly;
                meta.innerHTML = `Type: <code>${escapeHtml(controller.type ?? controller.Type ?? 'unknown')}</code>${assembly ? ` • Assembly: <code>${escapeHtml(assembly)}</code>` : ''}`;
                panel.append(meta);

                const actions = nodes.filter(node => (node.type ?? node.Type) === 'endpoint.controller_action' && (node.properties?.controller_id ?? node.Properties?.controller_id) === (controller.id ?? controller.Id));
                const actionList = document.createElement('div');
                actionList.className = 'node-list';

                if (actions.length === 0) {
                    const empty = document.createElement('p');
                    empty.textContent = 'No actions were discovered for this controller.';
                    panel.append(empty);
                }

                for (const action of actions) {
                    if (query && !matchesQuery(action, query)) {
                        continue;
                    }

                    const card = document.createElement('div');
                    card.className = 'node-card';

                    const actionTitle = document.createElement('h3');
                    actionTitle.textContent = action.fqdn ?? action.Fqdn ?? action.name ?? action.Name ?? 'Action';
                    card.append(actionTitle);

                    const metaBlock = document.createElement('div');
                    metaBlock.className = 'node-meta';
                    metaBlock.innerHTML = renderProperties(action.properties ?? action.Properties);
                    card.append(metaBlock);

                    const flowDetails = document.createElement('details');
                    const summary = document.createElement('summary');
                    summary.textContent = 'Flow edges';
                    flowDetails.append(summary);

                    const edgesForAction = collectEdges(action.id ?? action.Id, edgesByFrom, nodesById);
                    const edgeText = edgesForAction.length === 0
                        ? 'No outbound edges'
                        : edgesForAction.map(item => `${item.label} -> ${item.target}`).join('\n');

                    const edgeList = document.createElement('div');
                    edgeList.className = 'edge-list';
                    edgeList.textContent = edgeText;
                    flowDetails.append(edgeList);

                    card.append(flowDetails);
                    actionList.append(card);
                }

                if (actionList.childElementCount > 0) {
                    panel.append(actionList);
                }

                fragment.append(panel);
            }

            content.innerHTML = '';
            content.append(fragment);
        }

        function collectEdges(actionId, edgesByFrom, nodesById) {
            const results = [];
            const queue = [{ id: actionId, depth: 0 }];
            const visited = new Set([actionId]);

            while (queue.length > 0) {
                const current = queue.shift();
                const edges = edgesByFrom.get(current.id) ?? [];

                for (const edge of edges) {
                    const toId = edge.to ?? edge.To;
                    if (!toId) continue;
                    const target = nodesById.get(toId);
                    const label = (edge.kind ?? edge.Kind ?? 'flow');
                    results.push({
                        label: `${label} (${current.depth + 1})`,
                        target: target ? (target.fqdn ?? target.Fqdn ?? target.name ?? target.Name ?? toId) : toId
                    });

                    if (!visited.has(toId)) {
                        visited.add(toId);
                        queue.push({ id: toId, depth: current.depth + 1 });
                    }
                }
            }

            return results;
        }

        function matchesQuery(node, query) {
            if (!query) return true;
            const text = [node.name ?? node.Name, node.fqdn ?? node.Fqdn, node.type ?? node.Type, node.properties?.route ?? node.Properties?.route, node.properties?.full_route ?? node.Properties?.full_route]
                .filter(Boolean)
                .map(value => String(value).toLowerCase())
                .join(' ');
            return text.includes(query);
        }

        function renderProperties(properties) {
            if (!properties) return 'No additional metadata';
            const entries = Object.entries(properties);
            if (entries.length === 0) return 'No additional metadata';
            return entries
                .map(([key, value]) => `<code>${escapeHtml(key)}</code>: ${escapeHtml(String(value))}`)
                .join('<br>');
        }

        function escapeHtml(value) {
            return value
                .replace(/&/g, '&amp;')
                .replace(/</g, '&lt;')
                .replace(/>/g, '&gt;')
                .replace(/"/g, '&quot;')
                .replace(/'/g, '&#39;');
        }
    </script>
</body>
</html>
""";

    private enum FlowOutputFormat
    {
        Text,
        Mermaid,
        Dot,
        PlantUml
    }

    private interface IFlowRenderer
    {
        string FileExtension { get; }

        string Render(FlowNode? controller, IReadOnlyList<FlowActionFlow> actions);
    }

    private static class FlowRendererFactory
    {
        public static IFlowRenderer Create(FlowOutputFormat format)
        {
            return format switch
            {
                FlowOutputFormat.Mermaid => new MermaidFlowRenderer(),
                FlowOutputFormat.Dot => new DotFlowRenderer(),
                FlowOutputFormat.PlantUml => new PlantUmlFlowRenderer(),
                _ => new TextFlowRenderer()
            };
        }
    }

    private sealed class TextFlowRenderer : IFlowRenderer
    {
        public string FileExtension => ".flow.txt";

        public string Render(FlowNode? controller, IReadOnlyList<FlowActionFlow> actions)
        {
            using var writer = new StringWriter();
            var controllerHeading = controller is not null
                ? $"controller -> {FormatNode(controller)}"
                : "controller -> (missing)";
            writer.WriteLine(controllerHeading);
            writer.WriteLine($"  actions: {string.Join(", ", actions.Select(a => a.Action.Name))}");

            foreach (var actionFlow in actions)
            {
                writer.WriteLine();
                writer.WriteLine($"action -> {FormatNode(actionFlow.Action)}");
                RenderEdges(actionFlow, actionFlow.Action.Id, 1, writer);
            }

            return writer.ToString();
        }

        private void RenderEdges(FlowActionFlow flow, string sourceId, int depth, TextWriter writer)
        {
            if (!flow.Traversal.TryGetEdges(sourceId, out var edges) || edges.Count == 0)
            {
                return;
            }

            foreach (var edge in edges)
            {
                var label = GetEdgeLabel(edge.Edge.Kind);
                writer.WriteLine($"{new string(' ', depth * 2)}{label} -> {FormatNode(edge.Target)}");

                if (edge.IsMaxDepthEdge)
                {
                    writer.WriteLine($"{new string(' ', (depth + 1) * 2)}... (max depth reached)");
                    continue;
                }

                if (edge.LeadsToVisitedNode)
                {
                    continue;
                }

                RenderEdges(flow, edge.Target.Id, depth + 1, writer);
            }
        }
    }

    private sealed class MermaidFlowRenderer : IFlowRenderer
    {
        public string FileExtension => ".flow.mmd";

        public string Render(FlowNode? controller, IReadOnlyList<FlowActionFlow> actions)
        {
            var builder = new StringBuilder();
            builder.AppendLine("```mermaid");
            builder.AppendLine("flowchart TD");

            if (controller is not null)
            {
                builder.Append("    %% ");
                builder.AppendLine(FormatNode(controller));
            }

            var aliasMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var definedNodes = new HashSet<string>(StringComparer.Ordinal);
            var emittedEdges = new HashSet<string>(StringComparer.Ordinal);

            foreach (var flow in actions)
            {
                builder.AppendLine($"    %% action: {FormatNode(flow.Action)}");
                EnsureNode(flow.Action, builder, aliasMap, definedNodes);
                WriteEdges(flow.Traversal, flow.Action.Id, builder, aliasMap, definedNodes, emittedEdges);
            }

            builder.AppendLine("```");
            return builder.ToString();
        }

        private static void WriteEdges(
            FlowTraversal traversal,
            string sourceId,
            StringBuilder builder,
            Dictionary<string, string> aliasMap,
            HashSet<string> definedNodes,
            HashSet<string> emittedEdges)
        {
            if (!traversal.TryGetEdges(sourceId, out var edges) || edges.Count == 0)
            {
                return;
            }

            if (!traversal.TryGetNode(sourceId, out var sourceNode))
            {
                return;
            }

            EnsureNode(sourceNode, builder, aliasMap, definedNodes);

            foreach (var edge in edges)
            {
                EnsureNode(edge.Target, builder, aliasMap, definedNodes);

                var fromAlias = aliasMap[sourceNode.Id];
                var toAlias = aliasMap[edge.Target.Id];
                var label = FormatEdgeLabel(edge);
                var edgeKey = $"{fromAlias}->{toAlias}:{label}";
                if (!emittedEdges.Add(edgeKey))
                {
                    continue;
                }

                builder.AppendLine($"    {fromAlias} -- \"{Escape(label)}\" --> {toAlias}");

                if (!edge.IsMaxDepthEdge && !edge.LeadsToVisitedNode)
                {
                    WriteEdges(traversal, edge.Target.Id, builder, aliasMap, definedNodes, emittedEdges);
                }
            }
        }

        private static void EnsureNode(
            FlowNode node,
            StringBuilder builder,
            Dictionary<string, string> aliasMap,
            HashSet<string> definedNodes)
        {
            var alias = GetAlias(node, aliasMap);
            if (definedNodes.Add(alias))
            {
                builder.AppendLine($"    {alias}[\"{Escape(FormatNode(node))}\"]");
            }
        }

        private static string GetAlias(FlowNode node, Dictionary<string, string> aliasMap)
        {
            if (!aliasMap.TryGetValue(node.Id, out var alias))
            {
                alias = $"n{aliasMap.Count}";
                aliasMap[node.Id] = alias;
            }

            return alias;
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    private sealed class DotFlowRenderer : IFlowRenderer
    {
        public string FileExtension => ".flow.dot";

        public string Render(FlowNode? controller, IReadOnlyList<FlowActionFlow> actions)
        {
            var builder = new StringBuilder();
            builder.AppendLine("digraph Flow {");
            builder.AppendLine("    rankdir=LR;");
            builder.AppendLine("    node [shape=box];");

            var aliasMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var definedNodes = new HashSet<string>(StringComparer.Ordinal);
            var emittedEdges = new HashSet<string>(StringComparer.Ordinal);

            if (controller is not null)
            {
                EnsureNode(controller, builder, aliasMap, definedNodes, shape: "folder");
            }

            foreach (var flow in actions)
            {
                EnsureNode(flow.Action, builder, aliasMap, definedNodes);
                WriteEdges(flow.Traversal, flow.Action.Id, builder, aliasMap, definedNodes, emittedEdges);
            }

            builder.AppendLine("}");
            return builder.ToString();
        }

        private static void WriteEdges(
            FlowTraversal traversal,
            string sourceId,
            StringBuilder builder,
            Dictionary<string, string> aliasMap,
            HashSet<string> definedNodes,
            HashSet<string> emittedEdges)
        {
            if (!traversal.TryGetEdges(sourceId, out var edges) || edges.Count == 0)
            {
                return;
            }

            if (!traversal.TryGetNode(sourceId, out var sourceNode))
            {
                return;
            }

            EnsureNode(sourceNode, builder, aliasMap, definedNodes);

            foreach (var edge in edges)
            {
                EnsureNode(edge.Target, builder, aliasMap, definedNodes);

                var fromAlias = aliasMap[sourceNode.Id];
                var toAlias = aliasMap[edge.Target.Id];
                var label = FormatEdgeLabel(edge);
                var edgeKey = $"{fromAlias}->{toAlias}:{label}";
                if (!emittedEdges.Add(edgeKey))
                {
                    continue;
                }

                var attributes = new List<string> { $"label=\"{Escape(label)}\"" };
                if (edge.Edge.Kind.Equals("remote", StringComparison.OrdinalIgnoreCase))
                {
                    attributes.Add("style=\"dashed\"");
                }

                builder.AppendLine($"    {fromAlias} -> {toAlias} [{string.Join(", ", attributes)}];");

                if (!edge.IsMaxDepthEdge && !edge.LeadsToVisitedNode)
                {
                    WriteEdges(traversal, edge.Target.Id, builder, aliasMap, definedNodes, emittedEdges);
                }
            }
        }

        private static void EnsureNode(
            FlowNode node,
            StringBuilder builder,
            Dictionary<string, string> aliasMap,
            HashSet<string> definedNodes,
            string shape = "box")
        {
            var alias = GetAlias(node, aliasMap);
            if (definedNodes.Add(alias))
            {
                builder.AppendLine($"    {alias} [label=\"{Escape(FormatNode(node))}\", shape={shape}];");
            }
        }

        private static string GetAlias(FlowNode node, Dictionary<string, string> aliasMap)
        {
            if (!aliasMap.TryGetValue(node.Id, out var alias))
            {
                alias = $"n{aliasMap.Count}";
                aliasMap[node.Id] = alias;
            }

            return alias;
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    private sealed class PlantUmlFlowRenderer : IFlowRenderer
    {
        public string FileExtension => ".flow.puml";

        public string Render(FlowNode? controller, IReadOnlyList<FlowActionFlow> actions)
        {
            var builder = new StringBuilder();
            builder.AppendLine("@startuml");
            builder.AppendLine("left to right direction");

            if (controller is not null)
            {
                builder.AppendLine($"' Controller: {FormatNode(controller)}");
            }

            var aliasMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var definedNodes = new HashSet<string>(StringComparer.Ordinal);
            var emittedEdges = new HashSet<string>(StringComparer.Ordinal);

            foreach (var flow in actions)
            {
                builder.AppendLine($"' Action: {FormatNode(flow.Action)}");
                EnsureNode(flow.Action, builder, aliasMap, definedNodes);
                WriteEdges(flow.Traversal, flow.Action.Id, builder, aliasMap, definedNodes, emittedEdges);
            }

            builder.AppendLine("@enduml");
            return builder.ToString();
        }

        private static void WriteEdges(
            FlowTraversal traversal,
            string sourceId,
            StringBuilder builder,
            Dictionary<string, string> aliasMap,
            HashSet<string> definedNodes,
            HashSet<string> emittedEdges)
        {
            if (!traversal.TryGetEdges(sourceId, out var edges) || edges.Count == 0)
            {
                return;
            }

            if (!traversal.TryGetNode(sourceId, out var sourceNode))
            {
                return;
            }

            EnsureNode(sourceNode, builder, aliasMap, definedNodes);

            foreach (var edge in edges)
            {
                EnsureNode(edge.Target, builder, aliasMap, definedNodes);

                var fromAlias = aliasMap[sourceNode.Id];
                var toAlias = aliasMap[edge.Target.Id];
                var label = FormatEdgeLabel(edge);
                var edgeKey = $"{fromAlias}->{toAlias}:{label}";
                if (!emittedEdges.Add(edgeKey))
                {
                    continue;
                }

                var arrow = edge.Edge.Kind.Equals("remote", StringComparison.OrdinalIgnoreCase) ? "..>" : "-->";
                builder.AppendLine($"{fromAlias} {arrow} {toAlias} : {Escape(label)}");

                if (!edge.IsMaxDepthEdge && !edge.LeadsToVisitedNode)
                {
                    WriteEdges(traversal, edge.Target.Id, builder, aliasMap, definedNodes, emittedEdges);
                }
            }
        }

        private static void EnsureNode(
            FlowNode node,
            StringBuilder builder,
            Dictionary<string, string> aliasMap,
            HashSet<string> definedNodes)
        {
            var alias = GetAlias(node, aliasMap);
            if (definedNodes.Add(alias))
            {
                builder.AppendLine($"rectangle \"{Escape(FormatNode(node))}\" as {alias}");
            }
        }

        private static string GetAlias(FlowNode node, Dictionary<string, string> aliasMap)
        {
            if (!aliasMap.TryGetValue(node.Id, out var alias))
            {
                alias = $"n{aliasMap.Count}";
                aliasMap[node.Id] = alias;
            }

            return alias;
        }

        private static string Escape(string value)
        {
            return value.Replace("\"", "\\\"");
        }
    }

    private sealed class FlowActionFlow
    {
        public FlowActionFlow(FlowNode action, FlowTraversal traversal)
        {
            Action = action;
            Traversal = traversal;
        }

        public FlowNode Action { get; }

        public FlowTraversal Traversal { get; }
    }

    private sealed class FlowTraversal
    {
        private readonly Dictionary<string, FlowNode> _nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<FlowTraversalEdge>> _edgesByFrom = new(StringComparer.Ordinal);

        public FlowTraversal(FlowNode root)
        {
            Root = root;
            _nodes[root.Id] = root;
        }

        public FlowNode Root { get; }

        public bool TryGetEdges(string nodeId, out IReadOnlyList<FlowTraversalEdge> edges)
        {
            if (_edgesByFrom.TryGetValue(nodeId, out var list))
            {
                edges = list;
                return true;
            }

            edges = Array.Empty<FlowTraversalEdge>();
            return false;
        }

        public bool TryGetNode(string nodeId, out FlowNode node)
        {
            if (_nodes.TryGetValue(nodeId, out var found))
            {
                node = found;
                return true;
            }

            node = null!;
            return false;
        }

        public void AddEdge(string fromId, FlowTraversalEdge edge)
        {
            if (!_edgesByFrom.TryGetValue(fromId, out var list))
            {
                list = new List<FlowTraversalEdge>();
                _edgesByFrom[fromId] = list;
            }

            list.Add(edge);
            _nodes[edge.Target.Id] = edge.Target;
        }
    }

    private sealed class FlowTraversalEdge
    {
        public FlowTraversalEdge(FlowEdge edge, FlowNode target, int depth, bool isMaxDepthEdge, bool leadsToVisitedNode)
        {
            Edge = edge;
            Target = target;
            Depth = depth;
            IsMaxDepthEdge = isMaxDepthEdge;
            LeadsToVisitedNode = leadsToVisitedNode;
        }

        public FlowEdge Edge { get; }

        public FlowNode Target { get; }

        public int Depth { get; }

        public bool IsMaxDepthEdge { get; }

        public bool LeadsToVisitedNode { get; }
    }

    private static string FormatEdgeLabel(FlowTraversalEdge edge)
    {
        var label = GetEdgeLabel(edge.Edge.Kind);
        var annotations = new List<string>();
        if (edge.IsMaxDepthEdge)
        {
            annotations.Add("max depth");
        }

        if (edge.LeadsToVisitedNode)
        {
            annotations.Add("cycle");
        }

        if (annotations.Count > 0)
        {
            label += " (" + string.Join(", ", annotations) + ")";
        }

        return label;
    }

    private static void RenderFlows(FlowGraph graph, List<string> patterns, int? maxDepth, string? outputDirectory, FlowOutputFormat outputFormat)
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

        var renderer = FlowRendererFactory.Create(outputFormat);

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

            var actionFlows = actionNodes
                .Select(action => BuildActionFlow(action, nodesById, edgesByFrom, maxDepth))
                .ToList();

            var sectionText = renderer.Render(controller, actionFlows).TrimEnd();
            if (outputDirectory is null)
            {
                Console.WriteLine(sectionText);
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine($"{controllerHeading} [{string.Join(", ", actionSummaries)}]");
                var controllerName = controller?.Name ?? controller?.Fqdn ?? group.Key;
                var fileName = SanitizeFileName(controllerName) + renderer.FileExtension;
                var destination = Path.Combine(outputDirectory, fileName);
                File.WriteAllText(destination, sectionText + Environment.NewLine);
            }
        }
    }

}
