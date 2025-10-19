using System;

namespace Selectorlyzer.Qulaly.Matcher.Selectors.Properties
{
    public abstract class PropertyStringMatchSelector : PropertySelector
    {
        protected PropertyStringMatchSelector(string propertyName, string value, bool caseInsensitive, bool negate)
            : base(propertyName)
        {
            Value = value ?? string.Empty;
            Comparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            CaseInsensitive = caseInsensitive;
            Negate = negate;
        }

        protected string Value { get; }

        protected StringComparison Comparison { get; }

        protected bool CaseInsensitive { get; }

        protected bool Negate { get; }

        protected bool Evaluate(in SelectorMatcherContext ctx, Func<string, bool> predicate)
        {
            if (!TryResolveProperty(ctx, out var raw) || raw is null)
            {
                return false;
            }

            var candidate = raw.ToString();
            if (candidate == null)
            {
                return false;
            }

            var matched = predicate(candidate);
            return Negate ? !matched : matched;
        }
    }
}
