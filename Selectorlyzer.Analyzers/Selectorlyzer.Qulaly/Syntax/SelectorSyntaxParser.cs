using System;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Qulaly.Syntax;
using Selectorlyzer.Qulaly.Matcher.Selectors;
using Selectorlyzer.Qulaly.Matcher.Selectors.Combinators;
using Selectorlyzer.Qulaly.Matcher.Selectors.Properties;
using Selectorlyzer.Qulaly.Matcher.Selectors.Pseudos;

namespace Selectorlyzer.Qulaly.Syntax
{
    public class SelectorSyntaxParser
    {
        public Selector Parse(string selector)
        {
            var root = GetProduction(selector);
            var selectorElement = Visit(root);

            return (Selector)selectorElement;
        }

        public Production GetProduction(string selector)
        {
            var reader = new SelectorSyntaxReader(selector);

            var root = reader.GetRoot();
            if (root == null || selector.Length != reader.Index)
            {
                throw new QulalyParseException($"'{selector}' is a not valid selector. (Unexpected token: '{selector.Substring(reader.Index, 1)}' at position {reader.Index + 1})");
            }

            return root;
        }

        public SelectorElement Visit(Production production)
        {
            return production.Kind switch
            {
                ProductionKind.Root => Visit(production.Children[0]),
                ProductionKind.ComplexSelectorList => new ComplexSelectorList(production.Children.Select(x => Visit(x)).Cast<Selector>().ToArray()),
                ProductionKind.CompoundSelector => new CompoundSelector(production.Children.Select(x => Visit(x)).Cast<Selector>().ToArray()),
                ProductionKind.ComplexSelector => new ComplexSelector(production.Children.Select(x => Visit(x)).ToArray()),
                ProductionKind.SubclassSelector => Visit(production.Children[0]),
                ProductionKind.PseudoClassSelector => VisitPseudoClassSelector(production),
                ProductionKind.IsPseudoClassSelector => VisitIsPseudoClassSelector(production),
                ProductionKind.HasPseudoClassSelector => VisitHasPseudoClassSelector(production),
                ProductionKind.ImplementsPseudoClassSelector => VisitImplementsPseudoClassSelector(production),
                ProductionKind.NthChildPseudoClassSelector => VisitNthChildPseudoClassSelector(production),
                ProductionKind.NotPseudoClassSelector => VisitNotPseudoClassSelector(production),
                ProductionKind.AttributeSelector => VisitAttributeSelector(production),
                ProductionKind.AttributeSelectorQulalyExtensionNumber => VisitAttributeQulalyExtensionNumberSelector(production),
                ProductionKind.Combinator => VisitCombinator(production),
                ProductionKind.TypeSelector => VisitTypeSelector(production),
                _ => throw new QulalyParseException($"Unknown Kind: {production.Kind}"),
            };
        }

        private SelectorElement VisitIsPseudoClassSelector(Production production)
        {
            var selectorList = production.Children[0]/* PseudoClassSelectorValue */.Children[0]/* ComplexSelectorList */;
            return new IsPseudoClassSelector(selectorList.Children.Select(x => Visit(x)).Cast<Selector>().ToArray());
        }
        private SelectorElement VisitHasPseudoClassSelector(Production production)
        {
            var selectorList = production.Children[0]/* PseudoClassSelectorValue */.Children[0]/* ComplexSelectorList */;
            return new HasPseudoClassSelector(selectorList.Children.Select(x => Visit(x)).Cast<Selector>().ToArray());
        }
        private SelectorElement VisitImplementsPseudoClassSelector(Production production)
        {
            var selectorList = production.Children[0]/* PseudoClassSelectorValue */.Children[0]/* ComplexSelectorList */;
            return new ImplementsPseudoClassSelector(selectorList.Children.Select(x => Visit(x)).Cast<Selector>().ToArray());
        }
        private SelectorElement VisitNthChildPseudoClassSelector(Production production)
        {
            var selectorList = string.Join(string.Empty, production.Children[0].Captures);
            return new NthChildPseudoClassSelector(selectorList);
        }
        private SelectorElement VisitNotPseudoClassSelector(Production production)
        {
            var compoundSelector = production.Children[0]/* PseudoClassSelectorValue */.Children[0]/* CompoundSelector */;
            return new NotPseudoClassSelector((Selector)Visit(compoundSelector));
        }

