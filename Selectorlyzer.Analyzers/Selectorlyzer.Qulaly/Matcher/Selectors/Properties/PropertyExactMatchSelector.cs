namespace Selectorlyzer.Qulaly.Matcher.Selectors.Properties
{
    public class PropertyExactMatchSelector : PropertyStringMatchSelector
    {
        public PropertyExactMatchSelector(string propertyName, string value, bool caseInsensitive = false, bool negate = false)
            : base(propertyName, value, caseInsensitive, negate)
        {
        }

        public override SelectorMatcher GetMatcher()
        {
            return (in SelectorMatcherContext ctx) => Evaluate(ctx, candidate => string.Equals(candidate, Value, Comparison));
        }

        public override string ToSelectorString()
        {
            var negation = Negate ? "!" : string.Empty;
            var modifier = CaseInsensitive ? " i" : string.Empty;
            return $"[{PropertyName}{negation}='{Value}'{modifier}]";
        }
    }
}
