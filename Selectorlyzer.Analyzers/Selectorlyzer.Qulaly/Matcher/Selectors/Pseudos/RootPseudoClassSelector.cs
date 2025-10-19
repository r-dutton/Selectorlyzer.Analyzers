namespace Selectorlyzer.Qulaly.Matcher.Selectors.Pseudos
{
    public class RootPseudoClassSelector : PseudoClassSelector
    {
        public override SelectorMatcher GetMatcher()
        {
            return (in SelectorMatcherContext ctx) => ctx.Node == ctx.Root;
        }

        public override string ToSelectorString()
        {
            return ":root";
        }
    }
}
