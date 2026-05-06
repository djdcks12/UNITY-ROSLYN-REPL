using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using RoslynRepl.Editor.Core;

namespace RoslynRepl.Editor.UI
{
    /// <summary>
    /// Auxiliary popup for managing named snippets. Provides three actions
    /// per row — Load (sends the snippet's code back to the host editor
    /// via <see cref="OnSnippetChosen"/>), Rename (prompts for a new name),
    /// and Delete (with confirmation). The Save form at the bottom asks the
    /// host for its current editor contents via <see cref="OnSaveRequested"/>;
    /// the host commits via <see cref="SnippetStore.Save"/>. Keeping the
    /// "fetch the live code" step in the host avoids the popup going stale
    /// the moment the user edits the host buffer after opening the popup.
    /// </summary>
    public class SnippetLibraryWindow : EditorWindow
    {
        private static SnippetLibraryWindow _instance;

        public static event Action<string> OnSnippetChosen;
        public static event Action<string> OnSaveRequested;

        public static void Open()
        {
            if (_instance != null)
            {
                _instance.Refresh();
                _instance.Focus();
                return;
            }
            _instance = CreateInstance<SnippetLibraryWindow>();
            _instance.titleContent = new GUIContent("REPL Snippets");
            _instance.minSize = new Vector2(440, 360);
            _instance.ShowUtility();
        }

        /// <summary>Re-load the list — host calls this after Save commits.</summary>
        public static void NotifyChanged()
        {
            if (_instance != null) _instance.Refresh();
        }

        private List<SnippetEntry> _entries = new();
        private ListView _list;
        private TextField _nameField;

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.paddingLeft = 6;
            root.style.paddingRight = 6;
            root.style.paddingTop = 6;
            root.style.paddingBottom = 6;
            root.style.flexDirection = FlexDirection.Column;

            var title = new Label("Saved snippets");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = new StyleColor(new Color(0.85f, 0.85f, 0.85f));
            title.style.marginBottom = 4;
            root.Add(title);

            _list = new ListView
            {
                fixedItemHeight = 56,
                selectionType = SelectionType.Single,
                showBorder = true,
                makeItem = MakeRow,
                bindItem = BindRow,
            };
            _list.style.flexGrow = 1;
            // Double-click on the row body acts as Load — same convention
            // as RunHistoryWindow so the two popups feel consistent.
            _list.itemsChosen += chosen =>
            {
                foreach (var item in chosen)
                {
                    if (item is SnippetEntry e)
                    {
                        OnSnippetChosen?.Invoke(e.Code ?? string.Empty);
                        return;
                    }
                }
            };
            root.Add(_list);

            // Save form
            var saveBox = new VisualElement();
            saveBox.style.marginTop = 8;
            saveBox.style.flexDirection = FlexDirection.Column;

            var saveTitle = new Label("Save current editor contents as…");
            saveTitle.style.color = new StyleColor(new Color(0.7f, 0.7f, 0.7f));
            saveTitle.style.fontSize = 11;
            saveTitle.style.marginBottom = 2;
            saveBox.Add(saveTitle);

            var saveRow = new VisualElement();
            saveRow.style.flexDirection = FlexDirection.Row;
            saveRow.style.alignItems = Align.Center;