        private SelectorElement VisitTypeSelector(Production production)
        {
            if (production.Children.Any())
            {
                // wq-name
                var wqName = production.Children[0];
                var name = wqName.Captures[0];

                if (Enum.TryParse<SyntaxKind>(name, out var value))
                {
                    return new TypeSelector(value);
                }
                else
                {
                    throw new QulalyParseException($"Invalid SyntaxKind Type: {name}", production);
                }
            }
            else
            {
                // *
                return new UniversalTypeSelector();
            }
        }

        private SelectorElement VisitCombinator(Production production)
        {
            var combinator = production.Captures[0].Trim();
            return combinator switch
            {
                "" => new DescendantCombinator(),
                ">" => new ChildCombinator(),
                "+" => new NextSiblingCombinator(),
                "~" => new SubsequentSiblingCombinator(),
                _ => throw new QulalyParseException($"Unknown Combinator: {combinator}", production),
            };
        }

        private SelectorElement VisitPseudoClassSelector(Production production)
        {
            if (production.Captures.Any())
            {
                var pseudoName = production.Captures[0];
                return pseudoName switch
                {
                    "class" => new ClassPseudoClassSelector(),
                    "method" => new MethodPseudoClassSelector(),
                    "property" => new PropertyPseudoClassSelector(),
                    "interface" => new InterfacePseudoClassSelector(),
                    "namespace" => new NamespacePseudoClassSelector(),
                    "lambda" => new LambdaPseudoClassSelector(),
                    "first-child" => new FirstChildPseudoClassSelector(),
                    "last-child" => new LastChildPseudoClassSelector(),
                    _ => throw new QulalyParseException($"Unknown Pseudo-class: {pseudoName}", production),
                };
            }
            else
            {
                return Visit(production.Children[0]);
            }
        }

        private SelectorElement VisitAttributeSelector(Production production)
        {
            var name = production.Children[0].Captures[0]; // wq-name
            if (production.Captures.Any())
            {
                var matcher = production.Captures[0];
                var value = production.Captures[1];
                if (value.StartsWith("'") || value.EndsWith("\""))
                {
                    value = value.Substring(1, value.Length - 2);
                }

                return matcher switch
                {
                    "=" => new PropertyExactMatchSelector(name, value),
                    "*=" => new PropertySubstringMatchSelector(name, value),
                    "^=" => new PropertyPrefixMatchSelector(name, value),
                    "$=" => new PropertySuffixMatchSelector(name, value),
                    "~=" => new PropertyItemContainsMatchSelector(name, value),
                    _ => throw new QulalyParseException($"Unknown Matcher: {matcher}", production)
                };
            }
            else
            {
                return new PropertyNameSelector(name);
            }
        }

        private SelectorElement VisitAttributeQulalyExtensionNumberSelector(Production production)
        {
            var name = production.Children[0].Captures[0]; // wq-name
            if (production.Captures.Any())
            {
                var matcher = production.Captures[0];

                if (!int.TryParse(production.Captures[1], out var value))
                {
                    throw new QulalyParseException($"Invalid Number: {value}", production);
                }

                return matcher switch
                {
                    "=" => new PropertyEqualMatchSelector(name, value),
                    "<" => new PropertyLessThanMatchSelector(name, value),
                    "<=" => new PropertyLessThanEqualMatchSelector(name, value),
                    ">" => new PropertyGreaterThanMatchSelector(name, value),
                    ">=" => new PropertyGreaterThanEqualMatchSelector(name, value),
                    _ => throw new QulalyParseException($"Unknown Matcher: {matcher}", production)
                };
            }
            else
            {
                return new PropertyNameSelector(name);
            }
        }

    }
}
