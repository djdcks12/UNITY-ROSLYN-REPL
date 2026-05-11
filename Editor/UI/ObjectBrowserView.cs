using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using RoslynRepl.Editor.Core;

namespace RoslynRepl.Editor.UI
{
    /// <summary>
    /// Side-panel UI for browsing scene MonoBehaviours, ScriptableObject
    /// assets, and singletons. Rebuilds the underlying list whenever the
    /// category, search filter, or refresh button changes. Emits
    /// <see cref="OnInstanceChosen"/> when the user double-clicks a row.
    /// </summary>
    public class ObjectBrowserView
    {
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

        private static VisualElement MakeRow()
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

            return row;
        }

        private void BindRow(VisualElement ve, int index)
        {
            if (index < 0 || index >= _entries.Count) return;
            var entry = _entries[index];
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
