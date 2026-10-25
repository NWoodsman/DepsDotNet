namespace DepsDotNet;

public enum DependencyFileChange { None, Unchanged, Create, Update, Delete }

/// <summary>What gets staged in a repo before its commit, besides what the user staged and Dependencies.json.</summary>
/// <param name="Unstaged">Stage unstaged changes to tracked files (<c>git add -u</c>).</param>
/// <param name="Untracked">Track and stage untracked files.</param>
public sealed record StageChoice(bool Unstaged, bool Untracked)
{
	public static readonly StageChoice None = new(false, false);
	public static readonly StageChoice All = new(true, true);
}

/// <summary>A repo's working state before anything is committed.</summary>
public sealed record RepoState(
	RepoNode Repo,
	RepoStatus Status,
	List<StatusEntry> Staged,
	List<StatusEntry> Unstaged,
	List<StatusEntry> Untracked,
	List<string> Problems);

/// <summary>What committing one repo did, or in a dry run, would do.</summary>
/// <param name="ToCommit">The user's changes included in the commit (Dependencies.json is reported separately).</param>
/// <param name="Hash">The repo's commit after this step: a new commit, its unchanged HEAD, or in a dry run a placeholder.</param>
public sealed record RepoStep(
	RepoState State,
	StageChoice Choice,
	List<StatusEntry> ToCommit,
	DependencyFileChange FileChange,
	string? DependencyFileText,
	bool Committed,
	string Hash);

public sealed class CommitException(RepoNode repo, Exception inner)
	: Exception($"Committing {repo.Name} failed: {inner.Message}", inner)
{
	public RepoNode Repo { get; } = repo;
}

/// <summary>
/// Walks a <see cref="RepoPlan"/> in commit order. A repo's Dependencies.json can only be written once every repo it
/// depends on has committed, so hashes are collected as the walk goes.
/// </summary>
public static class CommitRunner
{
	public static string Placeholder(RepoNode repo) => $"<new commit of {repo.Name}>";

	/// <summary>Reads every repo's state and the problems that must stop the run before anything is committed.</summary>
	public static List<RepoState> Inspect(RepoPlan plan)
	{
		var states = new List<RepoState>();

		foreach (var repo in plan.Order)
		{
			var status = Git.Status(repo.Path);

			// Dependencies.json is written and staged by us from the plan, so its working-tree state doesn't matter.
			var staged = status.Entries.Where(e => e.IsStaged && !IsDependencyFile(e)).ToList();
			var unstaged = status.Entries.Where(e => e.IsUnstaged && !IsDependencyFile(e)).ToList();
			var untracked = status.Entries.Where(e => !e.IsTracked && !IsDependencyFile(e)).ToList();

			var problems = CheckReady(repo.Path, status);
			states.Add(new RepoState(repo, status, staged, unstaged, untracked, problems));
		}

		return states;
	}

	/// <summary>
	/// Simulates the commit pass without changing anything. Repos that would commit get a placeholder hash,
	/// which their dependents' Dependencies.json shows in place of the real one.
	/// </summary>
	public static List<RepoStep> DryRun(RepoPlan plan, IReadOnlyList<RepoState> states, Func<RepoState, StageChoice> choiceOf)
	{
		var hashes = new Dictionary<string, string>();
		var steps = new List<RepoStep>();

		foreach (var state in states)
		{
			var choice = choiceOf(state);
			var toCommit = ChangesToCommit(state, choice);
			var (change, text) = PlanDependencyFile(plan, state, hashes);

			var willCommit = toCommit.Count > 0 || change is DependencyFileChange.Create or DependencyFileChange.Update or DependencyFileChange.Delete;
			// A repo with no commits yet has no hash until it commits.
			var hash = willCommit || state.Status.Head is null ? Placeholder(state.Repo) : state.Status.Head;
			hashes[state.Repo.Path] = hash;

			steps.Add(new RepoStep(state, choice, toCommit, change, text, willCommit, hash));
		}

		return steps;
	}

