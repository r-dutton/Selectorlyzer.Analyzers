using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Selectorlyzer.Qulaly.Matcher.Selectors.Properties
{
    public abstract class PropertySelector : Selector
    {
        protected PropertySelector(string propertyName)
        {
            PropertyName = propertyName ?? throw new ArgumentNullException(nameof(propertyName));
        }

        public string PropertyName { get; }

        protected string? GetFriendlyName(in SelectorMatcherContext ctx)
        {
            return ctx.Node switch
            {
                MethodDeclarationSyntax methodDeclSyntax => methodDeclSyntax.Identifier.ToString(),
                PropertyDeclarationSyntax propertyDeclSyntax => propertyDeclSyntax.Identifier.ToString(),
                TypeDeclarationSyntax typeDeclSyntax => typeDeclSyntax.Identifier.ToString(),
                ParameterSyntax paramSyntax => paramSyntax.Identifier.ToString(),
                NameSyntax nameSyntax => nameSyntax.ToString(),
                _ => default,
            };
        }

        protected bool TryResolveProperty(in SelectorMatcherContext ctx, out object? value)
        {
            value = null;

            if (string.Equals(PropertyName, "Name", StringComparison.OrdinalIgnoreCase))
            {
                var friendly = GetFriendlyName(ctx);
                if (friendly != null)
                {
                    value = friendly;
                    return true;
                }
            }

            var segments = PropertyName.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return false;
            }

            var current = ResolveRoot(segments[0], in ctx, out var index);
            if (current is null)
            {
                return false;
            }

            for (var i = index; i < segments.Length; i++)
            {
                if (current is null)
                {
                    return false;
                }

                var segment = segments[i];

                if (TryResolveFromEnumerable(current, segment, out var fromEnumerable))
                {
                    current = fromEnumerable;
                    continue;
                }

                if (!TryResolveSegment(current, segment, out current))
                {
                    return false;
                }
            }

            value = NormalizeValue(current);
            return true;
        }

        protected string? GetPropertyValue(in SelectorMatcherContext ctx)
        {
            return TryResolveProperty(ctx, out var value) ? value?.ToString() : null;
        }

        private static object? ResolveRoot(string head, in SelectorMatcherContext ctx, out int nextIndex)
        {
            nextIndex = 1;

            if (string.Equals(head, "@", StringComparison.OrdinalIgnoreCase))
            {
                var combined = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                if (ctx.Metadata is { } metadata)
                {
                    foreach (var kvp in metadata)
                    {
                        if (kvp.Key is null)
                        {
                            continue;
                        }

                        combined[kvp.Key] = kvp.Value;
                    }
                }

                if (ctx.MatchState.Captures is { } captures)
                {
                    foreach (var kvp in captures)
                    {
                        if (kvp.Key is null)
                        {
                            continue;
                        }

                        combined[kvp.Key] = kvp.Value;
                    }
                }

                return combined;
            }

            if (head.Length > 1 && head[0] == '@')
            {
                var key = head.Substring(1);
                if (string.IsNullOrWhiteSpace(key))
                {
                    return null;
                }

                if (ctx.MatchState.TryGetCapture(key, out var captureValue))
                {
                    return captureValue;
                }

                if (ctx.Metadata is { } metadata && metadata.TryGetValue(key, out var metadataValue))
                {
                    return metadataValue;
                }

                return null;
            }

            if (string.Equals(head, "Symbol", StringComparison.OrdinalIgnoreCase))
            {
                return ctx.Symbol;
            }

            if (string.Equals(head, "Type", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveNodeType(ctx);
            }

            if (string.Equals(head, "ConvertedType", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveConvertedType(ctx);
            }

            if (string.Equals(head, "DeclaredSymbol", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveDeclaredSymbol(ctx);
            }

            if (string.Equals(head, "ConstantValue", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveConstantValue(ctx);
            }

            if (string.Equals(head, "SemanticModel", StringComparison.OrdinalIgnoreCase))
            {
                return ctx.SemanticModel;
            }

            if (string.Equals(head, "Compilation", StringComparison.OrdinalIgnoreCase))
            {
                return ctx.Compilation;
            }

            if (string.Equals(head, "Context", StringComparison.OrdinalIgnoreCase))
            {
                return ctx.Metadata;
            }

            if (string.Equals(head, "Scope", StringComparison.OrdinalIgnoreCase))
            {
                return ctx.Scope;
            }

            if (string.Equals(head, "Root", StringComparison.OrdinalIgnoreCase))
            {
                return ctx.Root;
            }

            if (string.Equals(head, "Node", StringComparison.OrdinalIgnoreCase))
            {
                return ctx.Node;
            }

            nextIndex = 0;
            return ctx.Node;
        }

        private static object? ResolveSymbolMember(ISymbol symbol, string memberName, bool isInvocation, out bool found)
        {
            if (!isInvocation && string.Equals(memberName, "DisplayString", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                return symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            }

            var symbolType = symbol.GetType();

            if (TryResolveMember(symbolType, symbol, memberName, isInvocation, out var value))
            {
                found = true;
                return value;
            }

            foreach (var iface in symbolType.GetInterfaces())
            {
                if (TryResolveMember(iface, symbol, memberName, isInvocation, out value))
                {
                    found = true;
                    return value;
                }
            }

            found = false;
            return null;
        }

        private static bool TryResolveMember(Type type, object target, string memberName, bool isInvocation, out object? value)
        {
            const BindingFlags bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase | BindingFlags.FlattenHierarchy;

            if (isInvocation)
            {
                var method = type.GetMethod(memberName, bindingFlags, null, Type.EmptyTypes, null);
                if (method != null)
                {
                    value = method.Invoke(target, Array.Empty<object?>());
                    return true;
                }
            }
            else
            {
                if (type.GetProperty(memberName, bindingFlags) is { } property)
                {
                    value = property.GetValue(target);
                    return true;
                }

                if (type.GetField(memberName, bindingFlags) is { } field)
                {
                    value = field.GetValue(target);
                    return true;
                }
            }

            value = null;
            return false;
        }

        private static bool TryResolveFromEnumerable(object current, string segment, out object? value)
        {
            if (current is string || current is SyntaxNode || current is SyntaxToken || current is SyntaxTrivia)
            {
                value = null;
                return false;
            }

            if (current is not IEnumerable enumerable)
            {
                value = null;
                return false;
            }

            var results = new List<object?>();
            foreach (var item in enumerable)
            {
                if (item is null)
                {
                    continue;
                }

                if (TryResolveSegment(item, segment, out var resolved) && resolved is not null)
                {
                    results.Add(resolved);
                }
            }

            if (results.Count == 0)
            {
                value = null;
                return false;
            }

            value = results;
            return true;
        }

        private static bool TryResolveSegment(object current, string segment, out object? value)
        {
            var isInvocation = segment.EndsWith("()", StringComparison.Ordinal);
            var memberName = isInvocation ? segment.Substring(0, segment.Length - 2) : segment;
            if (string.IsNullOrWhiteSpace(memberName))
            {
                value = null;
                return false;
            }

            if (current is IReadOnlyDictionary<string, object?> readDict)
            {
                if (readDict.TryGetValue(memberName, out value))
                {
                    return true;
                }

                value = null;
                return false;
            }

            if (current is IDictionary<string, object?> dict)
            {
                if (dict.TryGetValue(memberName, out value))
                {
                    return true;
                }

                value = null;
                return false;
            }

            if (current is IDictionary dictionary)
            {
                if (dictionary.Contains(memberName))
                {
                    value = dictionary[memberName];
                    return true;
                }

                value = null;
                return false;
            }

            if (current is ISymbol symbol)
            {
                var resolved = ResolveSymbolMember(symbol, memberName, isInvocation, out var found);
                value = resolved;
                return found;
            }

            if (TryResolveMember(current.GetType(), current, memberName, isInvocation, out value))
            {
                return true;
            }

            value = null;
            return false;
        }

        private static object? ResolveNodeType(in SelectorMatcherContext ctx)
        {
            if (ctx.SemanticModel is not null)
            {
                var info = ctx.SemanticModel.GetTypeInfo(ctx.Node);
                if (info.Type is not null)
                {
                    return info.Type;
                }

                if (info.ConvertedType is not null)
                {
                    return info.ConvertedType;
                }
            }

            if (ctx.Symbol is ITypeSymbol typeSymbol)
            {
                return typeSymbol;
            }

            if (ctx.Symbol is IMethodSymbol methodSymbol)
            {
                return methodSymbol.ReturnType;
            }

            if (ctx.Symbol is IPropertySymbol propertySymbol)
            {
                return propertySymbol.Type;
            }

            if (ctx.Symbol is IFieldSymbol fieldSymbol)
            {
                return fieldSymbol.Type;
            }

            if (ctx.Symbol is IEventSymbol eventSymbol)
            {
                return eventSymbol.Type;
            }

            if (ctx.Symbol is IParameterSymbol parameterSymbol)
            {
                return parameterSymbol.Type;
            }

            if (ctx.Symbol is ILocalSymbol localSymbol)
            {
                return localSymbol.Type;
            }

            if (ctx.Symbol is IAliasSymbol aliasSymbol)
            {
                return aliasSymbol.Target is ITypeSymbol aliasTarget ? aliasTarget : null;
            }

            return null;
        }

        private static object? ResolveConvertedType(in SelectorMatcherContext ctx)
        {
            if (ctx.SemanticModel is null)
            {
                return null;
            }

            var info = ctx.SemanticModel.GetTypeInfo(ctx.Node);
            return info.ConvertedType ?? info.Type;
        }

        private static object? ResolveDeclaredSymbol(in SelectorMatcherContext ctx)
        {
            if (ctx.SemanticModel is null)
            {
                return ctx.Symbol;
            }

            return ctx.SemanticModel.GetDeclaredSymbol(ctx.Node) ?? ctx.Symbol;
        }

        private static object? ResolveConstantValue(in SelectorMatcherContext ctx)
        {
            if (ctx.SemanticModel is null)
            {
                return null;
            }

            var constant = ctx.SemanticModel.GetConstantValue(ctx.Node);
            return constant.HasValue ? constant.Value : null;
        }

        private static object? NormalizeValue(object? value)
        {
            if (value is null)
            {
                return null;
            }

            if (value is string || value is char)
            {
                return value;
            }

            if (value is ISymbol symbol)
            {
                return symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            }

            if (value is IEnumerable enumerable && value is not SyntaxNode && value is not SyntaxToken && value is not SyntaxTrivia)
            {
                var parts = new List<string>();
                foreach (var item in enumerable)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    var normalized = NormalizeValue(item);
                    if (normalized is null)
                    {
                        continue;
                    }

                    parts.Add(normalized.ToString() ?? string.Empty);
                }

                return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
            }

            if (value is ITypeSymbol typeSymbol)
            {
                return typeSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            }

            return value;
        }

        protected int? ConvertToNumber(object? value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is int i)
            {
                return i;
            }

            if (value is long l)
            {
                if (l > int.MaxValue || l < int.MinValue)
                {
                    return null;
                }

                return (int)l;
            }

            if (value is short s)
            {
                return s;
            }

            if (value is byte b)
            {
                return b;
            }

            if (value is IConvertible convertible)
            {
                try
                {
                    return Convert.ToInt32(convertible, CultureInfo.InvariantCulture);
                }
                catch
                {
                    return null;
                }
            }

            if (value is IEnumerable enumerable && value.GetType().IsClass && !(value is string))
            {
                var countProperty = value.GetType().GetProperty("Count");
                if (countProperty != null)
                {
                    var countValue = countProperty.GetValue(value);
                    return ConvertToNumber(countValue);
                }

                var counter = 0;
                foreach (var _ in enumerable)
                {
                    counter++;
                }

                return counter;
            }

            if (value is string strValue && int.TryParse(strValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            return null;
        }
    }
}
