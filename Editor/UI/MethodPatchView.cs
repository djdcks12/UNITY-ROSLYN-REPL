using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using RoslynRepl.Editor.Patches;

namespace RoslynRepl.Editor.UI
{
    /// <summary>
    /// In-window panel for the Runtime Method Patch feature
    /// (issue #14, Phase A MVP). Mounts inside the main REPL window's
    /// shared bottom area — same physical space as the Output panel,
    /// switched via the Output / Patches mode tab in the pane header.
    ///
    /// Phase A constraints from the engine:
    ///   • void instance methods only
    ///   • private members reached through __get / __set / __call
    /// </summary>
    public class MethodPatchView
    {
        private const string DefaultBody =
@"// Phase A MVP scope: void instance methods.
// Use __get<T>(""name"") / __set(""name"", value) / __call<T>(""name"", args)
// to reach private members of the target.
UnityEngine.Debug.Log(""[patched] "" + __instance.GetType().Name);";

        private readonly VisualElement _host;
        private TextField _targetField;
        private TextField _methodField;
        private TextField _paramsField;
        private TextField _bodyField;
        private Label _statusLabel;
        private VisualElement _activeListContainer;

        public MethodPatchView(VisualElement host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            BuildLayout();

            PatchRegistry.Changed -= OnRegistryChanged;
            PatchRegistry.Changed += OnRegistryChanged;
        }

        public void Dispose()
        {
            PatchRegistry.Changed -= OnRegistryChanged;
        }

        private void OnRegistryChanged()
        {
            if (_activeListContainer != null) RebuildActiveList();
            UpdateStatusForCurrentForm();
        }

        private void BuildLayout()
        {
            _host.Clear();
            _host.AddToClassList("rr-patch-view");
            _host.style.paddingLeft = 6;
            _host.style.paddingRight = 6;
            _host.style.paddingTop = 4;
            _host.style.paddingBottom = 4;
            _host.style.flexDirection = FlexDirection.Column;

            var subtitle = new Label("Phase A MVP — redirect a void instance method's calls to a runtime-compiled body. Reverts cleanly. Survives until next domain reload.");
            subtitle.style.color = new StyleColor(new Color(0.55f, 0.55f, 0.55f));
            subtitle.style.fontSize = 10;
            subtitle.style.whiteSpace = WhiteSpace.Normal;
            subtitle.style.marginBottom = 4;
            _host.Add(subtitle);

            _targetField = new TextField("Target type") { tooltip = "Full type name including namespace, e.g. MyGame.GameManager" };
            _methodField = new TextField("Method name") { tooltip = "Method to redirect — must be a void instance method" };
            _paramsField = new TextField("Parameter types") { tooltip = "Comma-joined full type names. Empty = no parameters. Example: System.Int32,System.String" };
            _host.Add(_targetField);
            _host.Add(_methodField);
            _host.Add(_paramsField);

            var bodyLabel = new Label("Patch body");
            bodyLabel.style.marginTop = 4;
            bodyLabel.style.marginBottom = 1;
            bodyLabel.style.fontSize = 10;
            bodyLabel.style.color = new StyleColor(new Color(0.7f, 0.7f, 0.7f));
            _host.Add(bodyLabel);

            _bodyField = new TextField { multiline = true, value = DefaultBody };
            _bodyField.style.minHeight = 110;
            _bodyField.style.flexGrow = 0;
            _bodyField.style.whiteSpace = WhiteSpace.Normal;
            _host.Add(_bodyField);

            var actionRow = new VisualElement();
            actionRow.style.flexDirection = FlexDirection.Row;
            actionRow.style.alignItems = Align.Center;
            actionRow.style.marginTop = 4;
            actionRow.style.marginBottom = 4;

            var applyBtn = new Button(OnApplyClicked) { text = "Apply Patch" };
            applyBtn.style.marginRight = 4;
            actionRow.Add(applyBtn);

            var revertBtn = new Button(OnRevertClicked) { text = "Revert" };
            revertBtn.style.marginRight = 4;
            actionRow.Add(revertBtn);

            var revertAllBtn = new Button(OnRevertAllClicked) { text = "Revert All" };
            actionRow.Add(revertAllBtn);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            actionRow.Add(spacer);

            _statusLabel = new Label("Status: idle");
            _statusLabel.style.color = new StyleColor(new Color(0.65f, 0.65f, 0.65f));
            _statusLabel.style.fontSize = 11;
            actionRow.Add(_statusLabel);

            _host.Add(actionRow);

            var listTitle = new Label("Active patches");
            listTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            listTitle.style.color = new StyleColor(new Color(0.8f, 0.8f, 0.8f));
            listTitle.style.fontSize = 10;
            listTitle.style.marginTop = 2;
            listTitle.style.marginBottom = 1;
            _host.Add(listTitle);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.style.minHeight = 60;
            scroll.style.backgroundColor = new StyleColor(new Color(0.14f, 0.14f, 0.14f));

            _activeListContainer = new VisualElement();
            scroll.Add(_activeListContainer);
            _host.Add(scroll);

            RebuildActiveList();
        }

        public void LoadIntoForm(MethodPatchSpec spec)
        {
            if (spec == null) return;
            _targetField.SetValueWithoutNotify(spec.TargetTypeName);
            _methodField.SetValueWithoutNotify(spec.MethodName);
            _paramsField.SetValueWithoutNotify(spec.ParameterTypes ?? string.Empty);
            _bodyField.SetValueWithoutNotify(spec.PatchBody ?? string.Empty);
            UpdateStatusForCurrentForm();
        }

