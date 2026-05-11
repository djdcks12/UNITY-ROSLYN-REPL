using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RoslynRepl.Editor.Core
{
    public enum InstanceCategory
    {
        All,
        MonoBehaviour,
        ScriptableObject,
        Singleton,
    }

    public class InstanceEntry
    {
        public object Value;                  // the live instance — Unity Object or plain C# object
        public Type DeclaredType;             // C# type of the entry
        public string DisplayName;            // GO/asset/instance name
        public string TypeName;               // short type name (e.g. "PopupMinimap")
        public string SubLabel;               // scene name / "asset" / "Singleton"
        public InstanceCategory Category;
        public bool IsActive;                 // active in hierarchy (MB) / true otherwise

        // Convenience for Unity-Object-typed entries.
        public UnityEngine.Object UnityObject => Value as UnityEngine.Object;
    }

    /// <summary>
    /// Collects user-side instances the REPL window can browse: scene
    /// MonoBehaviours, ScriptableObject assets, and types exposing a static
    /// <c>Instance</c> singleton accessor. Filters out Unity-shipped types so
    /// the list stays focused on what the user wrote.
    /// </summary>
    public static class InstanceLocator
    {
        public static List<InstanceEntry> Find(
            InstanceCategory category,
            string filter,
            int maxResults = 200)
        {
            if (maxResults <= 0) return new List<InstanceEntry>();

            // "All" includes every browseable source the window exposes. The
            // singleton scanner uses cached reflection metadata, so the first
            // All refresh after a domain reload may do more work, but users
            // expect singleton-style managers to appear in the catch-all view.
            IEnumerable<InstanceEntry> pool = category switch
            {
                InstanceCategory.MonoBehaviour    => FindMonoBehaviours(),
                InstanceCategory.ScriptableObject => FindScriptableObjects(),
                InstanceCategory.Singleton        => SingletonScanner.Find(),
                _                                 => FindMonoBehaviours()
                                                       .Concat(FindScriptableObjects())
                                                       .Concat(SingletonScanner.Find()),
            };

            // Bounded fast path (issue #25 PR review). The previous
            // implementation always ran a full LINQ OrderByDescending
            // / ThenBy / ThenBy across every materialized candidate
            // and only `.Take(maxResults).ToList()` at the very end —
            // so a Refresh in a project with thousands of MBs sorted
            // all of them just to return the top 200. Splitting the
            // path lets the bounded case stop iterating once it has
            // enough Active rows: Active entries are collected first,
            // and the moment the Active bucket reaches `maxResults`
            // we break the enumerator and skip the Inactive walk
            // entirely. Per-bucket sort is then over at most
            // `maxResults` entries instead of the full pool.
            //
            // The unbounded path (Load more in the UI passes
            // int.MaxValue) keeps the original LINQ shape so the
            // ordering match between the bounded preview and the
            // full list is byte-identical for the prefix.
            if (maxResults < int.MaxValue)
                return FindBounded(pool, filter, maxResults);

            return FindUnbounded(pool, filter);
        }

        private static List<InstanceEntry> FindBounded(
            IEnumerable<InstanceEntry> pool,
            string filter,
            int maxResults)
        {
            // Partial top-K: walk every candidate once, but maintain
            // only a sorted list of size `maxResults`. Earlier draft
            // tried to break out of the enumerator early once the
            // Active bucket filled — that was strictly faster but
            // produced a cross-category bias (the All category
            // walked MonoBehaviours first, hit the cap, and never
            // saw Singletons / ScriptableObjects whose type names
            // would have sorted earlier). The PR reviewer flagged
            // exactly this divergence, so we eat the full
            // enumeration but cap the *insert* cost: each candidate
            // either gets dropped immediately (it would land past
            // index `maxResults` in the global ordering) or
            // BinarySearch-inserted into the sorted prefix and
            // truncated. With a 200 cap and a project of N
            // candidates, the work is roughly O(N · log 200 + N ·
            // 200) for the worst-case insert shift, vs. the
            // baseline OrderByDescending / ThenBy / ThenBy which
            // still ran a full O(N log N) sort over every entry.
            //
            // Stability tie-breaker (PR review followup): each
            // candidate is wrapped in a RankedEntry that carries
            // its filter-pass enumeration ordinal, and the comparer
            // uses that ordinal as a fourth key. LINQ's OrderBy /
            // ThenBy chain is a stable sort — same-key entries
            // stay in their input order — but BinarySearch is not
            // stable: it returns *any* index whose comparer returns
            // 0, so a later duplicate row could splice in front of
            // an earlier one. With the ordinal tie-breaker every
            // RankedEntry is unique, BinarySearch lands in a
            // deterministic position, and the bounded prefix
            // matches `unbounded[0..maxResults]` byte-for-byte —
            // including in projects with multiple instances of the
            // same MonoBehaviour type on identically-named GOs.
            var sorted = new List<RankedEntry>(maxResults + 1);
            bool needFilter = !string.IsNullOrEmpty(filter);
            string f = filter;
            int ordinal = 0;
            foreach (var e in pool)
            {
                if (needFilter && !MatchesFilter(e, f)) continue;
                var ranked = new RankedEntry { Entry = e, Ordinal = ordinal++ };
                InsertSortedCapped(sorted, ranked, maxResults);
            }

            var result = new List<InstanceEntry>(sorted.Count);
            foreach (var r in sorted) result.Add(r.Entry);
            return result;
        }

        private static List<InstanceEntry> FindUnbounded(
            IEnumerable<InstanceEntry> pool,
            string filter)
        {
            if (!string.IsNullOrEmpty(filter))
            {
                var f = filter;
                pool = pool.Where(e => MatchesFilter(e, f));
            }

            return pool
                .OrderByDescending(e => e.IsActive)
                .ThenBy(e => e.TypeName, StringComparer.Ordinal)
                .ThenBy(e => e.DisplayName, StringComparer.Ordinal)
                .ToList();
        }

        private static bool MatchesFilter(InstanceEntry e, string f)
        {
            return (e.TypeName    != null && e.TypeName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                || (e.DisplayName != null && e.DisplayName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Wrapper that carries the candidate's filter-pass ordinal
        // alongside the entry. The ordinal exists purely as a
        // stable-sort tie-breaker for FindBounded — see the comment
        // there for why it has to land in the comparer.
        private sealed class RankedEntry
        {
            public InstanceEntry Entry;
            public int Ordinal;
        }

        // Comparer that mirrors the unbounded LINQ chain
        // (OrderByDescending IsActive → ThenBy TypeName ordinal →
        // ThenBy DisplayName ordinal) plus an Ordinal asc tie-
        // breaker. The first three keys reproduce the unbounded
        // path's ordering for distinct rows; the fourth key makes
        // the comparison strict so BinarySearch can't surface a
        // non-deterministic insert index for two entries that
        // collide on (IsActive, TypeName, DisplayName) — without
        // it, a later same-key row could land in front of an
        // earlier same-key row and the bounded preview would
        // diverge from `unbounded[0..max]`. Used by both
        // BinarySearch and the early-skip check inside
        // InsertSortedCapped so the "would I beat the worst kept
        // entry?" decision honors the same total order.
        private static readonly IComparer<RankedEntry> _orderComparer =
            Comparer<RankedEntry>.Create((a, b) =>
            {
                var ea = a.Entry;
                var eb = b.Entry;
                // bool descending: true (Active) sorts before false.
                int ia = ea.IsActive ? 0 : 1;
                int ib = eb.IsActive ? 0 : 1;
                int c = ia.CompareTo(ib);
                if (c != 0) return c;
                c = string.CompareOrdinal(ea.TypeName ?? string.Empty, eb.TypeName ?? string.Empty);
                if (c != 0) return c;
                c = string.CompareOrdinal(ea.DisplayName ?? string.Empty, eb.DisplayName ?? string.Empty);
                if (c != 0) return c;
                // Stability tie-breaker: matches LINQ stable-sort
                // semantics — earlier in the input wins when the
                // visible sort keys are equal.
                return a.Ordinal.CompareTo(b.Ordinal);
            });

        private static void InsertSortedCapped(List<RankedEntry> sorted, RankedEntry r, int max)
        {
            // Early skip: if we already have `max` entries and this
            // one would sort at-or-after the worst kept entry, it
            // can never end up in the final result. With the
            // ordinal tie-breaker the comparer never returns 0 for
            // distinct RankedEntry instances, so `>= 0` here is
            // equivalent to `> 0` for the visible keys plus
            // "ordinal is later" — both cases mean "this row would
            // land outside the prefix".
            if (sorted.Count >= max && _orderComparer.Compare(r, sorted[sorted.Count - 1]) >= 0)
                return;

            int idx = sorted.BinarySearch(r, _orderComparer);
            if (idx < 0) idx = ~idx;
            sorted.Insert(idx, r);
            // Trim from the tail to keep the prefix invariant.
            if (sorted.Count > max) sorted.RemoveAt(sorted.Count - 1);
        }

        private static IEnumerable<InstanceEntry> FindMonoBehaviours()
        {
            var all = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
            foreach (var mb in all)
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (IsHiddenAssembly(t)) continue;
                // Editor-only / hidden objects (preview scenes, internal helpers)
                if ((mb.hideFlags & (HideFlags.HideAndDontSave | HideFlags.HideInHierarchy)) != 0)
                {
                    if (!IsUserMonoBehaviour(t)) continue;
                }

                string subLabel = "(no scene)";
                bool active = false;
                if (mb.gameObject != null)
                {
                    var scene = mb.gameObject.scene;
                    subLabel = scene.IsValid() ? scene.name : "(prefab/asset)";
                    active = mb.gameObject.activeInHierarchy;
                }

                yield return new InstanceEntry
                {
                    Value = mb,
                    DeclaredType = t,
                    DisplayName = mb.name,
                    TypeName = TypeFormatter.Short(t),
                    SubLabel = subLabel,
                    Category = InstanceCategory.MonoBehaviour,
                    IsActive = active,
                };
            }
        }

        private static IEnumerable<InstanceEntry> FindScriptableObjects()
        {
            var all = Resources.FindObjectsOfTypeAll<ScriptableObject>();
            foreach (var so in all)
            {
                if (so == null) continue;
                var t = so.GetType();
                if (IsHiddenAssembly(t)) continue;

                yield return new InstanceEntry
                {
                    Value = so,
                    DeclaredType = t,
                    DisplayName = string.IsNullOrEmpty(so.name) ? t.Name : so.name,
                    TypeName = TypeFormatter.Short(t),
                    SubLabel = "ScriptableObject",
                    Category = InstanceCategory.ScriptableObject,
                    IsActive = true,
                };
            }
        }

        // True for Unity-shipped or framework assemblies whose objects we want
        // to hide from the browser. Mirrors the property-skip rule used by
        // SimpleObjectSerializer.
        private static bool IsHiddenAssembly(Type t)
        {
            if (t == null) return true;
            var asm = t.Assembly.GetName().Name;
            if (string.IsNullOrEmpty(asm)) return false;
            return asm.StartsWith("UnityEngine", StringComparison.Ordinal)
                || asm.StartsWith("UnityEditor", StringComparison.Ordinal)
                || asm.StartsWith("Unity.",       StringComparison.Ordinal)
                || asm.StartsWith("System",       StringComparison.Ordinal)
                || asm.StartsWith("Microsoft.",   StringComparison.Ordinal)
                || asm.StartsWith("Mono.",        StringComparison.Ordinal)
                || asm.StartsWith("netstandard",  StringComparison.Ordinal)
                || asm == "mscorlib"
                || asm == "RoslynRepl.Editor";
        }

        // Override to let user MBs through even if they have unusual hideFlags
        // (some pooling systems hide their objects in hierarchy).
        private static bool IsUserMonoBehaviour(Type t) => !IsHiddenAssembly(t);
    }
}
