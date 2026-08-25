using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AiNative.Tools.ArchitectureCheck;

public sealed class ArchitectureValidator
{
    private static readonly StringComparer RepositoryPathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private const string ForbiddenDependencyCode = "ARC001";
    private const string CycleCode = "ARC002";
    private const string RuntimeEditorCode = "ARC003";
    private const string SharedApiCode = "ARC004";
    private const string UnresolvedReferenceCode = "ARC005";
    private const string SolutionDriftCode = "ARC006";
    private const string UnityPackageGeneratedOutputCode = "ARC007";
    private const string FantasyBoundaryCode = "ARC008";

    public ArchitectureValidationResult Validate(string repositoryRoot, string? rulesPath = null)
    {
        string root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Repository root does not exist: {root}");
        }

        string resolvedRulesPath = rulesPath is null
            ? Path.Combine(root, "tools", "ArchitectureCheck", "architecture-rules.json")
            : Path.GetFullPath(rulesPath);
        ArchitectureRules rules = ArchitectureRules.Load(resolvedRulesPath);
        RepositoryFiles files = RepositoryFiles.Discover(root, rules);
        List<ArchitectureDiagnostic> diagnostics = [];
        Graph graph = BuildGraph(root, files, rules, diagnostics);

        ValidateDependencies(graph, rules, diagnostics);
        ValidateCycles(graph, diagnostics);
        ValidateSharedSources(root, files.CSharpFiles, rules, diagnostics);
        ValidateFantasyBoundary(root, files.CSharpFiles, files.ProjectFiles, rules, diagnostics);
        ValidateSolutions(root, files, diagnostics);
        ValidateUnityPackageGeneratedDirectories(root, files.PackageManifests, rules, diagnostics);

