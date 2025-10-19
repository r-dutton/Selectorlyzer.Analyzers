using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Selectorlyzer.FlowBuilder;

public sealed class FlowGraphComposer
{
    private readonly FlowWorkspaceDefinition _workspace;
    private readonly IReadOnlyDictionary<string, FlowServiceDefinition> _servicesByAssembly;
    private readonly IReadOnlyDictionary<string, FlowServiceDefinition> _serviceByBaseUrl;
    private readonly IReadOnlyDictionary<string, List<FlowServiceBinding>> _bindingsByClient;

    public FlowGraphComposer(FlowWorkspaceDefinition workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _servicesByAssembly = BuildServicesByAssembly(workspace.Services);
        _serviceByBaseUrl = BuildServiceByBaseUrl(workspace.Services);
        _bindingsByClient = BuildBindingsByClient(workspace.Bindings);
    }

    public FlowGraph Compose(IEnumerable<FlowGraph?> graphs)
    {
        var composition = CreateComposition();
        foreach (var graph in graphs ?? Enumerable.Empty<FlowGraph?>())
        {
            composition.AddGraph(graph);
        }

        return composition.Build();
    }

    public Composition CreateComposition() => new(this);

    private FlowGraph AugmentRemoteEdges(FlowGraph graph)
    {
        var nodesById = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var edges = graph.Edges.ToList();
        var edgeKeys = new HashSet<(string From, string To, string Kind)>(edges.Select(e => (e.From, e.To, e.Kind)));

        var actions = graph.Nodes
            .Where(n => string.Equals(n.Type, "endpoint.controller_action", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var httpCalls = graph.Nodes
            .Where(n => string.Equals(n.Type, "infra.http_call", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (httpCalls.Count == 0 || actions.Count == 0)
        {
            return graph;
        }

        var servicesByName = _workspace.Services;

        foreach (var call in httpCalls)
        {
            var route = GetStringProperty(call, "route");
            var verb = GetStringProperty(call, "verb");
            var clientType = GetStringProperty(call, "client_type");
            var callerType = GetStringProperty(call, "caller_type");
            var callerId = GetStringProperty(call, "caller_id");
            var baseUrl = GetStringProperty(call, "base_url");

            var targetServices = new List<FlowServiceDefinition>();

            if (!string.IsNullOrWhiteSpace(clientType) && _bindingsByClient.TryGetValue(clientType!, out var clientBindings))
            {
                foreach (var binding in clientBindings)
                {
                    if (servicesByName.TryGetValue(binding.TargetService, out var service))
                    {
                        targetServices.Add(service);
                    }
                }
            }

            if (targetServices.Count == 0 && !string.IsNullOrWhiteSpace(callerType) && _bindingsByClient.TryGetValue(callerType!, out var callerBindings))
            {
                foreach (var binding in callerBindings)
                {
                    if (servicesByName.TryGetValue(binding.TargetService, out var service))
                    {
                        targetServices.Add(service);
                    }
                }
            }

            if (targetServices.Count == 0 && !string.IsNullOrWhiteSpace(baseUrl))
            {
                var normalized = NormalizeUrl(baseUrl!);
                if (_serviceByBaseUrl.TryGetValue(normalized, out var service))
                {
                    targetServices.Add(service);
                }
            }

            if (targetServices.Count == 0 && !string.IsNullOrWhiteSpace(call.Assembly) && _servicesByAssembly.TryGetValue(call.Assembly!, out var assemblyService))
            {
                targetServices.Add(assemblyService);
            }

            HashSet<string>? targetAssemblies = null;
            if (targetServices.Count > 0)
            {
                targetAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var service in targetServices)
                {
                    foreach (var assembly in service.AssemblyNames)
                    {
                        if (!string.IsNullOrWhiteSpace(assembly))
                        {
                            targetAssemblies.Add(assembly);
                        }
                    }
                }
            }

            var candidateActions = targetAssemblies is null
                ? actions
                : actions.Where(a =>
                {
                    var assembly = a.Assembly;
                    return !string.IsNullOrWhiteSpace(assembly) && targetAssemblies.Contains(assembly);
                }).ToList();

            if (candidateActions.Count == 0)
            {
                continue;
            }

            var matchedActions = FilterActionsByRouteAndVerb(candidateActions, route, verb);
            if (matchedActions.Count == 0)
            {
                matchedActions = candidateActions;
            }

            if (!string.IsNullOrWhiteSpace(callerId) && nodesById.TryGetValue(callerId!, out var callerNode))
            {
                AddEdge(edgeKeys, edges, callerNode.Id, call.Id, "flow");
            }

            foreach (var action in matchedActions)
            {
                AddEdge(edgeKeys, edges, call.Id, action.Id, "remote");
            }
        }

        var augmentedNodes = graph.Nodes;
        var augmentedEdges = edges
            .OrderBy(e => e.From, StringComparer.Ordinal)
            .ThenBy(e => e.To, StringComparer.Ordinal)
            .ThenBy(e => e.Kind, StringComparer.Ordinal)
            .ToImmutableArray();

        return new FlowGraph(augmentedNodes, augmentedEdges);
    }

    private static IReadOnlyDictionary<string, FlowServiceDefinition> BuildServicesByAssembly(
        IReadOnlyDictionary<string, FlowServiceDefinition> services)
    {
        var dictionary = new Dictionary<string, FlowServiceDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in services.Values)
        {
            foreach (var assembly in service.AssemblyNames)
            {
                if (!dictionary.ContainsKey(assembly))
                {
                    dictionary[assembly] = service;
                }
            }
        }

        return dictionary;
    }

    private static IReadOnlyDictionary<string, FlowServiceDefinition> BuildServiceByBaseUrl(
        IReadOnlyDictionary<string, FlowServiceDefinition> services)
    {
        var dictionary = new Dictionary<string, FlowServiceDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in services.Values)
        {
            foreach (var baseAddress in service.BaseAddresses.Values)
            {
                if (string.IsNullOrWhiteSpace(baseAddress))
                {
                    continue;
                }

                var normalized = NormalizeUrl(baseAddress);
                if (!dictionary.ContainsKey(normalized))
                {
                    dictionary[normalized] = service;
                }
            }
        }

        return dictionary;
    }

    private static IReadOnlyDictionary<string, List<FlowServiceBinding>> BuildBindingsByClient(
        IReadOnlyCollection<FlowServiceBinding> bindings)
    {
        var dictionary = new Dictionary<string, List<FlowServiceBinding>>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings)
        {
            if (!dictionary.TryGetValue(binding.Client, out var list))
            {
                list = new List<FlowServiceBinding>();
                dictionary[binding.Client] = list;
            }

            list.Add(binding);
        }

        return dictionary;
    }

