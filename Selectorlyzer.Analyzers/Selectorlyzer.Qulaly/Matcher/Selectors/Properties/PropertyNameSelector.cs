namespace Selectorlyzer.Qulaly.Matcher.Selectors.Properties
{
    public class PropertyNameSelector : PropertySelector
    {
        public PropertyNameSelector(string propertyName)
            : base(propertyName)
        {
        }

        public override SelectorMatcher GetMatcher()
        {
            return (in SelectorMatcherContext ctx) => TryResolveProperty(ctx, out _);
        }

        public override string ToSelectorString()
        {
            return $"[{PropertyName}]";
        }
    }
}
