using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Selectorlyzer.Qulaly;
using Selectorlyzer.Qulaly.Matcher;

namespace Qulaly.Tests
{
    public class MatcherTests
    {
        [Fact]
        public void Complex_1()
        {
            var selector = QulalySelector.Parse(":class SwitchSection ObjectCreationExpression > :is(PredefinedType, GenericName, IdentifierName):not([Name^='List']) ");
            var syntaxTree = CSharpSyntaxTree.ParseText(@"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ConsoleApp22
{
    public class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine(""Hello World!"");
        }

        public static async ValueTask<T> Test<T>(int a, string b, T c)
        {
            return default;
        }

        object Bar(int key)
        {
            switch (key)
            {
                case 0: return new object();
                case 1: return new List<int>();
                case 2: return new Program();
            }
            return null;
        }

        object Foo(int key)
        {
            switch (key)
            {
                case 0: return new int();
                case 1: return new List<string>();
                case 2: return new Exception();
            }
            return null;
        }
    }

    public readonly struct AStruct
    {}

    [MyNantoka]
    public class BClass<T>
        where T: class
    {}

    public class MyNantokaAttribute : Attribute { }
}
");

            var compilation = CSharpCompilation.Create("Test")
                    .AddSyntaxTrees(syntaxTree);

            var root = syntaxTree.GetCompilationUnitRoot();
            var matches = root.QuerySelectorAll(selector, compilation).ToArray();
            matches.Should().HaveCount(4); // object, Program, int, Exception
            matches.Select(x => x.ToString()).Should().ContainInOrder("object", "Program", "int", "Exception");
        }

