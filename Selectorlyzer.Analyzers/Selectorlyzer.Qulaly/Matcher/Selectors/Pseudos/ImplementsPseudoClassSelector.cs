using System;
using System.Linq;
using Selectorlyzer.Qulaly.Matcher;

namespace Selectorlyzer.Qulaly.Matcher.Selectors.Pseudos
{
    /// <summary>
    /// Custom
    /// </summary>
    public class ImplementsPseudoClassSelector : PseudoClassSelector
    {
        private readonly Selector[] _relativeSelectors;

        public ImplementsPseudoClassSelector(params Selector[] relativeSelectors)
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
                var baseTypeNode = ctx.Node.QuerySelector("BaseList SimpleBaseType", ctx.Compilation, ctx.QueryContext);
                if (baseTypeNode is null)
                {
                    return false;
                }

                var baseContext = new SelectorMatcherContext(baseTypeNode, ctx.SemanticModel, ctx.QueryContext, ctx.Scope, ctx.Root);
                return EnumerableMatcher.GetEnumerable(query, baseContext).Any();
            };
        }

        public override string ToSelectorString()
        {
            return $":implements({string.Join(",", _relativeSelectors.Select(x => x.ToSelectorString()))})";
        }
    }
}