        private void OnApplyClicked()
        {
            var spec = new MethodPatchSpec
            {
                TargetTypeName = _targetField.value?.Trim() ?? string.Empty,
                MethodName     = _methodField.value?.Trim() ?? string.Empty,
                ParameterTypes = _paramsField.value?.Trim() ?? string.Empty,
                PatchBody      = _bodyField.value ?? string.Empty,
            };

            if (string.IsNullOrEmpty(spec.TargetTypeName) || string.IsNullOrEmpty(spec.MethodName))
            {
                SetStatus("Target type and method name are required.", error: true);
                return;
            }

            try
            {
                PatchEngine.Apply(spec);
                SetStatus($"Applied: {spec.TargetTypeName}.{spec.MethodName}", error: false);
            }
            catch (Exception ex)
            {
                spec.Status = PatchStatus.Failed;
                spec.LastError = ex.Message;
                PatchRegistry.AddOrUpdate(spec);
                SetStatus(ex.Message, error: true);
            }
        }

        private void OnRevertClicked()
        {
            var spec = PatchRegistry.Find(
                _targetField.value?.Trim() ?? string.Empty,
                _methodField.value?.Trim() ?? string.Empty,
                _paramsField.value?.Trim() ?? string.Empty);
            if (spec == null)
            {
                SetStatus("No patch matching that target is registered.", error: true);
                return;
            }
            PatchEngine.Revert(spec);
            // Phase A is in-memory only — once reverted there's no
            // reason to keep the spec around. Phase B persistence will
            // revisit.
            PatchRegistry.Remove(spec);
            SetStatus($"Reverted: {spec.TargetTypeName}.{spec.MethodName}", error: false);
        }

        private void OnRevertAllClicked()
        {
            int n = PatchEngine.RevertAll();
            foreach (var s in PatchRegistry.Specs.ToList()) PatchRegistry.Remove(s);
            SetStatus($"Reverted {n} patch{(n == 1 ? "" : "es")}.", error: false);
        }

        private void SetStatus(string message, bool error)
        {
            if (_statusLabel == null) return;
            _statusLabel.text = "Status: " + message;
            _statusLabel.style.color = new StyleColor(error
                ? new Color(0.95f, 0.55f, 0.55f)
                : new Color(0.55f, 0.85f, 0.65f));
        }

        private void UpdateStatusForCurrentForm()
        {
            var spec = PatchRegistry.Find(
                _targetField?.value?.Trim() ?? string.Empty,
                _methodField?.value?.Trim() ?? string.Empty,
                _paramsField?.value?.Trim() ?? string.Empty);
            if (spec == null) return;
            switch (spec.Status)
            {
                case PatchStatus.Active:   SetStatus($"Active on {spec.TargetTypeName}.{spec.MethodName}", error: false); break;
                case PatchStatus.Failed:   SetStatus(spec.LastError ?? "failed", error: true); break;
                case PatchStatus.Inactive: SetStatus("idle", error: false); break;
            }
        }

        private void RebuildActiveList()
        {
            if (_activeListContainer == null) return;
            _activeListContainer.Clear();

            var specs = PatchRegistry.Specs.ToList();
            if (specs.Count == 0)
            {
                var empty = new Label("(no active patches)");
                empty.style.color = new StyleColor(new Color(0.45f, 0.45f, 0.45f));
                empty.style.paddingLeft = 6;
                empty.style.paddingTop = 6;
                empty.style.paddingBottom = 6;
                _activeListContainer.Add(empty);
                return;
            }

            foreach (var s in specs)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.paddingLeft = 6;
                row.style.paddingRight = 4;
                row.style.paddingTop = 3;
                row.style.paddingBottom = 3;
                row.style.borderBottomWidth = 1;
                row.style.borderBottomColor = new StyleColor(new Color(0.25f, 0.25f, 0.25f));

                var dot = new VisualElement();
                dot.style.width = 8;
                dot.style.height = 8;
                dot.style.marginRight = 6;
                dot.style.borderTopLeftRadius = dot.style.borderTopRightRadius =
                dot.style.borderBottomLeftRadius = dot.style.borderBottomRightRadius = 4;
                dot.style.backgroundColor = new StyleColor(s.Status switch
                {
                    PatchStatus.Active   => new Color(0.5f, 0.8f, 0.5f),
                    PatchStatus.Failed   => new Color(0.95f, 0.55f, 0.55f),
                    _                    => new Color(0.55f, 0.55f, 0.55f),
                });
                row.Add(dot);

                var info = new Label($"{s.TargetTypeName}.{s.MethodName}({s.ParameterTypes})");
                info.style.flexGrow = 1;
                info.style.color = new StyleColor(new Color(0.88f, 0.88f, 0.88f));
                info.tooltip = s.Status == PatchStatus.Failed && !string.IsNullOrEmpty(s.LastError)
                    ? s.LastError
                    : null;
                row.Add(info);

                var loadBtn = new Button(() => LoadIntoForm(s)) { text = "Load" };
                loadBtn.style.minWidth = 50;
                row.Add(loadBtn);

                var revertBtn = new Button(() =>
                {
                    PatchEngine.Revert(s);
                    PatchRegistry.Remove(s);
                })
                { text = "Revert" };
                revertBtn.style.minWidth = 60;
                row.Add(revertBtn);

                _activeListContainer.Add(row);
            }
        }
    }
}
