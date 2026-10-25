using System.Diagnostics;
using System.Text;

namespace DepsDotNet;

public sealed record GitResult(int ExitCode, string Output, string Error);

public sealed class GitException(string message) : Exception(message);

/// <summary>One entry of <c>git status --porcelain=v2</c>.</summary>
/// <param name="Kind">'1' changed, '2' renamed or copied, 'u' unmerged, '?' untracked.</param>
/// <param name="Code">The two-letter XY status (index, worktree), or "??" for untracked.</param>
public sealed record StatusEntry(char Kind, string Code, string Path)
{
	public bool IsTracked => Kind is '1' or '2' or 'u';

	/// <summary>The index differs from HEAD, so a commit would include this change.</summary>
	public bool IsStaged => Kind is '1' or '2' && Code[0] != '.';

	/// <summary>The working tree differs from the index; a commit would leave this change out.</summary>
	public bool IsUnstaged => Kind is '1' or '2' && Code[1] != '.';
}

/// <param name="Head">The HEAD commit, or null on a branch with no commits yet.</param>
/// <param name="Branch">The checked-out branch, or null when HEAD is detached.</param>
public sealed record RepoStatus(string? Head, string? Branch, List<StatusEntry> Entries);

/// <summary>
/// Runs the git CLI directly (no shell), so it works wherever git is on PATH and honours the user's config and hooks.
/// Every call uses <c>git -C &lt;repo&gt;</c> and reads machine-readable output or exit codes, never localized text.
/// </summary>
public static class Git
{
	public static GitResult Run(string repo, params string[] args) => RunWithInput(repo, null, args);

	/// <summary>Runs git with <paramref name="input"/> written to its stdin.</summary>
	public static GitResult RunWithInput(string repo, string? input, params string[] args)
	{
		var psi = new ProcessStartInfo("git")
		{
			RedirectStandardInput = input is not null,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
		};
		psi.ArgumentList.Add("-C");
		psi.ArgumentList.Add(repo);
		foreach (var a in args) psi.ArgumentList.Add(a);

		// Read-only commands like status must not take the index lock.
		psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

		Process proc;
		try
		{
			proc = Process.Start(psi)!;
		}
		catch (System.ComponentModel.Win32Exception)
		{
			throw new GitException("git was not found on PATH.");
		}

		using (proc)
		{
			if (input is not null)
			{
				proc.StandardInput.Write(input);
				proc.StandardInput.Close();
			}
			var errorTask = proc.StandardError.ReadToEndAsync();
			var output = proc.StandardOutput.ReadToEnd();
			proc.WaitForExit();
			return new GitResult(proc.ExitCode, output, errorTask.Result);
		}
	}

	/// <summary>Runs git and throws if it fails; returns trimmed stdout.</summary>
	public static string RunChecked(string repo, params string[] args) => RunCheckedWithInput(repo, null, args);

	public static string RunCheckedWithInput(string repo, string? input, params string[] args)
	{
		var result = RunWithInput(repo, input, args);
		if (result.ExitCode != 0)
			throw new GitException($"git {string.Join(' ', args)} failed in '{repo}': {result.Error.Trim()}");
		return result.Output.Trim();
	}

	public static RepoStatus Status(string repo)
	{
		// -z: NUL-separated records, paths unquoted. A rename record is followed by its original path.
		var records = RunChecked(repo, "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all")
			.Split('\0', StringSplitOptions.RemoveEmptyEntries);

		string? head = null, branch = null;
		var entries = new List<StatusEntry>();

		for (int i = 0; i < records.Length; i++)
		{
			var r = records[i];
			switch (r[0])
			{
				case '#':
					if (r.StartsWith("# branch.oid ")) head = r["# branch.oid ".Length..] is var oid && oid != "(initial)" ? oid : null;
					else if (r.StartsWith("# branch.head ")) branch = r["# branch.head ".Length..] is var b && b != "(detached)" ? b : null;
					break;
				case '1':
					entries.Add(new StatusEntry('1', r.Substring(2, 2), r.Split(' ', 9)[8]));
					break;
				case '2':
					entries.Add(new StatusEntry('2', r.Substring(2, 2), r.Split(' ', 10)[9]));
					i++; // original path
					break;
				case 'u':
					entries.Add(new StatusEntry('u', r.Substring(2, 2), r.Split(' ', 11)[10]));
					break;
				case '?':
					entries.Add(new StatusEntry('?', "??", r[2..]));
					break;
			}
		}

		return new RepoStatus(head, branch, entries);
	}

	/// <summary>Stages exactly these repo-relative paths, taken literally (no glob or pathspec magic).</summary>
	public static void StagePaths(string repo, IEnumerable<string> paths)
	{
		var list = string.Concat(paths.Select(p => p + '\0'));
		if (list.Length == 0) return;
		RunCheckedWithInput(repo, list, "--literal-pathspecs", "add", "--pathspec-from-file=-", "--pathspec-file-nul");
	}

	/// <summary>Whether the index differs from HEAD, i.e. a commit would record something.</summary>
	public static bool HasStagedChanges(string repo)
	{
		var result = Run(repo, "diff", "--cached", "--quiet");
		return result.ExitCode switch
		{
			0 => false,
			1 => true,
			_ => throw new GitException($"git diff --cached failed in '{repo}': {result.Error.Trim()}"),
		};
	}

	/// <summary>The content of a file at a revision, or null if it does not exist there.</summary>
	public static string? ShowFile(string repo, string revision, string path)
	{
		var result = Run(repo, "show", $"{revision}:{path}");
		return result.ExitCode == 0 ? result.Output : null;
	}

	/// <summary>The name of a merge, rebase, cherry-pick or revert in progress, or null if none.</summary>
	public static string? OperationInProgress(string repo)
	{
		(string marker, string name)[] markers =
		[
			("MERGE_HEAD", "merge"),
			("rebase-merge", "rebase"),
			("rebase-apply", "rebase"),
			("CHERRY_PICK_HEAD", "cherry-pick"),
			("REVERT_HEAD", "revert"),
		];

		foreach (var (marker, name) in markers)
		{
			// --git-path resolves worktrees and custom git dirs; the result is relative to the repo.
			var path = Path.GetFullPath(RunChecked(repo, "rev-parse", "--git-path", marker), repo);
			if (File.Exists(path) || Directory.Exists(path)) return name;
		}
		return null;
	}
}
