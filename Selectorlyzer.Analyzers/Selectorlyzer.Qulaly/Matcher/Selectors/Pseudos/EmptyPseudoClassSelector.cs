using System.Linq;

namespace Selectorlyzer.Qulaly.Matcher.Selectors.Pseudos
{
    public class EmptyPseudoClassSelector : PseudoClassSelector
    {
        public override SelectorMatcher GetMatcher()
        {
            return (in SelectorMatcherContext ctx) => !ctx.Node.ChildNodes().Any();
        }

        public override string ToSelectorString()
        {
            return ":empty";
        }
    }
}
