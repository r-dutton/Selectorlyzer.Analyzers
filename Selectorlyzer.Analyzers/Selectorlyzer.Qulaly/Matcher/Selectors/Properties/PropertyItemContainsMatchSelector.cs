using System;

namespace Selectorlyzer.Qulaly.Matcher.Selectors.Properties
{
    public class PropertyItemContainsMatchSelector : PropertyStringMatchSelector
    {
        public PropertyItemContainsMatchSelector(string propertyName, string value, bool caseInsensitive = false, bool negate = false)
            : base(propertyName, value, caseInsensitive, negate)
        {
        }

        public override SelectorMatcher GetMatcher()
        {
            return (in SelectorMatcherContext ctx) => Evaluate(ctx, candidate =>
            {
                var tokens = candidate.Split(new[] { ' ', '\t', '\r', '\n', '\f' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var token in tokens)
                {
                    if (string.Equals(token, Value, Comparison))
                    {
                        return true;
                    }
                }

                return false;
            });
        }

        public override string ToSelectorString()
        {
            var negation = Negate ? "!" : string.Empty;
            var modifier = CaseInsensitive ? " i" : string.Empty;
            return $"[{PropertyName}{negation}~='{Value}'{modifier}]";
        }
    }
}
