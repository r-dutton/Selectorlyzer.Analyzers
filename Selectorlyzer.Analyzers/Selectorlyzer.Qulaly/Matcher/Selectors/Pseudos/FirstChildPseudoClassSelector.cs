using System.Linq;

namespace Selectorlyzer.Qulaly.Matcher.Selectors.Pseudos
{
    public class FirstChildPseudoClassSelector : PseudoClassSelector
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

                return parent.ChildNodes().FirstOrDefault() == ctx.Node;
            };
        }

        public override string ToSelectorString()
        {
            return ":first-child";
        }
    }
}
