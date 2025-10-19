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
            var effectiveCompilation = compilation ?? queryContext?.Compilation;
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
            var effectiveCompilation = compilation ?? queryContext?.Compilation;
            var semanticModel = effectiveCompilation?.GetSemanticModel(node.SyntaxTree);
            var context = (queryContext ?? SelectorQueryContext.Empty).WithCompilation(effectiveCompilation);
            return EnumerableMatcher.GetMatches(node, selector, semanticModel, context);
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
            var effectiveCompilation = compilation ?? queryContext?.Compilation;
            var semanticModel = effectiveCompilation?.GetSemanticModel(node.SyntaxTree);
            var context = (queryContext ?? SelectorQueryContext.Empty).WithCompilation(effectiveCompilation);
            return EnumerableMatcher.GetEnumerable(node, selector, semanticModel, context).FirstOrDefault();
        }
    }
}
