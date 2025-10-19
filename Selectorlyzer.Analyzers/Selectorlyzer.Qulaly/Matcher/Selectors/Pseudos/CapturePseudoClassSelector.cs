using System;
using Microsoft.CodeAnalysis;

namespace Selectorlyzer.Qulaly.Matcher.Selectors.Pseudos
{
    public sealed class CapturePseudoClassSelector : PseudoClassSelector
    {
        private readonly string _alias;
        private readonly string? _propertyPath;
        private readonly CapturePropertyResolver? _resolver;

        public CapturePseudoClassSelector(string alias, string? propertyPath)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                throw new ArgumentException("Alias must be provided.", nameof(alias));
            }

            _alias = alias;
            _propertyPath = string.IsNullOrWhiteSpace(propertyPath) ? null : propertyPath;
            _resolver = _propertyPath is null ? null : new CapturePropertyResolver(_propertyPath);
        }

        public override SelectorMatcher GetMatcher()
        {
            return (in SelectorMatcherContext ctx) =>
            {
                object? value = null;

                if (_resolver is not null)
                {
                    if (!_resolver.TryResolve(ctx, out value))
                    {
                        value = null;
                    }
                }
                else if (ctx.Symbol is ISymbol symbol)
                {
                    value = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                }
                else
                {
                    value = ctx.Node;
                }

                ctx.MatchState.SetCapture(_alias, value);
                return true;
            };
        }

        public override string ToSelectorString()
        {
            if (string.IsNullOrEmpty(_propertyPath))
            {
                return $":capture({_alias})";
            }

            return $":capture({_alias}, {_propertyPath})";
        }

        private sealed class CapturePropertyResolver : Properties.PropertySelector
        {
            public CapturePropertyResolver(string propertyName)
                : base(propertyName)
            {
            }

            public bool TryResolve(in SelectorMatcherContext ctx, out object? value)
            {
                return TryResolveProperty(ctx, out value);
            }

            public override SelectorMatcher GetMatcher()
            {
                throw new NotSupportedException("Capture property resolver is not intended to be used as a matcher.");
            }

            public override string ToSelectorString()
            {
                return PropertyName;
            }
        }
    }
}
