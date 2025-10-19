using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Selectorlyzer.Qulaly;
using Selectorlyzer.Qulaly.Matcher;

namespace Selectorlyzer.FlowBuilder
{
    public sealed class SelectorFlowBuilder
    {
        private readonly IReadOnlyList<SelectorNodeRule> _rules;

        public SelectorFlowBuilder()
            : this(DefaultSelectorNodeRules.Rules)
        {
        }

        internal SelectorFlowBuilder(IReadOnlyList<SelectorNodeRule> rules)
        {
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        }

        public FlowGraph Build(Compilation compilation, SelectorQueryContext? baseContext = null)
        {
            if (compilation == null)
            {
                throw new ArgumentNullException(nameof(compilation));
            }

            var context = (baseContext ?? SelectorQueryContext.Empty).WithCompilation(compilation);
            var registry = new NodeRegistry(compilation, context, _rules);
            registry.Index();
            registry.Propagate();
            return registry.ToGraph();
        }

        private sealed class NodeRegistry
        {
            private Compilation _compilation;
            private SelectorQueryContext _baseContext;
            private readonly IReadOnlyList<SelectorNodeRule> _rules;
            private readonly Dictionary<string, NodeBuilder> _nodes = new(StringComparer.Ordinal);
            private readonly Dictionary<ISymbol, string> _symbolToId = new(SymbolEqualityComparer.Default);
            private readonly List<FlowEdge> _edges = new();
            private readonly HashSet<(string From, string To, string Kind)> _edgeKeys = new();
            private readonly string _defaultProject;
            private readonly string _defaultAssembly;
            private readonly Dictionary<INamedTypeSymbol, ImmutableArray<INamedTypeSymbol>> _derivedTypesByBase = new(SymbolEqualityComparer.Default);
            private readonly Dictionary<INamedTypeSymbol, ImmutableArray<INamedTypeSymbol>> _implementationsByInterface = new(SymbolEqualityComparer.Default);
            private readonly Dictionary<ITypeSymbol, ImmutableArray<INamedTypeSymbol>> _mediatorRequestHandlers = new(SymbolEqualityComparer.Default);
            private readonly Dictionary<ITypeSymbol, ImmutableArray<INamedTypeSymbol>> _mediatorNotificationHandlers = new(SymbolEqualityComparer.Default);
            private bool _typeRelationsBuilt;

            public NodeRegistry(Compilation compilation, SelectorQueryContext baseContext, IReadOnlyList<SelectorNodeRule> rules)
            {
                _compilation = compilation;
                _baseContext = baseContext;
                _rules = rules;
                _defaultAssembly = compilation.AssemblyName ?? "Assembly";
                _defaultProject = baseContext.Metadata?.GetValueOrDefault("project") as string ?? _defaultAssembly;
            }

            public void Index()
            {
                BuildTypeRelationMaps();

                foreach (var tree in _compilation.SyntaxTrees)
                {
                    var root = tree.GetRoot();
                    foreach (var rule in _rules)
                    {
                        foreach (var match in root.QueryMatches(rule.Selector, _compilation, _baseContext))
                        {
                            var symbol = rule.UseSymbolIdentity ? match.Symbol : null;
                            var builder = GetOrCreateBuilder(symbol, match.Node);
                            if (builder is null)
                            {
                                continue;
                            }

                            builder.ApplyMatch(rule, match, _defaultProject, _defaultAssembly);
                        }
                    }
                }
            }

            public void Propagate()
            {
                var queue = new Queue<NodeBuilder>(_nodes.Values);

                while (queue.Count > 0)
                {
                    var node = queue.Dequeue();
                    if (node.IsPropagated)
                    {
                        continue;
                    }

                    node.EnsureInitialized(_defaultProject, _defaultAssembly);
                    node.IsPropagated = true;

                    if (node.Symbol is null)
                    {
                        continue;
                    }

                    foreach (var targetSymbol in CollectReferencedSymbols(node))
                    {
                        var target = GetOrCreateBuilder(targetSymbol, null);
                        if (target is null)
                        {
                            continue;
                        }

                        target.EnsureInitialized(_defaultProject, _defaultAssembly);

                        if (_edgeKeys.Add((node.Id, target.Id, "flow")))
                        {
                            _edges.Add(CreateEdge(node, target));
                        }

                        if (!target.IsPropagated)
                        {
                            queue.Enqueue(target);
                        }
                    }
                }
            }

