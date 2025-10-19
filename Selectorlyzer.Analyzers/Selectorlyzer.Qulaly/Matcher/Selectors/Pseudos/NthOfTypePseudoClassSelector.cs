using System;
using System.Linq;
using Selectorlyzer.Qulaly.Helpers;

namespace Selectorlyzer.Qulaly.Matcher.Selectors.Pseudos
{
    public class NthOfTypePseudoClassSelector : PseudoClassSelector
    {
        private readonly string _expression;

        public NthOfTypePseudoClassSelector(string expression)
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

                var kind = ctx.Node.RawKind;
                var siblings = parent.ChildNodes().Where(n => n.RawKind == kind).ToList();
                for (var index = 0; index < siblings.Count; index++)
                {
                    if (siblings[index] == ctx.Node)
                    {
                        return NthHelper.IndexMatchesOffsetAndStep(index, offset, step);
                    }
                }

                return false;
            };
        }

        public override string ToSelectorString()
        {
            return $":nth-of-type({_expression})";
        }
    }
}
