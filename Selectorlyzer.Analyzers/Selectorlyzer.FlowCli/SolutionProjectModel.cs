using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Selectorlyzer.FlowCli;

internal sealed record SolutionProjectModel(
    string SolutionPath,
    IReadOnlyList<ProjectModel> Projects,
    IReadOnlyList<ProjectModel> SortedProjects,
    IReadOnlyDictionary<string, ProjectModel> ProjectsByPath,
    string SolutionDirectory)
{
    public ProjectModel? TryGetProject(string projectPath)
        => ProjectsByPath.TryGetValue(NormalizePath(projectPath), out var model) ? model : null;

    private static string NormalizePath(string path)
        => SolutionProjectLoader.NormalizePath(path);
}

internal sealed record ProjectModel(
    string ProjectPath,
    string ProjectName,
    string AssemblyName,
    string ProjectDirectory,
    IReadOnlyList<string> SourceFiles,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PreprocessorSymbols,
    LanguageVersion LanguageVersion,
    NullableContextOptions NullableContext,
    bool AllowUnsafe)
{
    public string NormalizedPath { get; } = SolutionProjectLoader.NormalizePath(ProjectPath);
}

internal static class SolutionProjectLoader
{
    private static readonly string[] DirectoryExclusions =
    {
        "bin",
        "obj",
        ".git",
        "node_modules",
        "packages",
        "TestResults",
        "artifacts"
    };

    public static SolutionProjectModel Load(string solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            throw new ArgumentException("Solution path must be provided.", nameof(solutionPath));
        }

        var normalizedSolution = NormalizePath(solutionPath);
        if (!File.Exists(normalizedSolution))
        {
            throw new FileNotFoundException($"Solution '{solutionPath}' could not be found.", normalizedSolution);
        }

