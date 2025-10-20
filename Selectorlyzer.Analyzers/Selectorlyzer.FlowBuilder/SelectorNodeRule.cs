using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Selectorlyzer.Qulaly;
using Selectorlyzer.Qulaly.Matcher;

namespace Selectorlyzer.FlowBuilder
{
    internal sealed record SelectorNodeRule(
        string Type,
        QulalySelector Selector,
        IReadOnlyList<string> Tags,
        bool UseSymbolIdentity,
        Func<SelectorMatch, IReadOnlyDictionary<string, object?>?>? PropertyExtractor)
    {
        public static SelectorNodeRule Create(
            string type,
            string selector,
            IEnumerable<string>? tags = null,
            bool useSymbolIdentity = true,
            Func<SelectorMatch, IReadOnlyDictionary<string, object?>?>? propertyExtractor = null)
        {
            return new SelectorNodeRule(
                type,
                QulalySelector.Parse(selector),
                tags?.ToImmutableArray() ?? ImmutableArray<string>.Empty,
                useSymbolIdentity,
                propertyExtractor);
        }
    }

    internal static class DefaultSelectorNodeRules
    {
        public static IReadOnlyList<SelectorNodeRule> Rules { get; } = new List<SelectorNodeRule>
        {
            SelectorNodeRule.Create(
                "endpoint.controller",
                ":class[Symbol.BaseType.Name='ControllerBase']",
                tags: new[] { "controller" },
                propertyExtractor: SelectorNodePropertyExtractors.ExtractControllerProperties),
            SelectorNodeRule.Create(
                "endpoint.controller_action",
                ":method[Symbol.ContainingType.BaseType.Name='ControllerBase']",
                tags: new[] { "action" },
                propertyExtractor: SelectorNodePropertyExtractors.ExtractControllerActionProperties),
            SelectorNodeRule.Create(
                "endpoint.minimal",
                "InvocationExpression[Symbol.Name^='Map'][Symbol.ContainingType.Name='WebApplication']",
                tags: new[] { "endpoint" }),
            SelectorNodeRule.Create("app.service", ":class[Identifier.ValueText$='Service']", tags: new[] { "service" }),
            SelectorNodeRule.Create("data.repository", ":class[Identifier.ValueText$='Repository']", tags: new[] { "repository" }),
            SelectorNodeRule.Create("contract.dto", "RecordDeclaration[Identifier.ValueText$='Dto']", tags: new[] { "dto" }),
            SelectorNodeRule.Create("data.entity", ":class:has(Attribute > IdentifierName[Identifier.ValueText='Table'])", tags: new[] { "entity" }),
            SelectorNodeRule.Create("data.dbcontext", ":class:has(PropertyDeclaration[Type*='DbSet'])", tags: new[] { "dbcontext" }),
            SelectorNodeRule.Create("app.validator", ":class:has(BaseList > SimpleBaseType > GenericName[Identifier.ValueText='AbstractValidator'])", tags: new[] { "validator" }),
            SelectorNodeRule.Create("cqrs.handler", ":class[Symbol.Interfaces*='IRequestHandler']", tags: new[] { "handler" }),
            SelectorNodeRule.Create("cqrs.notification_handler", ":class[Symbol.Interfaces*='INotificationHandler']", tags: new[] { "notification", "handler" }),
            SelectorNodeRule.Create("cqrs.pipeline_behavior", ":class[Symbol.Interfaces*='IPipelineBehavior']", tags: new[] { "pipeline" }),
            SelectorNodeRule.Create("cqrs.request_processor", ":class[Symbol.Interfaces*='IRequestProcessor']", tags: new[] { "processor" }),
            SelectorNodeRule.Create("messaging.publisher", ":class:has(InvocationExpression[Symbol.Name='Publish'])", tags: new[] { "publisher" }),
            SelectorNodeRule.Create("cqrs.request", ":class[Symbol.Interfaces*='IRequest']", tags: new[] { "message" }),
            SelectorNodeRule.Create("app.background_service", ":class[Symbol.BaseType.Name='BackgroundService']", tags: new[] { "background" }),
            SelectorNodeRule.Create("config.options", ":class[Identifier.ValueText$='Settings']", tags: new[] { "options" }),
            SelectorNodeRule.Create("infra.cache", ":class:has(FieldDeclaration[Declaration.Type.Identifier.Text='IMemoryCache'])", tags: new[] { "cache" }),
            SelectorNodeRule.Create("infra.http_client", ":class[Identifier.ValueText$='Client']", tags: new[] { "http" }),
            SelectorNodeRule.Create(
                "infra.http_call",
                "InvocationExpression[Symbol.ContainingType.Name~='HttpClient']",
                tags: new[] { "http", "call" },
                useSymbolIdentity: false,
                propertyExtractor: SelectorNodePropertyExtractors.ExtractHttpCallProperties),
            SelectorNodeRule.Create("utility.guard", "InvocationExpression[Symbol.ContainingType.Name='Guard']", tags: new[] { "guard" }),
            SelectorNodeRule.Create("mapping.mapper", ":class[Symbol.Interfaces*='IMapper']", tags: new[] { "mapping" }),
            SelectorNodeRule.Create("mapping.operation", "InvocationExpression[Symbol.ContainingType.Name='IMapper'][Symbol.Name='Map']", tags: new[] { "mapping" }),
            SelectorNodeRule.Create("security.authorization", ":class[Symbol.GetAttributes().AttributeClass.Name~='AuthorizeAttribute']", tags: new[] { "authorization" }),
        };
    }

