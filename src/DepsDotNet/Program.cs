using System.Diagnostics.CodeAnalysis;
using DepsDotNet;

const string Usage = """
	Usage: DepsDotNet <solution (.sln/.slnx) | project file | project.assets.json> (-m <message> | --dry-run) [--force]

	  -m, --message <msg>  Commit each repo that has changes, in dependency order, with this message.
	  --dry-run            Show what would be committed without changing anything.
	  --force              Stage all changes, including untracked files, without asking.

	  Without --force, each repo with unstaged changes or untracked files is asked about (y/n/a)
	  before anything is committed; a aborts the whole run.
	""";

string? input = null, message = null;
bool dryRun = false, force = false;

for (int i = 0; i < args.Length; i++)
{
	switch (args[i])
	{
		case "--dry-run": dryRun = true; break;
		case "--force": force = true; break;
		case "-m" or "--message":
			if (++i >= args.Length) Fail($"{args[i - 1]} needs a commit message.\n\n{Usage}");
			message = args[i];
			break;
		case var a when a.StartsWith('-') || input is not null:
			Fail($"Unexpected argument '{a}'.\n\n{Usage}");
			break;
		default:
			input = args[i];
			break;
	}
}

if (input is null) Fail(Usage);
if (!dryRun && message is null) Fail($"Pass -m <message> to commit, or --dry-run to preview.\n\n{Usage}");

var plan = RepoPlan.Build(ProjectGraph.Load(input));
var states = CommitRunner.Inspect(plan);
var blocked = states.Where(s => s.Problems.Count > 0).ToList();

if (dryRun)
{
	// No questions: preview as if every question is answered n, and flag where an answer of y would change things.
	var steps = CommitRunner.DryRun(plan, states, _ => force ? StageChoice.All : StageChoice.None);
	var ifYes = force ? steps : CommitRunner.DryRun(plan, states, _ => StageChoice.All);
	var decisions = force ? 0 : states.Sum(s => (s.Unstaged.Count > 0 ? 1 : 0) + (s.Untracked.Count > 0 ? 1 : 0));

	Console.WriteLine($"Dry run: {steps.Count(s => s.Committed)} of {steps.Count} repos would commit, in this order.");
	if (decisions > 0)
		Console.WriteLine($"A real run will ask {decisions} question(s) first. This preview assumes every answer is n.");
	Console.WriteLine();

	for (int i = 0; i < steps.Count; i++)
		PrintDryRunStep(i + 1, steps[i], ifYes[i], force);

	if (blocked.Count > 0)
	{
		Console.WriteLine($"A real run would stop before committing anything: {string.Join(", ", blocked.Select(s => s.Repo.Name))} {(blocked.Count == 1 ? "has" : "have")} problems listed above.");
		Environment.Exit(1);
	}
	return;
}

// Every repo must be ready before anything is asked or committed.
if (blocked.Count > 0)
{
	Console.Error.WriteLine("Cannot commit; nothing was changed.\n");
	foreach (var s in blocked)
	{
		Console.Error.WriteLine($"{s.Repo.Name}\t{s.Repo.Path}");
		foreach (var p in s.Problems) Console.Error.WriteLine($"  ! {p}");
	}
	Environment.Exit(1);
}

// Confirmation pass: visit every repo and collect its choices before anything is committed.
var choices = new Dictionary<RepoNode, StageChoice>();
foreach (var s in states)
{
	if (force) { choices[s.Repo] = StageChoice.All; continue; }

	var stageUnstaged = s.Unstaged.Count > 0 && Ask(s, s.Unstaged, $"! {s.Repo.Name} has unstaged changes. Do you want to stage them? y/n/a ");
	var stageUntracked = s.Untracked.Count > 0 && Ask(s, s.Untracked, $"! {s.Repo.Name} has untracked files. Do you want to track and stage them? y/n/a ");
	choices[s.Repo] = new StageChoice(stageUnstaged, stageUntracked);
}

Console.WriteLine($"Committing {states.Count} repos in dependency order.\n");
int n = 0;
try
{
	var steps = CommitRunner.Commit(plan, states, s => choices[s.Repo], message!, step => PrintCommitStep(++n, step));
	Console.WriteLine($"Done: {steps.Count(s => s.Committed)} of {steps.Count} repos committed.");
}
catch (CommitException e)
{
	Console.Error.WriteLine($"{e.Message}\n");
	Console.Error.WriteLine(n == 0
		? "No repos were committed."
		: $"The {n} repo(s) above are committed. Fix the problem and rerun the same command; committed repos will not commit again.");
	Environment.Exit(1);
}

