using System.Collections.Generic;
using RoslynRepl.Editor.Core;

namespace RoslynRepl.Editor.UI.Find
{
    /// <summary>
    /// Side-table the Output / Watch result-tree builders attach to
    /// their <c>MultiColumnTreeView.userData</c> so the Find overlay
    /// can look up a <see cref="ReplValueNode"/>'s tree id +
    /// ancestor-id chain without re-walking the data on every
    /// keystroke. <see cref="MultiColumnTreeView"/> is virtualized —
    /// off-screen rows don't materialise — so the only reliable way
    /// to scroll-and-highlight a node by Find query is to know its
    /// id and ancestor ids ahead of time. <c>ExpandItem(id)</c> on
    /// every ancestor + <c>ScrollToItemById(targetId)</c> +
    /// <c>SetSelectionById(targetId)</c> then brings the node into
    /// view consistently across Unity versions.
    /// </summary>
    internal sealed class OutputTreeIndex
    {
        public readonly List<NodeRef> Refs = new();
    }

    internal sealed class NodeRef
    {
        public ReplValueNode Node;
        public int Id;
        public NodeRef Parent;

        /// <summary>Walk ancestors root-first, appending their ids
        /// to <paramref name="buffer"/> (does not include this
        /// node's own id). The caller passes the buffer through to
        /// <c>ExpandItem(id)</c> in order so the path from root to
        /// the target node is open before <c>ScrollToItemById</c>
        /// fires.</summary>
        public void CollectAncestorIds(List<int> buffer)
        {
            // Build into a temp list then reverse so we end up
            // root-first. Recursion would be cleaner but trees
            // pulled from deep object graphs can exceed C#'s
            // default stack — the iterative form is safer.
            int start = buffer.Count;
            for (var p = Parent; p != null; p = p.Parent)
                buffer.Add(p.Id);
            buffer.Reverse(start, buffer.Count - start);
        }
    }
}
