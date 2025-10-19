using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Selectorlyzer.Qulaly;
using Selectorlyzer.Qulaly.Matcher;
using Selectorlyzer.TestUtilities;
using Xunit;

namespace Qulaly.Tests
{
    public class FlowBuilderCompatibilityTests
    {
        [Fact]
        public void Selectors_CanIdentify_ControllerPatterns()
        {
            var (compilation, root) = FlowBuilderSample.BuildSample();

            var controllers = root
                .QuerySelectorAll(":class[Symbol.BaseType.Name='ControllerBase']", compilation)
                .OfType<ClassDeclarationSyntax>()
                .Select(c => c.Identifier.Text)
                .ToArray();
            controllers.Should().ContainSingle().Which.Should().Be("UserController");

            var decorated = root
                .QuerySelectorAll(":class:has(Attribute > IdentifierName[Identifier.ValueText='HttpController'])", compilation)
                .OfType<ClassDeclarationSyntax>()
                .ToArray();
            decorated.Should().ContainSingle();

            var httpActions = root
                .QuerySelectorAll(":method:has(Attribute > IdentifierName[Identifier.ValueText='HttpGet'])", compilation)
                .OfType<MethodDeclarationSyntax>()
                .Select(m => m.Identifier.Text)
                .ToArray();
            httpActions.Should().ContainSingle().Which.Should().Be("GetUser");

            var mediatorCalls = root
                .QuerySelectorAll(":method:has(InvocationExpression[Symbol.Name='Send'][Symbol.ContainingType.Name='IMediator'])", compilation)
                .OfType<MethodDeclarationSyntax>()
                .Select(m => m.Identifier.Text)
                .ToArray();
            mediatorCalls.Should().Contain("GetUser");
        }

        [Fact]
        public void Selectors_CanIdentify_ServiceAndDataFlows()
        {
            var (compilation, root) = FlowBuilderSample.BuildSample();

            var serviceUsages = root
                .QuerySelectorAll("InvocationExpression > SimpleMemberAccessExpression > IdentifierName[Type.Name='IUserService']", compilation)
                .OfType<IdentifierNameSyntax>()
                .ToArray();
            serviceUsages.Should().NotBeEmpty();

            var repositoryWrites = root
                .QuerySelectorAll("InvocationExpression[Symbol.Name='Add'][Symbol.ContainingType.Name='IUserRepository']", compilation)
                .ToArray();
            repositoryWrites.Should().ContainSingle();

            var httpClientCalls = root
                .QuerySelectorAll("InvocationExpression[Symbol.ContainingType.Name='HttpClient'][Symbol.Name='Get']", compilation)
                .ToArray();
            httpClientCalls.Should().ContainSingle();

            var loggerCalls = root
                .QuerySelectorAll("InvocationExpression[Symbol.ContainingType*='Sample.ILogger<Sample.UserController>'][Symbol.Name='Log']", compilation)
                .ToArray();
            loggerCalls.Should().ContainSingle();

            var optionsIdentifiers = root
                .QuerySelectorAll("IdentifierName[Identifier.ValueText='_settings'][Type*='Sample.IOptions<Sample.UserSettings>']", compilation)
                .ToArray();
            optionsIdentifiers.Should().NotBeEmpty();
        }

        [Fact]
        public void Selectors_CanIdentify_DiRegistrations()
        {
            var (compilation, root) = FlowBuilderSample.BuildSample();

            var registrations = root
                .QuerySelectorAll("InvocationExpression[Symbol.Name='AddScoped'][Symbol.TypeArguments*='Sample.IUserService'][Symbol.TypeArguments*='Sample.UserService']", compilation)
                .ToArray();
            registrations.Should().ContainSingle();
        }