            public FlowGraph ToGraph()
            {
                foreach (var node in _nodes.Values)
                {
                    node.EnsureInitialized(_defaultProject, _defaultAssembly);
                }

                var orderedNodes = _nodes.Values
                    .Select(n => n.ToNode())
                    .OrderBy(n => n.Fqdn, StringComparer.Ordinal)
                    .ToImmutableArray();

                var orderedEdges = _edges
                    .OrderBy(e => e.From, StringComparer.Ordinal)
                    .ThenBy(e => e.To, StringComparer.Ordinal)
                    .ThenBy(e => e.Kind, StringComparer.Ordinal)
                    .ToImmutableArray();

                return new FlowGraph(orderedNodes, orderedEdges);
            }

            private SyntaxTree EnsureCompilationContainsTree(SyntaxTree syntaxTree)
            {
                if (_compilation.SyntaxTrees.Contains(syntaxTree))
                {
                    return syntaxTree;
                }

                var treeToAdd = syntaxTree;

                if (_compilation is CSharpCompilation cSharpCompilation)
                {
                    var baselineOptions = cSharpCompilation.SyntaxTrees
                        .OfType<CSharpSyntaxTree>()
                        .FirstOrDefault()?.Options;

                    if (baselineOptions is not null)
                    {
                        if (syntaxTree.FilePath is { Length: > 0 } filePath && File.Exists(filePath))
                        {
                            var text = File.ReadAllText(filePath);
                            treeToAdd = CSharpSyntaxTree.ParseText(text, baselineOptions, filePath);
                        }
                        else if (syntaxTree is CSharpSyntaxTree cSharpTree)
                        {
                            var text = cSharpTree.GetText();
                            treeToAdd = CSharpSyntaxTree.ParseText(text, baselineOptions, syntaxTree.FilePath);
                        }
                    }
                }

                var updatedCompilation = _compilation.AddSyntaxTrees(treeToAdd);
                UpdateCompilation(updatedCompilation);
                return treeToAdd;
            }

            private void UpdateCompilation(Compilation compilation)
            {
                if (ReferenceEquals(_compilation, compilation))
                {
                    return;
                }

                _compilation = compilation;
                _baseContext = _baseContext.WithCompilation(compilation);
                ResetTypeRelationMaps();
            }

            private NodeBuilder? GetOrCreateBuilder(ISymbol? symbol, SyntaxNode? fallbackNode)
            {
                if (symbol is not null)
                {
                    if (_symbolToId.TryGetValue(symbol, out var existingId))
                    {
                        return _nodes[existingId];
                    }

                    if (!symbol.Locations.Any(l => l.IsInSource))
                    {
                        return null;
                    }

                    var id = symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                    var builder = new NodeBuilder(id, symbol);
                    _symbolToId[symbol] = id;
                    _nodes[id] = builder;
                    return builder;
                }

                if (fallbackNode is null)
                {
                    return null;
                }

                var syntheticId = BuildSyntheticId(fallbackNode);
                if (!_nodes.TryGetValue(syntheticId, out var synthetic))
                {
                    synthetic = new NodeBuilder(syntheticId, null);
                    _nodes[syntheticId] = synthetic;
                }

                return synthetic;
            }

            private IEnumerable<ISymbol> CollectReferencedSymbols(NodeBuilder node)
            {
                var origin = node.Symbol;
                var referenced = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

                void AddSymbol(ISymbol? candidate)
                {
                    if (candidate is null)
                    {
                        return;
                    }

                    if (candidate.Kind == SymbolKind.Namespace)
                    {
                        return;
                    }

                    if (!candidate.Locations.Any(l => l.IsInSource))
                    {
                        return;
                    }

                    if (origin is not null && SymbolEqualityComparer.Default.Equals(candidate, origin))
                    {
                        return;
                    }

                    referenced.Add(candidate);
                }

                if (origin is not null)
                {
                    CollectSymbolsFromSymbol(origin, AddSymbol);
                }

                foreach (var match in node.Matches)
                {
                    if (match.SemanticModel is not { } semanticModel)
                    {
                        continue;
                    }

                    CollectSymbolsFromNode(match.Node, semanticModel, AddSymbol);
                }

                foreach (var candidate in referenced.ToArray())
                {
                    ExpandCandidateRelations(candidate, AddSymbol);
                }

                ExpandOriginRelations(origin, AddSymbol);

                return referenced;
            }

