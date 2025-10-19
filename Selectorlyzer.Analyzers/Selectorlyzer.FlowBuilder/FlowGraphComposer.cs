using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Selectorlyzer.FlowBuilder;

public sealed class FlowGraphComposer
{
    private readonly FlowWorkspaceDefinition _workspace;

    public FlowGraphComposer(FlowWorkspaceDefinition workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    public FlowGraph Compose(IEnumerable<FlowGraph?> graphs)
    {
        var accumulators = new Dictionary<string, NodeAccumulator>(StringComparer.Ordinal);
        var edges = new List<FlowEdge>();
        var edgeKeys = new HashSet<(string From, string To, string Kind)>();

        foreach (var graph in graphs ?? Enumerable.Empty<FlowGraph?>())
        {
            if (graph is null)
            {
                continue;
            }

            foreach (var node in graph.Nodes)
            {
                if (!accumulators.TryGetValue(node.Id, out var accumulator))
                {
                    accumulator = new NodeAccumulator(node);
                    accumulators[node.Id] = accumulator;
                }
                else
                {
                    accumulator.Merge(node);
                }
            }

            foreach (var edge in graph.Edges)
            {
                if (edgeKeys.Add((edge.From, edge.To, edge.Kind)))
                {
                    edges.Add(edge);
                }
            }
        }

        var mergedNodes = accumulators.Values
            .Select(a => a.ToNode())
            .OrderBy(n => n.Fqdn, StringComparer.Ordinal)
            .ToImmutableArray();

        var mergedEdges = edges
            .OrderBy(e => e.From, StringComparer.Ordinal)
            .ThenBy(e => e.To, StringComparer.Ordinal)
            .ThenBy(e => e.Kind, StringComparer.Ordinal)
            .ToImmutableArray();

        var mergedGraph = new FlowGraph(mergedNodes, mergedEdges);
        return AugmentRemoteEdges(mergedGraph);
    }

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
        var servicesByAssembly = new Dictionary<string, FlowServiceDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in servicesByName.Values)
        {
            foreach (var assembly in service.AssemblyNames)
            {
                if (!servicesByAssembly.ContainsKey(assembly))
                {
                    servicesByAssembly[assembly] = service;
                }
            }
        }

        var serviceByBaseUrl = new Dictionary<string, FlowServiceDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in servicesByName.Values)
        {
            foreach (var baseAddress in service.BaseAddresses.Values)
            {
                if (string.IsNullOrWhiteSpace(baseAddress))
                {
                    continue;
                }

                var normalized = NormalizeUrl(baseAddress);
                if (!serviceByBaseUrl.ContainsKey(normalized))
                {
                    serviceByBaseUrl[normalized] = service;
                }
            }
        }

        var bindingsByClient = _workspace.Bindings
            .GroupBy(b => b.Client, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var call in httpCalls)
        {
            var route = GetStringProperty(call, "route");
            var verb = GetStringProperty(call, "verb");
            var clientType = GetStringProperty(call, "client_type");
            var callerType = GetStringProperty(call, "caller_type");
            var callerId = GetStringProperty(call, "caller_id");
            var baseUrl = GetStringProperty(call, "base_url");

            var targetServices = new List<FlowServiceDefinition>();

            if (!string.IsNullOrWhiteSpace(clientType) && bindingsByClient.TryGetValue(clientType!, out var clientBindings))
            {
                foreach (var binding in clientBindings)
                {
                    if (servicesByName.TryGetValue(binding.TargetService, out var service))
                    {
                        targetServices.Add(service);
                    }
                }
            }

            if (targetServices.Count == 0 && !string.IsNullOrWhiteSpace(callerType) && bindingsByClient.TryGetValue(callerType!, out var callerBindings))
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
                if (serviceByBaseUrl.TryGetValue(normalized, out var service))
                {
                    targetServices.Add(service);
                }
            }

            if (targetServices.Count == 0 && !string.IsNullOrWhiteSpace(call.Assembly) && servicesByAssembly.TryGetValue(call.Assembly!, out var assemblyService))
            {
                targetServices.Add(assemblyService);
            }

            var candidateActions = targetServices.Count == 0
                ? actions
                : actions.Where(a => targetServices.Any(service => service.AssemblyNames.Contains(a.Assembly ?? string.Empty, StringComparer.OrdinalIgnoreCase))).ToList();

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

        IReadOnlyList<FlowNode> Match(Func<FlowNode, bool> predicate)
            => candidates.Where(predicate).ToList();

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

        if (canonicalRoute is not null && canonicalVerb is not null)
        {
            var both = Match(node => RouteMatches(node) && VerbMatches(node));
            if (both.Count > 0)
            {
                return both;
            }
        }

        if (canonicalRoute is not null)
        {
            var byRoute = Match(RouteMatches);
            if (byRoute.Count > 0)
            {
                return byRoute;
            }
        }

        if (canonicalVerb is not null)
        {
            var byVerb = Match(VerbMatches);
            if (byVerb.Count > 0)
            {
                return byVerb;
            }
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
