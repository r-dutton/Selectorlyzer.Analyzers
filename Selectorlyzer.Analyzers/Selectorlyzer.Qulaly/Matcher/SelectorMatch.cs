using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Selectorlyzer.Qulaly.Matcher
{
    public readonly struct SelectorMatch
    {
        public SelectorMatch(SyntaxNode node, SelectorMatcherContext context)
        {
            Node = node;
            Context = context;
        }

        public SyntaxNode Node { get; }

        public SelectorMatcherContext Context { get; }

        public SemanticModel? SemanticModel => Context.SemanticModel;

        public ISymbol? Symbol => Context.Symbol;

        public IReadOnlyDictionary<string, object?>? Captures => Context.MatchState.Captures;
    }
}