    internal static class SelectorNodePropertyExtractors
    {
        private static readonly Dictionary<string, string> HttpAttributeVerbs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["HttpGetAttribute"] = "GET",
            ["HttpPostAttribute"] = "POST",
            ["HttpPutAttribute"] = "PUT",
            ["HttpDeleteAttribute"] = "DELETE",
            ["HttpPatchAttribute"] = "PATCH",
            ["HttpHeadAttribute"] = "HEAD"
        };

        public static IReadOnlyDictionary<string, object?>? ExtractControllerProperties(SelectorMatch match)
        {
            return match.Symbol is INamedTypeSymbol typeSymbol
                ? ExtractControllerProperties(typeSymbol)
                : null;
        }

        public static IReadOnlyDictionary<string, object?>? ExtractControllerProperties(INamedTypeSymbol typeSymbol)
        {
            if (typeSymbol is null)
            {
                return null;
            }

            var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["controller_name"] = typeSymbol.Name,
                ["controller_id"] = typeSymbol.GetDocumentationCommentId() ?? string.Empty,
                ["controller_type"] = typeSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            };

            if (TryGetRouteTemplate(typeSymbol.GetAttributes(), out var route))
            {
                dictionary["route_prefix"] = CanonicalizeRoute(route, typeSymbol.Name);
            }

            if (TryGetAuthorizationPolicy(typeSymbol.GetAttributes(), out var policy))
            {
                dictionary["authorization_policy"] = policy;
            }

