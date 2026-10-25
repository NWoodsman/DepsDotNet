using System.Text.Encodings.Web;
using System.Text.Json;

namespace DepsDotNet;

/// <summary>
/// The Dependencies.json written at the root of each repo that depends on other repos:
/// a map of dependency repo (path relative to this repo) to its commit hash.
/// </summary>
public static class DependencyFile
{
	public const string FileName = "Dependencies.json";

	static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
		NewLine = "\n",
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	public static string Key(RepoNode repo, RepoNode dependency) =>
		Path.GetRelativePath(repo.Path, dependency.Path).Replace('\\', '/');

	/// <summary>
	/// Renders the file deterministically (ordinal key order, LF, trailing newline), so an unchanged set of
	/// hashes produces byte-identical output and no commit.
	/// </summary>
	public static string Render(IEnumerable<KeyValuePair<string, string>> hashes)
	{
		var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
		foreach (var (key, hash) in hashes) sorted[key] = hash;
		return JsonSerializer.Serialize(sorted, Options) + "\n";
	}
}
