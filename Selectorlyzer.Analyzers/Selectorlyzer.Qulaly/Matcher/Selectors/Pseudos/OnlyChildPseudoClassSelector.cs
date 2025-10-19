
namespace Selectorlyzer.Qulaly.Matcher.Selectors.Pseudos
{
    public class OnlyChildPseudoClassSelector : PseudoClassSelector
    {
        public override SelectorMatcher GetMatcher()
        {
            return (in SelectorMatcherContext ctx) =>
            {
                var parent = ctx.Node.Parent;
                if (parent == null)
                {
                    return false;
                }

                using var enumerator = parent.ChildNodes().GetEnumerator();
                if (!enumerator.MoveNext())
                {
                    return false;
                }

                var first = enumerator.Current;
                if (enumerator.MoveNext())
                {
                    return false;
                }

                return first == ctx.Node;
            };
        }

        public override string ToSelectorString()
        {
            return ":only-child";
        }
    }
}