            _nameField = new TextField();
            _nameField.style.flexGrow = 1;
            _nameField.style.marginRight = 4;
            _nameField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    SubmitSave();
                    evt.StopPropagation();
                }
            });
            saveRow.Add(_nameField);

            var saveBtn = new Button(SubmitSave) { text = "Save" };
            saveRow.Add(saveBtn);
            saveBox.Add(saveRow);

            var hint = new Label("Saving an existing name overwrites it.");
            hint.style.color = new StyleColor(new Color(0.5f, 0.5f, 0.5f));
            hint.style.fontSize = 10;
            hint.style.marginTop = 2;
            saveBox.Add(hint);

            root.Add(saveBox);

            Refresh();
        }

        private void OnDestroy() => _instance = null;

        private void Refresh()
        {
            _entries = SnippetStore.Load();
            if (_list != null)
            {
                _list.itemsSource = _entries;
                _list.Rebuild();
            }
        }

        private void SubmitSave()
        {
            var name = _nameField.value?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                EditorUtility.DisplayDialog("Name required",
                    "Type a snippet name before saving.", "OK");
                return;
            }
            if (SnippetStore.Exists(name))
            {
                if (!EditorUtility.DisplayDialog("Overwrite snippet?",
                    $"\"{name}\" already exists. Overwrite it with the current editor contents?",
                    "Overwrite", "Cancel"))
                {
                    return;
                }
            }
            // The host owns the live code — let it pull and commit.
            OnSaveRequested?.Invoke(name);
            _nameField.SetValueWithoutNotify(string.Empty);
        }

        private static VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = 6;
            row.style.paddingRight = 6;
            row.style.paddingTop = 4;
            row.style.paddingBottom = 4;

            var info = new VisualElement();
            info.AddToClassList("rrs-row-info");
            info.style.flexGrow = 1;
            info.style.flexDirection = FlexDirection.Column;

            var name = new Label();
            name.AddToClassList("rrs-row-name");
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.color = new StyleColor(new Color(0.9f, 0.9f, 0.9f));
            info.Add(name);

            var preview = new Label();
            preview.AddToClassList("rrs-row-preview");
            preview.style.color = new StyleColor(new Color(0.55f, 0.55f, 0.55f));
            preview.style.fontSize = 10;
            preview.style.overflow = Overflow.Hidden;
            preview.style.textOverflow = TextOverflow.Ellipsis;
            preview.style.whiteSpace = WhiteSpace.NoWrap;
            info.Add(preview);

            row.Add(info);

            var loadBtn = new Button { text = "Load" };
            loadBtn.AddToClassList("rrs-row-load");
            loadBtn.style.minWidth = 50;
            row.Add(loadBtn);

            var renameBtn = new Button { text = "Rename" };
            renameBtn.AddToClassList("rrs-row-rename");
            renameBtn.style.minWidth = 60;
            row.Add(renameBtn);

            var deleteBtn = new Button { text = "✕" };
            deleteBtn.AddToClassList("rrs-row-delete");
            deleteBtn.style.minWidth = 22;
            row.Add(deleteBtn);

            return row;
        }

        private void BindRow(VisualElement element, int index)
        {
            if (index < 0 || index >= _entries.Count) return;
            var entry = _entries[index];

            element.Q<Label>(className: "rrs-row-name").text = entry.Name ?? "(unnamed)";

            string code = entry.Code ?? string.Empty;
            int newlineIdx = code.IndexOf('\n');
            string firstLine = newlineIdx >= 0 ? code.Substring(0, newlineIdx) : code;
            element.Q<Label>(className: "rrs-row-preview").text =
                string.IsNullOrEmpty(firstLine) ? "(empty)" : firstLine;

            // Buttons: re-bind on every row reuse so closures capture the
            // right entry instead of the one that was bound when the row
            // was first pooled.
            var loadBtn = element.Q<Button>(className: "rrs-row-load");
            loadBtn.clickable = new Clickable(() =>
            {
                OnSnippetChosen?.Invoke(entry.Code ?? string.Empty);
            });

            var renameBtn = element.Q<Button>(className: "rrs-row-rename");
            renameBtn.clickable = new Clickable(() =>
            {
                var prompt = $"Rename \"{entry.Name}\" to:";
                var newName = PromptForString("Rename snippet", prompt, entry.Name);
                if (string.IsNullOrWhiteSpace(newName)) return;
                newName = newName.Trim();
                if (newName == entry.Name) return;
                if (SnippetStore.Exists(newName))
                {
                    EditorUtility.DisplayDialog("Name taken",
                        $"A snippet named \"{newName}\" already exists. Rename it first or pick a different name.",
                        "OK");
                    return;
                }
                SnippetStore.Rename(entry.Name, newName);
                Refresh();
            });

            var deleteBtn = element.Q<Button>(className: "rrs-row-delete");
            deleteBtn.clickable = new Clickable(() =>
            {
                if (EditorUtility.DisplayDialog("Delete snippet?",
                    $"Delete \"{entry.Name}\"? This cannot be undone.",
                    "Delete", "Cancel"))
                {
                    SnippetStore.Delete(entry.Name);
                    Refresh();
                }
            });
        }

        // Built-in EditorUtility doesn't ship a string-prompt dialog —
        // re-implement the minimum we need with a tiny modal IMGUI window.
        private static string PromptForString(string title, string label, string defaultValue)
        {
            return SimpleStringPrompt.Show(title, label, defaultValue);
        }

        private class SimpleStringPrompt : EditorWindow
        {
            private string _label;
            private string _value;
            private bool _confirmed;
            private bool _shouldClose;

            public static string Show(string title, string label, string defaultValue)
            {
                var w = CreateInstance<SimpleStringPrompt>();
                w.titleContent = new GUIContent(title);
                w._label = label;
                w._value = defaultValue ?? string.Empty;
                w.minSize = new Vector2(320, 90);
                w.maxSize = new Vector2(560, 90);
                w.ShowModal(); // blocks until window closes
                return w._confirmed ? w._value : null;
            }

            private void OnGUI()
            {
                GUILayout.Space(6);
                EditorGUILayout.LabelField(_label);
                GUI.SetNextControlName("input");
                _value = EditorGUILayout.TextField(_value);
                EditorGUI.FocusTextInControl("input");
                GUILayout.FlexibleSpace();
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Cancel", GUILayout.Width(80)))
                    {
                        _confirmed = false;
                        _shouldClose = true;
                    }
                    if (GUILayout.Button("OK", GUILayout.Width(80)))
                    {
                        _confirmed = true;
                        _shouldClose = true;
                    }
                }
                if (Event.current.type == EventType.KeyDown)
                {
                    if (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                    {
                        _confirmed = true;
                        _shouldClose = true;
                        Event.current.Use();
                    }
                    else if (Event.current.keyCode == KeyCode.Escape)
                    {
                        _confirmed = false;
                        _shouldClose = true;
                        Event.current.Use();
                    }
                }
                if (_shouldClose) Close();
            }
        }
    }
}
