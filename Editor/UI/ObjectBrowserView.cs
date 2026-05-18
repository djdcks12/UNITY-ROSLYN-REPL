using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using RoslynRepl.Editor.Core;
using RoslynRepl.Editor.Patches;

namespace RoslynRepl.Editor.UI
{
    /// <summary>
    /// Side-panel UI for browsing scene MonoBehaviours, ScriptableObject
    /// assets, and singletons. Rebuilds the underlying list whenever the
    /// category, search filter, or refresh button changes. Emits
    /// <see cref="OnInstanceChosen"/> when the user double-clicks a row
    /// (existing flow, preserved for compatibility), and
    /// <see cref="OnRowAction"/> for the right-click context menu actions
    /// added in #60.
    /// </summary>
    public class ObjectBrowserView
    {
        /// <summary>
        /// Discrete user actions available on a row's context menu.
        /// Host (RoslynReplWindow) maps each one to the same underlying
        /// hooks that drive double-click / the toolbar `_` flow, so the
        /// menu is a discoverability layer rather than a parallel
        /// pipeline.
        /// </summary>
        public enum BrowserRowAction
        {
            /// <summary>Render the value into Output as a tree and bind it to <c>_</c>.</summary>
            Inspect,
            /// <summary>Bind the value to <c>_</c> without changing Output.</summary>
            SetAsUnderscore,
            /// <summary>Open the method picker on the value's runtime type and route the pick into the Patches form.</summary>
            PatchMethod,
            /// <summary>Copy the type's full name to the system clipboard.</summary>
            CopyTypeName,
            /// <summary>Copy a small C# snippet that re-locates the instance to the system clipboard.</summary>
            CopyInspectSnippet,
        }

        // Issue #25: the previous int.MaxValue cap let a single
        // category change trigger a multi-thousand entry scan + sort
        // + ListView rebuild on the editor main thread. Default cap
        // keeps interaction responsive in big projects; "Load more"
        // re-runs the scan unbounded so the user can opt into the
        // long path on demand.
        private const int DefaultMaxResults = 200;
        // Debounce so a search typed at normal speed only kicks one
        // Refresh at the end of the burst, not one per keystroke.
        // 200 ms balances "feels live" against "doesn't redo the
        // scan four times for a four-letter search". The category
        // dropdown bypasses the debounce — that change is one
        // explicit click, not a typing burst.
        private const long SearchDebounceMs = 200;

        private readonly VisualElement _root;
        private EnumField _categoryField;
        private ToolbarSearchField _searchField;
        private ListView _listView;
        private Label _statusLabel;
        private Button _loadMoreBtn;

        private readonly List<InstanceEntry> _entries = new();

        // When true, the next Refresh asks InstanceLocator for an
        // unbounded scan. Reset to false on every category / search
        // change so a heavy scan on category A doesn't bleed into
        // category B.
        private bool _showAll;

        // Schedule handle for the search debounce. Pause + reassign
        // on every keystroke so the scheduled Refresh always reflects
        // the latest text rather than a stale snapshot from the
        // beginning of a typing burst.
        private IVisualElementScheduledItem _searchDebounce;

        public event Action<InstanceEntry> OnInstanceChosen;

        /// <summary>
        /// Raised when the user picks an action from a row's right-click
        /// context menu. The host is expected to dispatch on
        /// <paramref name="BrowserRowAction"/> — see the enum for the
        /// concrete actions surfaced today. Existing double-click
        /// behaviour is unchanged; this is a parallel channel for
        /// explicit "do X with this row" intents that double-click
        /// alone couldn't express (Set as `_`, Patch Method, copy
        /// helpers).
        /// </summary>
        public event Action<InstanceEntry, BrowserRowAction> OnRowAction;

        public ObjectBrowserView(VisualElement host)
        {
            _root = new VisualElement();
            _root.AddToClassList("rr-browser");
            host.Add(_root);
            BuildUI();
            // Don't auto-Refresh on construction. The first scan can be heavy
            // (especially with the Singleton category, which sweeps the whole
            // app domain) and we don't want to freeze the Editor whenever the
            // window opens or a domain reload completes. The user triggers it
            // by changing category, typing in search, or pressing the refresh
            // button.
            if (_statusLabel != null)
                _statusLabel.text = "Press ↻ or change filter to populate.";
        }

