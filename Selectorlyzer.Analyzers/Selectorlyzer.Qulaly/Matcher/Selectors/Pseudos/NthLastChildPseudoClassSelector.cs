using System;
using System.Linq;
using Selectorlyzer.Qulaly.Helpers;

namespace Selectorlyzer.Qulaly.Matcher.Selectors.Pseudos
{
    public class NthLastChildPseudoClassSelector : PseudoClassSelector
    {
        private readonly string _expression;

        public NthLastChildPseudoClassSelector(string expression)
        {
            _expression = expression ?? throw new ArgumentNullException(nameof(expression));
        }

        public override SelectorMatcher GetMatcher()
        {
            var (offset, step) = NthHelper.GetOffsetAndStep(_expression);

            return (in SelectorMatcherContext ctx) =>
            {
                var parent = ctx.Node.Parent;
                if (parent == null)
                {
                    return false;
                }

                var children = parent.ChildNodes().ToList();
                for (var index = 0; index < children.Count; index++)
                {
                    if (children[index] == ctx.Node)
                    {
                        var reverseIndex = children.Count - index - 1;
                        return NthHelper.IndexMatchesOffsetAndStep(reverseIndex, offset, step);
                    }
                }

                return false;
            };
        }

        public override string ToSelectorString()
        {
            return $":nth-last-child({_expression})";
        }
    }
}
