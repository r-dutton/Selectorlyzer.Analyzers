namespace Selectorlyzer.Qulaly.Matcher.Selectors.Properties
{
    public class PropertyDashMatchSelector : PropertyStringMatchSelector
    {
        public PropertyDashMatchSelector(string propertyName, string value, bool caseInsensitive = false, bool negate = false)
            : base(propertyName, value, caseInsensitive, negate)
        {
        }

        public override SelectorMatcher GetMatcher()
        {
            return (in SelectorMatcherContext ctx) => Evaluate(ctx, candidate =>
            {
                if (string.Equals(candidate, Value, Comparison))
                {
                    return true;
                }

                if (candidate.Length > Value.Length && candidate.StartsWith(Value, Comparison) && candidate[Value.Length] == '-')
                {
                    return true;
                }

                return false;
            });
        }

        public override string ToSelectorString()
        {
            var negation = Negate ? "!" : string.Empty;
            var modifier = CaseInsensitive ? " i" : string.Empty;
            return $"[{PropertyName}{negation}|='{Value}'{modifier}]";
        }
    }
}
