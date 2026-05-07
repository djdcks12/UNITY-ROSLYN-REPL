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
        private readonly HashSet<string> _expanded = new();

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

            // Phase 11d: opt-out for the global-search fallback. Some
            // projects don't want passive instance-pool walking on every
            // Run; flipping this off makes uncompilable Watches simply
            // fail instead of resolving via best-effort search.
            var fallbackToggle = new Toggle("Fallback")
            {
                tooltip =
                    "When a Watch expression doesn't compile cleanly, also try resolving it against\n" +
                    "the previous result `_` and the live MonoBehaviour / ScriptableObject pool.\n" +
                    "Turn off to skip the fallback (compile-fail Watches stay failed)."
            };
            fallbackToggle.AddToClassList("rr-watch-fallback-toggle");
            fallbackToggle.value = WatchSettings.FallbackEnabled;
            fallbackToggle.RegisterValueChangedCallback(evt =>
            {
                WatchSettings.FallbackEnabled = evt.newValue;
                Refresh();
            });
            header.Add(fallbackToggle);

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
            var liveExpressions = new HashSet<string>();
            foreach (var r in rows)
            {
                liveExpressions.Add(r.Expression);
                if (!CanExpand(r)) _expanded.Remove(r.Expression);
                var row = BuildRow(r);
                _rowsContainer.Add(row);
                if (r.JustChanged) { _highlighted.Add(r.Expression); changed++; }
            }
            _expanded.RemoveWhere(expression => !liveExpressions.Contains(expression));
            _statusLabel.text = changed > 0 ? $"{rows.Count} watches • {changed} changed" : $"{rows.Count} watches";
        }

        private VisualElement BuildRow(WatchResult r)
        {
            var block = new VisualElement();
            block.AddToClassList("rr-watch-row-block");

            var row = new VisualElement();
            row.AddToClassList("rr-watch-row");
            if (r.Failed) row.AddToClassList("rr-watch-row--failed");
            if (r.JustChanged) row.AddToClassList("rr-watch-row--changed");

            bool canExpand = CanExpand(r);
            bool expanded = canExpand && _expanded.Contains(r.Expression);
            var expandBtn = new Button(() => ToggleExpanded(r.Expression)) { text = canExpand ? (expanded ? "▾" : "▸") : string.Empty };
            expandBtn.AddToClassList("rr-watch-expand-btn");
            expandBtn.SetEnabled(canExpand);
            row.Add(expandBtn);

            var expr = new Label(r.Expression);
            expr.AddToClassList("rr-watch-cell-expr");
            // Source descriptor: only present on global-search fallback
            // hits, where the value is otherwise ambiguous about which
            // owner it came from. Surfacing it on the expression label
            // (and dimming the visual style via the modifier class)
            // tells the user "this row resolved by walking instances —
            // here's the one we picked."
            if (!string.IsNullOrEmpty(r.SourceDescription))
            {
                expr.AddToClassList("rr-watch-cell-expr--global");
                expr.tooltip = $"Resolved from: {r.SourceDescription}";
            }
            row.Add(expr);

            var value = new Label(r.Failed ? r.ErrorMessage ?? r.Preview : r.Preview);
            value.AddToClassList("rr-watch-cell-value");
            string valueTooltip = r.Failed
                ? r.ErrorMessage
                : (r.TypeName + ": " + r.Preview);
            if (!r.Failed && !string.IsNullOrEmpty(r.SourceDescription))
                valueTooltip += "\nSource: " + r.SourceDescription;
            value.tooltip = valueTooltip;
            row.Add(value);

            var typeLabel = new Label(r.Failed ? string.Empty : r.TypeName ?? string.Empty);
            typeLabel.AddToClassList("rr-watch-cell-type");
            row.Add(typeLabel);

            var removeBtn = new Button(() => WatchStore.Remove(r.Expression)) { text = "✕" };
            removeBtn.AddToClassList("rr-watch-cell-remove");
            row.Add(removeBtn);
            block.Add(row);

            // Phase 11a: secondary line under fallback rows. The dim
            // expression label + tooltip in Phase 10b were too easy to
            // miss — users blamed the wrong owner for a value because
            // the resolver's choice of instance never made it into
            // pixels they actually scanned. A persistent "Resolved
            // from: …" line under the row width-matches the value cell
            // so the breadcrumb is read-without-hover.
            if (!string.IsNullOrEmpty(r.SourceDescription) && !r.Failed)
            {
                var sourceLabel = new Label("↳ Resolved from: " + r.SourceDescription);
                sourceLabel.AddToClassList("rr-watch-row-source");
                block.Add(sourceLabel);
            }

            if (expanded)
            {
                var tree = BuildWatchTree(r.Tree);
                tree.AddToClassList("rr-watch-tree");
                block.Add(tree);
            }

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

            return block;
        }

        private void ToggleExpanded(string expression)
        {
            if (!_expanded.Add(expression))
                _expanded.Remove(expression);
            RebuildRows();
        }

        private static bool CanExpand(WatchResult result)
        {
            return result != null
                && !result.Failed
                && result.Tree != null
                && result.Tree.IsExpandable
                && result.Tree.Children.Count > 0;
        }

        private static MultiColumnTreeView BuildWatchTree(ReplValueNode root)
        {
            var tv = new MultiColumnTreeView();
            tv.fixedItemHeight = 22;
            var treeHeight = CalculateWatchTreeHeight(root);
            tv.style.height = treeHeight;
            tv.style.minHeight = treeHeight;
            tv.style.maxHeight = treeHeight;
            tv.style.flexGrow = 0;
            tv.style.flexShrink = 0;

            tv.columns.Add(MakeColumn("name", "Name", 180, n => n?.Name ?? string.Empty, "rr-treecell--name", tv));
            tv.columns.Add(MakeColumn("type", "Type", 130, n => n?.TypeName ?? string.Empty, "rr-treecell--type", tv));
            tv.columns.Add(MakeColumn("value", "Value", 260, n => n?.Preview ?? string.Empty, "rr-treecell--value", tv));

            int nextId = 0;
            tv.SetRootItems(new List<TreeViewItemData<ReplValueNode>>
            {
                ToItemData(root, ref nextId)
            });
            tv.Rebuild();
            tv.ExpandRootItems();
            return tv;
        }

        private static float CalculateWatchTreeHeight(ReplValueNode root)
        {
            const float HeaderHeight = 24f;
            const float RowHeight = 22f;
            const float MinHeight = 96f;
            const float MaxHeight = 260f;

            int rowCount = CountTreeRows(root);
            return Mathf.Clamp(HeaderHeight + rowCount * RowHeight, MinHeight, MaxHeight);
        }

        private static int CountTreeRows(ReplValueNode node)
        {
            if (node == null) return 0;

            int count = 1;
            if (node.Children == null) return count;
            foreach (var child in node.Children)
                count += CountTreeRows(child);
            return count;
        }

        private static Column MakeColumn(
            string name, string title, float width,
            System.Func<ReplValueNode, string> getter,
            string extraClass,
            MultiColumnTreeView tv)
        {
            var col = new Column
            {
                name = name,
                title = title,
                width = width,
                minWidth = 70,
                stretchable = true
            };
            col.makeCell = () =>
            {
                var lbl = new Label();
                lbl.AddToClassList("rr-treecell");
                if (!string.IsNullOrEmpty(extraClass)) lbl.AddToClassList(extraClass);
                return lbl;
            };
            col.bindCell = (ve, idx) =>
            {
                var node = tv.GetItemDataForIndex<ReplValueNode>(idx);
                ((Label)ve).text = getter(node);
            };
            return col;
        }

        private static TreeViewItemData<ReplValueNode> ToItemData(ReplValueNode node, ref int nextId)
        {
            var children = new List<TreeViewItemData<ReplValueNode>>();
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                    children.Add(ToItemData(child, ref nextId));
            }
            return new TreeViewItemData<ReplValueNode>(nextId++, node, children);
        }
    }
}