        var solutionDirectory = Path.GetDirectoryName(normalizedSolution) ?? Directory.GetCurrentDirectory();
        var projectPaths = ParseSolution(normalizedSolution);
        var projects = new List<ProjectModel>();
        var projectMap = new Dictionary<string, ProjectModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var projectPath in projectPaths)
        {
            try
            {
                if (!File.Exists(projectPath))
                {
                    continue;
                }

                var model = LoadProject(projectPath);
                projects.Add(model);
                projectMap[model.NormalizedPath] = model;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[flow] Skipping project {projectPath}: {ex.Message}");
            }
        }

        var sorted = TopologicallySort(projects, projectMap);
        return new SolutionProjectModel(normalizedSolution, projects, sorted, projectMap, solutionDirectory);
    }

    private static IReadOnlyList<string> ParseSolution(string solutionPath)
    {
        var directory = Path.GetDirectoryName(solutionPath) ?? Directory.GetCurrentDirectory();
        var projects = new List<string>();

        foreach (var line in File.ReadLines(solutionPath))
        {
            if (!line.StartsWith("Project(\"", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split('"');
            if (parts.Length < 6)
            {
                continue;
            }

            var relativePath = parts[5].Replace('\\', Path.DirectorySeparatorChar);
            if (!relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fullPath = NormalizePath(Path.Combine(directory, relativePath));
            projects.Add(fullPath);
        }

        return projects;
    }

    private static ProjectModel LoadProject(string projectPath)
    {
        var normalizedPath = NormalizePath(projectPath);
        var projectDirectory = Path.GetDirectoryName(normalizedPath) ?? Directory.GetCurrentDirectory();
        var projectName = Path.GetFileNameWithoutExtension(normalizedPath);

        var document = XDocument.Load(normalizedPath);
        var ns = document.Root?.Name.Namespace ?? XNamespace.None;

        string assemblyName = ExtractProperty(document, ns, "AssemblyName") ?? projectName;
        var nullableValue = ExtractProperty(document, ns, "Nullable");
        var allowUnsafe = ParseBoolean(ExtractProperty(document, ns, "AllowUnsafeBlocks"));
        var languageVersionValue = ExtractProperty(document, ns, "LangVersion");
        var defineConstants = ExtractDefineConstants(document, ns);
        var projectReferences = ExtractProjectReferences(document, ns, projectDirectory);

        var languageVersion = ParseLanguageVersion(languageVersionValue);
        var nullable = ParseNullable(nullableValue);

        var sourceFiles = CollectSourceFiles(projectDirectory);

        return new ProjectModel(
            normalizedPath,
            projectName,
            assemblyName,
            projectDirectory,
            sourceFiles,
            projectReferences,
            defineConstants,
            languageVersion,
            nullable,
            allowUnsafe);
    }

    private static IReadOnlyList<string> ExtractProjectReferences(XDocument document, XNamespace ns, string projectDirectory)
    {
        var references = new List<string>();
        foreach (var reference in document.Descendants(ns + "ProjectReference"))
        {
            var include = reference.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            var resolved = NormalizePath(Path.Combine(projectDirectory, include.Replace('\\', Path.DirectorySeparatorChar)));
            references.Add(resolved);
        }

        return references;
    }

    private static IReadOnlyList<string> ExtractDefineConstants(XDocument document, XNamespace ns)
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var propertyGroup in document.Descendants(ns + "PropertyGroup"))
        {
            var element = propertyGroup.Element(ns + "DefineConstants");
            if (element is null)
            {
                continue;
            }

            foreach (var symbol in SplitSymbols(element.Value))
            {
                if (!string.IsNullOrWhiteSpace(symbol))
                {
                    symbols.Add(symbol);
                }
            }
        }

        symbols.Add("TRACE");
        symbols.Add("DEBUG");

        return symbols.ToImmutableArray();
    }

    private static LanguageVersion ParseLanguageVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return LanguageVersion.Preview;
        }

        value = value.Trim();
        if (value.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return LanguageVersion.Default;
        }

        if (value.Equals("latest", StringComparison.OrdinalIgnoreCase) || value.Equals("latestmajor", StringComparison.OrdinalIgnoreCase))
        {
            return LanguageVersion.Latest;
        }

        if (value.Equals("preview", StringComparison.OrdinalIgnoreCase))
        {
            return LanguageVersion.Preview;
        }

        if (LanguageVersionFacts.TryParse(value, out var parsed))
        {
            return parsed;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) && numeric >= 1)
        {
            var computed = MapNumericLanguageVersion(numeric);
            if (computed.HasValue)
            {
                return computed.Value;
            }
        }

        return LanguageVersion.Preview;
    }

    private static LanguageVersion? MapNumericLanguageVersion(int value)
        => value switch
        {
            1 => LanguageVersion.CSharp1,
            2 => LanguageVersion.CSharp2,
            3 => LanguageVersion.CSharp3,
            4 => LanguageVersion.CSharp4,
            5 => LanguageVersion.CSharp5,
            6 => LanguageVersion.CSharp6,
            7 => LanguageVersion.CSharp7,
            71 => LanguageVersion.CSharp7_1,
            72 => LanguageVersion.CSharp7_2,
            73 => LanguageVersion.CSharp7_3,
            8 => LanguageVersion.CSharp8,
            9 => LanguageVersion.CSharp9,
            10 => LanguageVersion.CSharp10,
            11 => LanguageVersion.CSharp11,
            12 => LanguageVersion.CSharp12,
            _ => null
        };

    private static NullableContextOptions ParseNullable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return NullableContextOptions.Annotations;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "enable" => NullableContextOptions.Enable,
            "warnings" => NullableContextOptions.Warnings,
            "annotations" => NullableContextOptions.Annotations,
            "disable" => NullableContextOptions.Disable,
            _ => NullableContextOptions.Annotations
        };
    }

    private static bool ParseBoolean(string? value)
        => value is not null && bool.TryParse(value, out var parsed) && parsed;

    private static string? ExtractProperty(XDocument document, XNamespace ns, string propertyName)
    {
        foreach (var propertyGroup in document.Descendants(ns + "PropertyGroup"))
        {
            var element = propertyGroup.Element(ns + propertyName);
            if (!string.IsNullOrWhiteSpace(element?.Value))
            {
                return element.Value.Trim();
            }
        }

        return null;
    }

    private static IReadOnlyList<ProjectModel> TopologicallySort(
        IReadOnlyList<ProjectModel> projects,
        IReadOnlyDictionary<string, ProjectModel> projectMap)
    {
        var result = new List<ProjectModel>(projects.Count);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(ProjectModel project)
        {
            if (visited.Contains(project.NormalizedPath))
            {
                return;
            }

            if (!visiting.Add(project.NormalizedPath))
            {
                return;
            }

            foreach (var reference in project.ProjectReferences)
            {
                if (projectMap.TryGetValue(reference, out var referencedProject))
                {
                    Visit(referencedProject);
                }
            }

            visiting.Remove(project.NormalizedPath);
            visited.Add(project.NormalizedPath);
            result.Add(project);
        }

        foreach (var project in projects)
        {
            Visit(project);
        }

        return result;
    }

    private static IReadOnlyList<string> CollectSourceFiles(string projectDirectory)
    {
        if (!Directory.Exists(projectDirectory))
        {
            return Array.Empty<string>();
        }

        var files = new List<string>();
        var stack = new Stack<string>();
        stack.Push(projectDirectory);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                var name = Path.GetFileName(directory);
                if (DirectoryExclusions.Any(exclusion => name.Equals(exclusion, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                stack.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(current, "*.cs", SearchOption.TopDirectoryOnly))
            {
                files.Add(NormalizePath(file));
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static IEnumerable<string> SplitSymbols(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var raw = value.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var symbol in raw)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            var cleaned = symbol.Replace("$(DefineConstants)", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                yield return cleaned;
            }
        }
    }

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path ?? string.Empty;
        }

        return Path.GetFullPath(path);
    }
}
