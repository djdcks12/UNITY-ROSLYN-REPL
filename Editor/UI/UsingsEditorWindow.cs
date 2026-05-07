using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using RoslynRepl.Editor.Core;

namespace RoslynRepl.Editor.UI
{
    /// <summary>
    /// Auxiliary popup that lets the user manage the *additional* using
    /// namespaces injected at the top of every snippet. The defaults from
    /// <see cref="ReplOptions.DefaultUsings"/> are shown as read-only rows
    /// (so the user knows what's already there); the editable list lives
    /// underneath and is persisted via <see cref="UsingsStore"/>. Saving
    /// fires <c>UsingsStore.Changed</c>; the host window listens for that
    /// event to rebuild its effective options on the next run.
    /// </summary>
    public class UsingsEditorWindow : EditorWindow
    {
        private static UsingsEditorWindow _instance;

        public static void Open()
        {
            if (_instance != null)
            {
                _instance.Focus();
                return;
            }
            _instance = CreateInstance<UsingsEditorWindow>();
            _instance.titleContent = new GUIContent("REPL Usings");
            _instance.minSize = new Vector2(360, 280);
            _instance.ShowUtility();
        }

        private List<string> _customUsings;
        private VisualElement _customList;
        private TextField _newField;

        public void CreateGUI()
        {
            _customUsings = UsingsStore.LoadCustom();

            var root = rootVisualElement;
            // CreateGUI is not a one-shot hook: panel rebuilds and domain
            // reload recovery can fire it again on the same window. Without
            // this clear, the second call appends a duplicate control tree
            // on top of the first — visible duplicate sections plus stale
            // refs in the stale rows because _customList only points at the
            // most recently constructed list element.
            root.Clear();
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.paddingTop = 8;
            root.style.paddingBottom = 8;
            root.style.flexDirection = FlexDirection.Column;

            // Defaults section (read-only)
            var defHeader = new Label("Defaults (always applied)");
            defHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            defHeader.style.color = new StyleColor(new Color(0.8f, 0.8f, 0.8f));
            defHeader.style.marginBottom = 4;
            root.Add(defHeader);

            var defBox = new VisualElement();
            defBox.style.backgroundColor = new StyleColor(new Color(0.18f, 0.18f, 0.18f));
            defBox.style.paddingLeft = 6;
            defBox.style.paddingRight = 6;
            defBox.style.paddingTop = 4;
            defBox.style.paddingBottom = 4;
            defBox.style.marginBottom = 10;
            foreach (var ns in ReplOptions.DefaultUsings)
            {
                var l = new Label("using " + ns + ";");
                l.style.color = new StyleColor(new Color(0.55f, 0.55f, 0.55f));
                defBox.Add(l);
            }
            root.Add(defBox);

            // Custom section (editable)
            var customHeader = new Label("Your additions");
            customHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            customHeader.style.color = new StyleColor(new Color(0.8f, 0.8f, 0.8f));
            customHeader.style.marginBottom = 4;
            root.Add(customHeader);

            var scrollHost = new ScrollView(ScrollViewMode.Vertical);
            scrollHost.style.flexGrow = 1;
            scrollHost.style.minHeight = 80;
            scrollHost.style.backgroundColor = new StyleColor(new Color(0.14f, 0.14f, 0.14f));
            scrollHost.style.marginBottom = 6;

            _customList = new VisualElement();
            _customList.style.flexDirection = FlexDirection.Column;
            scrollHost.Add(_customList);
            root.Add(scrollHost);

            RebuildCustomList();

            // Add row
            var addRow = new VisualElement();
            addRow.style.flexDirection = FlexDirection.Row;
            addRow.style.alignItems = Align.Center;

            _newField = new TextField();
            _newField.style.flexGrow = 1;
            _newField.style.marginRight = 4;
            // Tab-friendly: Enter commits the new entry
            _newField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    AddFromField();
                    evt.StopPropagation();
                }
            });

            var addBtn = new Button(AddFromField) { text = "+ Add" };
            addRow.Add(_newField);
            addRow.Add(addBtn);
            root.Add(addRow);

            // Footer
            var footer = new Label("Saved automatically. Restored every time the REPL opens.");
            footer.style.color = new StyleColor(new Color(0.5f, 0.5f, 0.5f));
            footer.style.marginTop = 6;
            footer.style.fontSize = 10;
            root.Add(footer);
        }

        private void OnEnable()
        {
            // External writes (most importantly Tools / Roslyn REPL /
            // Reset Project Data) need to refresh the open popup —
            // otherwise stale `using` rows stay visible and the user
            // can re-save them, repopulating the cleared store. Mirrors
            // RunHistoryWindow's Phase 5 subscription pattern.
            UsingsStore.Changed -= OnStoreChanged;
            UsingsStore.Changed += OnStoreChanged;
        }

        private void OnDisable()
        {
            UsingsStore.Changed -= OnStoreChanged;
        }

        private void OnDestroy()
        {
            _instance = null;
        }

        private void OnStoreChanged()
        {
            // Reload from the store rather than trusting the in-memory
            // list — the cleared state isn't representable as "remove
            // these specific entries", and trying to diff would let a
            // race re-introduce removed rows.
            _customUsings = UsingsStore.LoadCustom();
            if (_customList != null) RebuildCustomList();
        }

        private void AddFromField()
        {
            var raw = _newField.value?.Trim();
            if (string.IsNullOrEmpty(raw)) return;
            // Strip a leading "using " and trailing ";" so users can paste
            // either form and have it normalize.
            if (raw.StartsWith("using ", System.StringComparison.Ordinal))
                raw = raw.Substring("using ".Length).Trim();
            if (raw.EndsWith(";", System.StringComparison.Ordinal))
                raw = raw.Substring(0, raw.Length - 1).Trim();
            if (string.IsNullOrEmpty(raw)) return;

            // Don't accept duplicates of either defaults or already-added items.
            bool exists = ReplOptions.DefaultUsings.Contains(raw)
                       || _customUsings.Contains(raw);
            if (!exists)
            {
                _customUsings.Add(raw);
                UsingsStore.Save(_customUsings);
                RebuildCustomList();
            }
            _newField.SetValueWithoutNotify(string.Empty);
            _newField.Focus();
        }

        private void RebuildCustomList()
        {
            _customList.Clear();
            if (_customUsings.Count == 0)
            {
                var empty = new Label("(none — type a namespace below and press Enter or +Add)");
                empty.style.color = new StyleColor(new Color(0.45f, 0.45f, 0.45f));
                empty.style.paddingLeft = 6;
                empty.style.paddingTop = 6;
                empty.style.paddingBottom = 6;
                _customList.Add(empty);
                return;
            }

            foreach (var ns in _customUsings.ToList())
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.paddingLeft = 6;
                row.style.paddingRight = 4;
                row.style.paddingTop = 2;
                row.style.paddingBottom = 2;

                var label = new Label("using " + ns + ";");
                label.style.flexGrow = 1;
                label.style.color = new StyleColor(new Color(0.85f, 0.85f, 0.85f));
                row.Add(label);

                var removeBtn = new Button(() =>
                {
                    _customUsings.Remove(ns);
                    UsingsStore.Save(_customUsings);
                    RebuildCustomList();
                })
                { text = "✕" };
                removeBtn.style.minWidth = 22;
                row.Add(removeBtn);

                _customList.Add(row);
            }
        }
    }
}
