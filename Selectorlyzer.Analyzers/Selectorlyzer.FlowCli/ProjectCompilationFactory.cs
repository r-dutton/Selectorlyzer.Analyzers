using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Selectorlyzer.FlowCli;

internal sealed class ProjectCompilationFactory
{
    private readonly SolutionProjectModel _solution;
    private readonly int _parseConcurrency;
    private readonly ImmutableArray<MetadataReference> _frameworkReferences;
    private readonly CSharpParseOptions _baselineParseOptions;
    private readonly Dictionary<string, CSharpCompilation> _compilations = new(StringComparer.OrdinalIgnoreCase);

    public ProjectCompilationFactory(SolutionProjectModel solution, int parseConcurrency)
    {
        _solution = solution ?? throw new ArgumentNullException(nameof(solution));
        _parseConcurrency = Math.Max(1, parseConcurrency);
        _frameworkReferences = FrameworkReferenceCache.Value;
        _baselineParseOptions = DetermineBaselineParseOptions(solution);
    }

    private static CSharpParseOptions DetermineBaselineParseOptions(SolutionProjectModel solution)
    {
        foreach (var project in solution.SortedProjects)
        {
            foreach (var file in project.SourceFiles)
            {
                if (!File.Exists(file))
                {
                    continue;
                }

                try
                {
                    var text = File.ReadAllText(file);
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    if (CSharpSyntaxTree.ParseText(text, path: file) is CSharpSyntaxTree tree)
                    {
                        var options = tree.Options;
                        return options
                            .WithDocumentationMode(DocumentationMode.Parse)
                            .WithKind(SourceCodeKind.Regular);
                    }
                }
                catch
                {
                    // Ignore unreadable files; fall back to defaults below.
                }
            }
        }

        return CSharpParseOptions.Default
            .WithDocumentationMode(DocumentationMode.Parse)
            .WithKind(SourceCodeKind.Regular);
    }

    public void BuildAll(CancellationToken cancellationToken)
    {
        foreach (var project in _solution.SortedProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = CreateCompilation(project, cancellationToken);
            _compilations[project.NormalizedPath] = compilation;
        }
    }

    public CSharpCompilation GetCompilation(ProjectModel project)
    {
        if (_compilations.TryGetValue(project.NormalizedPath, out var compilation))
        {
            return compilation;
        }

        throw new InvalidOperationException($"Compilation for project '{project.ProjectName}' has not been built.");
    }

    private CSharpCompilation CreateCompilation(ProjectModel project, CancellationToken cancellationToken)
    {
        var parseOptions = _baselineParseOptions
            .WithLanguageVersion(project.LanguageVersion)
            .WithPreprocessorSymbols(project.PreprocessorSymbols.ToArray());

        var syntaxTrees = project.SourceFiles.Count == 0
            ? Array.Empty<SyntaxTree>()
            : ParseSyntaxTrees(project, project.SourceFiles, parseOptions, cancellationToken);

        var references = BuildMetadataReferences(project);

        var compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            reportSuppressedDiagnostics: false,
            optimizationLevel: OptimizationLevel.Release,
            checkOverflow: false,
            allowUnsafe: project.AllowUnsafe,
            nullableContextOptions: project.NullableContext,
            deterministic: false,
            concurrentBuild: true);

        try
        {
            return CSharpCompilation.Create(
                project.AssemblyName,
                syntaxTrees,
                references,
                compilationOptions);
        }
        catch (ArgumentException ex)
        {
            var versionGroups = syntaxTrees
                .OfType<CSharpSyntaxTree>()
                .GroupBy(tree => tree.Options.LanguageVersion)
                .Select(group => $"{group.Key}: {string.Join(", ", group.Select(t => t.FilePath).Take(5))}{(group.Count() > 5 ? ", ..." : string.Empty)}")
                .ToArray();

            var message = $"Inconsistent parse options detected for project '{project.ProjectName}'. Versions: {string.Join(" | ", versionGroups)}";
            Console.Error.WriteLine(message);
            throw new InvalidOperationException(message, ex);
        }
    }

    private SyntaxTree[] ParseSyntaxTrees(
        ProjectModel project,
        IReadOnlyList<string> sourceFiles,
        CSharpParseOptions parseOptions,
        CancellationToken cancellationToken)
    {
        var trees = new SyntaxTree[sourceFiles.Count];
        if (sourceFiles.Count < 4 || _parseConcurrency == 1)
        {
            for (var i = 0; i < sourceFiles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = sourceFiles[i];
                var text = File.ReadAllText(file);
                trees[i] = CSharpSyntaxTree.ParseText(text, parseOptions, file);
            }

            return trees;
        }

        var rangePartitioner = Partitioner.Create(0, sourceFiles.Count);
        Parallel.ForEach(rangePartitioner, new ParallelOptions
        {
            MaxDegreeOfParallelism = _parseConcurrency,
            CancellationToken = cancellationToken
        }, range =>
        {
            for (var index = range.Item1; index < range.Item2; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = sourceFiles[index];
                var text = File.ReadAllText(file);
                trees[index] = CSharpSyntaxTree.ParseText(text, parseOptions, file);
            }
        });

        return trees;
    }

    private ImmutableArray<MetadataReference> BuildMetadataReferences(ProjectModel project)
    {
        var references = new List<MetadataReference>(_frameworkReferences);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddReference(MetadataReference reference)
        {
            if (reference is PortableExecutableReference pe && pe.FilePath is { } path)
            {
                if (!seen.Add(path))
                {
                    return;
                }
            }

            references.Add(reference);
        }

        foreach (var frameworkReference in _frameworkReferences)
        {
            if (frameworkReference is PortableExecutableReference pe && pe.FilePath is { } path)
            {
                seen.Add(path);
            }
        }

        foreach (var referencePath in project.ProjectReferences)
        {
            if (_solution.ProjectsByPath.TryGetValue(referencePath, out var referencedProject) &&
                _compilations.TryGetValue(referencedProject.NormalizedPath, out var referencedCompilation))
            {
                AddReference(referencedCompilation.ToMetadataReference());
                continue;
            }

            if (File.Exists(referencePath) && referencePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                AddReference(MetadataReference.CreateFromFile(referencePath));
            }
        }

        return references.ToImmutableArray();
    }

    private static readonly Lazy<ImmutableArray<MetadataReference>> FrameworkReferenceCache = new(() =>
    {
        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;

        if (!string.IsNullOrWhiteSpace(trusted))
        {
            foreach (var path in trusted.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (!seen.Add(path))
                {
                    continue;
                }

                try
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                }
                catch
                {
                    // Ignore assemblies that cannot be loaded as metadata.
                }
            }
        }

        if (references.Count == 0)
        {
            foreach (var assembly in new[]
            {
                typeof(object).Assembly,
                typeof(Uri).Assembly,
                typeof(Enumerable).Assembly,
                typeof(Task).Assembly
            })
            {
                var location = assembly.Location;
                if (!string.IsNullOrWhiteSpace(location) && seen.Add(location))
                {
                    references.Add(MetadataReference.CreateFromFile(location));
                }
            }
        }

        return references.ToImmutableArray();
    }, isThreadSafe: true);
}