            private void CollectSymbolsFromSymbol(ISymbol symbol, Action<ISymbol?> addSymbol)
            {
                foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
                {
                    var syntax = syntaxReference.GetSyntax();
                    var ensuredTree = EnsureCompilationContainsTree(syntax.SyntaxTree);
                    var mappedNode = syntax;
                    if (!ReferenceEquals(ensuredTree, syntax.SyntaxTree))
                    {
                        var root = ensuredTree.GetRoot();
                        mappedNode = root.FindNode(syntax.Span, getInnermostNodeForTie: true);
                    }

                    var semanticModel = _compilation.GetSemanticModel(ensuredTree);
                    CollectSymbolsFromNode(mappedNode, semanticModel, addSymbol);
                }
            }

            private static void CollectSymbolsFromNode(SyntaxNode node, SemanticModel semanticModel, Action<ISymbol?> addSymbol)
            {
                var symbolCache = new Dictionary<SyntaxNode, ISymbol?>();
                foreach (var descendant in node.DescendantNodesAndSelf())
                {
                    // Filter: Only resolve symbols for relevant node types
                    if (!(descendant is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax
                        || descendant is Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax
                        || descendant is Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax
                        || descendant is Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax
                        || descendant is Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax
                        || descendant is Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax
                        || descendant is Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax
                        || descendant is Microsoft.CodeAnalysis.CSharp.Syntax.RecordDeclarationSyntax))
                    {
                        continue;
                    }

                    if (!symbolCache.TryGetValue(descendant, out var symbol))
                    {
                        var symbolInfo = semanticModel.GetSymbolInfo(descendant);
                        symbol = symbolInfo.Symbol;
                        if (symbol == null && !symbolInfo.CandidateSymbols.IsDefaultOrEmpty)
                        {
                            symbol = symbolInfo.CandidateSymbols[0];
                        }
                        symbolCache[descendant] = symbol;
                    }
                    if (symbol is not null)
                    {
                        addSymbol(symbol);
                    }
                    var typeInfo = semanticModel.GetTypeInfo(descendant);
                    addSymbol(typeInfo.Type);
                    addSymbol(typeInfo.ConvertedType);
                }
            }

            private void ExpandCandidateRelations(ISymbol candidate, Action<ISymbol?> addSymbol)
            {
                switch (candidate)
                {
                    case IMethodSymbol methodSymbol:
                        addSymbol(methodSymbol.ContainingType);
                        addSymbol(methodSymbol.ReturnType);
                        if (methodSymbol.PartialDefinitionPart is not null)
                        {
                            addSymbol(methodSymbol.PartialDefinitionPart);
                        }

                        if (methodSymbol.PartialImplementationPart is not null)
                        {
                            addSymbol(methodSymbol.PartialImplementationPart);
                        }

                        foreach (var parameter in methodSymbol.Parameters)
                        {
                            addSymbol(parameter.Type);
                        }
                        break;
                    case IPropertySymbol propertySymbol:
                        addSymbol(propertySymbol.ContainingType);
                        addSymbol(propertySymbol.Type);
                        break;
                    case IEventSymbol eventSymbol:
                        addSymbol(eventSymbol.ContainingType);
                        addSymbol(eventSymbol.Type);
                        break;
                    case IFieldSymbol fieldSymbol:
                        addSymbol(fieldSymbol.ContainingType);
                        addSymbol(fieldSymbol.Type);
                        break;
                    case INamedTypeSymbol typeSymbol:
                        if (typeSymbol.BaseType is not null)
                        {
                            addSymbol(typeSymbol.BaseType);
                        }

                        foreach (var iface in typeSymbol.Interfaces)
                        {
                            addSymbol(iface);
                        }

                        foreach (var typeArgument in typeSymbol.TypeArguments)
                        {
                            addSymbol(typeArgument);
                        }
                        break;
                }
            }

