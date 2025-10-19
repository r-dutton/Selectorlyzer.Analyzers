using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Selectorlyzer.Qulaly;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using VerifyCS = Selectorlyzer.Analyzers.Test.CSharpAnalyzerVerifier<
    Selectorlyzer.Analyzers.SelectorlyzerDiagnosticAnalyzer>;

namespace Selectorlyzer.Analyzers.Test
{
    public class SelectorlyzerDiagnosticAnalyzerTest
    {
        [Fact]
        public async Task Should_Verify_With_No_Diagnostic_For_No_Code()
        {
            var test = @"";
            await VerifyCS.VerifyAnalyzerAsync(test, new SelectorlyzerConfig
            {
                Rules = new List<SelectorlyzerRule>
                {
                    new SelectorlyzerRule
                    {
                        Message = "Valid Class Name",
                        Selector = ":class[Name='SampleClass']",
                        Severity = "warning"
                    }
                }
            });
        }

        [Fact]
        public async Task Should_Verify_With_No_Diagnostic_For_No_Config()
        {
            var test = @"
namespace ConsoleApplication1;
class SampleClass {}";
            await VerifyCS.VerifyAnalyzerAsync(test, new SelectorlyzerConfig());
        }

        [Fact]
        public async Task Should_Verify_With_No_Diagnostic_For_Simple_Selector()
        {
            var test = @"
namespace ConsoleApplication1;
class ValidClassName {}
class AnotherValidClassName {}";

            await VerifyCS.VerifyAnalyzerAsync(test, new SelectorlyzerConfig
            {
                Rules = new List<SelectorlyzerRule>
                {
                    new SelectorlyzerRule
                    {
                        Message = "Invalid Class Name",
                        Selector = ":class[Name='InvalidClassName']",
                        Severity = "warning"
                    }
                }
            });
        }

        [Fact]
        public async Task Should_Verify_With_Diagnostic_For_Simple_Selector()
        {
            var test = @"
namespace ConsoleApplication1;
class ValidClassName {}
class InvalidClassName {}";

            var expected = VerifyCS.Diagnostic(DiagnosticDescriptors.SelectorlyzerWarning).WithSpan(4, 1, 4, 26).WithArguments("Invalid Class Name");
            await VerifyCS.VerifyAnalyzerAsync(test, new SelectorlyzerConfig
            {
                Rules = new List<SelectorlyzerRule>
                {
                    new SelectorlyzerRule
                    {
                        Message = "Invalid Class Name",
                        Selector = ":class[Name='InvalidClassName']",
                        Severity = "warning"
                    }
                }
            }, expected);
        }

        [Fact]
        public async Task Should_Verify_With_Diagnostic_For_Nested_Selector()
        {
            var test = @"
namespace ConsoleApplication1;
class SampleClassName {
    public void ValidMethodName() {}
    public void InvalidMethodName() {}
}
class AnotherClassName {
    public void ValidMethodName() {}
    public void InvalidMethodName() {}
}";

            var expected = VerifyCS.Diagnostic(DiagnosticDescriptors.SelectorlyzerWarning).WithSpan(9, 5, 9, 39).WithArguments("Invalid Method Name In AnotherClassName");
            await VerifyCS.VerifyAnalyzerAsync(test, new SelectorlyzerConfig
            {
                Rules = new List<SelectorlyzerRule>
                {
                    new SelectorlyzerRule
                    {
                        Message = "Invalid Method Name In AnotherClassName",
                        Selector = ":class[Name='AnotherClassName'] :method[Name='InvalidMethodName']",
                        Severity = "warning"
                    }
                }
            }, expected);
        }


        [Fact]
        public async Task Should_Verify_With_Diagnostic_For_Interface_Check()
        {
            var test = @"
namespace ConsoleApplication1;
interface IValidClassName {}
abstract class BaseClass {}
class ValidClassName: BaseClass, IValidClassName {}
class InvalidClassName: BaseClass, IValidClassName {}";

            var expected = VerifyCS.Diagnostic(DiagnosticDescriptors.SelectorlyzerWarning).WithSpan(6, 1, 6, 54).WithArguments("Class must implement interface with class name");
            await VerifyCS.VerifyAnalyzerAsync(test, new SelectorlyzerConfig
            {
                Rules = new List<SelectorlyzerRule>
                {
                    new SelectorlyzerRule
                    {
                        Message = "Class must implement interface with class name",
                        Selector = ":class:not([Modifiers='abstract'])",
                        Rule = ":implements([Name='I{Name}'])",
                        Severity = "warning"
                    }
                }
            }, expected);
        }

        [Theory]
        [InlineData("error", DiagnosticDescriptors.ErrorId)]
        [InlineData("Error", DiagnosticDescriptors.ErrorId)]
        [InlineData("warning", DiagnosticDescriptors.WarningId)]
        [InlineData("Warning", DiagnosticDescriptors.WarningId)]
        [InlineData("info", DiagnosticDescriptors.InfoId)]
        [InlineData("Info", DiagnosticDescriptors.InfoId)]
        [InlineData("invalid", DiagnosticDescriptors.WarningId)]
        [InlineData("other", DiagnosticDescriptors.WarningId)]
        public void GetDiagnosticDescriptor_returns_correct_diagnostic_descriptor(string severity, string expectedId)
        {
            var result = SelectorlyzerDiagnosticAnalyzer.Analyzer.GetDiagnosticDescriptor(severity);
            result.Id.Should().Be(expectedId);
        }

        [Fact]
        public void Analyzer_precompiles_selector_when_rule_has_no_placeholders()
        {
            var analyzer = new SelectorlyzerDiagnosticAnalyzer.Analyzer(
                QulalySelector.Parse(":class"),
                ":method[Name='SomeMethod']",
                "Message",
                "warning");

            var ruleSelectorField = typeof(SelectorlyzerDiagnosticAnalyzer.Analyzer).GetField(
                "ruleSelector",
                BindingFlags.Instance | BindingFlags.NonPublic);

            var placeholderSelectorField = typeof(SelectorlyzerDiagnosticAnalyzer.Analyzer).GetField(
                "placeholderRuleSelectors",
                BindingFlags.Instance | BindingFlags.NonPublic);

            var ruleSelector = (QulalySelector?)ruleSelectorField?.GetValue(analyzer);
            var placeholderSelectors = (ConcurrentDictionary<string, QulalySelector>?)placeholderSelectorField?.GetValue(analyzer);

            ruleSelector.Should().NotBeNull();
            placeholderSelectors.Should().BeNull();
        }

        [Fact]
        public void CheckRule_caches_placeholder_selectors()
        {
            var source = @"class SampleClass {}";
            var syntaxTree = CSharpSyntaxTree.ParseText(source);
            var node = syntaxTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Single();

            var analyzer = new SelectorlyzerDiagnosticAnalyzer.Analyzer(
                QulalySelector.Parse(":class"),
                ":implements([Name='I{Name}'])",
                "Message",
                "warning");

            analyzer.CheckRule(node).Should().BeFalse();

            var placeholderSelectorField = typeof(SelectorlyzerDiagnosticAnalyzer.Analyzer).GetField(
                "placeholderRuleSelectors",
                BindingFlags.Instance | BindingFlags.NonPublic);

            var placeholderSelectors = (ConcurrentDictionary<string, QulalySelector>?)placeholderSelectorField?.GetValue(analyzer);

            placeholderSelectors.Should().NotBeNull();
            placeholderSelectors!.Count.Should().Be(1);

            analyzer.CheckRule(node).Should().BeFalse();
            placeholderSelectors.Count.Should().Be(1);
        }
    }
}
