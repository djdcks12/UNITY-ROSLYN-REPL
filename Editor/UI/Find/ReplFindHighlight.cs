using System;
using System.Text;
using UnityEngine.UIElements;

namespace RoslynRepl.Editor.UI.Find
{
    /// <summary>
    /// Shared character-level highlight machinery used by the Ctrl+F
    /// overlay. Holds the currently-active query string in a static
    /// (so virtualized <c>MultiColumnTreeView</c> bind-cell closures
    /// can read it without threading the controller through every
    /// column factory) and exposes <see cref="Decorate"/>, a small
    /// rich-text wrapper that highlights case-insensitive matches in
    /// place of the previous full-row yellow background.
    ///
    /// Each panel's labels register via <see cref="BindLabelText"/>:
    /// the helper stores the original (undecorated) text and
    /// subscribes the label to <see cref="ActiveQueryChanged"/>, so
    /// the rich-text re-decoration happens every time the user types
    /// without each panel having to reach into its rebuild logic
    /// again. The subscription auto-detaches on
    /// <c>DetachFromPanelEvent</c>, so a rebuild that recreates the
    /// label tree doesn't leak event handlers.
    /// </summary>
    public static class ReplFindHighlight
    {
        /// <summary>
        /// Hook the active <see cref="ReplFindOverlay"/> sets to its
        /// input.Focus() while open and clears on close. Hits that
        /// need to navigate keyboard focus to a <c>TextField</c>
        /// (e.g. Patches body / Type / Method / Params, where the
        /// underlying TextField needs <c>Focus()</c> for
        /// <c>SelectRange()</c> to scroll the field's internal
        /// scroll view to the match) call this immediately after
        /// the focus-stealing pair so keyboard focus snaps back to
        /// the Find overlay's input on the same frame — the user
        /// keeps typing the query without interruption, and the
        /// brief native-selection paint on the destination field is
        /// the visible "go to here" cue.
        ///
        /// The static-delegate shape avoids threading the overlay
        /// (or controller) through every <see cref="IReplFindable"/>
        /// implementation, and the null-check makes the hook a
        /// no-op when no overlay is open.
        /// </summary>
        public static Action RequestRefocusInput;

        // Rich-text wrappers. UI Toolkit's TextCore supports <color>
        // and <b>; <mark> isn't reliable across Editor versions. We
        // emit a bold orange-yellow run for every match — bright
        // enough to scan in a tree column, not so heavy that it
        // dominates surrounding text.
        private const string OpenTag = "<color=#ffd060><b>";
        private const string CloseTag = "</b></color>";

        private static string _activeQuery;

        /// <summary>Currently-active query string. <c>null</c> when
        /// the overlay is closed or the query is empty.</summary>
        public static string ActiveQuery => _activeQuery;

        /// <summary>Raised whenever <see cref="ActiveQuery"/>
        /// changes. Trees subscribe to refresh their bind-cell
        /// output; non-tree labels subscribed via
        /// <see cref="BindLabelText"/> auto-redecorate.</summary>
        public static event Action ActiveQueryChanged;

        internal static void SetActiveQuery(string query)
        {
            var normalised = string.IsNullOrEmpty(query) ? null : query;
            if (string.Equals(normalised, _activeQuery, StringComparison.Ordinal)) return;
            _activeQuery = normalised;
            ActiveQueryChanged?.Invoke();
        }

        /// <summary>
        /// Wrap every case-insensitive occurrence of
        /// <see cref="ActiveQuery"/> inside <paramref name="text"/>
        /// with the highlight rich-text run, preserving the
        /// original casing. Returns the input unchanged when there
        /// is no active query or no match — that keeps callers free
        /// to use <see cref="Decorate"/> as a no-op default in
        /// bind-cell paths.
        /// </summary>
        public static string Decorate(string text)
        {
            var query = _activeQuery;
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return text;
            int firstHit = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (firstHit < 0) return text;

            var sb = new StringBuilder(text.Length + 32);
            int idx = 0;
            int qlen = query.Length;
            while (idx < text.Length)
            {
                int hit = text.IndexOf(query, idx, StringComparison.OrdinalIgnoreCase);
                if (hit < 0)
                {
                    sb.Append(text, idx, text.Length - idx);
                    break;
                }
                if (hit > idx) sb.Append(text, idx, hit - idx);
                sb.Append(OpenTag);
                sb.Append(text, hit, qlen);
                sb.Append(CloseTag);
                idx = hit + qlen;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Bind a non-tree <see cref="Label"/> to the active query
        /// so it re-decorates itself on every
        /// <see cref="ActiveQueryChanged"/>. The label's initial
        /// text is set to <c>Decorate(<paramref name="originalText"/>)</c>
        /// and <see cref="Label.enableRichText"/> is forced on.
        /// Subscription cleanup is automatic on
        /// <c>DetachFromPanelEvent</c> — call this once per row
        /// build and forget.
        /// </summary>
        public static void BindLabelText(Label label, string originalText)
        {
            if (label == null) return;
            label.enableRichText = true;
            label.text = Decorate(originalText);

            void Handler() => label.text = Decorate(originalText);
            ActiveQueryChanged += Handler;
            label.RegisterCallback<DetachFromPanelEvent>(_ => ActiveQueryChanged -= Handler);
        }

        /// <summary>
        /// Sibling to <see cref="BindLabelText"/> for
        /// <see cref="MultiColumnTreeView"/> / other tree widgets
        /// whose visible content rebinds on scroll. The tree's
        /// own <c>bindCell</c> handles per-row text decoration via
        /// <see cref="Decorate"/>; this method just queues a
        /// <c>RefreshItems</c> on every query change so visible
        /// rows re-bind with the new wrapping.
        /// </summary>
        public static void BindTreeRefresh(BaseTreeView tree)
        {
            if (tree == null) return;

            void Handler() => tree.RefreshItems();
            ActiveQueryChanged += Handler;
            tree.RegisterCallback<DetachFromPanelEvent>(_ => ActiveQueryChanged -= Handler);
        }
    }
}
