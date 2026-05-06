using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using RoslynRepl.Editor.Core;

namespace RoslynRepl.Editor.UI
{
    /// <summary>
    /// In-window panel that lists watch expressions and re-evaluates them
    /// after every Run. Lives at the bottom of the host window so users
    /// can keep an eye on values while iterating in the code panel above.
    /// The host calls <see cref="Refresh"/> after each successful Run; the
    /// view also refreshes itself on <see cref="WatchStore.Changed"/> so
    /// adding / removing rows from anywhere updates the list immediately.
    /// </summary>
    public class WatchPanelView
    {
        private readonly WatchEvaluator _evaluator = new();
        private readonly VisualElement _host;
        private VisualElement _rowsContainer;
        private TextField _addField;
        private Label _statusLabel;

        // Highlight state — when an expression's preview differs from its
        // previous snapshot, the row gets a "changed" CSS class for a
        // brief window. We track which rows are currently highlighted so
        // we can clear them on the next refresh without scanning the
        // tree.
        private readonly HashSet<string> _highlighted = new();

        public WatchPanelView(VisualElement host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            BuildLayout();

            WatchStore.Changed -= OnStoreChanged;
            WatchStore.Changed += OnStoreChanged;

            // Initial population — also ensures the user sees their
            // saved expressions when the window opens, even before
            // they hit Run.
            Refresh();
        }

        public void Dispose()
        {
            WatchStore.Changed -= OnStoreChanged;
        }

        public void Refresh()
        {
            _evaluator.RefreshAll();
            RebuildRows();
        }

        private void OnStoreChanged()
        {
            // When the user adds / removes a row the store fires Changed;
            // re-evaluate so the new row gets a value or the gap closes.
            Refresh();
        }

        private void BuildLayout()
        {
            _host.Clear();
            _host.AddToClassList("rr-watch-pane");

            // Header row: title, status, + Add field
            var header = new VisualElement();
            header.AddToClassList("rr-watch-header");

            var title = new Label("Watch");
            title.AddToClassList("rr-pane-title");
            header.Add(title);

            _statusLabel = new Label();
            _statusLabel.AddToClassList("rr-pane-meta");
            _statusLabel.style.marginLeft = 8;
            header.Add(_statusLabel);

            // Spacer
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            header.Add(spacer);

            _addField = new TextField();
            _addField.AddToClassList("rr-watch-add-field");
            // Slim, fits in the header bar.
            _addField.style.minWidth = 180;
            _addField.style.flexShrink = 0;
            // Placeholder via tooltip until UI Toolkit grows real
            // placeholder support — TextField has no native placeholder.
            _addField.tooltip = "expression to watch (e.g. Manager.Instance.Count)";
            _addField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    SubmitAdd();
                    evt.StopPropagation();
                }
            });
            header.Add(_addField);

            var addBtn = new Button(SubmitAdd) { text = "+" };
            addBtn.AddToClassList("rr-watch-add-btn");
            addBtn.style.minWidth = 22;
            header.Add(addBtn);

            _host.Add(header);

            // Body — rows scrollable
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("rr-watch-scroll");
            scroll.style.flexGrow = 1;

            _rowsContainer = new VisualElement();
            _rowsContainer.AddToClassList("rr-watch-rows");
            scroll.Add(_rowsContainer);
            _host.Add(scroll);
        }

        private void SubmitAdd()
        {
            var expr = _addField.value?.Trim();
            if (string.IsNullOrEmpty(expr)) return;
            WatchStore.Add(expr);
            _addField.SetValueWithoutNotify(string.Empty);
            // Refresh is triggered via WatchStore.Changed; no need to
            // call manually.
        }

        private void RebuildRows()
        {
            _rowsContainer.Clear();
            _highlighted.Clear();

            var rows = _evaluator.Current;
            if (rows.Count == 0)
            {
                var empty = new Label("(no watches — type an expression above and press Enter)");
                empty.AddToClassList("rr-watch-empty");
                _rowsContainer.Add(empty);
                _statusLabel.text = string.Empty;
                return;
            }

            int changed = 0;
            foreach (var r in rows)
            {
                var row = BuildRow(r);
                _rowsContainer.Add(row);
                if (r.JustChanged) { _highlighted.Add(r.Expression); changed++; }
            }
            _statusLabel.text = changed > 0 ? $"{rows.Count} watches • {changed} changed" : $"{rows.Count} watches";
        }

        private VisualElement BuildRow(WatchResult r)
        {
            var row = new VisualElement();
            row.AddToClassList("rr-watch-row");
            if (r.Failed) row.AddToClassList("rr-watch-row--failed");
            if (r.JustChanged) row.AddToClassList("rr-watch-row--changed");

            var expr = new Label(r.Expression);
            expr.AddToClassList("rr-watch-cell-expr");
            row.Add(expr);

            var value = new Label(r.Failed ? r.ErrorMessage ?? r.Preview : r.Preview);
            value.AddToClassList("rr-watch-cell-value");
            value.tooltip = r.Failed ? r.ErrorMessage : (r.TypeName + ": " + r.Preview);
            row.Add(value);

            var typeLabel = new Label(r.Failed ? string.Empty : r.TypeName ?? string.Empty);
            typeLabel.AddToClassList("rr-watch-cell-type");
            row.Add(typeLabel);

            var removeBtn = new Button(() => WatchStore.Remove(r.Expression)) { text = "✕" };
            removeBtn.AddToClassList("rr-watch-cell-remove");
            row.Add(removeBtn);

            // Schedule a clear of the change-highlight class after a
            // short delay so the user has time to notice the change
            // before the row settles back to normal styling. Clearing
            // via schedule.Execute survives panel rebuilds because we
            // also wipe the class in the next RebuildRows.
            if (r.JustChanged)
            {
                row.schedule.Execute(() =>
                {
                    if (row.parent != null)
                        row.RemoveFromClassList("rr-watch-row--changed");
                }).StartingIn(1500);
            }

            return row;
        }
    }
}
