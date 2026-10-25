using System.Diagnostics;
using System.Text.Json.Nodes;

namespace DepsDotNet;

public sealed record ProjectNode(string Name, string ProjectPath, HashSet<string> References);

/// <summary>
/// The project-reference graph of one or more restored projects, read from their project.assets.json files.
/// Nodes are keyed by absolute project file path; package dependencies are ignored.
/// </summary>
public sealed class ProjectGraph : IGraphDependencies<string>
{
	static readonly StringComparer PathComparer =
		OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	readonly Dictionary<string, ProjectNode> nodes = new(PathComparer);
	readonly List<ProjectNode> roots = new();

	/// <summary>The projects the graph was loaded from: one for a project or assets file, every project for a solution.</summary>
	public IReadOnlyList<ProjectNode> Roots => roots;

	public IReadOnlyDictionary<string, ProjectNode> Nodes => nodes;

	/// <summary>
	/// Accepts a solution (.sln/.slnx), a project file, or a project.assets.json.
	/// Project and solution assets files are located via MSBuild.
	/// </summary>
	public static ProjectGraph Load(string path)
	{
		path = Path.GetFullPath(path);
		var graph = new ProjectGraph();

		switch (Path.GetExtension(path).ToLowerInvariant())
		{
			case ".sln" or ".slnx":
				// Each project's own assets file is read even if another project already reached it:
				// a consumer's file only shows the references for the TFM that consumer picked.
				foreach (var project in ListSolutionProjects(path))
				{
					var assets = FindAssetsFile(project);
					if (assets is null)
					{
						Console.Error.WriteLine($"Skipping '{project}': it has no ProjectAssetsFile (not a restorable SDK project).");
						continue;
					}
					graph.roots.Add(graph.AddAssets(ReadAssets(assets, $"Run 'dotnet restore \"{path}\"'.")));
				}
				break;

			case ".json":
				graph.roots.Add(graph.AddAssets(ReadAssets(path, "Has the project been restored?")));
				break;

			default:
				var projectAssets = FindAssetsFile(path)
					?? throw new InvalidOperationException($"Could not resolve ProjectAssetsFile for '{path}'.");
				graph.roots.Add(graph.AddAssets(ReadAssets(projectAssets, $"Run 'dotnet restore \"{path}\"'.")));
				break;
		}

		return graph;
	}

	public static ProjectGraph FromAssets(JsonNode doc)
	{
		var graph = new ProjectGraph();
		graph.roots.Add(graph.AddAssets(doc));
		return graph;
	}

	/// <summary>Merges one assets file into the graph and returns the node of the project that owns it.</summary>
	ProjectNode AddAssets(JsonNode doc)
	{
		var restore = doc["project"]?["restore"] ?? throw new InvalidDataException("project.assets.json has no project.restore section");
		var rootPath = NormalizePath(restore["projectPath"]!.GetValue<string>(), null);
		var rootDir = Path.GetDirectoryName(rootPath)!;

		// Direct references of the owning project, already absolute.
		var root = GetOrAdd(restore["projectName"]!.GetValue<string>(), rootPath);
		foreach (var fw in restore["frameworks"]?.AsObject() ?? [])
			foreach (var r in fw.Value?["projectReferences"]?.AsObject() ?? [])
				root.References.Add(NormalizePath(r.Value?["projectPath"]?.GetValue<string>() ?? r.Key, null));

		// "Name/Version" -> absolute path, for every project in the closure. Paths are relative to the owning project.
		var libPaths = new Dictionary<string, string>();
		var nameToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var lib in doc["libraries"]?.AsObject() ?? [])
		{
			if (lib.Value?["type"]?.GetValue<string>() != "project") continue;
			var rel = lib.Value["msbuildProject"]?.GetValue<string>() ?? lib.Value["path"]!.GetValue<string>();
			var abs = NormalizePath(rel, rootDir);
			libPaths[lib.Key] = abs;
			nameToPath[LibName(lib.Key)] = abs;
		}

		// Each target (TFM, optionally with RID) lists the direct dependencies of every library; union them.
		// Projects that appear in libraries but in no target (e.g. a TFM mismatch) still get a node.
		foreach (var (key, libPath) in libPaths)
			GetOrAdd(LibName(key), libPath);

		foreach (var target in doc["targets"]?.AsObject() ?? [])
		{
			foreach (var lib in target.Value?.AsObject() ?? [])
			{
				if (!libPaths.TryGetValue(lib.Key, out var libPath)) continue;
				var node = nodes[libPath];

				foreach (var dep in lib.Value?["dependencies"]?.AsObject() ?? [])
					if (nameToPath.TryGetValue(dep.Key, out var depPath))
						node.References.Add(depPath);
			}
		}

		return root;
	}

	ProjectNode GetOrAdd(string name, string path)
	{
		if (!nodes.TryGetValue(path, out var node))
			nodes[path] = node = new ProjectNode(name, path, new(PathComparer));
		return node;
	}

	/// <summary>Projects ordered so every project comes after all of its references.</summary>
	public List<ProjectNode> TopologicalOrder()
	{
		// The sorter keeps its visited set across calls, so projects shared between roots appear once.
		TopoSorter<string> sorter = new(this);
		foreach (var root in roots) sorter.Sort(root.ProjectPath);
		return sorter.Sorted.Select(p => nodes[p]).ToList();
	}

	public IEnumerable<string> GetDependencies(string ofElement) =>
		nodes.TryGetValue(ofElement, out var node) ? node.References : [];

	static string LibName(string libKey) => libKey.Split('/')[0];

	static string NormalizePath(string path, string? baseDir)
	{
		// Assets and .sln files written on Windows use backslashes.
		if (!OperatingSystem.IsWindows()) path = path.Replace('\\', '/');
		return baseDir is null ? Path.GetFullPath(path) : Path.GetFullPath(path, baseDir);
	}

	static JsonNode ReadAssets(string assetsPath, string hint)
	{
		if (!File.Exists(assetsPath))
			throw new FileNotFoundException($"No project.assets.json at '{assetsPath}'. {hint}", assetsPath);

		return JsonNode.Parse(File.ReadAllText(assetsPath))!;
	}

	static IEnumerable<string> ListSolutionProjects(string solutionPath)
	{
		// Output is a "Project(s)" header, a dashed rule, then one solution-relative path per line.
		var (exitCode, output, error) = RunDotnet("sln", solutionPath, "list");
		if (exitCode != 0)
			throw new InvalidOperationException($"Could not list projects in '{solutionPath}': {error}");

		var solutionDir = Path.GetDirectoryName(solutionPath)!;
		return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.SkipWhile(line => !line.StartsWith("---"))
			.Skip(1)
			.Select(line => NormalizePath(line, solutionDir));
	}

	/// <summary>Returns null when MSBuild cannot evaluate the project or it defines no assets file.</summary>
	static string? FindAssetsFile(string projectPath)
	{
		var (exitCode, output, _) = RunDotnet("msbuild", projectPath, "-getProperty:ProjectAssetsFile");
		return exitCode == 0 && output.Length > 0 ? output : null;
	}

	static (int exitCode, string output, string error) RunDotnet(params string[] args)
	{
		var psi = new ProcessStartInfo("dotnet", args)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		using var proc = Process.Start(psi)!;
		var errorTask = proc.StandardError.ReadToEndAsync();
		var output = proc.StandardOutput.ReadToEnd().Trim();
		proc.WaitForExit();
		return (proc.ExitCode, output, errorTask.Result);
	}
}
