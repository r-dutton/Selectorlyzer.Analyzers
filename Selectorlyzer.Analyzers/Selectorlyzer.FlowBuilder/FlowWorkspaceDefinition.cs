using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace Selectorlyzer.FlowBuilder;

public sealed class FlowWorkspaceDefinition
{
    public FlowWorkspaceDefinition(
        string rootPath,
        IEnumerable<string>? solutionPaths = null,
        IEnumerable<FlowServiceDefinition>? services = null,
        IEnumerable<FlowServiceBinding>? bindings = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Root path must be provided.", nameof(rootPath));
        }

        RootPath = rootPath;
        SolutionPaths = solutionPaths?.Select(PathNormalizer.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray()
            ?? ImmutableArray<string>.Empty;
        Services = services?.ToImmutableDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase)
            ?? ImmutableDictionary<string, FlowServiceDefinition>.Empty;
        Bindings = bindings?.ToImmutableArray() ?? ImmutableArray<FlowServiceBinding>.Empty;
    }

    public string RootPath { get; }

    public ImmutableArray<string> SolutionPaths { get; }

    public ImmutableDictionary<string, FlowServiceDefinition> Services { get; }

    public ImmutableArray<FlowServiceBinding> Bindings { get; }
}

public sealed class FlowServiceDefinition
{
    public FlowServiceDefinition(
        string name,
        string? solution,
        IEnumerable<string>? assemblyNames = null,
        IDictionary<string, string>? baseAddresses = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Service name must be provided.", nameof(name));
        }

        Name = name;
        Solution = string.IsNullOrWhiteSpace(solution) ? null : PathNormalizer.Normalize(solution!);
        AssemblyNames = assemblyNames?
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray() ?? ImmutableArray<string>.Empty;
        BaseAddresses = baseAddresses is null
            ? ImmutableDictionary<string, string>.Empty
            : baseAddresses
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                .ToImmutableDictionary(
                    kv => kv.Key.Trim(),
                    kv => kv.Value.Trim(),
                    StringComparer.OrdinalIgnoreCase);
    }

    public string Name { get; }

    public string? Solution { get; }

    public ImmutableArray<string> AssemblyNames { get; }

    public ImmutableDictionary<string, string> BaseAddresses { get; }

    public FlowServiceDefinition With(
        string? solution = null,
        IEnumerable<string>? assemblyNames = null,
        IDictionary<string, string>? baseAddresses = null)
    {
        var mergedSolution = solution ?? Solution;
        var mergedAssemblies = AssemblyNames;
        if (assemblyNames is not null)
        {
            mergedAssemblies = AssemblyNames
                .Concat(assemblyNames.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
        }

        var mergedBaseAddresses = BaseAddresses;
        if (baseAddresses is not null)
        {
            var builder = BaseAddresses.ToBuilder();
            foreach (var kv in baseAddresses)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
                {
                    continue;
                }

                builder[kv.Key.Trim()] = kv.Value.Trim();
            }

            mergedBaseAddresses = builder.ToImmutable();
        }

        return new FlowServiceDefinition(Name, mergedSolution, mergedAssemblies, mergedBaseAddresses);
    }
}

public sealed class FlowServiceBinding
{
    public FlowServiceBinding(string caller, string client, string targetService)
    {
        Caller = caller?.Trim() ?? string.Empty;
        Client = client?.Trim() ?? throw new ArgumentNullException(nameof(client));
        TargetService = targetService?.Trim() ?? throw new ArgumentNullException(nameof(targetService));
    }

    public string Caller { get; }

    public string Client { get; }

    public string TargetService { get; }
}

internal static class PathNormalizer
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return System.IO.Path.GetFullPath(path);
    }
}
