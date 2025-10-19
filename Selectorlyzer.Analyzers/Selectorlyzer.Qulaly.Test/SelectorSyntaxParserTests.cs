using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Selectorlyzer.Qulaly;
using Selectorlyzer.Qulaly.Matcher.Selectors;
using Selectorlyzer.Qulaly.Matcher.Selectors.Combinators;
using Selectorlyzer.Qulaly.Matcher.Selectors.Properties;
using Selectorlyzer.Qulaly.Matcher.Selectors.Pseudos;
using Selectorlyzer.Qulaly.Syntax;

namespace Qulaly.Tests
{
    public class SelectorSyntaxParserTests
    {
        [Fact]
        public void Invalid()
        {
            Assert.Throws<QulalyParseException>(() => new SelectorSyntaxParser().Parse(">"));
        }

        [Fact]
        public void IgnoreAroundWhitespaces()
        {
            var selector = new SelectorSyntaxParser().Parse("     ClassDeclaration         ");
            selector.ToSelectorString().Should().Be(new ComplexSelectorList(new ComplexSelector(new TypeSelector(SyntaxKind.ClassDeclaration))).ToSelectorString());
        }

        [Fact]
        public void Complex_1()
        {
            var selector = new SelectorSyntaxParser().Parse(":class :method:not([Name='Foo'])  SwitchSection ObjectCreationExpression    > :is(PredefinedType, GenericName)[Name^='List'] ");
            var expected = new ComplexSelectorList(new ComplexSelector(
                new CompoundSelector(new ClassPseudoClassSelector()),
                new DescendantCombinator(),
                new CompoundSelector(new MethodPseudoClassSelector(), new NotPseudoClassSelector(new PropertyExactMatchSelector("Name", "Foo"))),
                new DescendantCombinator(),
                new CompoundSelector(new TypeSelector(SyntaxKind.SwitchSection)),
                new DescendantCombinator(),
                new CompoundSelector(new TypeSelector(SyntaxKind.ObjectCreationExpression)),
                new ChildCombinator(),
                new CompoundSelector(
                    new IsPseudoClassSelector(
                        new TypeSelector(SyntaxKind.PredefinedType),
                        new TypeSelector(SyntaxKind.GenericName)
                    ),
                    new PropertyPrefixMatchSelector("Name", "List")
                )
            ));

            selector.ToSelectorString().Should().Be(expected.ToSelectorString());
        }

        [Fact]
        public void PseudoNot()
        {
            var selector = new SelectorSyntaxParser().Parse(":not([Name='Foo'])");
            var expected = new ComplexSelectorList(new ComplexSelector(
                new NotPseudoClassSelector(new PropertyExactMatchSelector("Name", "Foo"))
            ));

            selector.ToSelectorString().Should().Be(expected.ToSelectorString());
        }

        [Fact]
        public void PseudoImplements()
        {
            var selector = new SelectorSyntaxParser().Parse(":implements([Name='Foo'])");
            var expected = new ComplexSelectorList(new ComplexSelector(
                new ImplementsPseudoClassSelector(new PropertyExactMatchSelector("Name", "Foo"))
            ));

            selector.ToSelectorString().Should().Be(expected.ToSelectorString());
        }

        [Fact]
        public void PseudoIs()
        {
            var selector = new SelectorSyntaxParser().Parse(":is(PredefinedType, GenericName, :not([Name='Foo']))");
            var expected = new ComplexSelectorList(new ComplexSelector(
                new IsPseudoClassSelector(
                    new TypeSelector(SyntaxKind.PredefinedType),
                    new TypeSelector(SyntaxKind.GenericName),
                    new NotPseudoClassSelector(new PropertyExactMatchSelector("Name", "Foo"))
                )
            ));

            selector.ToSelectorString().Should().Be(expected.ToSelectorString());
        }

        [Fact]
        public void PseudoCapture()
        {
            var selector = new SelectorSyntaxParser().Parse(":capture(flowId, Symbol.Name)");
            var expected = new ComplexSelectorList(new ComplexSelector(
                new CapturePseudoClassSelector("flowId", "Symbol.Name")));

            selector.ToSelectorString().Should().Be(expected.ToSelectorString());
        }

        [Fact]
        public void Property_Eq()
        {
            var selector = new SelectorSyntaxParser().Parse("[Name = 123]");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(new ComplexSelectorList(new ComplexSelector(new PropertyEqualMatchSelector("Name", 123))).ToSelectorString());
        }