    private static IReadOnlyList<FlowNode> FilterActionsByRouteAndVerb(
        IReadOnlyList<FlowNode> candidates,
        string? route,
        string? verb)
    {
        if (candidates.Count == 0)
        {
            return Array.Empty<FlowNode>();
        }

        var canonicalRoute = CanonicalizeRoute(route);
        var canonicalVerb = string.IsNullOrWhiteSpace(verb) ? null : verb!.Trim().ToUpperInvariant();

        bool RouteMatches(FlowNode node)
        {
            var full = GetStringProperty(node, "full_route");
            var simple = GetStringProperty(node, "route");
            var target = CanonicalizeRoute(full) ?? CanonicalizeRoute(simple);
            return canonicalRoute is not null && target is not null && string.Equals(target, canonicalRoute, StringComparison.OrdinalIgnoreCase);
        }

        bool VerbMatches(FlowNode node)
        {
            var target = GetStringProperty(node, "http_method");
            return canonicalVerb is not null && !string.IsNullOrWhiteSpace(target) && string.Equals(target.Trim().ToUpperInvariant(), canonicalVerb, StringComparison.OrdinalIgnoreCase);
        }

        List<FlowNode>? both = canonicalRoute is not null && canonicalVerb is not null ? new List<FlowNode>() : null;
        List<FlowNode>? byRoute = canonicalRoute is not null ? new List<FlowNode>() : null;
        List<FlowNode>? byVerb = canonicalVerb is not null ? new List<FlowNode>() : null;

        foreach (var node in candidates)
        {
            var routeMatches = RouteMatches(node);
            var verbMatches = VerbMatches(node);

            if (routeMatches && verbMatches)
            {
                both?.Add(node);
                continue;
            }

            if (routeMatches)
            {
                byRoute?.Add(node);
            }

            if (verbMatches)
            {
                byVerb?.Add(node);
            }
        }

        if (both is not null && both.Count > 0)
        {
            return both;
        }

        if (byRoute is not null && byRoute.Count > 0)
        {
            return byRoute;
        }

        if (byVerb is not null && byVerb.Count > 0)
        {
            return byVerb;
        }

        return Array.Empty<FlowNode>();
    }

    private static string? GetStringProperty(FlowNode node, string key)
    {
        if (node.Properties is null)
        {
            return null;
        }

        return node.Properties.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    private static void AddEdge(HashSet<(string From, string To, string Kind)> keys, List<FlowEdge> edges, string from, string to, string kind)
    {
        if (!keys.Add((from, to, kind)))
        {
            return;
        }

        edges.Add(new FlowEdge
        {
            From = from,
            To = to,
            Kind = kind,
            Source = "selector-flow",
            Confidence = 1.0
        });
    }

    private static string? CanonicalizeRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return null;
        }

