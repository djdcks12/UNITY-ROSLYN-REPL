using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace RoslynRepl.Editor.UI
{
    public class RoslynReplWindow : EditorWindow
    {
        private const string PackageRoot = "Packages/com.roslyn-repl";
        private const string UxmlPath = PackageRoot + "/Editor/UI/Layouts/RoslynReplWindow.uxml";
        private const string UssPath  = PackageRoot + "/Editor/UI/Layouts/RoslynReplWindow.uss";

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
            var version = ReadPackageVersion();
            var versionLabel = root.Q<Label>("version-label");
            if (versionLabel != null) versionLabel.text = $"v{version}";

            var modeLabel = root.Q<Label>("mode-label");
            if (modeLabel != null)
            {
                UpdateModeLabel(modeLabel);
                EditorApplication.playModeStateChanged += _ => UpdateModeLabel(modeLabel);
            }

            var verifyBtn = root.Q<Button>("verify-btn");
            if (verifyBtn != null)
            {
                verifyBtn.clicked += () => Diagnostics.SetupVerifier.Verify();
            }

            var statusLabel = root.Q<Label>("status-label");
            if (statusLabel != null)
            {
                statusLabel.text = "Phase 0 skeleton loaded. Phase 1 will add the code editor and execution engine.";
            }
        }

        private static void UpdateModeLabel(Label label)
        {
            label.text = EditorApplication.isPlayingOrWillChangePlaymode ? "PLAY" : "EDIT";
            label.RemoveFromClassList("rr-badge--play");
            label.RemoveFromClassList("rr-badge--edit");
            label.AddToClassList(EditorApplication.isPlayingOrWillChangePlaymode ? "rr-badge--play" : "rr-badge--edit");
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