static void PrintDryRunStep(int number, RepoStep s, RepoStep ifYes, bool force)
{
	var state = s.State;
	var name = state.Repo.Name;
	Console.WriteLine($"{number}. {name}\t{state.Repo.Path}");
	Console.WriteLine($"   HEAD:     {(state.Status.Head is null ? "no commits" : Short(state.Status.Head))} on {state.Status.Branch ?? "(detached)"}");
	Console.WriteLine($"   projects: {string.Join(", ", state.Repo.Projects.Select(p => p.Name))}");

	PrintEntries($"would commit ({s.ToCommit.Count}):", s.ToCommit);

	if (!force && state.Unstaged.Count > 0)
	{
		Console.WriteLine($"   Caution: {name} has unstaged changes which will require a decision.");
		foreach (var e in state.Unstaged) Console.WriteLine($"     {e.Code} {e.Path}");
		Console.WriteLine("     (y: stage and commit them, n: leave them out of the commit, a: abort the whole run)");
	}
	if (!force && state.Untracked.Count > 0)
	{
		Console.WriteLine($"   Caution: {name} has untracked files which will require a decision.");
		foreach (var e in state.Untracked) Console.WriteLine($"     ?? {e.Path}");
		Console.WriteLine("     (y: track and commit them, n: leave them untracked, a: abort the whole run;");
		Console.WriteLine("      or add them to .gitignore if they should never be committed)");
	}

	PrintDependencyFile(s, "would be ");

	foreach (var p in state.Problems) Console.WriteLine($"   PROBLEM: {p}");

	Console.WriteLine((s.Committed, ifYes.Committed) switch
	{
		(true, _) => "   -> would commit",
		(false, true) => $"   -> nothing to commit if you answer n; would commit if you answer y to questions here or in repos it depends on",
		(false, false) => $"   -> nothing to commit; dependents record {Short(s.Hash)}",
	});
	Console.WriteLine();
}

static void PrintCommitStep(int number, RepoStep s)
{
	var state = s.State;
	Console.WriteLine($"{number}. {state.Repo.Name}\t{state.Repo.Path}");
	Console.WriteLine($"   projects: {string.Join(", ", state.Repo.Projects.Select(p => p.Name))}");

	PrintEntries($"committed ({s.ToCommit.Count}):", s.ToCommit);
	if (!s.Choice.Unstaged) PrintEntries($"unstaged, not committed ({state.Unstaged.Count}):", state.Unstaged);
	if (!s.Choice.Untracked) PrintEntries($"untracked, not committed ({state.Untracked.Count}):", state.Untracked);

	PrintDependencyFile(s, "");

	Console.WriteLine(s.Committed
		? $"   -> committed {Short(s.Hash)}"
		: $"   -> nothing to commit; dependents record {Short(s.Hash)}");
	Console.WriteLine();
}

static void PrintEntries(string heading, List<StatusEntry> entries)
{
	if (entries.Count == 0) return;
	Console.WriteLine($"   {heading}");
	foreach (var e in entries) Console.WriteLine($"     {e.Code} {e.Path}");
}

static void PrintDependencyFile(RepoStep s, string verb)
{
	switch (s.FileChange)
	{
		case DependencyFileChange.Unchanged:
			Console.WriteLine($"   {DependencyFile.FileName}: unchanged");
			break;
		case DependencyFileChange.Delete:
			Console.WriteLine($"   {DependencyFile.FileName}: {verb}deleted (no dependencies)");
			break;
		case DependencyFileChange.Create or DependencyFileChange.Update:
			Console.WriteLine($"   {DependencyFile.FileName}: {verb}{(s.FileChange == DependencyFileChange.Create ? "created" : "updated")}");
			foreach (var line in s.DependencyFileText!.TrimEnd('\n').Split('\n')) Console.WriteLine($"     {line}");
			break;
	}
}

/// <summary>Lists the files and asks; y and n return, a ends the run before anything is committed.</summary>
static bool Ask(RepoState state, List<StatusEntry> entries, string question)
{
	Console.WriteLine($"{state.Repo.Name}\t{state.Repo.Path}");
	foreach (var e in entries) Console.WriteLine($"    {e.Code} {e.Path}");

	while (true)
	{
		Console.Write(question);
		var answer = Console.ReadLine();
		if (answer is null) Fail("\nNo answer (input closed); nothing was committed. Stage the changes yourself, or use --force.");
		switch (answer.Trim().ToLowerInvariant())
		{
			case "y" or "yes": Console.WriteLine(); return true;
			case "n" or "no": Console.WriteLine(); return false;
			case "a" or "abort": Fail("Aborted; nothing was committed."); break;
		}
	}
}

static string Short(string hash) => hash[..Math.Min(10, hash.Length)];

[DoesNotReturn]
static void Fail(string message)
{
	Console.Error.WriteLine(message);
	Environment.Exit(1);
}
