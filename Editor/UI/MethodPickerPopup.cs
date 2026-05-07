using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace RoslynRepl.Editor.UI
{
    /// <summary>
    /// Pop-up that lists every patchable method on a target type so the
    /// user doesn't have to remember the exact name + parameter
    /// signature. Filters to Phase A's scope (void instance methods)
    /// because picking a non-patchable method wastes a click — those
    /// would just fail at Apply.
    ///
    /// Two entry points share this picker: the Patches form's
    /// "Browse Methods" button and the Object Browser's
    /// double-click in Patches mode. Both pass a Type plus a callback
    /// that receives the chosen <see cref="MethodInfo"/>.
    /// </summary>
    public class MethodPickerPopup : EditorWindow
    {
        public static void Open(Type targetType, Action<MethodInfo> onChosen)
        {
            if (targetType == null) throw new ArgumentNullException(nameof(targetType));
            var w = CreateInstance<MethodPickerPopup>();
            w._targetType = targetType;
            w._onChosen = onChosen;
            w.titleContent = new GUIContent($"Pick method on {targetType.Name}");
            w.minSize = new Vector2(420, 320);
            w.ShowUtility();
        }

        private Type _targetType;
        private Action<MethodInfo> _onChosen;
        private List<MethodInfo> _methods;
        private List<MethodInfo> _filtered;
        private ListView _list;
        private ToolbarSearchField _search;

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            root.style.flexDirection = FlexDirection.Column;
            root.style.paddingLeft = 6;
            root.style.paddingRight = 6;
            root.style.paddingTop = 6;
            root.style.paddingBottom = 6;

            var title = new Label(_targetType?.FullName ?? "<unknown>");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = new StyleColor(new Color(0.85f, 0.85f, 0.85f));
            title.style.marginBottom = 2;
            root.Add(title);

            var sub = new Label("Phase A scope: void instance methods only.");
            sub.style.fontSize = 10;
            sub.style.color = new StyleColor(new Color(0.55f, 0.55f, 0.55f));
            sub.style.marginBottom = 4;
            root.Add(sub);

            _search = new ToolbarSearchField();
            _search.style.marginBottom = 4;
            _search.RegisterValueChangedCallback(_ => RefreshFilter());
            root.Add(_search);

            // Collect every declared + inherited instance method that
            // matches the Phase A scope. Exclude property accessors
            // (`get_X`, `set_X`) — those are special-cased to mention
            // they're unsupported, and the bare `MethodInfo.IsSpecial-
            // Name` flag catches them.
            const BindingFlags bf = BindingFlags.Instance
                                  | BindingFlags.Public
                                  | BindingFlags.NonPublic
                                  | BindingFlags.FlattenHierarchy;
            _methods = _targetType.GetMethods(bf)
                .Where(m => m.ReturnType == typeof(void))
                .Where(m => !m.IsSpecialName)            // hide getters/setters/event accessors
                .Where(m => !m.IsGenericMethodDefinition) // hide generics — Phase A out of scope
                .Where(m => !HasRefOrOut(m))             // hide ref/out — same
                .OrderBy(m => m.Name)
                .ThenBy(m => m.GetParameters().Length)
                .ToList();

            _filtered = new List<MethodInfo>(_methods);

            _list = new ListView
            {
                fixedItemHeight = 38,
                selectionType = SelectionType.Single,
                showBorder = true,
                makeItem = MakeRow,
                bindItem = BindRow,
            };
            _list.style.flexGrow = 1;
            _list.itemsChosen += picked =>
            {
                foreach (var item in picked)
                {
                    if (item is MethodInfo m) { Choose(m); return; }
                }
            };
            root.Add(_list);

            // Footer actions: explicit Pick / Cancel for users who
            // single-clicked rather than double-clicked.
            var footer = new VisualElement();
            footer.style.flexDirection = FlexDirection.Row;
            footer.style.marginTop = 4;

            var hint = new Label($"{_methods.Count} method{(_methods.Count == 1 ? "" : "s")} available");
            hint.style.color = new StyleColor(new Color(0.5f, 0.5f, 0.5f));
            hint.style.fontSize = 10;
            hint.style.flexGrow = 1;
            footer.Add(hint);

            var pickBtn = new Button(() =>
            {
                if (_list.selectedItem is MethodInfo m) Choose(m);
            }) { text = "Pick" };
            pickBtn.style.minWidth = 60;
            footer.Add(pickBtn);

            var cancelBtn = new Button(Close) { text = "Cancel" };
            cancelBtn.style.minWidth = 60;
            footer.Add(cancelBtn);

            root.Add(footer);

            RefreshFilter();
        }

        private static bool HasRefOrOut(MethodInfo m)
        {
            foreach (var p in m.GetParameters())
                if (p.ParameterType.IsByRef) return true;
            return false;
        }

        private void RefreshFilter()
        {
            string q = _search?.value;
            _filtered = string.IsNullOrEmpty(q)
                ? new List<MethodInfo>(_methods)
                : _methods.Where(m => m.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (_list != null)
            {
                _list.itemsSource = _filtered;
                _list.Rebuild();
            }
        }

        private static VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Column;
            row.style.paddingLeft = 6;
            row.style.paddingRight = 6;
            row.style.paddingTop = 4;
            row.style.paddingBottom = 4;

            var name = new Label();
            name.AddToClassList("rrmp-name");
            name.style.color = new StyleColor(new Color(0.9f, 0.9f, 0.9f));
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.fontSize = 12;
            row.Add(name);

            var sig = new Label();
            sig.AddToClassList("rrmp-sig");
            sig.style.color = new StyleColor(new Color(0.6f, 0.7f, 0.85f));
            sig.style.fontSize = 10;
            sig.style.unityFontStyleAndWeight = FontStyle.Italic;
            row.Add(sig);

            return row;
        }

        private void BindRow(VisualElement element, int index)
        {
            if (index < 0 || index >= _filtered.Count) return;
            var m = _filtered[index];
            var name = element.Q<Label>(className: "rrmp-name");
            var sig = element.Q<Label>(className: "rrmp-sig");

            string visibility =
                m.IsPublic    ? "public"
              : m.IsPrivate   ? "private"
              : m.IsFamily    ? "protected"
              : m.IsAssembly  ? "internal"
              : m.IsFamilyOrAssembly ? "protected internal"
              : "?";

            name.text = m.Name + RenderParams(m);
            sig.text = $"{visibility} void  •  declared on {m.DeclaringType?.Name ?? "?"}";
        }

        private static string RenderParams(MethodInfo m)
        {
            var ps = m.GetParameters();
            if (ps.Length == 0) return "()";
            return "(" + string.Join(", ", ps.Select(p => p.ParameterType.Name + " " + p.Name)) + ")";
        }

        private void Choose(MethodInfo m)
        {
            try { _onChosen?.Invoke(m); }
            finally { Close(); }
        }
    }
}
