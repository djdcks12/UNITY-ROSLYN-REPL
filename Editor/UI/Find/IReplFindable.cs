using System;
using System.Collections.Generic;

namespace RoslynRepl.Editor.UI.Find
{
    /// <summary>
    /// Contract implemented by every panel that participates in the
    /// Ctrl+F overlay search — Output, Watch, Patches. The controller
    /// asks each registered source for its hits on every query change
    /// and stitches them into a single flat list the overlay can
    /// navigate with Next/Prev.
    ///
    /// Why data-driven (not VisualElement scan):
    /// the Output result tree + the Watch inline tree use
    /// <see cref="UnityEngine.UIElements.MultiColumnTreeView"/>, which
    /// is virtualized — only rows currently in view materialise as
    /// real <c>Label</c> elements. A naïve <c>root.Query&lt;Label&gt;()</c>
    /// walk would miss every off-screen row and the user would Find
    /// fewer hits than the data actually contains. Each panel knows
    /// how to walk its own model (the <see cref="RoslynRepl.Editor.Core.ReplValueNode"/>
    /// tree, the watch expression list, the patch spec registry) and
    /// hand back hits whose <see cref="ReplFindHit.ScrollIntoView"/>
    /// brings the matching row into view on demand.
    /// </summary>
    // public because WatchPanelView / MethodPatchView are public
    // classes that implement this interface — C# requires the
    // interface to be at least as accessible as any class that
    // declares it as an implemented contract.
    public interface IReplFindable
    {
        /// <summary>Raised when this panel's user-visible content was
        /// rebuilt (Output got a new Run, Watch refreshed after a Run,
        /// Patches list updated). The controller subscribes so it can
        /// re-run <see cref="CollectMatches"/> when the underlying data
        /// changes — otherwise the hit list would point at stale rows.</summary>
        event Action ContentRebuilt;

        /// <summary>Append every hit whose name / type / preview /
        /// label contains <paramref name="query"/> (case-insensitive)
        /// to <paramref name="hits"/>. The caller guarantees
        /// <paramref name="query"/> is non-empty.</summary>
        void CollectMatches(string query, List<ReplFindHit> hits);
    }
}
