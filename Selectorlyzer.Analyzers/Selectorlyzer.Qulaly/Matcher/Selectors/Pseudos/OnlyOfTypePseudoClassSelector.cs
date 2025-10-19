using System.Linq;

namespace Selectorlyzer.Qulaly.Matcher.Selectors.Pseudos
{
    public class OnlyOfTypePseudoClassSelector : PseudoClassSelector
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

                var kind = ctx.Node.RawKind;
                var count = parent.ChildNodes().Count(n => n.RawKind == kind);
                return count == 1;
            };
        }

        public override string ToSelectorString()
        {
            return ":only-of-type";
        }
    }
}
