using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DepsDotNet;

enum NodeColor
{
	White,
	Gray,
	Black,
	Cyclic
}

public sealed class CycleException<T>(IReadOnlyList<T> cycle)
	: InvalidOperationException($"Dependency cycle: {string.Join(" -> ", cycle)}")
{
	/// <summary>The nodes of the cycle, starting and ending with the same node.</summary>
	public IReadOnlyList<T> Cycle { get; } = cycle;
}

internal interface IGraphDependencies<T>
{
	IEnumerable<T> GetDependencies(T ofElement);
}


internal class TopoSorter<T> where T : notnull
{
	bool nil;
	Dictionary<T, NodeColor> colors = new();
	Dictionary<T, (int index, int size)> sizes = new();
	List<T> Cycles = new();
	Stack<T> Thread = new();
	List<T> TopologicalSort = new();
	IGraphDependencies<T> dependencies;

	internal T this[int i]
	{
		get
		{
			return TopologicalSort[i];
		}
	}


	internal List<T> Sorted => TopologicalSort.ToList();

	internal int SortCount => TopologicalSort.Count;

	internal void Sort(T root, Func<T, bool>? filter = null!)
	{
		Recurse(root,filter);
		Thread.Clear();
	}

	private NodeColor Recurse(T element,Func<T,bool>? filter = null!)
	{
		ref NodeColor dictcolor = ref CollectionsMarshal.GetValueRefOrAddDefault(colors, element, out _);

		int size = TopologicalSort.Count ;

		if (dictcolor is NodeColor.White) Thread.Push(element);
		else if (dictcolor is NodeColor.Black || dictcolor is NodeColor.Gray || dictcolor is NodeColor.Cyclic) return dictcolor;

		dictcolor = NodeColor.Gray;

		var returncolor = dictcolor;

		// this is the last safe point that a ref accessor to the dictionary can be used

		var deps = dependencies.GetDependencies(element);

		foreach (var refID in deps)
		{
			NodeColor refcolor;

			if (filter is Func<T, bool> f && f(refID))
			{
				var childexists = colors.TryGetValue(refID, out var childcolor);

				if (childexists)
				{
					refcolor = childcolor;
				}
				else
				{
					TopologicalSort.Add(refID);
					colors[refID] = NodeColor.Black;
					continue;
				}
			}
			else
			{
					refcolor = Recurse(refID);
			}

			if (refcolor is NodeColor.Gray || refcolor is NodeColor.Cyclic)
			{
				// Thread holds the current DFS path, top first; the cycle runs from refID down to here.
				var path = Thread.Reverse().ToList();
				var cycle = path.Skip(path.IndexOf(refID)).Append(refID).ToList();
				throw new CycleException<T>(cycle);
			}
		}

		if (returncolor is NodeColor.Cyclic)
		{
			Cycles.Add(element);
			Thread.Pop();
			return returncolor;
		}
		else
		{
			returncolor = NodeColor.Black;
			int i = TopologicalSort.Count;
			size =  i - size;
			colors[element] = returncolor;
			sizes[element] = (i, size);
			TopologicalSort.Add(element);
			Thread.Pop();
			return returncolor;
		}

	}

	internal ReadOnlySpan<T> Slice(T element)
	{
		var lookup = sizes[element];

		var start = lookup.index - lookup.size;

		return CollectionsMarshal.AsSpan(TopologicalSort).Slice(start, lookup.size+1);

	}


	internal TopoSorter(IGraphDependencies<T> dependencies)
	{
		this.dependencies = dependencies;
	}

}