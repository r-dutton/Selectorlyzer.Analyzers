using System;
using System.Linq;
using Selectorlyzer.Qulaly.Matcher;

namespace Selectorlyzer.Qulaly.Matcher.Selectors.Pseudos
{
    /// <summary>
    /// 4.5. The Relational Pseudo-class: :has()
    /// </summary>
    public class HasPseudoClassSelector : PseudoClassSelector
    {
        private readonly Selector[] _relativeSelectors;

        public HasPseudoClassSelector(params Selector[] relativeSelectors)
        {
            _relativeSelectors = relativeSelectors ?? throw new ArgumentNullException(nameof(relativeSelectors));
        }

        public override SelectorMatcher GetMatcher()
        {
            SelectorMatcher matcher = (in SelectorMatcherContext _) => false;
            foreach (var selector in _relativeSelectors)
            {
                matcher = SelectorCompilerHelper.ComposeOr(matcher, selector.GetMatcher());
            }

            var query = new QulalySelector(matcher, this);

            return (in SelectorMatcherContext ctx) =>
            {
                return EnumerableMatcher.GetEnumerable(query, ctx).Any();
            };
        }

        public override string ToSelectorString()
        {
            return $":has({string.Join(",", _relativeSelectors.Select(x => x.ToSelectorString()))})";
        }
    }
}