            private void ExpandOriginRelations(ISymbol? origin, Action<ISymbol?> addSymbol)
            {
                if (origin is null)
                {
                    return;
                }

                BuildTypeRelationMaps();

                if (origin is IMethodSymbol methodSymbol && methodSymbol.ContainingType?.TypeKind == TypeKind.Interface)
                {
                    if (methodSymbol.ContainingType is { } interfaceType &&
                        _implementationsByInterface.TryGetValue(interfaceType, out var implementations))
                    {
                        foreach (var namedType in implementations)
                        {
                            var implementation = namedType.FindImplementationForInterfaceMember(methodSymbol);
                            if (implementation is not null)
                            {
                                addSymbol(namedType);
                                addSymbol(implementation);
                            }
                        }
                    }
                }

                if (origin is IPropertySymbol propertySymbol && propertySymbol.ContainingType?.TypeKind == TypeKind.Interface)
                {
                    if (propertySymbol.ContainingType is { } interfaceType &&
                        _implementationsByInterface.TryGetValue(interfaceType, out var implementations))
                    {
                        foreach (var namedType in implementations)
                        {
                            var implementation = namedType.FindImplementationForInterfaceMember(propertySymbol);
                            if (implementation is not null)
                            {
                                addSymbol(namedType);
                                addSymbol(implementation);
                            }
                        }
                    }
                }

                if (origin is INamedTypeSymbol namedTypeSymbol)
                {
                    if (_derivedTypesByBase.TryGetValue(namedTypeSymbol, out var derivedTypes))
                    {
                        foreach (var candidate in derivedTypes)
                        {
                            addSymbol(candidate);
                        }
                    }

                    if (namedTypeSymbol.TypeKind == TypeKind.Interface &&
                        _implementationsByInterface.TryGetValue(namedTypeSymbol, out var implementations))
                    {
                        foreach (var candidate in implementations)
                        {
                            addSymbol(candidate);
                        }
                    }

                    ExpandMediatorAndMessagingRelations(namedTypeSymbol, addSymbol);
                }
            }

            private void ExpandMediatorAndMessagingRelations(INamedTypeSymbol typeSymbol, Action<ISymbol?> addSymbol)
            {
                BuildTypeRelationMaps();

                var isRequest = typeSymbol.AllInterfaces.Any(i => string.Equals(i.Name, "IRequest", StringComparison.Ordinal));
                var isNotification = typeSymbol.AllInterfaces.Any(i => string.Equals(i.Name, "INotification", StringComparison.Ordinal));

                if (!isRequest && !isNotification)
                {
                    return;
                }

                if (isRequest && _mediatorRequestHandlers.TryGetValue(typeSymbol, out var requestHandlers))
                {
                    foreach (var handler in requestHandlers)
                    {
                        addSymbol(handler);
                    }
                }

                if (isNotification && _mediatorNotificationHandlers.TryGetValue(typeSymbol, out var notificationHandlers))
                {
                    foreach (var handler in notificationHandlers)
                    {
                        addSymbol(handler);
                    }
                }
            }

            private static ITypeSymbol? UnwrapNullable(ITypeSymbol? typeSymbol)
            {
                if (typeSymbol is INamedTypeSymbol named && named.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
                {
                    return named.TypeArguments.Length == 1 ? named.TypeArguments[0] : typeSymbol;
                }

                return typeSymbol;
            }

            private void BuildTypeRelationMaps()
            {
                if (_typeRelationsBuilt)
                {
                    return;
                }

                var derived = new Dictionary<INamedTypeSymbol, HashSet<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
                var implementations = new Dictionary<INamedTypeSymbol, HashSet<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
                var requestHandlers = new Dictionary<ITypeSymbol, HashSet<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
                var notificationHandlers = new Dictionary<ITypeSymbol, HashSet<INamedTypeSymbol>>(SymbolEqualityComparer.Default);

                foreach (var namedType in EnumerateCompilationNamedTypes(_compilation))
                {
                    if (!namedType.Locations.Any(l => l.IsInSource))
                    {
                        continue;
                    }

                    if (namedType.BaseType is INamedTypeSymbol baseType)
                    {
                        AddRelation(derived, baseType, namedType);
                    }

                    foreach (var iface in namedType.AllInterfaces)
                    {
                        if (iface is not INamedTypeSymbol interfaceType)
                        {
                            continue;
                        }

                        AddRelation(implementations, interfaceType, namedType);

                        if (iface.TypeArguments.Length == 0)
                        {
                            continue;
                        }

                        var messageType = UnwrapNullable(iface.TypeArguments[0]);
                        if (messageType is null)
                        {
                            continue;
                        }

                        if (string.Equals(iface.Name, "IRequestHandler", StringComparison.Ordinal) ||
                            string.Equals(iface.Name, "IRequestProcessor", StringComparison.Ordinal) ||
                            string.Equals(iface.Name, "IPipelineBehavior", StringComparison.Ordinal))
                        {
                            AddRelation(requestHandlers, messageType, namedType);
                        }
                        else if (string.Equals(iface.Name, "INotificationHandler", StringComparison.Ordinal))
                        {
                            AddRelation(notificationHandlers, messageType, namedType);
                        }
                    }
                }

                PopulateImmutableMap(_derivedTypesByBase, derived);
                PopulateImmutableMap(_implementationsByInterface, implementations);
                PopulateImmutableMap(_mediatorRequestHandlers, requestHandlers);
                PopulateImmutableMap(_mediatorNotificationHandlers, notificationHandlers);

                _typeRelationsBuilt = true;
            }

            private void ResetTypeRelationMaps()
            {
                _derivedTypesByBase.Clear();
                _implementationsByInterface.Clear();
                _mediatorRequestHandlers.Clear();
                _mediatorNotificationHandlers.Clear();
                _typeRelationsBuilt = false;
            }

            private static void AddRelation<TKey>(Dictionary<TKey, HashSet<INamedTypeSymbol>> map, TKey key, INamedTypeSymbol value)
                where TKey : notnull
            {
                if (!map.TryGetValue(key, out var set))
                {
                    set = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
                    map[key] = set;
                }

                set.Add(value);
            }

            private static void PopulateImmutableMap<TKey>(Dictionary<TKey, ImmutableArray<INamedTypeSymbol>> target, Dictionary<TKey, HashSet<INamedTypeSymbol>> source)
                where TKey : notnull
            {
                foreach (var (key, values) in source)
                {
                    target[key] = values
                        .OrderBy(v => v.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), StringComparer.Ordinal)
                        .ToImmutableArray();
                }
            }

