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
        private const int MaxBrowserResults = int.MaxValue;

        private readonly VisualElement _root;
        private EnumField _categoryField;
        private ToolbarSearchField _searchField;
        private ListView _listView;
        private Label _statusLabel;

        private readonly List<InstanceEntry> _entries = new();

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

            _entries.Clear();
            try
            {
                _entries.AddRange(InstanceLocator.Find(category, filter, MaxBrowserResults));
            }
            catch (Exception ex)
            {
                _entries.Clear();
                if (_statusLabel != null)
                    _statusLabel.text = $"<error: {ex.GetBaseException().Message}>";
                _listView?.RefreshItems();
                return;
            }

            _listView?.RefreshItems();
            if (_statusLabel != null)
                _statusLabel.text = _entries.Count == MaxBrowserResults
                    ? $"{MaxBrowserResults}+ items (truncated)"
                    : $"{_entries.Count} item(s)";
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

            // Category drop-down
            _categoryField = new EnumField(InstanceCategory.All);
            _categoryField.AddToClassList("rr-browser-category");
            _categoryField.RegisterValueChangedCallback(_ => Refresh());
            _root.Add(_categoryField);

            // Search field
            _searchField = new ToolbarSearchField();
            _searchField.AddToClassList("rr-browser-search");
            _searchField.RegisterValueChangedCallback(_ => Refresh());
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

            // Status row
            _statusLabel = new Label();
            _statusLabel.AddToClassList("rr-browser-status");
            _root.Add(_statusLabel);
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
        }
    }
}