        public void Refresh()
        {
            var category = _categoryField != null
                ? (InstanceCategory)_categoryField.value
                : InstanceCategory.All;
            var filter = _searchField?.value ?? string.Empty;
            int cap = _showAll ? int.MaxValue : DefaultMaxResults;

            _entries.Clear();
            try
            {
                _entries.AddRange(InstanceLocator.Find(category, filter, cap));
            }
            catch (Exception ex)
            {
                _entries.Clear();
                if (_statusLabel != null)
                    _statusLabel.text = $"<error: {ex.GetBaseException().Message}>";
                if (_loadMoreBtn != null) _loadMoreBtn.style.display = DisplayStyle.None;
                _listView?.RefreshItems();
                return;
            }

            _listView?.RefreshItems();

            // Cap-hit detection: InstanceLocator returning exactly
            // `cap` entries is treated as "may have hit the cap".
            // It's a false positive when the project actually has
            // exactly N matches, but the user-visible cost is a
            // "Load more" button that produces zero new rows on
            // click — preferable to silently dropping rows past
            // the cap with no recourse.
            bool capHit = !_showAll && _entries.Count >= DefaultMaxResults;
            if (_statusLabel != null)
            {
                if (_showAll)
                    _statusLabel.text = $"{_entries.Count} item(s) (showing all)";
                else if (capHit)
                    _statusLabel.text = $"{_entries.Count}+ items — capped at {DefaultMaxResults}, click Load more for the full scan";
                else
                    _statusLabel.text = $"{_entries.Count} item(s)";
            }
            if (_loadMoreBtn != null)
                _loadMoreBtn.style.display = capHit ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void BuildUI()
        {
            // Header row: title + refresh button
            var header = new VisualElement();
            header.AddToClassList("rr-browser-header");
            var title = new Label("Object Browser");
            title.AddToClassList("rr-browser-title");
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            var refreshBtn = new ToolbarButton(Refresh) { text = "↻" };
            refreshBtn.AddToClassList("rr-browser-refresh");
            refreshBtn.tooltip = "Refresh list";
            header.Add(title);
            header.Add(spacer);
            header.Add(refreshBtn);
            _root.Add(header);

            // Category drop-down. Category changes drop the
            // expanded-all flag so a heavy unbounded scan from the
            // previous category doesn't bleed into the next one.
            _categoryField = new EnumField(InstanceCategory.All);
            _categoryField.AddToClassList("rr-browser-category");
            _categoryField.RegisterValueChangedCallback(_ =>
            {
                _showAll = false;
                Refresh();
            });
            _root.Add(_categoryField);

            // Search field. Debounced so typing doesn't trigger one
            // full scan per keystroke. Pause-and-reschedule on each
            // change so the eventual Refresh always runs against the
            // text the user actually finished typing. _showAll
            // resets along with the change because a fresh search
            // has its own match count and shouldn't inherit the
            // previous filter's "show all" choice.
            _searchField = new ToolbarSearchField();
            _searchField.AddToClassList("rr-browser-search");
            _searchField.RegisterValueChangedCallback(_ =>
            {
                _showAll = false;
                _searchDebounce?.Pause();
                _searchDebounce = _root.schedule.Execute(Refresh).StartingIn(SearchDebounceMs);
            });
            _root.Add(_searchField);

            // ListView
            _listView = new ListView
            {
                itemsSource = _entries,
                fixedItemHeight = 38,
                makeItem = MakeRow,
                bindItem = BindRow,
                selectionType = SelectionType.Single,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
            };
            _listView.AddToClassList("rr-browser-list");
            _listView.itemsChosen += chosen =>
            {
                var first = chosen?.OfType<InstanceEntry>().FirstOrDefault();
                if (first != null) OnInstanceChosen?.Invoke(first);
            };
            _root.Add(_listView);

            // Status row + Load more affordance. The button is
            // hidden by default and only surfaces when Refresh
            // detects the cap was hit. Click flips _showAll and
            // re-runs Refresh, which now asks InstanceLocator for
            // the unbounded scan.
            var statusRow = new VisualElement();
            statusRow.style.flexDirection = FlexDirection.Row;
            statusRow.style.alignItems = Align.Center;

            _statusLabel = new Label();
            _statusLabel.AddToClassList("rr-browser-status");
            _statusLabel.style.flexGrow = 1;
            statusRow.Add(_statusLabel);

            _loadMoreBtn = new Button(() =>
            {
                _showAll = true;
                Refresh();
            }) { text = "Load more" };
            _loadMoreBtn.tooltip =
                $"Re-run the scan without the {DefaultMaxResults}-row cap. May freeze the editor briefly in projects with many matching objects.";
            _loadMoreBtn.style.display = DisplayStyle.None;
            _loadMoreBtn.style.marginLeft = 4;
            _loadMoreBtn.style.marginRight = 4;
            statusRow.Add(_loadMoreBtn);

            _root.Add(statusRow);
        }

        private VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("rr-browser-row");

            var nameLbl = new Label();
            nameLbl.AddToClassList("rr-browser-row-name");
            row.Add(nameLbl);

            var typeLbl = new Label();
            typeLbl.AddToClassList("rr-browser-row-type");
            row.Add(typeLbl);

            var subLbl = new Label();
            subLbl.AddToClassList("rr-browser-row-sub");
            row.Add(subLbl);

            // Attach the context menu once per pooled row. The
            // manipulator reads userData (re-set on every BindRow) so
            // a row's identity tracks the underlying InstanceEntry as
            // ListView recycles it during scroll. Building the menu
            // here — instead of re-adding the manipulator per bind —
            // avoids allocating five action lambdas every time the
            // user scrolls past a row.
            row.AddManipulator(new ContextualMenuManipulator(BuildRowContextMenu));

            return row;
        }

