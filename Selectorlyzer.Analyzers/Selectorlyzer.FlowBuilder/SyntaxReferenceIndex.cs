using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Selectorlyzer.FlowBuilder
{
    internal sealed class SyntaxReferenceIndex
    {
        private readonly Dictionary<SyntaxTree, TreeIndex> _indices = new();

        public void IndexTree(SyntaxTree syntaxTree, SemanticModel semanticModel)
        {
            if (syntaxTree is null)
            {
                throw new ArgumentNullException(nameof(syntaxTree));
            }

            if (semanticModel is null)
            {
                throw new ArgumentNullException(nameof(semanticModel));
            }

            if (_indices.ContainsKey(syntaxTree))
            {
                return;
            }

            var builder = new TreeIndexBuilder(semanticModel);
            builder.Visit(syntaxTree.GetRoot());
            _indices[syntaxTree] = new TreeIndex(builder.Complete());
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
            private readonly Dictionary<SyntaxNode, ImmutableArray<ISymbol>> _symbols;

            public TreeIndex(Dictionary<SyntaxNode, ImmutableArray<ISymbol>> symbols)
            {
                _symbols = symbols;
            }

            public IReadOnlyList<ISymbol> GetSymbols(SyntaxNode node)
            {
                return _symbols.TryGetValue(node, out var symbols)
                    ? symbols
                    : ImmutableArray<ISymbol>.Empty;
            }
        }

        private sealed class TreeIndexBuilder : CSharpSyntaxWalker
        {
            private readonly SemanticModel _semanticModel;
            private readonly Stack<List<ISymbol>> _symbolStack = new();
            private readonly Dictionary<SyntaxNode, ImmutableArray<ISymbol>> _symbolsByNode = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<SyntaxNode, ImmutableArray<ISymbol>> _boundSymbols = new(ReferenceEqualityComparer.Instance);

            public TreeIndexBuilder(SemanticModel semanticModel)
                : base(SyntaxWalkerDepth.StructuredTrivia)
            {
                _semanticModel = semanticModel ?? throw new ArgumentNullException(nameof(semanticModel));
            }

            public Dictionary<SyntaxNode, ImmutableArray<ISymbol>> Complete() => _symbolsByNode;

            public override void Visit(SyntaxNode? node)
            {
                if (node is null)
                {
                    return;
                }

                var collected = new List<ISymbol>();
                _symbolStack.Push(collected);

                base.Visit(node);

                _symbolStack.Pop();

                if (collected.Count == 0)
                {
                    return;
                }

                _symbolsByNode[node] = collected.ToImmutableArray();

                if (_symbolStack.Count > 0)
                {
                    _symbolStack.Peek().AddRange(collected);
                }
            }

            public override void VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                base.VisitInvocationExpression(node);
                CollectSymbolsFromNode(node);
            }

            public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
            {
                base.VisitObjectCreationExpression(node);
                CollectSymbolsFromNode(node);
            }

            public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
            {
                base.VisitImplicitObjectCreationExpression(node);
                CollectSymbolsFromNode(node);
            }

            public override void VisitAttribute(AttributeSyntax node)
            {
                base.VisitAttribute(node);
                CollectSymbolsFromNode(node);
            }

            private void CollectSymbolsFromNode(SyntaxNode node)
            {
                if (_symbolStack.Count == 0)
                {
                    return;
                }

                var symbols = GetOrComputeBoundSymbols(node);
                if (symbols.IsDefaultOrEmpty)
                {
                    return;
                }

                _symbolStack.Peek().AddRange(symbols);
            }

            private ImmutableArray<ISymbol> GetOrComputeBoundSymbols(SyntaxNode node)
            {
                if (_boundSymbols.TryGetValue(node, out var cached))
                {
                    return cached;
                }

                var result = ComputeBoundSymbols(node);
                _boundSymbols[node] = result;
                return result;
            }

            private ImmutableArray<ISymbol> ComputeBoundSymbols(SyntaxNode node)
            {
                switch (node)
                {
                    case InvocationExpressionSyntax invocation:
                        return ComputeInvocationSymbols(invocation);
                    case ImplicitObjectCreationExpressionSyntax implicitCreation:
                        return ComputeObjectCreationSymbols(implicitCreation);
                    case ObjectCreationExpressionSyntax objectCreation:
                        return ComputeObjectCreationSymbols(objectCreation);
                    case AttributeSyntax attribute:
                        return ComputeAttributeSymbols(attribute);
                    default:
                        return ImmutableArray<ISymbol>.Empty;
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
