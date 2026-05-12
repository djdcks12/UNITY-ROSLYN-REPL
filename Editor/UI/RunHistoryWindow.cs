using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using RoslynRepl.Editor.Core;

namespace RoslynRepl.Editor.UI
{
    /// <summary>
    /// Auxiliary popup listing recently-executed snippets, most-recent first.
    /// Double-clicking a row (or pressing Enter while one is selected) loads
    /// that snippet into the host window's code editor via
    /// <see cref="OnSnippetChosen"/>. The window is opened from the toolbar
    /// "History…" button; the host wires the chosen snippet into its
    /// CodeEditorView.value.
    /// </summary>
    public class RunHistoryWindow : EditorWindow
    {
        private static RunHistoryWindow _instance;

        public static event Action<string> OnSnippetChosen;

        public static void Open()
        {
            if (_instance != null)
            {
                _instance.Focus();
                return;
            }
            _instance = CreateInstance<RunHistoryWindow>();
            _instance.titleContent = new GUIContent("REPL History");
            _instance.minSize = new Vector2(420, 320);
            _instance.ShowUtility();
        }

        private List<string> _entries = new();
        private ListView _list;

        private void OnEnable()
        {
            // Subscribe at the EditorWindow lifecycle level (not in
            // CreateGUI) so the live ListView refreshes on every push
            // even when CreateGUI doesn't fire — e.g. when the popup is
            // already mounted and the user runs a snippet from the host
            // window. Without this the popup snapshots once at mount
            // and silently goes stale.
            RunHistoryStore.Changed -= OnHistoryChanged;
            RunHistoryStore.Changed += OnHistoryChanged;
        }

        public void CreateGUI()
        {
            _entries = RunHistoryStore.Load();

            var root = rootVisualElement;
            root.Clear();
            root.style.paddingLeft = 6;
            root.style.paddingRight = 6;
            root.style.paddingTop = 6;
            root.style.paddingBottom = 6;
            root.style.flexDirection = FlexDirection.Column;

            // Header row
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 4;

            var title = new Label("Recent runs");
            title.style.flexGrow = 1;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = new StyleColor(new Color(0.85f, 0.85f, 0.85f));
            header.Add(title);

            var clearBtn = new Button(() =>
            {
                if (!EditorUtility.DisplayDialog("Clear history?",
                    "Discard all recorded snippets for this project? This cannot be undone.",
                    "Clear", "Cancel"))
                {
                    return;
                }
                // PR-review followup on #27: don't blank the visible
                // list before we know the file actually went away. The
                // previous shape called RunHistoryStore.Clear() and
                // unconditionally reset _entries — so a locked /
                // read-only runHistory.json left the UI looking empty
                // while the file (and its sensitive contents) sat
                // untouched on disk, ready to resurface the moment
                // the popup reopened or the next Load fired.
                bool ok = RunHistoryStore.Clear();
                if (ok)
                {
                    _entries = new List<string>();
                }
                else
                {
                    // Refresh from disk so the visible list matches
                    // the actual surviving state — Clear's failure
                    // means the file is still there with whatever it
                    // had before, and the user needs to see that
                    // rather than think the wipe succeeded.
                    _entries = RunHistoryStore.Load();
                    EditorUtility.DisplayDialog(
                        "Clear history failed",
                        "Could not delete the run history file. The list above reflects what's still on disk — close any external editor holding the file open, then retry, or delete the file by hand. (See the Console for the exact path.)",
                        "OK");
                }
                RebuildListView();
            })
            { text = "Clear" };
            header.Add(clearBtn);
            root.Add(header);

            // List
            _list = new ListView
            {
                fixedItemHeight = 40,
                selectionType = SelectionType.Single,
                showBorder = true,
                makeItem = MakeRow,
                bindItem = BindRow,
            };
            _list.style.flexGrow = 1;
            _list.itemsChosen += chosen =>
            {
                foreach (var item in chosen)
                {
                    if (item is string code)
                    {
                        OnSnippetChosen?.Invoke(code);
                        Close();
                        return;
                    }
                }
            };
            root.Add(_list);

            // Footer hint
            var hint = new Label("Double-click a snippet to load it into the editor.");
            hint.style.color = new StyleColor(new Color(0.5f, 0.5f, 0.5f));
            hint.style.fontSize = 10;
            hint.style.marginTop = 4;
            root.Add(hint);

            RebuildListView();
        }

        private void OnDisable()
        {
            RunHistoryStore.Changed -= OnHistoryChanged;
        }

        private void OnDestroy()
        {
            _instance = null;
        }

        private void OnHistoryChanged()
        {
            // Reload from the store and rebuild the visible list. This runs
            // on whichever thread fired Changed; EditorPrefs writes happen
            // from the main thread today, so no marshal is needed — but if
            // that ever changes, route through EditorApplication.delayCall.
            _entries = RunHistoryStore.Load();
            if (_list != null)
            {
                _list.itemsSource = _entries;
                _list.RefreshItems();
            }
        }

        private void RebuildListView()
        {
            _list.itemsSource = _entries;
            _list.Rebuild();
        }

        // Each row: line-1 preview (first line, ellipsized), then a
        // size-meta in italics ("23 lines • 1.2 KB" or "1 line • 42 chars").
        private static VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Column;
            row.style.paddingLeft = 6;
            row.style.paddingRight = 6;
            row.style.paddingTop = 4;
            row.style.paddingBottom = 4;

            var preview = new Label();
            preview.AddToClassList("rrh-row-preview");
            preview.style.color = new StyleColor(new Color(0.88f, 0.88f, 0.88f));
            preview.style.overflow = Overflow.Hidden;
            preview.style.textOverflow = TextOverflow.Ellipsis;
            preview.style.whiteSpace = WhiteSpace.NoWrap;
            row.Add(preview);

            var meta = new Label();
            meta.AddToClassList("rrh-row-meta");
            meta.style.color = new StyleColor(new Color(0.5f, 0.5f, 0.5f));
            meta.style.fontSize = 10;
            meta.style.unityFontStyleAndWeight = FontStyle.Italic;
            row.Add(meta);

            return row;
        }

        private void BindRow(VisualElement element, int index)
        {
            if (index < 0 || index >= _entries.Count) return;
            var code = _entries[index] ?? string.Empty;
            var preview = element.Q<Label>(className: "rrh-row-preview");
            var meta = element.Q<Label>(className: "rrh-row-meta");

            int newlineIdx = code.IndexOf('\n');
            string firstLine = newlineIdx >= 0 ? code.Substring(0, newlineIdx) : code;
            preview.text = firstLine.Length > 0 ? firstLine : "(empty line)";

            int totalLines = 1;
            for (int i = 0; i < code.Length; i++)
                if (code[i] == '\n') totalLines++;
            meta.text = totalLines == 1
                ? $"1 line • {code.Length} chars"
                : $"{totalLines} lines • {code.Length} chars";
        }
    }
}
