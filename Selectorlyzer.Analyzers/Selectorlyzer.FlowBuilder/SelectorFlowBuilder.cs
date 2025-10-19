using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
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
            private readonly ImmutableArray<QulalySelector> _selectors;
            private readonly int[] _globalRuleIndices;
            private readonly Dictionary<SyntaxKind, int[]> _ruleIndicesByKind;
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
            private readonly Dictionary<INamedTypeSymbol, HashSet<INamedTypeSymbol>> _mutableDerivedTypesByBase = new(SymbolEqualityComparer.Default);
            private readonly Dictionary<INamedTypeSymbol, HashSet<INamedTypeSymbol>> _mutableImplementationsByInterface = new(SymbolEqualityComparer.Default);
            private readonly Dictionary<ITypeSymbol, HashSet<INamedTypeSymbol>> _mutableMediatorRequestHandlers = new(SymbolEqualityComparer.Default);
            private readonly Dictionary<ITypeSymbol, HashSet<INamedTypeSymbol>> _mutableMediatorNotificationHandlers = new(SymbolEqualityComparer.Default);
            private readonly SyntaxReferenceIndex _syntaxReferenceIndex = new();
            private bool _typeRelationsBuilt;

            public NodeRegistry(Compilation compilation, SelectorQueryContext baseContext, IReadOnlyList<SelectorNodeRule> rules)
            {
                _compilation = compilation;
                _baseContext = baseContext;
                _rules = rules;
                (_selectors, _globalRuleIndices, _ruleIndicesByKind) = BuildSelectorDispatchTables(rules);
                _defaultAssembly = compilation.AssemblyName ?? "Assembly";
                _defaultProject = baseContext.Metadata?.GetValueOrDefault("project") as string ?? _defaultAssembly;
            }

            public void Index()
            {
                BuildTypeRelationMaps();

                foreach (var tree in _compilation.SyntaxTrees)
                {
                    _syntaxReferenceIndex.IndexTree(tree, () => _compilation.GetSemanticModel(tree));
                    var root = tree.GetRoot();
                    root.QueryMatches(
                        _selectors,
                        GetCandidateRuleIndices,
                        HandleRuleMatch,
                        _compilation,
                        _baseContext);
                }
            }

            private static (ImmutableArray<QulalySelector> Selectors, int[] GlobalRuleIndices, Dictionary<SyntaxKind, int[]> RuleIndicesByKind)
                BuildSelectorDispatchTables(IReadOnlyList<SelectorNodeRule> rules)
            {
                var selectorBuilder = ImmutableArray.CreateBuilder<QulalySelector>(rules.Count);
                var global = new List<int>();
                var targeted = new Dictionary<SyntaxKind, List<int>>();

                for (var i = 0; i < rules.Count; i++)
                {
                    var selector = rules[i].Selector;
                    selectorBuilder.Add(selector);

                    var kinds = selector.GetTopLevelSyntaxKinds();
                    if (kinds.Count == 0)
                    {
                        global.Add(i);
                        continue;
                    }

                    foreach (var kind in kinds)
                    {
                        if (!targeted.TryGetValue(kind, out var list))
                        {
                            list = new List<int>();
                            targeted[kind] = list;
                        }

                        list.Add(i);
                    }
                }

                var globalArray = global.Count > 0 ? global.ToArray() : Array.Empty<int>();
                var ruleIndicesByKind = new Dictionary<SyntaxKind, int[]>(targeted.Count);

                foreach (var kvp in targeted)
                {
                    var specific = kvp.Value;
                    var combined = new int[globalArray.Length + specific.Count];
                    if (globalArray.Length > 0)
                    {
                        Array.Copy(globalArray, combined, globalArray.Length);
                    }

                    if (specific.Count > 0)
                    {
                        specific.CopyTo(combined, globalArray.Length);
                    }

                    ruleIndicesByKind[kvp.Key] = combined;
                }

                return (selectorBuilder.ToImmutable(), globalArray, ruleIndicesByKind);
            }

            private IReadOnlyList<int> GetCandidateRuleIndices(SyntaxNode node)
            {
                if (_ruleIndicesByKind.TryGetValue(node.Kind(), out var indices))
                {
                    return indices;
                }

                return _globalRuleIndices;
            }

            private void HandleRuleMatch(int ruleIndex, SelectorMatch match)
            {
                var rule = _rules[ruleIndex];
                var symbol = rule.UseSymbolIdentity ? match.Symbol : null;
                var builder = GetOrCreateBuilder(symbol, match.Node);
                if (builder is null)
                {
                    return;
                }

                builder.ApplyMatch(rule, match, _defaultProject, _defaultAssembly);
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
                if (!ReferenceEquals(_compilation, updatedCompilation))
                {
                    _compilation = updatedCompilation;
                    _baseContext = _baseContext.WithCompilation(updatedCompilation);
                }

                _syntaxReferenceIndex.IndexTree(treeToAdd, () => _compilation.GetSemanticModel(treeToAdd));

                if (_typeRelationsBuilt)
                {
                    ExtendTypeRelationMapsForTree(treeToAdd);
                }

                return treeToAdd;
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

                if (origin is null)
                {
                    var rootCache = new Dictionary<SyntaxTree, SyntaxNode>();
                    foreach (var snapshot in node.MatchSnapshots)
                    {
                        if (snapshot.SyntaxTree is not { } syntaxTree)
                        {
                            continue;
                        }

                        var ensuredTree = EnsureCompilationContainsTree(syntaxTree);
                        _syntaxReferenceIndex.IndexTree(ensuredTree, () => _compilation.GetSemanticModel(ensuredTree));

                        if (!rootCache.TryGetValue(ensuredTree, out var ensuredRoot))
                        {
                            ensuredRoot = ensuredTree.GetRoot();
                            rootCache[ensuredTree] = ensuredRoot;
                        }

                        var mappedNode = ensuredRoot.FindNode(snapshot.Span, getInnermostNodeForTie: true);

                        foreach (var referencedSymbol in _syntaxReferenceIndex.GetSymbols(mappedNode))
                        {
                            AddSymbol(referencedSymbol);
                        }
                    }
                }

                foreach (var candidate in referenced.ToArray())
                {
                    ExpandCandidateRelations(candidate, AddSymbol);
                }

                ExpandOriginRelations(origin, AddSymbol);

                node.ClearMatchSnapshots();

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

                    _syntaxReferenceIndex.IndexTree(ensuredTree, () => _compilation.GetSemanticModel(ensuredTree));

                    foreach (var referencedSymbol in _syntaxReferenceIndex.GetSymbols(mappedNode))
                    {
                        addSymbol(referencedSymbol);
                    }
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

                InitializeTypeRelationMaps();
            }

            private void InitializeTypeRelationMaps()
            {
                _mutableDerivedTypesByBase.Clear();
                _mutableImplementationsByInterface.Clear();
                _mutableMediatorRequestHandlers.Clear();
                _mutableMediatorNotificationHandlers.Clear();
                _derivedTypesByBase.Clear();
                _implementationsByInterface.Clear();
                _mediatorRequestHandlers.Clear();
                _mediatorNotificationHandlers.Clear();

                foreach (var namedType in EnumerateCompilationNamedTypes(_compilation))
                {
                    AddTypeRelations(namedType);
                }

                _typeRelationsBuilt = true;
            }

            private void ExtendTypeRelationMapsForTree(SyntaxTree tree)
            {
                if (!_typeRelationsBuilt || tree == null)
                {
                    return;
                }

                if (tree.GetRoot() is not CSharpSyntaxNode root)
                {
                    return;
                }

                var semanticModel = _compilation.GetSemanticModel(tree);
                foreach (var declaration in CollectTypeDeclarations(root))
                {
                    if (semanticModel.GetDeclaredSymbol(declaration) is INamedTypeSymbol namedType)
                    {
                        AddTypeRelations(namedType);
                    }
                }
            }

            private void AddTypeRelations(INamedTypeSymbol namedType)
            {
                if (namedType is null || !namedType.Locations.Any(l => l.IsInSource))
                {
                    return;
                }

                if (namedType.BaseType is INamedTypeSymbol baseType)
                {
                    AddMutableRelation(_mutableDerivedTypesByBase, _derivedTypesByBase, baseType, namedType);
                }

                foreach (var iface in namedType.AllInterfaces)
                {
                    if (iface is not INamedTypeSymbol interfaceType)
                    {
                        continue;
                    }

                    AddMutableRelation(_mutableImplementationsByInterface, _implementationsByInterface, interfaceType, namedType);

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
                        AddMutableRelation(_mutableMediatorRequestHandlers, _mediatorRequestHandlers, messageType, namedType);
                    }
                    else if (string.Equals(iface.Name, "INotificationHandler", StringComparison.Ordinal))
                    {
                        AddMutableRelation(_mutableMediatorNotificationHandlers, _mediatorNotificationHandlers, messageType, namedType);
                    }
                }
            }

            private static IEnumerable<MemberDeclarationSyntax> CollectTypeDeclarations(CSharpSyntaxNode root)
            {
                var collector = new TypeDeclarationCollector();
                collector.Visit(root);
                return collector.Declarations;
            }

            private void AddMutableRelation<TKey>(
                Dictionary<TKey, HashSet<INamedTypeSymbol>> mutable,
                Dictionary<TKey, ImmutableArray<INamedTypeSymbol>> immutable,
                TKey key,
                INamedTypeSymbol value)
                where TKey : notnull
            {
                if (!mutable.TryGetValue(key, out var set))
                {
                    set = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
                    mutable[key] = set;
                }

                if (set.Add(value))
                {
                    immutable[key] = set
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

            private sealed class TypeDeclarationCollector : CSharpSyntaxWalker
            {
                private readonly List<MemberDeclarationSyntax> _declarations = new();

                public IReadOnlyList<MemberDeclarationSyntax> Declarations => _declarations;

                public override void VisitClassDeclaration(ClassDeclarationSyntax node)
                {
                    _declarations.Add(node);
                    base.VisitClassDeclaration(node);
                }

                public override void VisitStructDeclaration(StructDeclarationSyntax node)
                {
                    _declarations.Add(node);
                    base.VisitStructDeclaration(node);
                }

                public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
                {
                    _declarations.Add(node);
                    base.VisitInterfaceDeclaration(node);
                }

                public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
                {
                    _declarations.Add(node);
                    base.VisitRecordDeclaration(node);
                }

                public override void VisitRecordStructDeclaration(RecordStructDeclarationSyntax node)
                {
                    _declarations.Add(node);
                    base.VisitRecordStructDeclaration(node);
                }

                public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
                {
                    _declarations.Add(node);
                    base.VisitEnumDeclaration(node);
                }

                public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
                {
                    _declarations.Add(node);
                    base.VisitDelegateDeclaration(node);
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
            private readonly List<MatchSnapshot> _matchSnapshots = new();
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

            internal IReadOnlyList<MatchSnapshot> MatchSnapshots => _matchSnapshots;

            public void ApplyMatch(SelectorNodeRule rule, SelectorMatch match, string projectName, string assemblyName)
            {
                _type ??= rule.Type;
                foreach (var tag in rule.Tags)
                {
                    _tags.Add(tag);
                }

                if (Symbol is null && match.Node.SyntaxTree is { } tree)
                {
                    _matchSnapshots.Add(new MatchSnapshot(tree, match.Node.Span));
                }

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

            internal void ClearMatchSnapshots()
            {
                _matchSnapshots.Clear();
            }

            internal readonly struct MatchSnapshot
            {
                public MatchSnapshot(SyntaxTree syntaxTree, TextSpan span)
                {
                    SyntaxTree = syntaxTree;
                    Span = span;
                }

                public SyntaxTree SyntaxTree { get; }

                public TextSpan Span { get; }
            }
        }
    }
}
