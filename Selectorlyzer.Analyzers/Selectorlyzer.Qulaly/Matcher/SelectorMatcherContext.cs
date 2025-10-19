using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Selectorlyzer.Qulaly.Matcher
{
    public readonly struct SelectorMatcherContext
    {
        private readonly Func<SyntaxNode, ISymbol?>? _symbolResolver;
        private readonly SelectorMatchState _matchState;

        public SelectorMatcherContext(
            SyntaxNode node,
            SemanticModel? semanticModel,
            SelectorQueryContext? queryContext = null,
            SyntaxNode? scope = null,
            SyntaxNode? root = null)
        {
            Node = node ?? throw new ArgumentNullException(nameof(node));
            SemanticModel = semanticModel;
            QueryContext = queryContext ?? SelectorQueryContext.Empty;
            Scope = scope ?? node;
            Root = root ?? node.SyntaxTree?.GetRoot() ?? node;
            Compilation = QueryContext.Compilation ?? semanticModel?.Compilation;
            Metadata = QueryContext.Metadata;
            _symbolResolver = BuildSymbolResolver(SemanticModel, QueryContext.SymbolResolver);
            Symbol = _symbolResolver?.Invoke(node);
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
            SemanticModel = semanticModel;
            QueryContext = queryContext;
            Scope = scope;
            Root = root;
            Compilation = QueryContext.Compilation ?? semanticModel?.Compilation;
            Metadata = QueryContext.Metadata;
            _symbolResolver = symbolResolver;
            Symbol = _symbolResolver?.Invoke(node);
            _matchState = matchState ?? throw new ArgumentNullException(nameof(matchState));
        }

        public SyntaxNode Node { get; }

        public SemanticModel? SemanticModel { get; }

        public Compilation? Compilation { get; }

        public SelectorQueryContext QueryContext { get; }

        public SyntaxNode Scope { get; }

        public SyntaxNode Root { get; }

        public IReadOnlyDictionary<string, object?>? Metadata { get; }

        public ISymbol? Symbol { get; }

        public SelectorMatchState MatchState => _matchState;

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
