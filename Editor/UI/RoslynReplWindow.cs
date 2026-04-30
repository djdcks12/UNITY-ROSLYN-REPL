using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using RoslynRepl.Editor.Core;
using RoslynRepl.Editor.Diagnostics;

namespace RoslynRepl.Editor.UI
{
    public class RoslynReplWindow : EditorWindow
    {
        private const string PackageRoot = "Packages/com.roslyn-repl";
        private const string UxmlPath = PackageRoot + "/Editor/UI/Layouts/RoslynReplWindow.uxml";
        private const string UssPath  = PackageRoot + "/Editor/UI/Layouts/RoslynReplWindow.uss";

        private const string SessionKey_CodeText = "RoslynRepl.CodeText";

        private const string DefaultCode =
@"// Roslyn REPL — write C# below, F5 or Ctrl+Enter to run.
// Use 'return X;' to surface a value. Debug.Log() output is captured.
return UnityEngine.Application.unityVersion;";

        private TextField _codeInput;
        private VisualElement _outputContent;
        private ScrollView _outputScroll;
        private Label _durationLabel;
        private Label _modeLabel;
        private Label _outputSummary;

        [MenuItem("Tools/Roslyn REPL/Open", priority = 10)]
        public static void Open()
        {
            var window = GetWindow<RoslynReplWindow>();
            window.titleContent = new GUIContent("Roslyn REPL");
            window.minSize = new Vector2(720, 480);
            window.Show();
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();

            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (uxml == null)
            {
                ShowFallback(root, $"UXML asset not found at:\n{UxmlPath}\n\nVerify the package was imported correctly.");
                return;
            }
            uxml.CloneTree(root);

            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (uss != null) root.styleSheets.Add(uss);

            BindControls(root);
        }

        private void BindControls(VisualElement root)
        {
            _codeInput     = root.Q<TextField>("code-input");
            _outputContent = root.Q<VisualElement>("output-content");
            _outputScroll  = root.Q<ScrollView>("output-scroll");
            _durationLabel = root.Q<Label>("duration-label");
            _modeLabel     = root.Q<Label>("mode-label");
            _outputSummary = root.Q<Label>("output-summary-label");

            // Code input: restore from session and persist on change
            if (_codeInput != null)
            {
                var saved = SessionState.GetString(SessionKey_CodeText, null);
                _codeInput.value = string.IsNullOrEmpty(saved) ? DefaultCode : saved;
                _codeInput.RegisterValueChangedCallback(evt =>
                    SessionState.SetString(SessionKey_CodeText, evt.newValue));
            }

            var runBtn = root.Q<Button>("run-btn");
            if (runBtn != null) runBtn.clicked += Run;

            var clearBtn = root.Q<Button>("clear-btn");
            if (clearBtn != null) clearBtn.clicked += ClearOutput;

            var verifyBtn = root.Q<Button>("verify-btn");
            if (verifyBtn != null) verifyBtn.clicked += () => SetupVerifier.Verify();

            UpdateModeLabel();
            // Dedupe: CreateGUI may be called multiple times (domain reload,
            // explicit rebuild). root.Clear() removes children but keeps
            // callbacks on root itself, and EditorApplication.playModeStateChanged
            // is a static event — both will accumulate without explicit unregister.
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;

            var versionLabel = root.Q<Label>("version-label");
            if (versionLabel != null) versionLabel.text = $"v{ReadPackageVersion()}";

            // F5 / Ctrl+Enter to run; intercept on TrickleDown so TextField
            // doesn't swallow the key first. Unregister first to avoid stacking
            // when CreateGUI rebuilds — otherwise one keypress triggers Run N times.
            root.UnregisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);

            ShowReadyMessage();
        }