        [Fact]
        public void Method()
        {
            var selector = QulalySelector.Parse(":method");
            var syntaxTree = CSharpSyntaxTree.ParseText(@"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ConsoleApp22
{
    public class Program
    {
        static void Main(string[] args)
        {
        }

        public static async ValueTask<T> Test<T>(int a, string b, T c)
        {
        }

        object Bar(int key)
        {
        }

        object Foo(int key)
        {
        }
    }
    public class Class1
    {
        object Bar(int key) => throw new NotImplementedException();

        object Foo(int key) => throw new NotImplementedException();
    }
}
");

            var compilation = CSharpCompilation.Create("Test")
                .AddSyntaxTrees(syntaxTree);

            var root = syntaxTree.GetCompilationUnitRoot();
            var matches = root.QuerySelectorAll(selector, compilation).ToArray();
            matches.Should().HaveCount(6);
            matches.OfType<MethodDeclarationSyntax>().Select(x => x.Identifier.ToString()).Should().ContainInOrder("Main", "Test", "Bar", "Foo", "Bar", "Foo");
        }

        [Fact]
        public void Class()
        {
            var selector = QulalySelector.Parse(":class");
            var syntaxTree = CSharpSyntaxTree.ParseText(@"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ConsoleApp22
{
    public class Program
    {
        static void Main(string[] args)
        {
        }

        public static async ValueTask<T> Test<T>(int a, string b, T c)
        {
        }

        object Bar(int key) => throw new NotImplementedException();
        object Foo(int key) => throw new NotImplementedException();
    }
    public class Class1
    {
        object Bar(int key) => throw new NotImplementedException();
        object Foo(int key) => throw new NotImplementedException();
    }
}
");

            var compilation = CSharpCompilation.Create("Test")
                .AddSyntaxTrees(syntaxTree);

            var root = syntaxTree.GetCompilationUnitRoot();
            var matches = root.QuerySelectorAll(selector, compilation).ToArray();
            matches.Should().HaveCount(2);
            matches.OfType<ClassDeclarationSyntax>().Select(x => x.Identifier.ToString()).Should().ContainInOrder("Program", "Class1");
        }

        [Fact]
        public void Property()
        {
            var selector = QulalySelector.Parse(":property");
            var syntaxTree = CSharpSyntaxTree.ParseText(@"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ConsoleApp22
{
    public class Program
    {
        static void Main(string[] args)
        {
        }
    }

    public class Class
    {
        public string? PropertyA { get; set; }
        public string? PropertyB { get; set; }
    }
}
");

            var compilation = CSharpCompilation.Create("Test")
                .AddSyntaxTrees(syntaxTree);

            var root = syntaxTree.GetCompilationUnitRoot();
            var matches = root.QuerySelectorAll(selector, compilation).ToArray();
            matches.Should().HaveCount(2);
            matches.OfType<PropertyDeclarationSyntax>().Select(x => x.Identifier.ToString()).Should().ContainInOrder("PropertyA", "PropertyB");
        }

        [Fact]
        public void PropertySelector_Invokes_Symbol_Methods_And_Projects_Collections()
        {
            var selector = QulalySelector.Parse(":class[Symbol.GetAttributes().AttributeClass.Name~='AuthorizeAttribute']");
            var syntaxTree = CSharpSyntaxTree.ParseText(@"using System;

[AttributeUsage(AttributeTargets.Class)]
public sealed class AuthorizeAttribute : Attribute { }

[Authorize]
public sealed class SecuredController { }

public sealed class PlainController { }
");

            var compilation = CSharpCompilation.Create("Test")
                .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
                .AddSyntaxTrees(syntaxTree);

            var root = syntaxTree.GetCompilationUnitRoot();
            var matches = root.QuerySelectorAll(selector, compilation).ToArray();
            matches.Should().ContainSingle();
            matches.OfType<ClassDeclarationSyntax>().Single().Identifier.Text.Should().Be("SecuredController");
        }

        [Fact]
        public void PropertyCount()
        {
            var selector = QulalySelector.Parse(":method:has(ParameterList[Count > 1])"); // the method has two or more parameters.
            var syntaxTree = CSharpSyntaxTree.ParseText(@"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ConsoleApp22
{
    public class Program
    {
        static void Main(string[] args)
        {
        }

        public static async ValueTask<T> Test<T>(int a, string b, T c)
        {
        }

        object Bar(int key)
        {
        }

        object Foo(int key)
        {
        }
    }
    public class Class1
    {
        object MethodA(int arg1) => throw new NotImplementedException();
        object MethodB(int arg1, string arg2) => throw new NotImplementedException();
    }
}
");

            var compilation = CSharpCompilation.Create("Test")
                .AddSyntaxTrees(syntaxTree);

            var root = syntaxTree.GetCompilationUnitRoot();
            var matches = root.QuerySelectorAll(selector, compilation).ToArray();
            matches.Should().HaveCount(2);
            matches.OfType<MethodDeclarationSyntax>().Select(x => x.Identifier.ToString()).Should().ContainInOrder("Test", "MethodB");
        }

        [Fact]
        public void PropertyCount_TypeParameter_Count()
        {
            var selector = QulalySelector.Parse(":method[TypeParameters.Count > 0]"); // the method has one or more type-parameters.
            var syntaxTree = CSharpSyntaxTree.ParseText(@"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ConsoleApp22
{
    public class Program
    {
        static void Main(string[] args)
        {
        }

        public static async ValueTask<T> Test<T>(int a, string b, T c)
        {
        }

        object Bar(int key)
        {
        }

        object Foo(int key)
        {
        }
    }
    public class Class1
    {
        object MethodA(int arg1) => throw new NotImplementedException();
        object MethodB(int arg1, string arg2) => throw new NotImplementedException();
    }
}
");

            var compilation = CSharpCompilation.Create("Test")
                .AddSyntaxTrees(syntaxTree);

            var root = syntaxTree.GetCompilationUnitRoot();
            var matches = root.QuerySelectorAll(selector, compilation).ToArray();
            matches.Should().HaveCount(1);
            matches.OfType<MethodDeclarationSyntax>().Select(x => x.Identifier.ToString()).Should().ContainInOrder("Test");
        }

        [Fact]
        public void PseudoClass_FirstChild()
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(@"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ConsoleApp22
{
    public class Program
    {
        static void Main(string[] args) => throw new NotImplementedException();
        public static async ValueTask<T> Test<T>(int a, string b, T c) => throw new NotImplementedException();
        object Bar(int key) => throw new NotImplementedException();
        object Foo(int key) => throw new NotImplementedException();
    }
    public class Class1
    {
        object MethodA(int arg1) => throw new NotImplementedException();
        object MethodB(int arg1, string arg2) => throw new NotImplementedException();
    }
}
");

            var firsts = syntaxTree.QuerySelectorAll(":method:first-child").ToArray();
            firsts.Should().HaveCount(2);
            firsts.OfType<MethodDeclarationSyntax>().Select(x => x.Identifier.ToFullString()).Should().ContainInOrder("Main", "MethodA");
        }

        [Fact]
        public void PseudoClass_LastChild()
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(@"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ConsoleApp22
{
    public class Program
    {
        static void Main(string[] args) => throw new NotImplementedException();
        public static async ValueTask<T> Test<T>(int a, string b, T c) => throw new NotImplementedException();
        object Bar(int key) => throw new NotImplementedException();
        object Foo(int key) => throw new NotImplementedException();
    }
    public class Class1
    {
        object MethodA(int arg1) => throw new NotImplementedException();
        object MethodB(int arg1, string arg2) => throw new NotImplementedException();
    }
}
");

            var lasts = syntaxTree.QuerySelectorAll(":method:last-child").ToArray();
            lasts.Should().HaveCount(2);
            lasts.OfType<MethodDeclarationSyntax>().Select(x => x.Identifier.ToFullString()).Should().ContainInOrder("Foo", "MethodB");
        }

        [Fact]
        public void Implements()
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(@"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ConsoleApp22
{
    public interface ISample {}
    public interface IOther {}
    public class Program
    {
        static void Main(string[] args) => throw new NotImplementedException();
        public static async ValueTask<T> Test<T>(int a, string b, T c) => throw new NotImplementedException();
        object Bar(int key) => throw new NotImplementedException();
        object Foo(int key) => throw new NotImplementedException();
    }
    public class Class1 : ISample
    {
        object MethodA(int arg1) => throw new NotImplementedException();
        object MethodB(int arg1, string arg2) => throw new NotImplementedException();
    }
    public class Class2 : ISample
    {
        object MethodA(int arg1) => throw new NotImplementedException();
        object MethodB(int arg1, string arg2) => throw new NotImplementedException();
    }
    public class Class3 : IOther
    {
        object MethodA(int arg1) => throw new NotImplementedException();
        object MethodB(int arg1, string arg2) => throw new NotImplementedException();
    }
}
");

            var matches = syntaxTree.QuerySelectorAll(":class:implements([Name='ISample'])").ToArray();
            matches.Should().HaveCount(2);
            matches.OfType<ClassDeclarationSyntax>().Select(x => x.Identifier.ToString()).Should().ContainInOrder("Class1", "Class2");
        }

        [Theory]
        [InlineData("2n", new[] { "Foo", "World" })]
        [InlineData("2n+1", new[] { "Bar", "Hello" })]
        [InlineData("1", new[] { "Bar" })]
        [InlineData("n", new[] { "Bar", "Foo", "Hello", "World" })]
        public void NthChild(string expression, string[] expected)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(@"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ConsoleApp22
{
    public class Program
    {
        void Bar()
        {
        }

        void Foo()
        {
        }

        void Hello()
        {
        }

        void World()
        {
        }
    }
}
");
            var matches = syntaxTree.QuerySelectorAll($":method:nth-child({expression})").ToArray();
            matches.Should().HaveCount(expected.Length);
            matches.OfType<MethodDeclarationSyntax>().Select(x => x.Identifier.ToString()).Should().ContainInOrder(expected);
        }

        [Fact]
        public void Non_Friendly_Name()
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(@"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ConsoleApp22
{
    public static string Value { get; set; }

    public class Program
    {
        static void Main(string[] args) => {
            var value = ConsoleApp22.Value;
        }
    }
}
");

            var firsts = syntaxTree.QuerySelectorAll("SimpleMemberAccessExpression[Name=Value]").ToArray();
            firsts.Should().HaveCount(1);
            firsts.OfType<MemberAccessExpressionSyntax>().Select(x => x.Name.ToFullString()).Should().ContainInOrder("Value");
        }


        [Fact]
        public void Friendly_Name()
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(@"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ConsoleApp22
{
    public class Program
    {
        void Bar()
        {
        }

        void Foo()
        {
        }

        void Hello()
        {
        }

        void World()
        {
        }
    }
}
");
            var firsts = syntaxTree.QuerySelectorAll("MethodDeclaration[Name=Hello]").ToArray();
            firsts.Should().HaveCount(1);
            firsts.OfType<MethodDeclarationSyntax>().Select(x => x.Identifier.ToFullString()).Should().ContainInOrder("Hello");
        }

        [Fact]
        public void Namespace()
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(@"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ConsoleApp22
{
    public class Test
    {
    }
}

namespace ConsoleApp23
{
    public class Test
    {
    }
}

namespace ConsoleApp24
{
    public class Test
    {
    }
}
");

            var matches = syntaxTree.QuerySelectorAll(":namespace:has(:class[Name=Test])").ToArray();
            matches.Should().HaveCount(3);
            matches.OfType<NamespaceDeclarationSyntax>().Select(x => x.Name.ToString()).Should().ContainInOrder("ConsoleApp22", "ConsoleApp23", "ConsoleApp24");
        }

        [Fact]
        public void File_Scoped_Namespace()
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(@"
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ConsoleApp22;

public class Test
{
}

");

            var matches = syntaxTree.QuerySelectorAll(":namespace:has(:class[Name=Test])").ToArray();
            matches.Should().HaveCount(1);
            matches.OfType<FileScopedNamespaceDeclarationSyntax>().Select(x => x.Name.ToString()).Should().ContainInOrder("ConsoleApp22");
        }




        [Fact]
        public void AttributeSelectors_ExtendedMatchers()
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(@"
using System;

namespace SampleApp
{
    class Controller
    {
        void HttpGetAction() { }
        void HttpPatchAction() { }
        void OtherAction() { }
    }
}
");

            var compilation = CSharpCompilation.Create("Test")
                .AddSyntaxTrees(syntaxTree);

            var root = syntaxTree.GetCompilationUnitRoot();

            var caseInsensitiveSelector = QulalySelector.Parse(":class[Name='controller' i]");
            var caseInsensitiveMatches = root.QuerySelectorAll(caseInsensitiveSelector, compilation).ToArray();
            caseInsensitiveMatches.Should().HaveCount(1);
            caseInsensitiveMatches.OfType<ClassDeclarationSyntax>().Single().Identifier.Text.Should().Be("Controller");

            var notSelector = QulalySelector.Parse(":method[Name!='HttpPatchAction']");
            var totalMethods = root.QuerySelectorAll(":method", compilation).OfType<MethodDeclarationSyntax>().ToArray();
            var filteredMethods = root.QuerySelectorAll(notSelector, compilation).OfType<MethodDeclarationSyntax>().ToArray();
            filteredMethods.Should().HaveCount(totalMethods.Length - 1);
            filteredMethods.Select(m => m.Identifier.Text).Should().NotContain("HttpPatchAction");

            var queryContext = new SelectorQueryContext(metadata: new Dictionary<string, object?>
            {
                ["Role"] = "role-admin"
            });

            var dashMatches = root.QuerySelectorAll(":scope[Context.Role|='role']", compilation, queryContext).ToArray();
            dashMatches.Should().ContainSingle().Which.Should().Be(root);

            var negativeDash = root.QuerySelectorAll(":scope[Context.Role!|='role']", compilation, queryContext).ToArray();
            negativeDash.Should().BeEmpty();
        }

        [Fact]
        public void PseudoClasses_AdvancedSelectors()
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(@"
namespace Sample
{
    class Parent
    {
        void First() { }
        void Second() { }
        void Third() { }
    }

    class Single
    {
        void Only() { }
    }

    class EmptyHolder
    {
    }
}
");

            var compilation = CSharpCompilation.Create("Test")
                .AddSyntaxTrees(syntaxTree);

            var root = syntaxTree.GetCompilationUnitRoot();

            var rootMatch = syntaxTree.QuerySelectorAll(":root", compilation).ToArray();
            rootMatch.Should().ContainSingle().Which.Should().Be(root);

            var scopeAtRoot = root.QuerySelectorAll(":scope", compilation).ToArray();
            scopeAtRoot.Should().ContainSingle().Which.Should().Be(root);

            var firstClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
            var scopeAtClass = firstClass.QuerySelectorAll(":scope", compilation).ToArray();
            scopeAtClass.Should().ContainSingle().Which.Should().Be(firstClass);

            var onlyChildMethods = syntaxTree.QuerySelectorAll(":method:only-child", compilation).OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.Text).ToArray();
            onlyChildMethods.Should().Contain("Only").And.NotContain(new[] { "First", "Second", "Third" });

            var onlyOfTypeMethods = syntaxTree.QuerySelectorAll(":method:only-of-type", compilation).OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.Text).ToArray();
            onlyOfTypeMethods.Should().Contain("Only").And.NotContain(new[] { "First", "Second", "Third" });

            var emptyClasses = syntaxTree.QuerySelectorAll(":class:empty", compilation).OfType<ClassDeclarationSyntax>().Select(c => c.Identifier.Text).ToArray();
            emptyClasses.Should().Contain("EmptyHolder");

            var nthLast = syntaxTree.QuerySelectorAll(":method:nth-last-child(1)", compilation).OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.Text).ToArray();
            nthLast.Should().Contain("Third").And.NotContain("First");

            var nthOfType = syntaxTree.QuerySelectorAll(":method:nth-of-type(2)", compilation).OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.Text).ToArray();
            nthOfType.Should().ContainSingle().Which.Should().Be("Second");

            var nthLastOfType = syntaxTree.QuerySelectorAll(":method:nth-last-of-type(1)", compilation).OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.Text).ToArray();
            nthLastOfType.Should().Contain("Third");

            var emptyBlocks = syntaxTree.QuerySelectorAll("Block:empty", compilation).OfType<BlockSyntax>().ToArray();
            emptyBlocks.Should().HaveCount(4);
        }

        [Fact]
        public void SymbolProperties_Accessible()
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(@"
namespace Sample
{
    class Program
    {
        void Run() { }
    }
}
");

            var compilation = CSharpCompilation.Create("Test")
                .AddSyntaxTrees(syntaxTree);

            var root = syntaxTree.GetCompilationUnitRoot();

            var symbolMatches = root.QuerySelectorAll(":class[Symbol.Name='Program']", compilation).OfType<ClassDeclarationSyntax>().ToArray();
            symbolMatches.Should().ContainSingle();
            symbolMatches.Single().Identifier.Text.Should().Be("Program");
        }

        [Fact]
        public void ContextMetadata_AccessibleInSelectors()
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(@"class Program { }");
            var compilation = CSharpCompilation.Create("Test").AddSyntaxTrees(syntaxTree);
            var root = syntaxTree.GetCompilationUnitRoot();

            var metadata = new Dictionary<string, object?>
            {
                ["Project"] = new Dictionary<string, object?>
                {
                    ["Name"] = "Sample"
                }
            };

            var context = new SelectorQueryContext(metadata: metadata);

            var matches = root.QuerySelectorAll(":scope[Context.Project.Name='Sample']", compilation, context).ToArray();
            matches.Should().ContainSingle().Which.Should().Be(root);

            var negative = root.QuerySelectorAll(":scope[Context.Project.Name!='Sample']", compilation, context).ToArray();
            negative.Should().BeEmpty();
        }

