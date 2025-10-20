using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Selectorlyzer.FlowBuilder
{
    internal sealed class SyntaxReferenceIndex
    {
        private readonly Dictionary<SyntaxTree, TreeIndex> _indices = new();

        public void IndexTree(SyntaxTree syntaxTree, Func<SemanticModel> semanticModelFactory)
        {
            if (syntaxTree is null)
            {
                throw new ArgumentNullException(nameof(syntaxTree));
            }

            if (semanticModelFactory is null)
            {
                throw new ArgumentNullException(nameof(semanticModelFactory));
            }

            if (_indices.TryGetValue(syntaxTree, out var existing))
            {
                existing.UpdateSemanticModelFactory(semanticModelFactory);
                return;
            }

            _indices[syntaxTree] = new TreeIndex(syntaxTree, semanticModelFactory);
        }

        public IReadOnlyList<ISymbol> GetSymbols(SyntaxNode? node)
        {
            if (node?.SyntaxTree is not { } syntaxTree)
            {
                return Array.Empty<ISymbol>();
            }

            if (_indices.TryGetValue(syntaxTree, out var index))
            {
                return index.GetSymbols(node);
            }

            return Array.Empty<ISymbol>();
        }

        public void Clear()
        {
            _indices.Clear();
        }
        private sealed class TreeIndex
        {
            private readonly SyntaxTree _syntaxTree;
            private readonly ConditionalWeakTable<SyntaxNode, SymbolList> _cache = new();
            private Func<SemanticModel> _semanticModelFactory;
            private WeakReference<SemanticModel>? _semanticModel;

            public TreeIndex(SyntaxTree syntaxTree, Func<SemanticModel> semanticModelFactory)
            {
                _syntaxTree = syntaxTree;
                _semanticModelFactory = semanticModelFactory;
            }

            public void UpdateSemanticModelFactory(Func<SemanticModel> semanticModelFactory)
            {
                _semanticModelFactory = semanticModelFactory ?? throw new ArgumentNullException(nameof(semanticModelFactory));
            }

            public IReadOnlyList<ISymbol> GetSymbols(SyntaxNode node)
            {
                if (!ReferenceEquals(node.SyntaxTree, _syntaxTree))
                {
                    return ImmutableArray<ISymbol>.Empty;
                }

                var semanticModel = GetSemanticModel();
                var holder = _cache.GetValue(
                    node,
                    n => new SymbolList(SymbolCollector.Collect(semanticModel, n)));

                return holder.Symbols;
            }

            private SemanticModel GetSemanticModel()
            {
                if (_semanticModel is { } existing && existing.TryGetTarget(out var cached))
                {
                    return cached;
                }

                var semanticModel = _semanticModelFactory();
                _semanticModel = new WeakReference<SemanticModel>(semanticModel);
                return semanticModel;
            }
        }

        private sealed class SymbolList
        {
            public SymbolList(ImmutableArray<ISymbol> symbols)
            {
                Symbols = symbols;
            }

            public ImmutableArray<ISymbol> Symbols { get; }
        }

        private sealed class SymbolCollector : CSharpSyntaxWalker
        {
            private readonly SemanticModel _semanticModel;
            private readonly HashSet<ISymbol> _symbols = new(SymbolEqualityComparer.Default);
            private readonly ImmutableArray<ISymbol>.Builder _builder = ImmutableArray.CreateBuilder<ISymbol>();

            private SymbolCollector(SemanticModel semanticModel)
                : base(SyntaxWalkerDepth.StructuredTrivia)
            {
                _semanticModel = semanticModel;
            }

            public static ImmutableArray<ISymbol> Collect(SemanticModel semanticModel, SyntaxNode node)
            {
                var collector = new SymbolCollector(semanticModel);
                collector.Visit(node);
                return collector._builder.ToImmutable();
            }

            public override void VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                AddSymbols(ComputeInvocationSymbols(node));
                base.VisitInvocationExpression(node);
            }

            public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
            {
                AddSymbols(ComputeObjectCreationSymbols(node));
                base.VisitObjectCreationExpression(node);
            }

            public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
            {
                AddSymbols(ComputeObjectCreationSymbols(node));
                base.VisitImplicitObjectCreationExpression(node);
            }

            public override void VisitAttribute(AttributeSyntax node)
            {
                AddSymbols(ComputeAttributeSymbols(node));
                base.VisitAttribute(node);
            }

            public override void VisitIdentifierName(IdentifierNameSyntax node)
            {
                AddTypeSymbol(node);
                base.VisitIdentifierName(node);
            }

            public override void VisitGenericName(GenericNameSyntax node)
            {
                AddTypeSymbol(node);
                base.VisitGenericName(node);
            }

            public override void VisitQualifiedName(QualifiedNameSyntax node)
            {
                AddTypeSymbol(node);
                base.VisitQualifiedName(node);
            }

            public override void VisitAliasQualifiedName(AliasQualifiedNameSyntax node)
            {
                AddTypeSymbol(node);
                base.VisitAliasQualifiedName(node);
            }

            private void AddSymbols(ImmutableArray<ISymbol> symbols)
            {
                foreach (var symbol in symbols)
                {
                    if (symbol is null)
                    {
                        continue;
                    }

                    if (_symbols.Add(symbol))
                    {
                        _builder.Add(symbol);
                    }
                }
            }

            private void AddSymbol(ISymbol? symbol)
            {
                if (symbol is null || symbol.Kind == SymbolKind.Namespace)
                {
                    return;
                }

                if (_symbols.Add(symbol))
                {
                    _builder.Add(symbol);
                }
            }

            private void AddTypeSymbol(SyntaxNode node)
            {
                var symbolInfo = _semanticModel.GetSymbolInfo(node);
                if (symbolInfo.Symbol is ITypeSymbol typeSymbol)
                {
                    AddSymbol(typeSymbol);
                    return;
                }

                var candidateType = symbolInfo.CandidateSymbols.OfType<ITypeSymbol>().FirstOrDefault();
                if (candidateType is not null)
                {
                    AddSymbol(candidateType);
                    return;
                }

                var typeInfo = _semanticModel.GetTypeInfo(node);
                var fallbackType = typeInfo.Type ?? typeInfo.ConvertedType;
                if (fallbackType is ITypeSymbol type)
                {
                    AddSymbol(type);
                }
            }

            private ImmutableArray<ISymbol> ComputeInvocationSymbols(InvocationExpressionSyntax node)
            {
                var builder = ImmutableArray.CreateBuilder<ISymbol>();
                var symbolInfo = _semanticModel.GetSymbolInfo(node);
                var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();

                if (symbol is not null)
                {
                    builder.Add(symbol);

                    if (symbol is IMethodSymbol method && method.ReducedFrom is { } reduced)
                    {
                        builder.Add(reduced);
                    }
                }

                return builder.ToImmutable();
            }

            private ImmutableArray<ISymbol> ComputeObjectCreationSymbols(BaseObjectCreationExpressionSyntax node)
            {
                var builder = ImmutableArray.CreateBuilder<ISymbol>();
                var symbolInfo = _semanticModel.GetSymbolInfo(node);
                var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();

                if (symbol is not null)
                {
                    builder.Add(symbol);
                }

                var typeInfo = _semanticModel.GetTypeInfo(node);
                var typeSymbol = typeInfo.Type ?? typeInfo.ConvertedType;

                if (typeSymbol is null && symbol is IMethodSymbol methodSymbol)
                {
                    typeSymbol = methodSymbol.ContainingType;
                }

                if (typeSymbol is not null)
                {
                    builder.Add(typeSymbol);
                }

                return builder.ToImmutable();
            }

            private ImmutableArray<ISymbol> ComputeAttributeSymbols(AttributeSyntax node)
            {
                var builder = ImmutableArray.CreateBuilder<ISymbol>();
                var symbolInfo = _semanticModel.GetSymbolInfo(node);
                var symbol = symbolInfo.Symbol ??
                    symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault() ??
                    symbolInfo.CandidateSymbols.FirstOrDefault();

                if (symbol is not null)
                {
                    builder.Add(symbol);

                    if (symbol is IMethodSymbol methodSymbol && methodSymbol.ContainingType is { } containingType)
                    {
                        builder.Add(containingType);
                    }
                }

                return builder.ToImmutable();
            }
        }
    }
}
