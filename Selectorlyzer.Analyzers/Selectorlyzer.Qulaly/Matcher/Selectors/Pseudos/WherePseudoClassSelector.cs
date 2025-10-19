using System;
using System.Linq;

using Selectorlyzer.Qulaly.Matcher;

namespace Selectorlyzer.Qulaly.Matcher.Selectors.Pseudos
{
    public class WherePseudoClassSelector : PseudoClassSelector
    {
        private readonly Selector[] _selectors;

        public WherePseudoClassSelector(params Selector[] selectors)
        {
            _selectors = selectors ?? throw new ArgumentNullException(nameof(selectors));
        }

        public override SelectorMatcher GetMatcher()
        {
            SelectorMatcher matcher = (in SelectorMatcherContext _) => false;
            foreach (var selector in _selectors)
            {
                matcher = SelectorCompilerHelper.ComposeOr(matcher, selector.GetMatcher());
            }

            return matcher;
        }

        public override string ToSelectorString()
        {
            return $":where({string.Join(",", _selectors.Select(x => x.ToSelectorString()))})";
        }
    }
}