        private void BuildRowContextMenu(ContextualMenuPopulateEvent evt)
        {
            // evt.target is the row's first leaf under the cursor (a
            // Label, usually); walk up to the row container we
            // tagged in MakeRow / BindRow via userData. If the
            // pointer landed somewhere without an entry attached,
            // skip — that means the row is mid-rebind or hasn't been
            // bound yet.
            var node = evt.target as VisualElement;
            InstanceEntry entry = null;
            while (node != null)
            {
                if (node.userData is InstanceEntry e) { entry = e; break; }
                node = node.parent;
            }
            if (entry == null) return;

            // The five actions match issue #60's spec. Patch Method
            // and Set as `_` need a live (non-null) Value; mark them
            // disabled when the underlying instance is gone so the
            // user gets the visual cue rather than a silent no-op.
            bool hasLiveValue = entry.Value != null
                                && !(entry.Value is UnityEngine.Object uo && uo == null);

            evt.menu.AppendAction("Inspect",
                _ => OnRowAction?.Invoke(entry, BrowserRowAction.Inspect),
                hasLiveValue ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            evt.menu.AppendAction("Set as `_`",
                _ => OnRowAction?.Invoke(entry, BrowserRowAction.SetAsUnderscore),
                hasLiveValue ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            evt.menu.AppendAction("Patch Method…",
                _ => OnRowAction?.Invoke(entry, BrowserRowAction.PatchMethod),
                // PatchMethod survives a null Value when DeclaredType
                // is present (singleton accessors), since the host
                // falls back to opening the picker on the declared
                // type. Mirrors the live-vs-null branch in
                // OnBrowserInstanceChosen.
                (hasLiveValue || entry.DeclaredType != null)
                    ? DropdownMenuAction.Status.Normal
                    : DropdownMenuAction.Status.Disabled);

            evt.menu.AppendSeparator();

            evt.menu.AppendAction("Copy Type Name",
                _ => OnRowAction?.Invoke(entry, BrowserRowAction.CopyTypeName));

            // Copy Inspect Snippet generates source code, so it only
            // makes sense when the row's type can actually be
            // rendered as C# (closed types, nested via `.`, generics
            // expanded). Open generics, generic parameters, and rows
            // without any Type at all surface as Disabled so the
            // menu doesn't promise a snippet it can't produce.
            var snippetType = entry.Value?.GetType() ?? entry.DeclaredType;
            bool canSnippet = snippetType != null && CSharpTypeName.IsRenderable(snippetType);
            evt.menu.AppendAction("Copy Inspect Snippet",
                _ => OnRowAction?.Invoke(entry, BrowserRowAction.CopyInspectSnippet),
                canSnippet ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
        }

        private void BindRow(VisualElement ve, int index)
        {
            if (index < 0 || index >= _entries.Count) return;
            var entry = _entries[index];
            // Stash the entry on the row so the shared
            // ContextualMenuManipulator can find it without
            // re-querying the ListView by index — the manipulator
            // fires on the leaf under the cursor and walks up to
            // the row container looking for this userData tag.
            ve.userData = entry;
            var labels = ve.Children().OfType<Label>().ToList();
            if (labels.Count >= 1) labels[0].text = entry.DisplayName ?? "<no name>";
            if (labels.Count >= 2) labels[1].text = entry.TypeName    ?? string.Empty;
            if (labels.Count >= 3) labels[2].text = entry.SubLabel    ?? string.Empty;

            ve.RemoveFromClassList("rr-browser-row--inactive");
            if (!entry.IsActive) ve.AddToClassList("rr-browser-row--inactive");

            // Phase 11e: paint a left-edge category indicator so users
            // can spot what kind of instance a row is at a glance,
            // especially in the All view where Phase 8 mixed
            // MonoBehaviour, ScriptableObject, and Singleton entries
            // into one list. SubLabel already carries the textual
            // origin ("Scene: Foo", "ScriptableObject", "Singleton")
            // but the eye reads color faster than text in a long list.
            ve.RemoveFromClassList("rr-browser-row--mb");
            ve.RemoveFromClassList("rr-browser-row--so");
            ve.RemoveFromClassList("rr-browser-row--singleton");
            switch (entry.Category)
            {
                case InstanceCategory.MonoBehaviour:    ve.AddToClassList("rr-browser-row--mb");        break;
                case InstanceCategory.ScriptableObject: ve.AddToClassList("rr-browser-row--so");        break;
                case InstanceCategory.Singleton:        ve.AddToClassList("rr-browser-row--singleton"); break;
            }
        }
    }
}
