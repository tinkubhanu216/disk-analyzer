using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Core.Algorithms;

public static class TreeAlgorithms
{
    /// <summary>Visits every node in the subtree of <paramref name="root"/> (including it), depth-first, without recursion.</summary>
    public static void VisitSubtree(ScanTree tree, int root, Action<int> visitor)
    {
        if (root == ScanTree.RootIndex)
        {
            int count = tree.Count;
            for (int i = 0; i < count; i++)
            {
                visitor(i);
            }

            return;
        }

        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            int node = stack.Pop();
            visitor(node);
            for (int c = tree.GetFirstChild(node); c != ScanTree.None; c = tree.GetNextSibling(c))
            {
                stack.Push(c);
            }
        }
    }

    /// <summary>Selects the <paramref name="count"/> nodes with the largest key, sorted descending. O(n log k).</summary>
    public static List<int> TopN(ScanTree tree, int root, int count, Func<int, bool> predicate, Func<int, long> key)
    {
        if (count <= 0)
        {
            return [];
        }

        var heap = new PriorityQueue<int, long>(count + 1);
        VisitSubtree(tree, root, node =>
        {
            if (!predicate(node))
            {
                return;
            }

            long k = key(node);
            if (heap.Count < count)
            {
                heap.Enqueue(node, k);
            }
            else if (heap.TryPeek(out _, out long min) && k > min)
            {
                heap.EnqueueDequeue(node, k);
            }
        });

        var result = new List<int>(heap.Count);
        while (heap.TryDequeue(out int node, out _))
        {
            result.Add(node);
        }

        result.Reverse();
        return result;
    }

    /// <summary>Children of a node, sorted by size descending.</summary>
    public static List<int> GetChildrenBySize(ScanTree tree, int node)
    {
        var children = new List<int>();
        foreach (int c in tree.GetChildren(node))
        {
            children.Add(c);
        }

        children.Sort((a, b) => tree.GetCountedSize(b).CompareTo(tree.GetCountedSize(a)));
        return children;
    }
}