        var trimmed = route.Trim();
        if (!trimmed.StartsWith('/'))
        {
            trimmed = "/" + trimmed.TrimStart('/');
        }

        return trimmed.Replace("//", "/", StringComparison.Ordinal);
    }

    private static string NormalizeUrl(string url)
    {
        var trimmed = url.Trim();
        if (trimmed.EndsWith('/'))
        {
            trimmed = trimmed.TrimEnd('/');
        }

        return trimmed;
    }

    public sealed class Composition
    {
        private readonly FlowGraphComposer _composer;
        private readonly Dictionary<string, NodeAccumulator> _accumulators = new(StringComparer.Ordinal);
        private readonly List<FlowEdge> _edges = new();
        private readonly HashSet<(string From, string To, string Kind)> _edgeKeys = new();
        private readonly object _sync = new();

        internal Composition(FlowGraphComposer composer)
        {
            _composer = composer;
        }

        public void AddGraph(FlowGraph? graph)
        {
            if (graph is null)
            {
                return;
            }

            lock (_sync)
            {
                foreach (var node in graph.Nodes)
                {
                    if (!_accumulators.TryGetValue(node.Id, out var accumulator))
                    {
                        accumulator = new NodeAccumulator(node);
                        _accumulators[node.Id] = accumulator;
                    }
                    else
                    {
                        accumulator.Merge(node);
                    }
                }

                foreach (var edge in graph.Edges)
                {
                    if (_edgeKeys.Add((edge.From, edge.To, edge.Kind)))
                    {
                        _edges.Add(edge);
                    }
                }
            }
        }

        public FlowGraph Build()
        {
            NodeAccumulator[] accumulators;
            FlowEdge[] edges;

            lock (_sync)
            {
                accumulators = _accumulators.Values.ToArray();
                edges = _edges.ToArray();
            }

            var mergedNodes = accumulators
                .Select(a => a.ToNode())
                .OrderBy(n => n.Fqdn, StringComparer.Ordinal)
                .ToImmutableArray();

            var mergedEdges = edges
                .OrderBy(e => e.From, StringComparer.Ordinal)
                .ThenBy(e => e.To, StringComparer.Ordinal)
                .ThenBy(e => e.Kind, StringComparer.Ordinal)
                .ToImmutableArray();

            var mergedGraph = new FlowGraph(mergedNodes, mergedEdges);
            return _composer.AugmentRemoteEdges(mergedGraph);
        }
    }

    private sealed class NodeAccumulator
    {
        private readonly string _id;
        private string? _type;
        private string? _name;
        private string? _fqdn;
        private string? _assembly;
        private string? _project;
        private string? _filePath;
        private FlowSpan? _span;
        private string _symbolId;
        private readonly HashSet<string> _tags = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, object?> _properties = new(StringComparer.OrdinalIgnoreCase);

        public NodeAccumulator(FlowNode node)
        {
            _id = node.Id;
            _symbolId = node.SymbolId;
            Merge(node);
        }

        public void Merge(FlowNode node)
        {
            _type ??= node.Type;
            _name ??= node.Name;
            _fqdn ??= node.Fqdn;
            _assembly ??= node.Assembly;
            _project ??= node.Project;
            _filePath ??= node.FilePath;
            _span ??= node.Span;
            _symbolId = string.IsNullOrWhiteSpace(_symbolId) ? node.SymbolId : _symbolId;

            foreach (var tag in node.Tags)
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    _tags.Add(tag);
                }
            }

            if (node.Properties is not null)
            {
                foreach (var kvp in node.Properties)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key))
                    {
                        continue;
                    }

                    if (!_properties.TryGetValue(kvp.Key, out var existing) || existing is null or "")
                    {
                        _properties[kvp.Key] = kvp.Value;
                    }
                }
            }
        }

        public FlowNode ToNode()
        {
            return new FlowNode
            {
                Id = _id,
                Type = _type ?? "node",
                Name = _name ?? _id,
                Fqdn = _fqdn ?? _name ?? _id,
                Assembly = _assembly,
                Project = _project,
                FilePath = _filePath,
                Span = _span,
                SymbolId = _symbolId,
                Tags = _tags.ToArray(),
                Properties = _properties.Count > 0 ? new Dictionary<string, object?>(_properties, StringComparer.OrdinalIgnoreCase) : null
            };
        }
    }
}
