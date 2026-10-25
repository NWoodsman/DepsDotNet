namespace DepsDotNet;

/// <summary>A git repository that owns one or more projects in the graph.</summary>
/// <param name="Slot">The order in which the repo was first seen while walking the sorted projects.</param>
public sealed record RepoNode(string Path, int Slot, List<ProjectNode> Projects, HashSet<string> DependsOn)
{
	public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// The git repositories that own a project graph, in the order they must be committed:
/// every repo comes after all the repos its projects reference.
/// </summary>
public sealed class RepoPlan : IGraphDependencies<string>
{
	static readonly StringComparer PathComparer =
		OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	// Repo path -> repo; each repo's Slot is the dictionary's Count when it was added.
	readonly Dictionary<string, RepoNode> repos = new(PathComparer);

	public IReadOnlyDictionary<string, RepoNode> Repos => repos;

	/// <summary>Repos in commit order.</summary>
	public IReadOnlyList<RepoNode> Order { get; }

	RepoPlan(ProjectGraph graph)
	{
		var projectRepo = new Dictionary<string, RepoNode>(PathComparer);
		var dirRepo = new Dictionary<string, string>(PathComparer);

		foreach (var project in graph.TopologicalOrder())
		{
			var repoPath = FindRepo(project.ProjectPath, dirRepo);
			if (!repos.TryGetValue(repoPath, out var repo))
				repos[repoPath] = repo = new RepoNode(repoPath, repos.Count, new(), new(PathComparer));

			repo.Projects.Add(project);
			projectRepo[project.ProjectPath] = repo;
		}

		// A project reference across repos means the referencing repo must commit after the referenced one.
		foreach (var repo in repos.Values)
			foreach (var project in repo.Projects)
				foreach (var reference in project.References)
					if (projectRepo[reference] is var dep && dep != repo)
						repo.DependsOn.Add(dep.Path);

		// Sorting from each repo in slot order means that when first-seen order is already valid, it is kept as is.
		TopoSorter<string> sorter = new(this);
		try
		{
			foreach (var repo in repos.Values.OrderBy(r => r.Slot)) sorter.Sort(repo.Path);
		}
		catch (CycleException<string> e)
		{
			throw new InvalidOperationException(
				$"Repositories depend on each other, so no commit order can record correct hashes: {DescribeCycle(e.Cycle)}", e);
		}

		Order = sorter.Sorted.Select(p => repos[p]).ToList();
	}

	public static RepoPlan Build(ProjectGraph graph) => new(graph);

	public IEnumerable<string> GetDependencies(string ofElement) =>
		repos.TryGetValue(ofElement, out var repo) ? repo.DependsOn : [];

	string DescribeCycle(IReadOnlyList<string> cycle)
	{
		// Name the project references that create each repo-to-repo edge.
		var steps = new List<string>();
		for (int i = 0; i < cycle.Count - 1; i++)
		{
			var from = repos[cycle[i]];
			var to = repos[cycle[i + 1]];
			var toProjects = to.Projects.Select(p => p.ProjectPath).ToHashSet(PathComparer);
			var links = from.Projects
				.SelectMany(p => p.References.Where(toProjects.Contains).Select(r => $"{p.Name} -> {Path.GetFileNameWithoutExtension(r)}"));
			steps.Add($"{from.Name} -> {to.Name} ({string.Join(", ", links)})");
		}
		return string.Join("; ", steps);
	}

	/// <summary>Walks up from the project to the nearest directory containing .git (a folder, or a file for worktrees and submodules).</summary>
	static string FindRepo(string projectPath, Dictionary<string, string> cache)
	{
		var start = Path.GetDirectoryName(projectPath)!;
		var visited = new List<string>();

		for (var dir = start; dir is not null; dir = Path.GetDirectoryName(dir))
		{
			if (cache.TryGetValue(dir, out var known))
			{
				foreach (var v in visited) cache[v] = known;
				return known;
			}

			visited.Add(dir);

			var git = Path.Join(dir, ".git");
			if (Directory.Exists(git) || File.Exists(git))
			{
				foreach (var v in visited) cache[v] = dir;
				return dir;
			}
		}

		throw new InvalidOperationException($"'{projectPath}' is not inside a git repository.");
	}
}
