namespace Selectorlyzer.Qulaly.Matcher.Selectors.Pseudos
{
    public class ScopePseudoClassSelector : PseudoClassSelector
    {
        public override SelectorMatcher GetMatcher()
        {
            return (in SelectorMatcherContext ctx) => ctx.Node == ctx.Scope;
        }

        public override string ToSelectorString()
        {
            return ":scope";
        }
    }
}