            private static IEnumerable<INamedTypeSymbol> EnumerateCompilationNamedTypes(Compilation compilation)
            {
                var stack = new Stack<INamespaceOrTypeSymbol>();
                stack.Push(compilation.GlobalNamespace);

                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    if (current is INamespaceSymbol namespaceSymbol)
                    {
                        foreach (var member in namespaceSymbol.GetMembers())
                        {
                            if (member is INamespaceOrTypeSymbol namespaceOrType)
                            {
                                stack.Push(namespaceOrType);
                            }
                        }
                    }
                    else if (current is INamedTypeSymbol namedType)
                    {
                        yield return namedType;

                        foreach (var nested in namedType.GetTypeMembers())
                        {
                            stack.Push(nested);
                        }
                    }
                }
            }

            private FlowEdge CreateEdge(NodeBuilder from, NodeBuilder to)
            {
                FlowEvidence? evidence = null;
                if (!string.IsNullOrEmpty(from.FilePath) && from.Span is not null)
                {
                    evidence = new FlowEvidence
                    {
                        Files = new[]
                        {
                            new FlowEvidenceFile
                            {
                                Path = from.FilePath!,
                                StartLine = from.Span.StartLine,
                                EndLine = from.Span.EndLine
                            }
                        }
                    };
                }

                return new FlowEdge
                {
                    From = from.Id,
                    To = to.Id,
                    Kind = "flow",
                    Evidence = evidence
                };
            }

            private static string BuildSyntheticId(SyntaxNode node)
            {
                var path = node.SyntaxTree?.FilePath ?? "synthetic";
                return $"{path}:{node.Span.Start}-{node.Span.End}";
            }
        }

        private sealed class NodeBuilder
        {
            private readonly HashSet<string> _tags = new(StringComparer.OrdinalIgnoreCase);
            private readonly List<SelectorMatch> _matches = new();
            private readonly Dictionary<string, object?> _properties = new(StringComparer.OrdinalIgnoreCase);
            private string? _type;
            private string? _name;
            private string? _fqdn;
            private string? _assembly;
            private string? _project;
            private string? _filePath;
            private FlowSpan? _span;
            private string _symbolId = string.Empty;

            public NodeBuilder(string id, ISymbol? symbol)
            {
                Id = id;
                Symbol = symbol;
            }

            public string Id { get; }

            public ISymbol? Symbol { get; }

            public bool IsPropagated { get; set; }

            public string? FilePath => _filePath;

            public FlowSpan? Span => _span;

            public IReadOnlyList<SelectorMatch> Matches => _matches;

            public void ApplyMatch(SelectorNodeRule rule, SelectorMatch match, string projectName, string assemblyName)
            {
                _type ??= rule.Type;
                foreach (var tag in rule.Tags)
                {
                    _tags.Add(tag);
                }

                _matches.Add(match);

                if (match.Captures is { Count: > 0 } captures)
                {
                    ApplyProperties(captures);
                }

                if (rule.PropertyExtractor is not null)
                {
                    var properties = rule.PropertyExtractor(match);
                    if (properties is not null)
                    {
                        ApplyProperties(properties);
                    }
                }

                InitializeFromMatch(match, projectName, assemblyName);
            }

