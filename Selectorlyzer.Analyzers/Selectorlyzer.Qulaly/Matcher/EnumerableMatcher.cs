using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Selectorlyzer.Qulaly.Matcher
{
    internal static class EnumerableMatcher
    {
        public static IEnumerable<SyntaxNode> GetEnumerable(
            SyntaxNode node,
            QulalySelector selector,
            SemanticModel? semanticModel,
            SelectorQueryContext? queryContext = null)
        {
            var context = new SelectorMatcherContext(node, semanticModel, queryContext, scope: node, root: node.SyntaxTree?.GetRoot() ?? node);
            return GetEnumerable(selector, context);
        }

        public static IEnumerable<SyntaxNode> GetEnumerable(
            QulalySelector selector,
            SelectorMatcherContext context)
        {
            foreach (var match in Enumerate(selector, context))
            {
                yield return match.Node;
            }
        }

        public static IEnumerable<SelectorMatch> GetMatches(
            SyntaxNode node,
            QulalySelector selector,
            SemanticModel? semanticModel,
            SelectorQueryContext? queryContext = null)
        {
            var context = new SelectorMatcherContext(node, semanticModel, queryContext, scope: node, root: node.SyntaxTree?.GetRoot() ?? node);
            return GetMatches(selector, context);
        }

        public static IEnumerable<SelectorMatch> GetMatches(
            QulalySelector selector,
            SelectorMatcherContext context)
        {
            foreach (var matchContext in Enumerate(selector, context))
            {
                yield return new SelectorMatch(matchContext.Node, matchContext);
            }
        }

        private static IEnumerable<SelectorMatcherContext> Enumerate(QulalySelector selector, SelectorMatcherContext context)
        {
            if (selector.Matcher(context))
            {
                yield return context;
            }

            foreach (var child in context.Node.ChildNodes())
            {
                var childContext = context.WithSyntaxNode(child);
                foreach (var match in Enumerate(selector, childContext))
                {
                    yield return match;
                }
            }
        }
    }
}
