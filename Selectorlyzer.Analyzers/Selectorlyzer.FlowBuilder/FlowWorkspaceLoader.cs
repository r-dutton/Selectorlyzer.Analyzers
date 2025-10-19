using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Selectorlyzer.FlowBuilder;

public static class FlowWorkspaceLoader
{
    public static FlowWorkspaceDefinition Load(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("Workspace root must be provided.", nameof(workspaceRoot));
        }

        var root = PathNormalizer.Normalize(workspaceRoot);
        var solutions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var services = new Dictionary<string, FlowServiceDefinition>(StringComparer.OrdinalIgnoreCase);
        var bindings = new List<FlowServiceBinding>();

        LoadWorkspaceFile(root, solutions, services);
        LoadMapFile(root, services, bindings);

        if (solutions.Count == 0)
        {
            foreach (var solution in Directory.EnumerateFiles(root, "*.sln", SearchOption.AllDirectories))
            {
                if (solution.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    solution.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    solution.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                solutions.Add(PathNormalizer.Normalize(solution));
            }
        }

        return new FlowWorkspaceDefinition(root, solutions, services.Values, bindings);
    }

    private static void LoadWorkspaceFile(
        string root,
        HashSet<string> solutions,
        Dictionary<string, FlowServiceDefinition> services)
    {
        var workspacePath = Path.Combine(root, "flow.workspace.json");
        if (!File.Exists(workspacePath))
        {
            return;
        }

        using var stream = File.OpenRead(workspacePath);
        using var document = JsonDocument.Parse(stream);
        var rootElement = document.RootElement;

        if (rootElement.TryGetProperty("solutions", out var solutionsElement))
        {
            foreach (var solutionElement in solutionsElement.EnumerateArray())
            {
                var text = solutionElement.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var resolved = ResolveRelativePath(root, text!);
                solutions.Add(resolved);
            }
        }

        if (rootElement.TryGetProperty("services", out var servicesElement))
        {
            foreach (var serviceProperty in servicesElement.EnumerateObject())
            {
                var name = serviceProperty.Name;
                var serviceObject = serviceProperty.Value;

                string? solution = null;
                if (serviceObject.TryGetProperty("solution", out var solutionElement) &&
                    solutionElement.ValueKind == JsonValueKind.String)
                {
                    var solutionValue = solutionElement.GetString();
                    if (!string.IsNullOrWhiteSpace(solutionValue))
                    {
                        solution = ResolveRelativePath(root, solutionValue!);
                        solutions.Add(solution);
                    }
                }

                var assemblies = ExtractStringArray(serviceObject, "assembly_names");
                var baseAddresses = ExtractStringDictionary(serviceObject, "base_addresses");

                if (services.TryGetValue(name, out var existing))
                {
                    services[name] = existing.With(solution, assemblies, baseAddresses);
                }
                else
                {
                    services[name] = new FlowServiceDefinition(name, solution, assemblies, baseAddresses);
                }
            }
        }
    }

    private static void LoadMapFile(
        string root,
        Dictionary<string, FlowServiceDefinition> services,
        List<FlowServiceBinding> bindings)
    {
        var mapPath = Path.Combine(root, "flow.map.json");
        if (!File.Exists(mapPath))
        {
            return;
        }

        using var stream = File.OpenRead(mapPath);
        using var document = JsonDocument.Parse(stream);
        var rootElement = document.RootElement;

        if (rootElement.TryGetProperty("services", out var servicesElement))
        {
            foreach (var serviceProperty in servicesElement.EnumerateObject())
            {
                var name = serviceProperty.Name;
                var serviceObject = serviceProperty.Value;

                var baseUrls = ExtractStringDictionary(serviceObject, "base_urls");
                var assemblies = ExtractStringArray(serviceObject, "assembly_names");
                string? solution = null;
                if (serviceObject.TryGetProperty("solution", out var solutionElement) &&
                    solutionElement.ValueKind == JsonValueKind.String)
                {
                    var solutionValue = solutionElement.GetString();
                    if (!string.IsNullOrWhiteSpace(solutionValue))
                    {
                        solution = ResolveRelativePath(root, solutionValue!);
                    }
                }

                if (services.TryGetValue(name, out var existing))
                {
                    services[name] = existing.With(solution, assemblies, baseUrls);
                }
                else
                {
                    services[name] = new FlowServiceDefinition(name, solution, assemblies, baseUrls);
                }
            }
        }

        if (rootElement.TryGetProperty("bindings", out var bindingsElement))
        {
            foreach (var bindingElement in bindingsElement.EnumerateArray())
            {
                var caller = bindingElement.TryGetProperty("caller", out var callerElement) && callerElement.ValueKind == JsonValueKind.String
                    ? callerElement.GetString()
                    : string.Empty;
                var client = bindingElement.TryGetProperty("client", out var clientElement) && clientElement.ValueKind == JsonValueKind.String
                    ? clientElement.GetString()
                    : null;
                var target = bindingElement.TryGetProperty("target_service", out var targetElement) && targetElement.ValueKind == JsonValueKind.String
                    ? targetElement.GetString()
                    : null;

                if (!string.IsNullOrWhiteSpace(client) && !string.IsNullOrWhiteSpace(target))
                {
                    bindings.Add(new FlowServiceBinding(caller ?? string.Empty, client!, target!));
                }
            }
        }
    }

    private static IEnumerable<string> ExtractStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var arrayElement) || arrayElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return arrayElement
            .EnumerateArray()
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToArray();
    }

    private static IDictionary<string, string> ExtractStringDictionary(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var dictElement) || dictElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>();
        }

        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in dictElement.EnumerateObject())
        {
            var key = property.Name;
            var value = property.Value.GetString();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            dictionary[key.Trim()] = value.Trim();
        }

        return dictionary;
    }

    private static string ResolveRelativePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return root;
        }

        return PathNormalizer.Normalize(Path.IsPathRooted(relative)
            ? relative
            : Path.Combine(root, relative));
    }
}
