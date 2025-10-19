using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Selectorlyzer.Qulaly.Matcher
{
    public readonly struct SelectorMatcherContext
    {
        private static readonly ConditionalWeakTable<SyntaxNode, ISymbol?> SymbolCache = new();
        private readonly Func<SyntaxNode, ISymbol?>? _symbolResolver;
        private readonly SelectorMatchState _matchState;
        private readonly SyntaxNode _node;
        private readonly SemanticModel? _semanticModel;

        public SelectorMatcherContext(
            SyntaxNode node,
            SemanticModel? semanticModel,
            SelectorQueryContext? queryContext = null,
            SyntaxNode? scope = null,
            SyntaxNode? root = null)
        {
            Node = node ?? throw new ArgumentNullException(nameof(node));
            _node = node ?? throw new ArgumentNullException(nameof(node));
            _semanticModel = semanticModel;
            SemanticModel = semanticModel;
            QueryContext = queryContext ?? SelectorQueryContext.Empty;
            Scope = scope ?? node;
            Root = root ?? node.SyntaxTree?.GetRoot() ?? node;
            Compilation = QueryContext.Compilation ?? semanticModel?.Compilation;
            Metadata = QueryContext.Metadata;
            _symbolResolver = BuildSymbolResolver(SemanticModel, QueryContext.SymbolResolver);
            _matchState = new SelectorMatchState();
        }

        private SelectorMatcherContext(
            SyntaxNode node,
            SemanticModel? semanticModel,
            SelectorQueryContext queryContext,
            SyntaxNode scope,
            SyntaxNode root,
            Func<SyntaxNode, ISymbol?>? symbolResolver,
            SelectorMatchState matchState)
        {
            Node = node ?? throw new ArgumentNullException(nameof(node));
            _node = node ?? throw new ArgumentNullException(nameof(node));
            _semanticModel = semanticModel;
            SemanticModel = semanticModel;
            QueryContext = queryContext;
            Scope = scope;
            Root = root;
            Compilation = QueryContext.Compilation ?? semanticModel?.Compilation;
            Metadata = QueryContext.Metadata;
            _symbolResolver = symbolResolver;
            _matchState = matchState ?? throw new ArgumentNullException(nameof(matchState));
        }

        public SyntaxNode Node { get; }
        public SemanticModel? SemanticModel { get; }
        public Compilation? Compilation { get; }
        public SelectorQueryContext QueryContext { get; }
        public SyntaxNode Scope { get; }
        public SyntaxNode Root { get; }
        public IReadOnlyDictionary<string, object?>? Metadata { get; }
        public SelectorMatchState MatchState => _matchState;

        public ISymbol? Symbol
        {
            get
            {
                if (_node == null || _symbolResolver == null)
                    return null;
                if (SymbolCache.TryGetValue(_node, out var cached))
                {
                    return cached;
                }
                var resolved = _symbolResolver.Invoke(_node);
                SymbolCache.Add(_node, resolved);
                return resolved;
            }
        }

        public SelectorMatcherContext WithSyntaxNode(SyntaxNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            return new SelectorMatcherContext(node, SemanticModel, QueryContext, Scope, Root, _symbolResolver, _matchState.CreateChild());
        }

        private static Func<SyntaxNode, ISymbol?>? BuildSymbolResolver(
            SemanticModel? semanticModel,
            Func<SyntaxNode, SemanticModel?, ISymbol?>? customResolver)
        {
            if (customResolver != null)
            {
                return node => customResolver(node, semanticModel);
            }
            if (semanticModel == null)
            {
                return null;
            }
            return node =>
            {
                if (node == null)
                {
                    return null;
                }
                var declared = semanticModel.GetDeclaredSymbol(node);
                if (declared != null)
                {
                    return declared;
                }
                var info = semanticModel.GetSymbolInfo(node);
                if (info.Symbol != null)
                {
                    return info.Symbol;
                }
                if (!info.CandidateSymbols.IsDefaultOrEmpty)
                {
                    return info.CandidateSymbols[0];
                }
                return null;
            };
        }
    }
}
