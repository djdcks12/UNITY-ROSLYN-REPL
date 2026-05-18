using System;
using System.Collections.Generic;
using UnityEngine.UIElements;
using RoslynRepl.Editor.Core;

namespace RoslynRepl.Editor.UI.Find
{
    /// <summary>
    /// <see cref="IReplFindable"/> for the Output panel. Mirrors
    /// every log line and result tree that <see cref="RoslynRepl.Editor.UI.RoslynReplWindow"/>
    /// appends to <c>_outputContent</c> so the Find overlay can scan
    /// the *data* (not the virtualized VisualElement children) for
    /// matches.
    ///
    /// The window calls <see cref="TrackLogLine"/> for every plain
    /// <c>Label</c> it appends and <see cref="TrackResultTree"/> for
    /// every <c>MultiColumnTreeView</c> it builds; <see cref="Clear"/>
    /// drops the bookkeeping when the panel is cleared (Run start,
    /// Reset Project Data). <see cref="RaiseRebuilt"/> tells the
    /// controller "I'm done mutating for now — recompute hits".
    /// </summary>
    internal sealed class OutputFindable : IReplFindable
    {
        private readonly ScrollView _outerScroll;
        private readonly List<OutputItem> _items = new();

        public event Action ContentRebuilt;

        public OutputFindable(ScrollView outerScroll)
        {
            _outerScroll = outerScroll;
        }

        public void TrackLogLine(Label label)
        {
            if (label != null) _items.Add(new OutputItem { Label = label });
        }

        public void TrackResultTree(MultiColumnTreeView tv, OutputTreeIndex index)
        {
            if (tv == null || index == null) return;
            _items.Add(new OutputItem { Tree = tv, TreeIndex = index });
        }

        public void Clear() => _items.Clear();

        public void RaiseRebuilt() => ContentRebuilt?.Invoke();

        public void CollectMatches(string query, List<ReplFindHit> hits)
        {
            if (string.IsNullOrEmpty(query) || _items.Count == 0) return;
            var q = query;
            foreach (var item in _items)
            {
                if (item.Label != null) CollectFromLabel(item.Label, q, hits);
                else if (item.Tree != null) CollectFromTree(item.Tree, item.TreeIndex, q, hits);
            }
        }

        private void CollectFromLabel(Label label, string query, List<ReplFindHit> hits)
        {
            string text = label.text;
            if (string.IsNullOrEmpty(text)) return;
            if (text.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) return;

            var preview = Truncate(text, 80);
            var capturedLabel = label;
            // Capture outerScroll to a local so the lambda doesn't
            // hold the field reference (the window may rebuild and
            // null the field out before the user reaches this hit).
            var scroll = _outerScroll;
            hits.Add(new ReplFindHit
            {
                Source = "Output",
                Label = $"Output > log: {preview}",
                ScrollIntoView = () =>
                {
                    if (capturedLabel?.parent == null) return;
                    try { scroll?.ScrollTo(capturedLabel); }
                    catch { /* destroyed mid-find — UI rebuilt while overlay was open */ }
                },
                SetCurrent = () =>
                {
                    if (capturedLabel?.parent != null)
                        capturedLabel.AddToClassList("rr-find-hit--current");
                },
                UnsetCurrent = () =>
                {
                    if (capturedLabel?.parent != null)
                        capturedLabel.RemoveFromClassList("rr-find-hit--current");
                },
            });
        }

        private void CollectFromTree(MultiColumnTreeView tv, OutputTreeIndex index, string query, List<ReplFindHit> hits)
        {
            if (tv == null || index == null) return;
            var q = query;
            var scroll = _outerScroll;

            // Walk the data tree (not VisualElements) so off-screen
            // virtualized rows still register as hits. Each NodeRef
            // already carries the id-chain back to root, so on
            // navigation we can expand ancestors + scroll.
            foreach (var nref in index.Refs)
            {
                var node = nref.Node;
                if (node == null) continue;
                if (!NodeMatchesQuery(node, q)) continue;

                var path = BuildPathString(nref);
                var preview = Truncate(node.Preview ?? string.Empty, 60);
                var label = string.IsNullOrEmpty(preview)
                    ? $"Output > {path}"
                    : $"Output > {path} = {preview}";
                var capturedTv = tv;
                var capturedRef = nref;
                hits.Add(new ReplFindHit
                {
                    Source = "Output",
                    Label = label,
                    ScrollIntoView = () =>
                    {
                        try
                        {
                            // 1. Expand the chain root → parent so
                            //    the target row is materialised.
                            var ancestorBuf = new List<int>(8);
                            capturedRef.CollectAncestorIds(ancestorBuf);
                            foreach (var ancId in ancestorBuf)
                                capturedTv.ExpandItem(ancId);
                            // 2. Scroll the outer ScrollView so the
                            //    TreeView itself is on screen.
                            if (capturedTv?.parent != null && scroll != null)
                                scroll.ScrollTo(capturedTv);
                            // 3. Scroll inside the TreeView to the
                            //    specific row and mark it as
                            //    selected — selection styling is
                            //    Unity's built-in "current" cue.
                            capturedTv.SetSelectionById(capturedRef.Id);
                            capturedTv.ScrollToItemById(capturedRef.Id);
                        }
                        catch
                        {
                            // The tree may have been torn down by a
                            // racing rebuild (Run finishing while
                            // the overlay holds the hit). The next
                            // ContentRebuilt fire will refresh hits.
                        }
                    },
                    SetCurrent   = null, // selection visual is enough for trees
                    UnsetCurrent = null,
                });
            }
        }

        private static bool NodeMatchesQuery(ReplValueNode node, string query)
        {
            if (Contains(node.Name, query)) return true;
            if (Contains(node.Preview, query)) return true;
            if (Contains(node.TypeName, query)) return true;
            return false;
        }

        private static bool Contains(string haystack, string needle)
            => !string.IsNullOrEmpty(haystack)
               && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string BuildPathString(NodeRef nref)
        {
            // Walk parents to root, then reverse to get a
            // root-first dotted path. Skip the synthetic "(result)"
            // root so the displayed path reads naturally
            // ("GameManager.hp") rather than "(result).GameManager.hp".
            var parts = new List<string>(8);
            for (var p = nref; p != null; p = p.Parent)
            {
                string name = p.Node?.Name;
                if (!string.IsNullOrEmpty(name)) parts.Add(name);
            }
            parts.Reverse();
            // Drop the leading "(result)" sentinel inserted by
            // SimpleObjectSerializer.ToTree if present — purely
            // cosmetic, the user thinks in terms of their own data.
            if (parts.Count > 0 && parts[0] == "(result)") parts.RemoveAt(0);
            return parts.Count == 0 ? "(result)" : string.Join(".", parts);
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        private struct OutputItem
        {
            public Label Label;
            public MultiColumnTreeView Tree;
            public OutputTreeIndex TreeIndex;
        }
    }
}