        [Fact]
        public void MetadataAlias_AccessibleViaShortcut()
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(@"class Program { }");
            var compilation = CSharpCompilation.Create("Test").AddSyntaxTrees(syntaxTree);
            var root = syntaxTree.GetCompilationUnitRoot();

            var metadata = new Dictionary<string, object?>
            {
                ["flow"] = new Dictionary<string, object?>
                {
                    ["Target"] = "UserService"
                }
            };

            var context = new SelectorQueryContext(metadata: metadata);

            var matches = root.QuerySelectorAll(":scope[@flow.Target='UserService']", compilation, context).ToArray();
            matches.Should().ContainSingle().Which.Should().Be(root);

            var negative = root.QuerySelectorAll(":scope[@flow.Target='Repository']", compilation, context).ToArray();
            negative.Should().BeEmpty();
        }

        [Fact]
        public void TypeProperty_Resolves_FromSemanticModel()
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(@"
namespace Sample
{
    interface IUserService
    {
        string GetUser(string id);
    }

    class Consumer
    {
        private readonly IUserService _service;

        public Consumer(IUserService service)
        {
            _service = service;
        }

        public void Run()
        {
            _service.GetUser(""id"");
        }
    }
}
");

            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
            };

            var compilation = CSharpCompilation.Create("Test", new[] { syntaxTree }, references);
            var root = syntaxTree.GetCompilationUnitRoot();

            var identifierMatches = root
                .QuerySelectorAll("InvocationExpression > SimpleMemberAccessExpression > IdentifierName[Type.Name='IUserService']", compilation)
                .OfType<IdentifierNameSyntax>()
                .ToArray();

            identifierMatches.Should().ContainSingle();
            identifierMatches.Single().Identifier.Text.Should().Be("_service");

            var invocationMatches = root
                .QuerySelectorAll("InvocationExpression[Symbol.Name='GetUser'][Symbol.ContainingType.Name='IUserService']", compilation)
                .ToArray();

            invocationMatches.Should().ContainSingle();
        }

        [Fact]
        public void CapturePseudoClass_PopulatesMatchCaptures()
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(@"class Demo { void Run() { } }");
            var compilation = CSharpCompilation.Create("Test").AddSyntaxTrees(syntaxTree);
            var root = syntaxTree.GetCompilationUnitRoot();

            var matches = root.QueryMatches(":class:capture(id, Symbol.Name)", compilation).ToArray();

            matches.Should().ContainSingle();
            matches[0].Captures.Should().NotBeNull();
            matches[0].Captures!.Should().ContainKey("id");
            matches[0].Captures!["id"].Should().Be("Demo");
        }

        [Fact]
        public void CapturePseudoClass_AllowsSelectorMetadataReuse()
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(@"class Demo { } class Other { }");
            var compilation = CSharpCompilation.Create("Test").AddSyntaxTrees(syntaxTree);
            var root = syntaxTree.GetCompilationUnitRoot();

            var matches = root.QuerySelectorAll(":class:capture(name, Symbol.Name)[@name='Demo']", compilation).ToArray();

            matches.Should().ContainSingle();
            matches[0].Should().BeOfType<ClassDeclarationSyntax>();
            ((ClassDeclarationSyntax)matches[0]).Identifier.Text.Should().Be("Demo");
        }

    }
}
