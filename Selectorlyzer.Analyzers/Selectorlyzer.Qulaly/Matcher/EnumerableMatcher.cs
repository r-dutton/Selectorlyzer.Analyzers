using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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
            var targetedKinds = ResolveTopLevelSyntaxKinds(selector.Selector);
            // Only prune yielding the current node, not recursion into children
            if (targetedKinds != null && targetedKinds.Count > 0 && !targetedKinds.Contains(context.Node.Kind()))
            {
                // Don't yield this node, but still recurse into children
            }
            else
            {
                if (selector.Matcher(context))
                {
                    yield return context;
                }
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

        private static HashSet<SyntaxKind>? ResolveTopLevelSyntaxKinds(object selector)
        {
            var kinds = new HashSet<SyntaxKind>();
            if (selector == null) return null;
            TryResolveTopLevelSyntaxKinds(selector, kinds);
            return kinds.Count > 0 ? kinds : null;
        }

        private static bool TryResolveTopLevelSyntaxKinds(object selector, ISet<SyntaxKind> kinds)
        {
            if (selector == null) return false;
            var type = selector.GetType();
            if (type.Name == "TypeSelector" && type.GetProperty("Kind") != null)
            {
                var kind = (SyntaxKind)type.GetProperty("Kind")!.GetValue(selector);
                kinds.Add(kind);
                return true;
            }
            if (type.Name == "ComplexSelectorList" && type.GetProperty("Children") != null)
            {
                var children = (IEnumerable<object>)type.GetProperty("Children")!.GetValue(selector);
                foreach (var child in children)
                {
                    TryResolveTopLevelSyntaxKinds(child, kinds);
                }
                return true;
            }
            if (type.Name == "ComplexSelector" && type.GetProperty("Children") != null)
            {
                var elements = (IEnumerable<object>)type.GetProperty("Children")!.GetValue(selector);
                foreach (var element in elements)
                {
                    TryResolveTopLevelSyntaxKinds(element, kinds);
                }
                return true;
            }
            if (type.Name == "CompoundSelector" && type.GetProperty("Children") != null)
            {
                var children = (IEnumerable<object>)type.GetProperty("Children")!.GetValue(selector);
                foreach (var child in children)
                {
                    TryResolveTopLevelSyntaxKinds(child, kinds);
                }
                return true;
            }
            if (type.Name.EndsWith("PseudoClassSelector"))
            {
                if (type.Name == "ClassPseudoClassSelector")
                {
                    kinds.Add(SyntaxKind.ClassDeclaration);
                    return true;
                }
                if (type.Name == "MethodPseudoClassSelector")
                {
                    kinds.Add(SyntaxKind.MethodDeclaration);
                    return true;
                }
                if (type.Name == "PropertyPseudoClassSelector")
                {
                    kinds.Add(SyntaxKind.PropertyDeclaration);
                    return true;
                }
                if (type.Name == "InterfacePseudoClassSelector")
                {
                    kinds.Add(SyntaxKind.InterfaceDeclaration);
                    return true;
                }
                if (type.Name == "StructPseudoClassSelector")
                {
                    kinds.Add(SyntaxKind.StructDeclaration);
                    return true;
                }
                if (type.Name == "NamespacePseudoClassSelector")
                {
                    kinds.Add(SyntaxKind.NamespaceDeclaration);
                    kinds.Add(SyntaxKind.FileScopedNamespaceDeclaration);
                    return true;
                }
            }
            return false;
        }
    }
}