            return dictionary;
        }

        public static IReadOnlyDictionary<string, object?>? ExtractControllerActionProperties(SelectorMatch match)
        {
            return match.Symbol is IMethodSymbol methodSymbol
                ? ExtractControllerActionProperties(methodSymbol)
                : null;
        }

        public static IReadOnlyDictionary<string, object?>? ExtractControllerActionProperties(IMethodSymbol methodSymbol)
        {
            if (methodSymbol is null)
            {
                return null;
            }

            var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["action_name"] = methodSymbol.Name,
                ["action_id"] = methodSymbol.GetDocumentationCommentId() ?? string.Empty,
                ["controller_type"] = methodSymbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? string.Empty,
                ["controller_id"] = methodSymbol.ContainingType?.GetDocumentationCommentId() ?? string.Empty
            };

            if (methodSymbol.ContainingType is { } containingType)
            {
                dictionary["controller_name"] = containingType.Name;
                if (TryGetRouteTemplate(containingType.GetAttributes(), out var controllerRoute))
                {
                    dictionary["controller_route"] = CanonicalizeRoute(controllerRoute, containingType.Name);
                }

                if (TryGetAuthorizationPolicy(containingType.GetAttributes(), out var authorizationPolicy))
                {
                    dictionary["authorization_policy"] = authorizationPolicy;
                }
            }

            if (TryGetAuthorizationPolicy(methodSymbol.GetAttributes(), out var actionPolicy))
            {
                dictionary["authorization_policy"] = actionPolicy;
            }

            string? methodRoute = null;
            string? httpMethod = null;

            foreach (var attribute in methodSymbol.GetAttributes())
            {
                if (attribute.AttributeClass is null)
                {
                    continue;
                }

                var name = attribute.AttributeClass.Name;
                if (HttpAttributeVerbs.TryGetValue(name, out var verb))
                {
                    httpMethod ??= verb;
                }
                else if (string.Equals(name, "AcceptVerbsAttribute", StringComparison.OrdinalIgnoreCase))
                {
                    if (attribute.ConstructorArguments.Length > 0)
                    {
                        var arg = attribute.ConstructorArguments[0];
                        if (arg.Kind == TypedConstantKind.Primitive && arg.Value is string stringVerb)
                        {
                            httpMethod ??= stringVerb.ToUpperInvariant();
                        }
                        else if (arg.Kind == TypedConstantKind.Array && arg.Values.Length > 0)
                        {
                            var arrayVerb = arg.Values[0].Value as string;
                            if (!string.IsNullOrWhiteSpace(arrayVerb))
                            {
                                httpMethod ??= arrayVerb.Trim().ToUpperInvariant();
                            }
                        }
                    }
                }

                if (methodRoute is null && TryGetRouteTemplate(attribute, out var route))
                {
                    methodRoute = route;
                }
            }

            if (!string.IsNullOrWhiteSpace(httpMethod))
            {
                dictionary["http_method"] = httpMethod;
            }

            if (methodRoute is null && TryGetRouteTemplate(methodSymbol.GetAttributes(), out var routeFromRouteAttribute))
            {
                methodRoute = routeFromRouteAttribute;
            }

            var combinedRoute = CombineRoutes(dictionary, methodRoute, methodSymbol.ContainingType?.Name);
            if (!string.IsNullOrWhiteSpace(methodRoute))
            {
                dictionary["route"] = CanonicalizeRoute(methodRoute, methodSymbol.ContainingType?.Name);
            }

            if (!string.IsNullOrWhiteSpace(combinedRoute))
            {
                dictionary["full_route"] = combinedRoute;
            }

            var statusCode = InferPrimaryStatusCode(methodSymbol);
            if (statusCode is not null)
            {
                dictionary["status_code"] = statusCode;
            }

            return dictionary;
        }

        public static IReadOnlyDictionary<string, object?>? ExtractHttpCallProperties(SelectorMatch match)
        {
            if (match.Node is not InvocationExpressionSyntax invocation)
            {
                return null;
            }

            var semanticModel = match.SemanticModel;
            var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (match.Symbol is IMethodSymbol methodSymbol)
            {
                var verb = MapMethodToVerb(methodSymbol, invocation, semanticModel);
                if (!string.IsNullOrWhiteSpace(verb))
                {
                    properties["verb"] = verb;
                }

                properties["method_symbol"] = methodSymbol.GetDocumentationCommentId() ?? string.Empty;
            }

            if (semanticModel?.GetEnclosingSymbol(invocation.SpanStart) is ISymbol enclosingSymbol)
            {
                properties["caller_id"] = enclosingSymbol.GetDocumentationCommentId() ?? string.Empty;
                properties["caller_name"] = enclosingSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

                if (enclosingSymbol.ContainingType is { } container)
                {
                    properties["caller_type"] = container.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                }
            }

            if (invocation.Expression is MemberAccessExpressionSyntax memberExpression)
            {
                if (semanticModel?.GetTypeInfo(memberExpression.Expression).Type is INamedTypeSymbol targetType)
                {
                    properties["client_type"] = targetType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                    properties["client_id"] = targetType.GetDocumentationCommentId() ?? string.Empty;
                }
            }

            var route = TryExtractRouteLiteral(invocation, semanticModel);
            if (!string.IsNullOrWhiteSpace(route))
            {
                properties["route"] = CanonicalizeRoute(route, null);
            }

            return properties.Count > 0 ? properties : null;
        }

        private static string? CombineRoutes(Dictionary<string, object?> dictionary, string? methodRoute, string? controllerName)
        {
            if (!dictionary.TryGetValue("controller_route", out var controllerRouteObj) && dictionary.TryGetValue("route_prefix", out var prefixObj))
            {
                controllerRouteObj = prefixObj;
            }

            var controllerRoute = controllerRouteObj as string;
            if (string.IsNullOrWhiteSpace(methodRoute))
            {
                return controllerRoute;
            }

            var methodCanonical = CanonicalizeRoute(methodRoute, controllerName);
            if (string.IsNullOrWhiteSpace(controllerRoute))
            {
                return methodCanonical;
            }

            if (methodCanonical?.StartsWith('/') == true)
            {
                return methodCanonical;
            }

            var controllerCanonical = CanonicalizeRoute(controllerRoute, controllerName) ?? string.Empty;
            if (!controllerCanonical.EndsWith('/'))
            {
                controllerCanonical += "/";
            }

            return CanonicalizeRoute(controllerCanonical + methodCanonical, controllerName);
        }

        private static bool TryGetRouteTemplate(IEnumerable<AttributeData> attributes, out string? template)
        {
            foreach (var attribute in attributes)
            {
                if (TryGetRouteTemplate(attribute, out template))
                {
                    return true;
                }
            }

            template = null;
            return false;
        }

        private static bool TryGetRouteTemplate(AttributeData attribute, out string? template)
        {
            template = null;
            var attributeName = attribute.AttributeClass?.Name;
            if (string.IsNullOrWhiteSpace(attributeName))
            {
                return false;
            }

            if (!attributeName.Contains("Route", StringComparison.OrdinalIgnoreCase) &&
                !attributeName.StartsWith("Http", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (attribute.ConstructorArguments.Length == 0)
            {
                return false;
            }

            var argument = attribute.ConstructorArguments[0];
            if (argument.Value is string stringValue && !string.IsNullOrWhiteSpace(stringValue))
            {
                template = stringValue;
                return true;
            }

            return false;
        }

        private static string? TryExtractRouteLiteral(InvocationExpressionSyntax invocation, SemanticModel? semanticModel)
        {
            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                if (semanticModel is not null)
                {
                    var constant = semanticModel.GetConstantValue(argument.Expression);
                    if (constant.HasValue && constant.Value is string text && !string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }

                if (argument.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax literal)
                {
                    var literalText = literal.Token.ValueText;
                    if (!string.IsNullOrWhiteSpace(literalText))
                    {
                        return literalText;
                    }
                }
            }

            return null;
        }

        private static string? MapMethodToVerb(IMethodSymbol methodSymbol, InvocationExpressionSyntax invocation, SemanticModel? semanticModel)
        {
            var name = methodSymbol.Name;
            if (name.StartsWith("Get", StringComparison.OrdinalIgnoreCase)) return "GET";
            if (name.StartsWith("Post", StringComparison.OrdinalIgnoreCase)) return "POST";
            if (name.StartsWith("Put", StringComparison.OrdinalIgnoreCase)) return "PUT";
            if (name.StartsWith("Delete", StringComparison.OrdinalIgnoreCase)) return "DELETE";
            if (name.StartsWith("Patch", StringComparison.OrdinalIgnoreCase)) return "PATCH";
            if (name.StartsWith("Head", StringComparison.OrdinalIgnoreCase)) return "HEAD";

            if (name.StartsWith("Send", StringComparison.OrdinalIgnoreCase) && invocation.ArgumentList.Arguments.Count > 0)
            {
                var first = invocation.ArgumentList.Arguments[0].Expression;
                if (semanticModel?.GetConstantValue(first) is { HasValue: true, Value: string text } && !string.IsNullOrWhiteSpace(text))
                {
                    return text.ToUpperInvariant();
                }
            }

            return null;
        }

        private static string? CanonicalizeRoute(string? route, string? controllerName)
        {
            if (string.IsNullOrWhiteSpace(route))
            {
                return null;
            }

            var normalized = route.Trim();
            if (controllerName is not null)
            {
                var simpleController = controllerName.EndsWith("Controller", StringComparison.Ordinal)
                    ? controllerName[..^10]
                    : controllerName;
                normalized = normalized
                    .Replace("[controller]", simpleController, StringComparison.OrdinalIgnoreCase)
                    .Replace("{controller}", simpleController, StringComparison.OrdinalIgnoreCase);
            }

            if (!normalized.StartsWith('/'))
            {
                normalized = "/" + normalized.TrimStart('/');
            }

            return normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        private static bool TryGetAuthorizationPolicy(IEnumerable<AttributeData> attributes, out string? policy)
        {
            foreach (var attribute in attributes)
            {
                if (TryGetAuthorizationPolicy(attribute, out policy))
                {
                    return true;
                }
            }

            policy = null;
            return false;
        }

        private static bool TryGetAuthorizationPolicy(AttributeData attribute, out string? policy)
        {
            policy = null;
            var attributeName = attribute.AttributeClass?.Name;
            if (string.IsNullOrWhiteSpace(attributeName))
            {
                return false;
            }

            if (!string.Equals(attributeName, "AuthorizeAttribute", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (var namedArgument in attribute.NamedArguments)
            {
                if (string.Equals(namedArgument.Key, "Policy", StringComparison.OrdinalIgnoreCase) && namedArgument.Value.Value is string stringPolicy)
                {
                    policy = stringPolicy;
                    return true;
                }
            }

            if (attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is string ctorPolicy && !string.IsNullOrWhiteSpace(ctorPolicy))
            {
                policy = ctorPolicy;
                return true;
            }

            return false;
        }

        private static string? InferPrimaryStatusCode(IMethodSymbol methodSymbol)
        {
            foreach (var attribute in methodSymbol.GetAttributes())
            {
                if (attribute.AttributeClass is null)
                {
                    continue;
                }

                var name = attribute.AttributeClass.Name;
                if (!name.StartsWith("ProducesResponseTypeAttribute", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is int status)
                {
                    return status.ToString();
                }
            }

            return null;
        }
    }
}
