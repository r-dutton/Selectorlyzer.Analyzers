using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Selectorlyzer.Qulaly.Matcher
{
    public sealed class SelectorQueryContext
    {
        public static SelectorQueryContext Empty { get; } = new SelectorQueryContext();

        public SelectorQueryContext(
            Compilation? compilation = null,
            IReadOnlyDictionary<string, object?>? metadata = null,
            Func<SyntaxNode, SemanticModel?, ISymbol?>? symbolResolver = null)
        {
            Compilation = compilation;
            Metadata = metadata;
            SymbolResolver = symbolResolver;
        }

        public Compilation? Compilation { get; }

        public IReadOnlyDictionary<string, object?>? Metadata { get; }

        public Func<SyntaxNode, SemanticModel?, ISymbol?>? SymbolResolver { get; }

        public SelectorQueryContext WithCompilation(Compilation? compilation)
        {
            if (Compilation == compilation)
            {
                return this;
            }

            return new SelectorQueryContext(compilation, Metadata, SymbolResolver);
        }

        public SelectorQueryContext WithMetadata(IReadOnlyDictionary<string, object?>? metadata)
        {
            if (ReferenceEquals(Metadata, metadata))
            {
                return this;
            }

            return new SelectorQueryContext(Compilation, metadata, SymbolResolver);
        }

        public SelectorQueryContext WithSymbolResolver(Func<SyntaxNode, SemanticModel?, ISymbol?>? symbolResolver)
        {
            if (SymbolResolver == symbolResolver)
            {
                return this;
            }

            return new SelectorQueryContext(Compilation, Metadata, symbolResolver);
        }
    }
}
