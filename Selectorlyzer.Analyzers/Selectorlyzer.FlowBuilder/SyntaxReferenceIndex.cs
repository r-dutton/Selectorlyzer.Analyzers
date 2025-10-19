using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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

                if (IsRelevant(node))
                {
                    AddNodeSymbols(node, collected);
                }

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

            private void AddNodeSymbols(SyntaxNode node, List<ISymbol> symbols)
            {
                var symbolInfo = _semanticModel.GetSymbolInfo(node);
                var symbol = symbolInfo.Symbol;

                if (symbol is null && !symbolInfo.CandidateSymbols.IsDefaultOrEmpty)
                {
                    symbol = symbolInfo.CandidateSymbols[0];
                }

                if (symbol is not null)
                {
                    symbols.Add(symbol);
                }

                var typeInfo = _semanticModel.GetTypeInfo(node);
                if (typeInfo.Type is not null)
                {
                    symbols.Add(typeInfo.Type);
                }

                if (typeInfo.ConvertedType is not null)
                {
                    symbols.Add(typeInfo.ConvertedType);
                }
            }

            private static bool IsRelevant(SyntaxNode node)
            {
                return node is IdentifierNameSyntax
                    or InvocationExpressionSyntax
                    or MethodDeclarationSyntax
                    or ClassDeclarationSyntax
                    or PropertyDeclarationSyntax
                    or InterfaceDeclarationSyntax
                    or StructDeclarationSyntax
                    or RecordDeclarationSyntax;
            }
        }
    }
}
