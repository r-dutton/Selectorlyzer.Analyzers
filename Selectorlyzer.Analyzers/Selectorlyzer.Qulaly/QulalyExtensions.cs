using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Selectorlyzer.Qulaly.Matcher;

namespace Selectorlyzer.Qulaly
{
    public static class QulalyExtensions
    {
        public static IEnumerable<SyntaxNode> QuerySelectorAll(this SyntaxTree syntaxTree, string selector, Compilation? compilation = default, SelectorQueryContext? queryContext = null)
        {
            return syntaxTree.GetRoot().QuerySelectorAll(selector, compilation, queryContext);
        }

        public static IEnumerable<SyntaxNode> QuerySelectorAll(this SyntaxNode node, string selector, Compilation? compilation = default, SelectorQueryContext? queryContext = null)
        {
            return node.QuerySelectorAll(QulalySelector.Parse(selector), compilation, queryContext);
        }

        public static IEnumerable<SyntaxNode> QuerySelectorAll(this SyntaxTree syntaxTree, QulalySelector selector, Compilation? compilation = default, SelectorQueryContext? queryContext = null)
        {
            return syntaxTree.GetRoot().QuerySelectorAll(selector, compilation, queryContext);
        }

        public static IEnumerable<SyntaxNode> QuerySelectorAll(this SyntaxNode node, QulalySelector selector, Compilation? compilation = default, SelectorQueryContext? queryContext = null)
        {
            var effectiveCompilation = EnsureCompilationContainsTree(
                compilation ?? queryContext?.Compilation,
                node.SyntaxTree);
            var semanticModel = effectiveCompilation?.GetSemanticModel(node.SyntaxTree);
            var context = (queryContext ?? SelectorQueryContext.Empty).WithCompilation(effectiveCompilation);
            return EnumerableMatcher.GetEnumerable(node, selector, semanticModel, context);
        }

        public static IEnumerable<SelectorMatch> QueryMatches(this SyntaxTree syntaxTree, string selector, Compilation? compilation = default, SelectorQueryContext? queryContext = null)
        {
            return syntaxTree.GetRoot().QueryMatches(selector, compilation, queryContext);
        }

        public static IEnumerable<SelectorMatch> QueryMatches(this SyntaxNode node, string selector, Compilation? compilation = default, SelectorQueryContext? queryContext = null)
        {
            return node.QueryMatches(QulalySelector.Parse(selector), compilation, queryContext);
        }

        public static IEnumerable<SelectorMatch> QueryMatches(this SyntaxTree syntaxTree, QulalySelector selector, Compilation? compilation = default, SelectorQueryContext? queryContext = null)
        {
            return syntaxTree.GetRoot().QueryMatches(selector, compilation, queryContext);
        }

        public static IEnumerable<SelectorMatch> QueryMatches(this SyntaxNode node, QulalySelector selector, Compilation? compilation = default, SelectorQueryContext? queryContext = null)
        {
            var effectiveCompilation = EnsureCompilationContainsTree(
                compilation ?? queryContext?.Compilation,
                node.SyntaxTree);
            var semanticModel = effectiveCompilation?.GetSemanticModel(node.SyntaxTree);
            var context = (queryContext ?? SelectorQueryContext.Empty).WithCompilation(effectiveCompilation);
            return EnumerableMatcher.GetMatches(node, selector, semanticModel, context);
        }

        public static void QueryMatches(
            this SyntaxNode node,
            IReadOnlyList<QulalySelector> selectors,
            Func<SyntaxNode, IReadOnlyList<int>> selectorProvider,
            Action<int, SelectorMatch> onMatch,
            Compilation? compilation = default,
            SelectorQueryContext? queryContext = null)
        {
            if (node is null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            if (selectors is null)
            {
                throw new ArgumentNullException(nameof(selectors));
            }

            if (selectorProvider is null)
            {
                throw new ArgumentNullException(nameof(selectorProvider));
            }

            if (onMatch is null)
            {
                throw new ArgumentNullException(nameof(onMatch));
            }

            if (selectors.Count == 0)
            {
                return;
            }

            var effectiveCompilation = EnsureCompilationContainsTree(
                compilation ?? queryContext?.Compilation,
                node.SyntaxTree);
            var semanticModel = effectiveCompilation?.GetSemanticModel(node.SyntaxTree);
            var context = (queryContext ?? SelectorQueryContext.Empty).WithCompilation(effectiveCompilation);
            EnumerableMatcher.ForEachMatch(node, selectors, semanticModel, context, selectorProvider, onMatch);
        }

        public static SyntaxNode? QuerySelector(this SyntaxTree syntaxTree, string selector, Compilation? compilation = default, SelectorQueryContext? queryContext = null)
        {
            return syntaxTree.GetRoot().QuerySelector(selector, compilation, queryContext);
        }

        public static SyntaxNode? QuerySelector(this SyntaxNode node, string selector, Compilation? compilation = default, SelectorQueryContext? queryContext = null)
        {
            return node.QuerySelector(QulalySelector.Parse(selector), compilation, queryContext);
        }

        public static SyntaxNode? QuerySelector(this SyntaxTree syntaxTree, QulalySelector selector, Compilation? compilation = default, SelectorQueryContext? queryContext = null)
        {
            return syntaxTree.GetRoot().QuerySelector(selector, compilation, queryContext);
        }

        public static SyntaxNode? QuerySelector(this SyntaxNode node, QulalySelector selector, Compilation? compilation = default, SelectorQueryContext? queryContext = null)
        {
            var effectiveCompilation = EnsureCompilationContainsTree(
                compilation ?? queryContext?.Compilation,
                node.SyntaxTree);
            var semanticModel = effectiveCompilation?.GetSemanticModel(node.SyntaxTree);
            var context = (queryContext ?? SelectorQueryContext.Empty).WithCompilation(effectiveCompilation);
            return EnumerableMatcher.GetEnumerable(node, selector, semanticModel, context).FirstOrDefault();
        }

        private static Compilation? EnsureCompilationContainsTree(Compilation? compilation, SyntaxTree? syntaxTree)
        {
            if (compilation is null || syntaxTree is null)
            {
                return compilation;
            }

            return compilation.SyntaxTrees.Contains(syntaxTree)
                ? compilation
                : compilation.AddSyntaxTrees(syntaxTree);
        }
    }
}
