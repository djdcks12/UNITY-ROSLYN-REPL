using System;

namespace RoslynRepl.Editor.UI.Find
{
    /// <summary>
    /// One match the Find overlay can navigate to. Each panel
    /// (<see cref="IReplFindable"/>) constructs its own hits with the
    /// callbacks pointing at the right <c>VisualElement</c> /
    /// <c>MultiColumnTreeView</c> / row block. The controller treats
    /// hits as opaque tokens — it only knows how to advance the
    /// current index and ask the active hit to bring itself into view.
    ///
    /// All callbacks are nullable. The controller no-ops on null so a
    /// panel can omit, say, <see cref="SetCurrent"/> when the
    /// underlying widget already shows visible selection state
    /// (a <c>MultiColumnTreeView</c> selection, for example).
    /// </summary>
    // public because IReplFindable.CollectMatches signature exposes
    // it through public WatchPanelView / MethodPatchView. The class
    // is sealed so consumers can't subclass — it's pure data with
    // callback fields, not an extension point.
    public sealed class ReplFindHit
    {
        /// <summary>Short panel-of-origin label — "Output", "Watch",
        /// "Patches". Surfaces in the overlay tooltip / hover so the
        /// user knows which pane a hit came from when navigating
        /// across heterogeneous sources.</summary>
        public string Source;

        /// <summary>Human-readable description of the hit, e.g.
        /// <c>"GameManager.hp = 42"</c> or <c>"watch: Time.frameCount"</c>.
        /// Used by the overlay's hover tooltip on the counter so the
        /// user can preview the next hit without jumping.</summary>
        public string Label;

        /// <summary>Bring the hit into view: expand any ancestor
        /// tree rows, scroll the enclosing ScrollView, scroll the
        /// inner TreeView. Called whenever the controller moves
        /// <see cref="ReplFindController.CurrentIndex"/> onto this
        /// hit.</summary>
        public Action ScrollIntoView;

        /// <summary>Apply the "current hit" highlight (a brighter
        /// background than the secondary all-hits highlight). Called
        /// right after <see cref="ScrollIntoView"/>; called again
        /// (with the other hit's <see cref="UnsetCurrent"/> first)
        /// every time the user hits Next / Prev.</summary>
        public Action SetCurrent;

        /// <summary>Remove the "current hit" highlight. Called when
        /// the user navigates away from this hit or closes the
        /// overlay.</summary>
        public Action UnsetCurrent;
    }
}
