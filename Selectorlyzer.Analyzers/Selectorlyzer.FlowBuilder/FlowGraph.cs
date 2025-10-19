using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Selectorlyzer.FlowBuilder
{
    public sealed class FlowGraph
    {
        public FlowGraph(IEnumerable<FlowNode> nodes, IEnumerable<FlowEdge> edges)
        {
            Nodes = nodes.ToImmutableArray();
            Edges = edges.ToImmutableArray();
        }

        public ImmutableArray<FlowNode> Nodes { get; }

        public ImmutableArray<FlowEdge> Edges { get; }

        public FlowNode? FindNode(string id)
        {
            return Nodes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.Ordinal));
        }
    }

    public sealed class FlowNode
    {
        public required string Id { get; init; }

        public required string Type { get; init; }

        public required string Name { get; init; }

        public required string Fqdn { get; init; }

        public string? Assembly { get; init; }

        public string? Project { get; init; }

        public string? FilePath { get; init; }

        public FlowSpan? Span { get; init; }

        public string SymbolId { get; init; } = string.Empty;

        public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

        public IReadOnlyDictionary<string, object?>? Properties { get; init; }
    }

    public sealed class FlowEdge
    {
        public required string From { get; init; }

        public required string To { get; init; }

        public required string Kind { get; init; }

        public string Source { get; init; } = "selector-flow";

        public double Confidence { get; init; } = 1.0;

        public FlowEvidence? Evidence { get; init; }
    }

    public sealed class FlowSpan
    {
        public required int StartLine { get; init; }

        public required int EndLine { get; init; }
    }

    public sealed class FlowEvidence
    {
        public IReadOnlyList<FlowEvidenceFile> Files { get; init; } = Array.Empty<FlowEvidenceFile>();
    }

    public sealed class FlowEvidenceFile
    {
        public required string Path { get; init; }

        public required int StartLine { get; init; }

        public required int EndLine { get; init; }
    }
}