        [Fact]
        public void Property_Lt()
        {
            var selector = new SelectorSyntaxParser().Parse("[Name < 123]");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(new ComplexSelectorList(new ComplexSelector(new PropertyLessThanMatchSelector("Name", 123))).ToSelectorString());
        }

        [Fact]
        public void Property_LtEq()
        {
            var selector = new SelectorSyntaxParser().Parse("[Name <= 123]");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(new ComplexSelectorList(new ComplexSelector(new PropertyLessThanEqualMatchSelector("Name", 123))).ToSelectorString());
        }

        [Fact]
        public void Property_Gt()
        {
            var selector = new SelectorSyntaxParser().Parse("[Name > 123]");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(new ComplexSelectorList(new ComplexSelector(new PropertyGreaterThanMatchSelector("Name", 123))).ToSelectorString());
        }

        [Fact]
        public void Property_GtEq()
        {
            var selector = new SelectorSyntaxParser().Parse("[Name >= 123]");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(new ComplexSelectorList(new ComplexSelector(new PropertyGreaterThanEqualMatchSelector("Name", 123))).ToSelectorString());
        }

        [Fact]
        public void Property_NameOnly()
        {
            var selector = new SelectorSyntaxParser().Parse("[Name]");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(new ComplexSelectorList(new ComplexSelector(new PropertyNameSelector("Name"))).ToSelectorString());
        }

        [Fact]
        public void Property_Exact()
        {
            var selector = new SelectorSyntaxParser().Parse("[Name=A]");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(new ComplexSelectorList(new ComplexSelector(new PropertyExactMatchSelector("Name", "A"))).ToSelectorString());
        }

        [Fact]
        public void Property_Exact_1()
        {
            var selector = new SelectorSyntaxParser().Parse("[Name='A']");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(new ComplexSelectorList(new ComplexSelector(new PropertyExactMatchSelector("Name", "A"))).ToSelectorString());
        }

        [Fact]
        public void Property_Context_DashMatch()
        {
            var selector = new SelectorSyntaxParser().Parse(":scope[Context.Role|='role']");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(
                new ComplexSelectorList(
                    new ComplexSelector(
                        new CompoundSelector(
                            new ScopePseudoClassSelector(),
                            new PropertyDashMatchSelector("Context.Role", "role")
                        )
                    )
                ).ToSelectorString());
        }

        [Fact]
        public void Property_Symbol_Access()
        {
            var selector = new SelectorSyntaxParser().Parse(":class[Symbol.Name='Program']");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(
                new ComplexSelectorList(
                    new ComplexSelector(
                        new CompoundSelector(
                            new ClassPseudoClassSelector(),
                            new PropertyExactMatchSelector("Symbol.Name", "Program")
                        )
                    )
                ).ToSelectorString());
        }

        [Fact]
        public void Property_MetadataAlias()
        {
            var selector = new SelectorSyntaxParser().Parse("[@flow.Target='UserService']");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(
                new ComplexSelectorList(
                    new ComplexSelector(
                        new PropertyExactMatchSelector("@flow.Target", "UserService")
                    )
                ).ToSelectorString());
        }

        [Fact]
        public void Property_ItemContainsMatchSelector()
        {
            var selector = new SelectorSyntaxParser().Parse("[Name ~= 'A']");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(new ComplexSelectorList(new ComplexSelector(new PropertyItemContainsMatchSelector("Name", "A"))).ToSelectorString());
        }

        [Fact]
        public void PseudoClass_Class()
        {
            var selector = new SelectorSyntaxParser().Parse(":class");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(new ComplexSelectorList(new ComplexSelector(new ClassPseudoClassSelector())).ToSelectorString());
        }

        [Fact]
        public void PseudoClass_Property()
        {
            var selector = new SelectorSyntaxParser().Parse(":property");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(new ComplexSelectorList(new ComplexSelector(new PropertyPseudoClassSelector())).ToSelectorString());
        }

        [Fact]
        public void PseudoClass_Method()
        {
            var selector = new SelectorSyntaxParser().Parse(":method");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(new ComplexSelectorList(new ComplexSelector(new MethodPseudoClassSelector())).ToSelectorString());
        }

