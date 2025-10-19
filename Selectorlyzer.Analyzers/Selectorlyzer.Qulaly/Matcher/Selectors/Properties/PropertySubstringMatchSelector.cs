namespace Selectorlyzer.Qulaly.Matcher.Selectors.Properties
{
    public class PropertySubstringMatchSelector : PropertyStringMatchSelector
    {
        public PropertySubstringMatchSelector(string propertyName, string value, bool caseInsensitive = false, bool negate = false)
            : base(propertyName, value, caseInsensitive, negate)
        {
        }

        public override SelectorMatcher GetMatcher()
        {
            return (in SelectorMatcherContext ctx) => Evaluate(ctx, candidate => candidate.IndexOf(Value, Comparison) >= 0);
        }

        public override string ToSelectorString()
        {
            var negation = Negate ? "!" : string.Empty;
            var modifier = CaseInsensitive ? " i" : string.Empty;
            return $"[{PropertyName}{negation}*='{Value}'{modifier}]";
        }
    }
}