        return new ArchitectureValidationResult(diagnostics
            .OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.File, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToArray());
    }

    private static Graph BuildGraph(
        string root,
        RepositoryFiles files,
        ArchitectureRules rules,
        List<ArchitectureDiagnostic> diagnostics)
    {
        List<Node> nodes = [];

        foreach (string path in files.PackageManifests)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("name", out JsonElement nameElement))
            {
                continue;
            }

            nodes.Add(new Node(
                nameElement.GetString() ?? throw new InvalidDataException($"Package name is empty: {path}"),
                Relative(root, path),
                LayerFor(root, path, rules),
                NodeKind.Package));
        }

        foreach (string path in files.AsmdefFiles)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            string name = document.RootElement.GetProperty("name").GetString()
                ?? throw new InvalidDataException($"Assembly name is empty: {path}");
            nodes.Add(new Node(name, Relative(root, path), LayerFor(root, path, rules), NodeKind.Assembly));
        }

        foreach (string path in files.ProjectFiles)
        {
            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            string name = document.Descendants("AssemblyName").Select(element => element.Value).FirstOrDefault()
                ?? Path.GetFileNameWithoutExtension(path);
            nodes.Add(new Node(name, Relative(root, path), LayerFor(root, path, rules), NodeKind.Project));
        }

        Node unityProject = new(
            "client/UnityProject",
            "client/UnityProject/Packages/manifest.json",
            "Client",
            NodeKind.UnityProject);
        if (File.Exists(Path.Combine(root, unityProject.Path.Replace('/', Path.DirectorySeparatorChar))))
        {
            nodes.Add(unityProject);
        }

        Dictionary<string, List<Node>> nodesById = new(StringComparer.Ordinal);
        foreach (Node node in nodes)
        {
            if (!nodesById.TryGetValue(node.Id, out List<Node>? manifests))
            {
                manifests = [];
                nodesById.Add(node.Id, manifests);
            }

            if (manifests.Any(manifest => manifest.Kind == node.Kind))
            {
                throw new InvalidDataException($"Duplicate internal {node.Kind} identifier '{node.Id}'.");
            }

            manifests.Add(node);
        }

        Dictionary<string, Node> projectsByPath = nodes
            .Where(node => node.Kind == NodeKind.Project)
            .ToDictionary(node => node.Path, RepositoryPathComparer);
        List<Edge> edges = [];

        foreach (string path in files.ProjectFiles)
        {
            string sourcePath = Relative(root, path);
            Node source = projectsByPath[sourcePath];
            XDocument document = XDocument.Load(path);
            foreach (XElement reference in document.Descendants("ProjectReference"))
            {
                string include = reference.Attribute("Include")?.Value
                    ?? throw new InvalidDataException($"ProjectReference has no Include: {sourcePath}");
                string targetPath = Relative(root, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, include)));
                if (projectsByPath.TryGetValue(targetPath, out Node? target))
                {
                    edges.Add(new Edge(source, target, sourcePath));
                }
                else
                {
                    diagnostics.Add(new ArchitectureDiagnostic(
                        UnresolvedReferenceCode,
                        sourcePath,
                        $"ProjectReference does not resolve to a repository project: {include}",
                        source.Id,
                        targetPath));
                }
            }
        }

        foreach (string path in files.AsmdefFiles)
        {
            string sourcePath = Relative(root, path);
            Node source = nodes.Single(node => node.Kind == NodeKind.Assembly && node.Path == sourcePath);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("references", out JsonElement references))
            {
                continue;
            }

            foreach (JsonElement reference in references.EnumerateArray())
            {
                string targetId = reference.GetString() ?? string.Empty;
                AddReferenceEdge(source, targetId, sourcePath, nodesById, rules, edges, diagnostics);
            }
        }

        foreach (string path in files.PackageManifests)
        {
            string sourcePath = Relative(root, path);
            Node source = nodes.Single(node => node.Kind == NodeKind.Package && node.Path == sourcePath);
            AddPackageEdges(path, source, sourcePath, nodesById, rules, edges, diagnostics);
        }

        string unityManifestPath = Path.Combine(root, "client", "UnityProject", "Packages", "manifest.json");
        if (File.Exists(unityManifestPath))
        {
            AddPackageEdges(
                unityManifestPath,
                unityProject,
                unityProject.Path,
                nodesById,
                rules,
                edges,
                diagnostics);
        }

        return new Graph(nodes, edges);
    }

    private static void AddPackageEdges(
        string path,
        Node source,
        string sourcePath,
        IReadOnlyDictionary<string, List<Node>> nodesById,
        ArchitectureRules rules,
        List<Edge> edges,
        List<ArchitectureDiagnostic> diagnostics)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("dependencies", out JsonElement dependencies))
        {
            return;
        }

        foreach (JsonProperty dependency in dependencies.EnumerateObject())
        {
            AddReferenceEdge(source, dependency.Name, sourcePath, nodesById, rules, edges, diagnostics);
        }
    }

    private static void AddReferenceEdge(
        Node source,
        string targetId,
        string sourcePath,
        IReadOnlyDictionary<string, List<Node>> nodesById,
        ArchitectureRules rules,
        List<Edge> edges,
        List<ArchitectureDiagnostic> diagnostics)
    {
        if (nodesById.TryGetValue(targetId, out List<Node>? targets))
        {
            NodeKind expectedKind = source.Kind is NodeKind.Package or NodeKind.UnityProject
                ? NodeKind.Package
                : NodeKind.Assembly;
            Node target = targets.FirstOrDefault(candidate => candidate.Kind == expectedKind) ?? targets[0];
            edges.Add(new Edge(source, target, sourcePath));
            return;
        }

        if (rules.InternalReferencePrefixes.Any(prefix => targetId.StartsWith(prefix, StringComparison.Ordinal)))
        {
            diagnostics.Add(new ArchitectureDiagnostic(
                UnresolvedReferenceCode,
                sourcePath,
                $"Internal reference does not resolve: {targetId}",
                source.Id,
                targetId));
        }
    }

    private static void ValidateDependencies(
        Graph graph,
        ArchitectureRules rules,
        List<ArchitectureDiagnostic> diagnostics)
    {
        foreach (Edge edge in graph.Edges)
        {
            if (string.IsNullOrEmpty(edge.Source.Layer) || string.IsNullOrEmpty(edge.Target.Layer))
            {
                continue;
            }

            if (!rules.AllowedDependencies.TryGetValue(edge.Source.Layer, out string[]? allowed)
                || !allowed.Contains(edge.Target.Layer, StringComparer.Ordinal))
            {
                diagnostics.Add(new ArchitectureDiagnostic(
                    ForbiddenDependencyCode,
                    edge.DeclaredIn,
                    $"Layer '{edge.Source.Layer}' may not depend on '{edge.Target.Layer}'.",
                    edge.Source.Id,
                    edge.Target.Id));
            }

            if (edge.Source.Kind == NodeKind.Assembly
                && IsRuntimePath(edge.Source.Path)
                && IsEditorPath(edge.Target.Path))
            {
                diagnostics.Add(new ArchitectureDiagnostic(
                    RuntimeEditorCode,
                    edge.DeclaredIn,
                    "A Runtime assembly may not reference an Editor assembly.",
                    edge.Source.Id,
                    edge.Target.Id));
            }
        }
    }

    private static void ValidateCycles(Graph graph, List<ArchitectureDiagnostic> diagnostics)
    {
        Dictionary<Node, VisitState> states = [];
        Stack<Node> path = [];

        foreach (Node node in graph.Nodes)
        {
            Visit(node);
        }

        void Visit(Node node)
        {
            if (states.TryGetValue(node, out VisitState state))
            {
                if (state == VisitState.Visiting)
                {
                    Node[] cycle = path.Reverse().SkipWhile(item => item != node).Append(node).ToArray();
                    diagnostics.Add(new ArchitectureDiagnostic(
                        CycleCode,
                        node.Path,
                        $"Dependency cycle detected: {string.Join(" -> ", cycle.Select(item => item.Id))}",
                        node.Id,
                        node.Id));
                }

                return;
            }

            states[node] = VisitState.Visiting;
            path.Push(node);
            foreach (Edge edge in graph.Edges.Where(edge => edge.Source == node))
            {
                Visit(edge.Target);
            }

            path.Pop();
            states[node] = VisitState.Visited;
        }
    }

    private static void ValidateSharedSources(
        string root,
        IReadOnlyList<string> sourceFiles,
        ArchitectureRules rules,
        List<ArchitectureDiagnostic> diagnostics)
    {
        foreach (string path in sourceFiles.Where(path => IsSharedRuntime(root, path)))
        {
            string relativePath = Relative(root, path);
            bool isPassiveContract = rules.SharedPassiveContractPathPrefixes.Any(prefix =>
                relativePath.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
            string source = File.ReadAllText(path);
            CompilationUnitSyntax compilation = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
            HashSet<string> violations = new(StringComparer.Ordinal);

            foreach (UsingDirectiveSyntax directive in compilation.Usings)
            {
                string namespaceName = directive.Name?.ToString() ?? string.Empty;
                if (IsForbiddenName(namespaceName, rules, isPassiveContract))
                {
                    violations.Add(namespaceName);
                }
            }

            foreach (NameSyntax name in compilation.DescendantNodes().OfType<NameSyntax>())
            {
                string qualifiedName = name.ToString();
                if (IsForbiddenName(qualifiedName, rules, isPassiveContract))
                {
                    violations.Add(qualifiedName);
                }
            }

            foreach (ObjectCreationExpressionSyntax creation in compilation.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                string typeName = creation.Type.ToString();
                if (typeName.Equals("Random", StringComparison.Ordinal)
                    || typeName.EndsWith(".Random", StringComparison.Ordinal))
                {
                    violations.Add(typeName);
                }

                if (isPassiveContract && IsActiveSchedulingType(typeName))
                {
                    violations.Add(typeName);
                }
            }

            foreach (AttributeSyntax attribute in compilation.DescendantNodes().OfType<AttributeSyntax>())
            {
                string name = attribute.Name.ToString();
                if (name.EndsWith("DllImport", StringComparison.Ordinal)
                    || name.EndsWith("DllImportAttribute", StringComparison.Ordinal)
                    || name.EndsWith("LibraryImport", StringComparison.Ordinal)
                    || name.EndsWith("LibraryImportAttribute", StringComparison.Ordinal))
                {
                    violations.Add(name);
                }
            }

            foreach (MemberAccessExpressionSyntax member in compilation.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                string expression = member.Expression.ToString();
                string name = member.Name.Identifier.ValueText;
                string fullMember = member.ToString();
                if (IsForbiddenName(fullMember, rules, isPassiveContract))
                {
                    violations.Add(fullMember);
                }

                if (((expression.EndsWith("DateTime", StringComparison.Ordinal)
                        || expression.EndsWith("DateTimeOffset", StringComparison.Ordinal))
                        && name is "Now" or "UtcNow" or "Today")
                    || (expression.EndsWith("Environment", StringComparison.Ordinal) && name is "TickCount" or "TickCount64")
                    || (expression.EndsWith("Stopwatch", StringComparison.Ordinal) && name is "GetTimestamp" or "StartNew"))
                {
                    violations.Add($"{expression}.{name}");
                }

                if (isPassiveContract && IsActiveSchedulingMember(expression, name))
                {
                    violations.Add(fullMember);
                }
            }

            if (compilation.DescendantTrivia(descendIntoTrivia: true).Any(trivia => trivia.IsDirective))
            {
                violations.Add("conditional compilation directive");
            }

            foreach (string violation in violations)
            {
                diagnostics.Add(new ArchitectureDiagnostic(
                    SharedApiCode,
                    Relative(root, path),
                    $"Shared Runtime uses forbidden API or namespace: {violation}"));
            }
        }
    }

    private static bool IsForbiddenName(string name, ArchitectureRules rules, bool isPassiveContract) =>
        rules.SharedForbiddenNamespacePrefixes.Any(prefix =>
            (name.Equals(prefix, StringComparison.Ordinal)
             || name.StartsWith(prefix + ".", StringComparison.Ordinal))
            && (!isPassiveContract
                || !rules.SharedPassiveContractAllowedNamespacePrefixes.Any(allowed =>
                    name.Equals(allowed, StringComparison.Ordinal)
                    || name.StartsWith(allowed + ".", StringComparison.Ordinal))));

    private static bool IsActiveSchedulingType(string typeName) =>
        typeName is "Thread" or "System.Threading.Thread"
            or "Timer" or "System.Threading.Timer"
            or "CancellationTokenSource" or "System.Threading.CancellationTokenSource"
            or "Task" or "System.Threading.Tasks.Task"
            or "TaskFactory" or "System.Threading.Tasks.TaskFactory";

    private static bool IsActiveSchedulingMember(string expression, string name) =>
        (expression.EndsWith("Task", StringComparison.Ordinal) && name == "Run")
        || (expression.EndsWith("Task.Factory", StringComparison.Ordinal) && name == "StartNew")
        || expression.EndsWith("ThreadPool", StringComparison.Ordinal)
        || (expression.EndsWith("Thread", StringComparison.Ordinal) && name is "Sleep" or "Yield");

    private static void ValidateFantasyBoundary(
        string root,
        IReadOnlyList<string> sourceFiles,
        IReadOnlyList<string> projectFiles,
        ArchitectureRules rules,
        List<ArchitectureDiagnostic> diagnostics)
    {
        foreach (string path in sourceFiles)
        {
            string relativePath = Relative(root, path);
            if (IsAllowedPath(relativePath, rules.FantasyNamespaceAllowedPathPrefixes))
            {
                continue;
            }

            CompilationUnitSyntax compilation = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetCompilationUnitRoot();
            bool referencesFantasy = compilation.Usings
                .Select(directive => directive.Name?.ToString() ?? string.Empty)
                .Any(IsFantasyNamespace)
                || compilation.DescendantNodes().OfType<NameSyntax>()
                    .Where(name => name is QualifiedNameSyntax or AliasQualifiedNameSyntax)
                    .Select(name => name.ToString())
                    .Any(IsFantasyNamespace);
            if (referencesFantasy)
            {
                diagnostics.Add(new ArchitectureDiagnostic(
                    FantasyBoundaryCode,
                    relativePath,
                    "Fantasy runtime namespaces must terminate inside the dedicated Server adapter."));
            }
        }

        foreach (string path in projectFiles)
        {
            string relativePath = Relative(root, path);
            XDocument document = XDocument.Load(path);
            bool referencesFantasyPackage = document.Descendants("PackageReference")
                .Select(reference => reference.Attribute("Include")?.Value ?? string.Empty)
                .Any(package => package.Equals("Fantasy-Net", StringComparison.OrdinalIgnoreCase));
            if (referencesFantasyPackage
                && !IsAllowedPath(relativePath, rules.FantasyPackageReferenceAllowedPathPrefixes))
            {
                diagnostics.Add(new ArchitectureDiagnostic(
                    FantasyBoundaryCode,
                    relativePath,
                    "Fantasy-Net may be referenced only by the dedicated Server adapter or Battle Host composition root."));
            }
        }
    }

    private static bool IsFantasyNamespace(string name)
    {
        string normalized = name.StartsWith("global::", StringComparison.Ordinal)
            ? name[8..]
            : name;
        return normalized.Equals("Fantasy", StringComparison.Ordinal)
            || normalized.StartsWith("Fantasy.", StringComparison.Ordinal);
    }

    private static bool IsAllowedPath(string path, IEnumerable<string> prefixes) =>
        prefixes.Any(prefix => path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));

    private static void ValidateSolutions(
        string root,
        RepositoryFiles files,
        List<ArchitectureDiagnostic> diagnostics)
    {
        HashSet<string> projects = files.ProjectFiles
            .Select(path => Relative(root, path))
            .ToHashSet(RepositoryPathComparer);
        HashSet<string> solutionProjects = new(RepositoryPathComparer);

        foreach (string solution in files.SolutionFiles)
        {
            string solutionDirectory = Path.GetDirectoryName(solution)!;
            foreach (Match match in Regex.Matches(
                         File.ReadAllText(solution),
                         "Project\\(\\\"[^\\\"]+\\\"\\) = \\\"[^\\\"]+\\\", \\\"(?<path>[^\\\"]+\\.csproj)\\\"",
                         RegexOptions.CultureInvariant))
            {
                string path = match.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar);
                solutionProjects.Add(Relative(root, Path.GetFullPath(Path.Combine(solutionDirectory, path))));
            }
        }

        foreach (string project in projects.Except(solutionProjects, RepositoryPathComparer))
        {
            diagnostics.Add(new ArchitectureDiagnostic(
                SolutionDriftCode,
                project,
                "Project is not included in a repository solution."));
        }

        foreach (string project in solutionProjects.Except(projects, RepositoryPathComparer))
        {
            diagnostics.Add(new ArchitectureDiagnostic(
                SolutionDriftCode,
                project,
                "Solution references a missing or excluded project."));
        }
    }

    private static void ValidateUnityPackageGeneratedDirectories(
        string root,
        IReadOnlyList<string> packageManifests,
        ArchitectureRules rules,
        List<ArchitectureDiagnostic> diagnostics)
    {
        foreach (string packageManifest in packageManifests)
        {
            string packageRoot = Path.GetDirectoryName(packageManifest)!;
            Stack<string> directories = new([packageRoot]);

            while (directories.TryPop(out string? directory))
            {
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    string name = Path.GetFileName(child);
                    string relative = Relative(root, child);

                    if (rules.UnityPackageForbiddenDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        diagnostics.Add(new ArchitectureDiagnostic(
                            UnityPackageGeneratedOutputCode,
                            relative,
                            $"Generated .NET output directory '{name}' must not be inside a Unity package.",
                            Relative(root, packageManifest),
                            relative));
                        continue;
                    }

                    if (!rules.IgnoredDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        directories.Push(child);
                    }
                }
            }
        }
    }

    private static string LayerFor(string root, string path, ArchitectureRules rules)
    {
        string relative = Relative(root, path);
        string firstSegment = relative.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return rules.LayerRoots.TryGetValue(firstSegment, out string? layer) ? layer : string.Empty;
    }

    private static bool IsRuntimePath(string path) =>
        path.Contains("/Runtime/", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".Runtime.asmdef", StringComparison.OrdinalIgnoreCase);

    private static bool IsEditorPath(string path) =>
        path.Contains("/Editor/", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".Editor.asmdef", StringComparison.OrdinalIgnoreCase);

    private static bool IsSharedRuntime(string root, string path)
    {
        string relative = Relative(root, path);
        return relative.StartsWith("shared/", StringComparison.OrdinalIgnoreCase)
            && relative.Contains("/Runtime/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private sealed record Node(string Id, string Path, string Layer, NodeKind Kind);

    private sealed record Edge(Node Source, Node Target, string DeclaredIn);

    private sealed record Graph(IReadOnlyList<Node> Nodes, IReadOnlyList<Edge> Edges);

    private enum NodeKind
    {
        Package,
        Assembly,
        Project,
        UnityProject,
    }

    private enum VisitState
    {
        Visiting,
        Visited,
    }

    private sealed record RepositoryFiles(
        IReadOnlyList<string> PackageManifests,
        IReadOnlyList<string> AsmdefFiles,
        IReadOnlyList<string> ProjectFiles,
        IReadOnlyList<string> SolutionFiles,
        IReadOnlyList<string> CSharpFiles)
    {
        public static RepositoryFiles Discover(string root, ArchitectureRules rules)
        {
            List<string> files = [];
            Stack<string> directories = new([root]);

            while (directories.TryPop(out string? directory))
            {
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    string name = Path.GetFileName(child);
                    string relative = Relative(root, child);
                    if (rules.IgnoredDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                        || rules.IgnoredPathPrefixes.Any(prefix =>
                            relative.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                            || relative.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    directories.Push(child);
                }

                files.AddRange(Directory.EnumerateFiles(directory));
            }

            return new RepositoryFiles(
                files.Where(path => Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase)).ToArray(),
                files.Where(path => path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)).ToArray(),
                files.Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToArray(),
                files.Where(path => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)).ToArray(),
                files.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToArray());
        }
    }
}