        [Fact]
        public void PseudoClass_Interface()
        {
            var selector = new SelectorSyntaxParser().Parse(":interface");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(new ComplexSelectorList(new ComplexSelector(new InterfacePseudoClassSelector())).ToSelectorString());
        }

        [Theory]
        [InlineData(":nth-child(even)", "even")]
        [InlineData(":nth-child(odd)", "odd")]
        [InlineData(":nth-child(-7)", "-7")]
        [InlineData(":nth-child(7)", "7")]
        [InlineData(":nth-child(+7)", "+7")]
        [InlineData(":nth-child(5n)", "5n")]
        [InlineData(":nth-child(n+7)", "n+7")]
        [InlineData(":nth-child(3n + 4)", "3n+4")]
        [InlineData(":nth-child(-n +3)", "-n+3")]
        [InlineData(":nth-child(n)", "n")]
        [InlineData(":nth-child(1)", "1")]
        [InlineData(":nth-child(0n+ 1)", "0n+1")]
        public void PseudoClass_Nth_Child(string selectorText, string nthExpression)
        {
            var selector = new SelectorSyntaxParser().Parse(selectorText);
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(new ComplexSelectorList(new ComplexSelector(new NthChildPseudoClassSelector(nthExpression))).ToSelectorString());
        }

        [Fact]
        public void PseudoClass_Namespace()
        {
            var selector = new SelectorSyntaxParser().Parse(":namespace");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(new ComplexSelectorList(new ComplexSelector(new NamespacePseudoClassSelector())).ToSelectorString());
        }

        [Fact]
        public void PseudoClass_Lambda()
        {
            var selector = new SelectorSyntaxParser().Parse(":lambda");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(new ComplexSelectorList(new ComplexSelector(new LambdaPseudoClassSelector())).ToSelectorString());
        }

        [Fact]
        public void TypeSelector_SyntaxKind_Invalid()
        {
            var ex = Assert.Throws<QulalyParseException>(() => new SelectorSyntaxParser().Parse("MethodDeclaration Unknown"));
            ex.Message.Should().EndWith("(at position 19)");
        }

        [Fact]
        public void TypeSelector_SyntaxKind()
        {
            var selector = new SelectorSyntaxParser().Parse("ClassDeclaration");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(new ComplexSelectorList(new ComplexSelector(new TypeSelector(SyntaxKind.ClassDeclaration))).ToSelectorString());
        }

        [Fact]
        public void UniversalTypeSelector()
        {
            var selector = new SelectorSyntaxParser().Parse("*");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(new ComplexSelectorList(new ComplexSelector(new UniversalTypeSelector())).ToSelectorString());
        }

        [Fact]
        public void DescendantCombinator()
        {
            var selector = new SelectorSyntaxParser().Parse("ClassDeclaration MethodDeclaration");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(
                new ComplexSelectorList(
                    new ComplexSelector(new TypeSelector(SyntaxKind.ClassDeclaration), new DescendantCombinator(), new TypeSelector(SyntaxKind.MethodDeclaration))
                ).ToSelectorString());
        }

        [Fact]
        public void ChildCombinator()
        {
            var selector = new SelectorSyntaxParser().Parse("ClassDeclaration > MethodDeclaration");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(
                new ComplexSelectorList(
                    new ComplexSelector(new TypeSelector(SyntaxKind.ClassDeclaration), new ChildCombinator(), new TypeSelector(SyntaxKind.MethodDeclaration))
                ).ToSelectorString());
        }

        [Fact]
        public void NextSiblingCombinator()
        {
            var selector = new SelectorSyntaxParser().Parse("ClassDeclaration + MethodDeclaration");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(
                new ComplexSelectorList(
                    new ComplexSelector(new TypeSelector(SyntaxKind.ClassDeclaration), new NextSiblingCombinator(), new TypeSelector(SyntaxKind.MethodDeclaration))
                ).ToSelectorString());
        }

        [Fact]
        public void SubsequentSiblingCombinator()
        {
            var selector = new SelectorSyntaxParser().Parse("ClassDeclaration ~ MethodDeclaration");
            selector.Should().BeOfType<ComplexSelectorList>();
            selector.ToSelectorString().Should().Be(
                new ComplexSelectorList(
                    new ComplexSelector(new TypeSelector(SyntaxKind.ClassDeclaration), new SubsequentSiblingCombinator(), new TypeSelector(SyntaxKind.MethodDeclaration))
                ).ToSelectorString());
        }
    }
}