	/// <summary>
	/// Commits each repo in order: writes its Dependencies.json from the hashes collected so far, stages per its choice,
	/// commits if anything is staged, and records its HEAD for the repos after it.
	/// Stops at the first failure; repos already committed stay committed, and rerunning resumes after them.
	/// </summary>
	public static List<RepoStep> Commit(RepoPlan plan, IReadOnlyList<RepoState> states, Func<RepoState, StageChoice> choiceOf,
		string message, Action<RepoStep>? onStep = null)
	{
		if (states.FirstOrDefault(s => s.Problems.Count > 0) is RepoState blocked)
			throw new InvalidOperationException($"{blocked.Repo.Name} is not ready to commit: {string.Join("; ", blocked.Problems)}");

		var hashes = new Dictionary<string, string>();
		var steps = new List<RepoStep>();

		foreach (var state in states)
		{
			var repo = state.Repo;
			try
			{
				var choice = choiceOf(state);
				var toCommit = ChangesToCommit(state, choice);
				var (change, text) = PlanDependencyFile(plan, state, hashes);

				var filePath = Path.Join(repo.Path, DependencyFile.FileName);
				if (text is not null)
					File.WriteAllText(filePath, text);
				else if (change is DependencyFileChange.Delete)
					File.Delete(filePath);

				if (choice.Unstaged)
					Git.RunChecked(repo.Path, "add", "-u");
				if (choice.Untracked)
					Git.StagePaths(repo.Path, state.Untracked.Select(e => e.Path));
				if (change is not DependencyFileChange.None)
					Git.RunChecked(repo.Path, "add", "-A", "--", DependencyFile.FileName);

				var committed = Git.HasStagedChanges(repo.Path);
				if (committed)
					Git.RunChecked(repo.Path, "commit", "--quiet", "-m", message);

				var hash = Git.RunChecked(repo.Path, "rev-parse", "HEAD");
				hashes[repo.Path] = hash;

				var step = new RepoStep(state, choice, toCommit, change, text, committed, hash);
				steps.Add(step);
				onStep?.Invoke(step);
			}
			catch (Exception e) when (e is GitException or IOException or UnauthorizedAccessException)
			{
				throw new CommitException(repo, e);
			}
		}

		return steps;
	}

	static List<StatusEntry> ChangesToCommit(RepoState state, StageChoice choice)
	{
		IEnumerable<StatusEntry> changes = state.Staged;
		if (choice.Unstaged) changes = changes.Concat(state.Unstaged);
		if (choice.Untracked) changes = changes.Concat(state.Untracked);
		// A file with both staged and unstaged changes appears in both lists.
		return changes.DistinctBy(e => e.Path).ToList();
	}

	/// <summary>The Dependencies.json a repo should have, compared with the one committed at HEAD.</summary>
	static (DependencyFileChange change, string? text) PlanDependencyFile(RepoPlan plan, RepoState state, Dictionary<string, string> hashes)
	{
		var repo = state.Repo;
		var committed = Git.ShowFile(repo.Path, "HEAD", DependencyFile.FileName);

		if (repo.DependsOn.Count == 0)
			return (committed is null ? DependencyFileChange.None : DependencyFileChange.Delete, null);

		// Topological order guarantees every dependency was visited first.
		var text = DependencyFile.Render(repo.DependsOn.Select(d =>
			KeyValuePair.Create(DependencyFile.Key(repo, plan.Repos[d]), hashes[d])));

		var change = committed is null ? DependencyFileChange.Create
			: committed == text ? DependencyFileChange.Unchanged
			: DependencyFileChange.Update;
		return (change, text);
	}

	static bool IsDependencyFile(StatusEntry e) => e.Path == DependencyFile.FileName;

	/// <summary>States in which committing the repo is unsafe or impossible.</summary>
	static List<string> CheckReady(string repo, RepoStatus status)
	{
		var problems = new List<string>();

		if (Git.OperationInProgress(repo) is string op)
			problems.Add($"a {op} is in progress");
		if (status.Entries.Any(e => e.Kind == 'u'))
			problems.Add("there are unresolved conflicts");
		if (status.Branch is null)
			problems.Add("HEAD is detached");
		if (status.Head is null)
			problems.Add("the branch has no commits yet");

		return problems;
	}
}