            public void EnsureInitialized(string projectName, string assemblyName)
            {
                if (Symbol is not null)
                {
                    _assembly ??= Symbol.ContainingAssembly?.Name ?? assemblyName;
                    _project ??= projectName;
                    _symbolId = Symbol.GetDocumentationCommentId() ?? _symbolId;

                    if (string.IsNullOrEmpty(_name))
                    {
                        _name = Symbol.Name;
                    }

                    if (string.IsNullOrEmpty(_fqdn))
                    {
                        _fqdn = Symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                    }

                    if (string.IsNullOrEmpty(_filePath) || _span is null)
                    {
                        var location = Symbol.Locations.FirstOrDefault(l => l.IsInSource);
                        if (location is not null)
                        {
                            _filePath ??= location.SourceTree?.FilePath;
                            var span = location.GetLineSpan();
                            _span ??= new FlowSpan
                            {
                                StartLine = span.StartLinePosition.Line + 1,
                                EndLine = span.EndLinePosition.Line + 1
                            };
                        }
                    }

                    return;
                }

                _project ??= projectName;
                _assembly ??= assemblyName;
                if (string.IsNullOrEmpty(_name))
                {
                    _name = Id;
                }

                _fqdn ??= _name;
            }

            public FlowNode ToNode()
            {
                return new FlowNode
                {
                    Id = Id,
                    Type = _type ?? DetermineDefaultType(),
                    Name = _name ?? Id,
                    Fqdn = _fqdn ?? _name ?? Id,
                    Assembly = _assembly,
                    Project = _project,
                    FilePath = _filePath,
                    Span = _span,
                    SymbolId = _symbolId,
                    Tags = _tags.ToArray(),
                    Properties = _properties.Count > 0 ? new Dictionary<string, object?>(_properties, StringComparer.OrdinalIgnoreCase) : null
                };
            }

            private void InitializeFromMatch(SelectorMatch match, string projectName, string assemblyName)
            {
                if (Symbol is not null)
                {
                    _project ??= projectName;
                    _assembly ??= Symbol.ContainingAssembly?.Name ?? assemblyName;
                    _symbolId = Symbol.GetDocumentationCommentId() ?? _symbolId;
                    _name ??= Symbol.Name;
                    _fqdn ??= Symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

                    if (string.IsNullOrEmpty(_filePath) || _span is null)
                    {
                        _filePath ??= match.Node.SyntaxTree?.FilePath;
                        _span ??= CreateSpan(match.Node);
                    }

                    return;
                }

                _project ??= projectName;
                _assembly ??= assemblyName;
                _filePath ??= match.Node.SyntaxTree?.FilePath;
                _span ??= CreateSpan(match.Node);
                _name ??= DetermineNodeName(match.Node);
                _fqdn ??= _name;
            }

            private string DetermineDefaultType()
            {
                if (Symbol is IMethodSymbol)
                {
                    return "method";
                }

                if (Symbol is INamedTypeSymbol typeSymbol)
                {
                    return typeSymbol.TypeKind.ToString().ToLowerInvariant();
                }

                if (Symbol is not null)
                {
                    return Symbol.Kind.ToString().ToLowerInvariant();
                }

                return "node";
            }

            private static FlowSpan? CreateSpan(SyntaxNode node)
            {
                var tree = node.SyntaxTree;
                if (tree is null)
                {
                    return null;
                }

                var span = tree.GetLineSpan(node.Span);
                return new FlowSpan
                {
                    StartLine = span.StartLinePosition.Line + 1,
                    EndLine = span.EndLinePosition.Line + 1
                };
            }

            private static string? DetermineNodeName(SyntaxNode node)
            {
                return node switch
                {
                    Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax classDecl => classDecl.Identifier.Text,
                    Microsoft.CodeAnalysis.CSharp.Syntax.RecordDeclarationSyntax recordDecl => recordDecl.Identifier.Text,
                    Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax methodDecl => methodDecl.Identifier.Text,
                    Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax invocation => invocation.Expression.ToString(),
                    _ => null
                };
            }

            private void ApplyProperties(IReadOnlyDictionary<string, object?> properties)
            {
                foreach (var kvp in properties)
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
    }
}