        [Fact]
        public void Selectors_CanIdentify_AllFlowBuilderAnalyzerPatterns()
        {
            var (compilation, roots, queryContext) = FlowBuilderSample.BuildComprehensiveSample();

            SyntaxNode[] Match(string selector)
            {
                return roots
                    .SelectMany(root => root.QuerySelectorAll(selector, compilation, queryContext))
                    .ToArray();
            }

            // Controllers & endpoints
            Match(":class[Identifier.ValueText='UserController']")
                .Should().ContainSingle();
            Match(":method:has(Attribute > IdentifierName[Identifier.ValueText='HttpGet'])")
                .Should().ContainSingle(m => ((MethodDeclarationSyntax)m).Identifier.Text == "GetUserAsync");
            Match(":method:has(InvocationExpression[Symbol.Name='SendAsync'][Symbol.ContainingType.Name='IMediator'])")
                .Should().NotBeEmpty();
            Match(":method:has(InvocationExpression[Symbol.ContainingType.Name='ServiceBusClient'])")
                .Should().ContainSingle();
            Match("InvocationExpression[Symbol.Name='MapGet'][Symbol.ContainingType.Name='WebApplication']")
                .Should().ContainSingle();

            // Services, repositories, and data
            Match(":class[Identifier.ValueText$='Service'][Symbol.Interfaces*='IUserService']")
                .Should().ContainSingle();
            Match(":class[Identifier.ValueText$='Repository'][Symbol.Interfaces*='IUserRepository']")
                .Should().ContainSingle();
            Match(":class:has(PropertyDeclaration[Type*='DbSet'])")
                .Should().ContainSingle();
            Match(":class:has(Attribute[Name.Identifier='Table'])")
                .Should().ContainSingle(c => ((ClassDeclarationSyntax)c).Identifier.Text == "UserEntity");
            Match("RecordDeclaration[Identifier.ValueText$='Dto']")
                .Should().ContainSingle();

            // Messaging, notifications, CQRS, and pipelines
            Match(":class[Symbol.Interfaces*='IRequestHandler'][Symbol.Name='GetUserQueryHandler']")
                .Should().ContainSingle();
            Match(":class:has(BaseList > SimpleBaseType > GenericName[Identifier.ValueText='INotificationHandler'])")
                .Should().ContainSingle();
            Match(":class[Symbol.BaseType.Name='BackgroundService']")
                .Should().ContainSingle();
            Match(":class[Symbol.Interfaces*='IPipelineBehavior']")
                .Should().ContainSingle();
            Match(":class:has(ObjectCreationExpression[Type*='ServiceBusMessage']:has(SimpleAssignmentExpression > IdentifierName[Identifier.ValueText='Subject']):has(StringLiteralExpression[Token.ValueText='user.created']))")
                .Should().ContainSingle();

            // Validation and authorization
            Match(":class:has(BaseList > SimpleBaseType > GenericName[Identifier.ValueText='AbstractValidator'])")
                .Should().ContainSingle(c => ((ClassDeclarationSyntax)c).Identifier.Text == "UserDtoValidator");
            Match(":class[Symbol.GetAttributes().AttributeClass.Name~='AuthorizeAttribute']")
                .Should().ContainSingle(c => ((ClassDeclarationSyntax)c).Identifier.Text == "UserController");

            // Configuration, options, caching, mapping, logging, and HTTP
            Match(":class:has(FieldDeclaration[Declaration.Type*='IOptions<UserSettings>'])")
                .Should().ContainSingle(c => ((ClassDeclarationSyntax)c).Identifier.Text == "UserController");
            Match(":class:has(FieldDeclaration[Declaration.Type.Identifier.Text='IMemoryCache'])")
                .Should().ContainSingle(c => ((ClassDeclarationSyntax)c).Identifier.Text == "UserService");
            Match(":method:has(InvocationExpression[Symbol.ContainingType.Name='IMemoryCache'][Symbol.Name='TryGetValue'])")
                .OfType<MethodDeclarationSyntax>()
                .Select(m => m.Identifier.Text)
                .Should().Contain("GetUserAsync");
            Match(":method:has(InvocationExpression[Symbol.ContainingType.Name='IMapper'][Symbol.Name='Map'])")
                .Should().ContainSingle();
            Match(":method:has(InvocationExpression[Symbol.ContainingType.Name='ILogger'][Symbol.Name='LogInformation'])")
                .Should().NotBeEmpty();
            Match(":method:has(InvocationExpression[Symbol.ContainingType.Name='HttpClient'])")
                .Should().NotBeEmpty();
            Match(":method:has(SimpleMemberAccessExpression[Expression.Identifier.Text='_settings'][Name.Identifier.Text='Value'])")
                .Should().ContainSingle();
            Match(":method:has(InvocationExpression[Symbol.Name='Null'][Symbol.ContainingType.Name='Guard'])")
                .OfType<MethodDeclarationSyntax>()
                .Select(m => m.Identifier.Text)
                .Should().Contain("GetUserAsync");

            // Dependency injection registrations and metadata
            Match("InvocationExpression[Symbol.Name='AddScoped'][Symbol.TypeArguments*='Sample.Services.IUserService'][Symbol.TypeArguments*='Sample.Services.UserService']")
                .Should().ContainSingle();
            Match("InvocationExpression[Symbol.Name='AddHttpClient'][Symbol.ContainingType.Name='IServiceCollection']")
                .Should().ContainSingle();
            Match("InvocationExpression:has(GenericName[Identifier.ValueText='AddHostedService'] > TypeArgumentList > IdentifierName[Identifier.ValueText='UserWorker'])")
                .Should().NotBeEmpty();
            Match("InvocationExpression[Symbol.Name='AddSingleton'][Symbol.TypeArguments*='Sample.Data.IUserRepository']")
                .Should().ContainSingle();

            // Query context metadata (configuration)
            Match(":class[Context.Configuration.UserSettings.Endpoint='https://api.sample.local']")
                .Should().NotBeEmpty();
        }

    }
}