        private void OnPlayModeChanged(PlayModeStateChange _) => UpdateModeLabel();

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            bool isF5         = evt.keyCode == KeyCode.F5;
            bool isCtrlReturn = evt.ctrlKey && (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter);
            if (isF5 || isCtrlReturn)
            {
                Run();
                evt.StopPropagation();
            }
        }

        private void Run()
        {
            if (_codeInput == null || _outputContent == null) return;

            var code = _codeInput.value ?? string.Empty;
            ClearOutput();
            AppendOutput($"▶ Running ({code.Length} chars)…", "info");

            var result = ReplEngine.Execute(code);
            RenderResult(result);
        }

        private void RenderResult(ReplResult result)
        {
            ClearOutput();

            // Captured logs first — but filter out background noise from other
            // Play Mode systems (server, ad SDK, etc.) that fired during the
            // Invoke window. ClassifyLogs in ReplEngine flags only logs whose
            // stack trace contains the generated wrapper class.
            foreach (var log in result.Logs)
            {
                if (!log.FromSnippet) continue;
                var sev = log.Type switch
                {
                    LogType.Error or LogType.Exception or LogType.Assert => "error",
                    LogType.Warning => "warning",
                    _ => "log"
                };
                AppendOutput($"[{log.Type}] {log.Message}", sev);
            }

            // Then the result, error, or diagnostics
            switch (result.Kind)
            {
                case ReplResultKind.Success:
                    // Suppress the synthetic "=> null" caused by the wrapper's
                    // fallback `return null;` for snippets that don't return a
                    // value. HasReturnValue is true only when Value != null, so
                    // an explicit `return null;` from user code is also hidden —
                    // a small acceptable false-negative; users wanting to surface
                    // a null indicator can `return "null"` (a non-null string).
                    if (result.HasReturnValue)
                        AppendResult(SimpleObjectSerializer.ToTree(result.Value));
                    break;

                case ReplResultKind.CompileError:
                    AppendOutput("Compilation failed:", "error");
                    foreach (var d in result.Diagnostics)
                    {
                        var prefix = d.IsInUserCode ? $"line {d.Line}, col {d.Column}" : "(internal)";
                        AppendOutput($"  {prefix}: {d.Code} — {d.Message}", "diagnostic");
                    }
                    break;

                case ReplResultKind.RuntimeError:
                    AppendOutput($"Runtime error: {result.ErrorMessage}", "error");
                    if (!string.IsNullOrEmpty(result.StackTrace))
                        AppendOutput(result.StackTrace, "diagnostic");
                    break;
            }

            UpdateStatusLabels(result);
            ScrollOutputToBottom();
        }

        private void UpdateStatusLabels(ReplResult result)
        {
            if (_durationLabel != null)
                _durationLabel.text = $"{result.Duration.TotalMilliseconds:0} ms";

            if (_outputSummary != null)
            {
                _outputSummary.text = result.Kind switch
                {
                    ReplResultKind.Success      => "OK",
                    ReplResultKind.CompileError => $"Compile error ({result.Diagnostics.Count})",
                    ReplResultKind.RuntimeError => "Runtime error",
                    _ => string.Empty
                };
            }
        }

        private void ClearOutput()
        {
            if (_outputContent != null) _outputContent.Clear();
            if (_outputSummary != null) _outputSummary.text = string.Empty;
        }

        private void AppendOutput(string text, string severity)
        {
            if (_outputContent == null) return;
            var label = new Label(text);
            label.AddToClassList("rr-output-line");
            label.AddToClassList($"rr-output-line--{severity}");
            label.style.whiteSpace = WhiteSpace.Normal;
            _outputContent.Add(label);
        }

        private void AppendResult(ReplValueNode root)
        {
            if (_outputContent == null || root == null) return;

            // Leaf result (primitive, string, Vector3, etc.) — render inline,
            // identical look to Phase 1.
            if (!root.IsExpandable || root.Children.Count == 0)
            {
                AppendOutput($"=> {root.Preview}", "result");
                return;
            }

            // Header above the tree
            var header = new Label($"=> {root.Preview}");
            header.AddToClassList("rr-output-line");
            header.AddToClassList("rr-output-line--result");
            _outputContent.Add(header);

            var tv = BuildResultTree(root);
            tv.AddToClassList("rr-result-tree");
            _outputContent.Add(tv);
        }

        private static MultiColumnTreeView BuildResultTree(ReplValueNode root)
        {
            var tv = new MultiColumnTreeView();
            tv.style.maxHeight = 360;
            tv.style.minHeight = 80;
            tv.style.flexGrow = 0;

            // --- columns ---
            tv.columns.Add(MakeColumn("name",  "Name",  220, n => n?.Name     ?? string.Empty, "rr-treecell--name", tv));
            tv.columns.Add(MakeColumn("type",  "Type",  160, n => n?.TypeName ?? string.Empty, "rr-treecell--type", tv));
            tv.columns.Add(MakeColumn("value", "Value", 320, n => n?.Preview  ?? string.Empty, "rr-treecell--value", tv));

            // --- data ---
            int nextId = 0;
            var rootItems = new List<TreeViewItemData<ReplValueNode>>
            {
                ToItemData(root, ref nextId)
            };
            tv.SetRootItems(rootItems);
            tv.Rebuild();
            tv.ExpandRootItems();
            return tv;
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
                minWidth = 80,
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
                foreach (var c in node.Children)
                    children.Add(ToItemData(c, ref nextId));
            }
            return new TreeViewItemData<ReplValueNode>(nextId++, node, children);
        }

        private void ScrollOutputToBottom()
        {
            if (_outputScroll == null) return;
            EditorApplication.delayCall += () =>
            {
                if (_outputScroll == null) return;
                _outputScroll.verticalScroller.value = _outputScroll.verticalScroller.highValue;
            };
        }

        private void ShowReadyMessage()
        {
            ClearOutput();
            AppendOutput("Roslyn REPL ready. Press F5 or Ctrl+Enter to run.", "info");
        }

        private void UpdateModeLabel()
        {
            if (_modeLabel == null) return;
            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
            _modeLabel.text = playing ? "PLAY" : "EDIT";
            _modeLabel.RemoveFromClassList("rr-badge--play");
            _modeLabel.RemoveFromClassList("rr-badge--edit");
            _modeLabel.AddToClassList(playing ? "rr-badge--play" : "rr-badge--edit");
        }

        private static string ReadPackageVersion()
        {
            try
            {
                var json = System.IO.File.ReadAllText(PackageRoot + "/package.json");
                const string key = "\"version\"";
                var i = json.IndexOf(key, StringComparison.Ordinal);
                if (i < 0) return "?";
                i = json.IndexOf('"', i + key.Length + 1);
                if (i < 0) return "?";
                var j = json.IndexOf('"', i + 1);
                return j > i ? json.Substring(i + 1, j - i - 1) : "?";
            }
            catch
            {
                return "?";
            }
        }

        private static void ShowFallback(VisualElement root, string message)
        {
            var label = new Label(message);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.paddingLeft = 12;
            label.style.paddingTop = 12;
            root.Add(label);
        }
    }
}
