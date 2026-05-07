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
            // Layout philosophy after the "outer scroll feels weird"
            // feedback: the host pane has its own boundaries, so don't
            // wrap the whole view in a ScrollView (that moves form +
            // body + list together when the user just wants to scroll
            // the body text). Instead lay out form / body / actions /
            // list as a flex column, with two specific elements that
            // *do* scroll on their own:
            //   • body field: multiline TextField inside a vertical
            //     ScrollView so long edits scroll inside the editor
            //     while the form/actions/list stay anchored.
            //   • active patches list: its own vertical ScrollView at
            //     the bottom, fixed height, so a long list doesn't
            //     push the body off-screen.
            _host.Clear();
            _host.AddToClassList("rr-patch-view");
            _host.style.flexDirection = FlexDirection.Column;
            _host.style.flexGrow = 1;
            _host.style.paddingLeft = 6;
            _host.style.paddingRight = 6;
            _host.style.paddingTop = 4;
            _host.style.paddingBottom = 4;

            // Compact form row: Target / Method / Parameter types in
            // one horizontal line. Saves ~3× the vertical space the
            // stacked TextFields used to take.
            var formRow = new VisualElement();
            formRow.style.flexDirection = FlexDirection.Row;
            formRow.style.alignItems = Align.Center;
            formRow.style.marginBottom = 4;
            formRow.style.flexShrink = 0;

            _targetField = new TextField("Type") { tooltip = "Full type name including namespace, e.g. MyGame.GameManager" };
            _targetField.style.flexGrow = 2;
            _targetField.style.marginRight = 4;
            formRow.Add(_targetField);

            _methodField = new TextField("Method") { tooltip = "Method to redirect — must be a void instance method" };
            _methodField.style.flexGrow = 1;
            _methodField.style.marginRight = 4;
            formRow.Add(_methodField);

            _paramsField = new TextField("Params") { tooltip = "Comma-joined full type names. Empty = no parameters. Example: System.Int32,System.String" };
            _paramsField.style.flexGrow = 1;
            formRow.Add(_paramsField);

            _host.Add(formRow);

            // Body header — Pull Original on the right.
            var bodyHeader = new VisualElement();
            bodyHeader.style.flexDirection = FlexDirection.Row;
            bodyHeader.style.alignItems = Align.Center;
            bodyHeader.style.marginTop = 2;
            bodyHeader.style.marginBottom = 1;
            bodyHeader.style.flexShrink = 0;

            var bodyLabel = new Label("Patch body");
            bodyLabel.style.fontSize = 10;
            bodyLabel.style.flexGrow = 1;
            bodyLabel.style.color = new StyleColor(new Color(0.7f, 0.7f, 0.7f));
            bodyHeader.Add(bodyLabel);

            var pullBtn = new Button(OnPullOriginalClicked) { text = "Pull Original" };
            pullBtn.style.fontSize = 10;
            pullBtn.tooltip =
                "Locate the target method's .cs source and copy its body into the editor.\n" +
                "Use as the starting point for an in-place edit. Phase C MVP: void instance methods,\n" +
                "block bodies (`{ … }`), source must live in Assets/ or Packages/.";
            bodyHeader.Add(pullBtn);
            _host.Add(bodyHeader);

            // Body editor: TextField inside a vertical ScrollView. The
            // TextField itself doesn't constrain height, so a long
            // patch body extends inside the ScrollView and the user
            // scrolls *inside the editor* without the rest of the
            // view moving. flex-grow=1 on the ScrollView absorbs the
            // leftover pane height.
            var bodyScroll = new ScrollView(ScrollViewMode.Vertical);
            bodyScroll.style.flexGrow = 1;
            bodyScroll.style.minHeight = 140;
            bodyScroll.style.backgroundColor = new StyleColor(new Color(0.14f, 0.14f, 0.14f));

            _bodyField = new TextField { multiline = true, value = DefaultBody };
            _bodyField.style.whiteSpace = WhiteSpace.Normal;
            _bodyField.style.flexGrow = 1;
            // No explicit height — let the inner content drive it so
            // the parent ScrollView is the one that scrolls. Some
            // platforms cap unbounded TextField height at zero, so
            // give it a sane minimum.
            _bodyField.style.minHeight = 200;
            bodyScroll.Add(_bodyField);
            _host.Add(bodyScroll);

            // Actions + status. Single row, doesn't scroll.
            var actionRow = new VisualElement();
            actionRow.style.flexDirection = FlexDirection.Row;
            actionRow.style.alignItems = Align.Center;
            actionRow.style.marginTop = 4;
            actionRow.style.marginBottom = 4;
            actionRow.style.flexShrink = 0;

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
            _statusLabel.style.whiteSpace = WhiteSpace.Normal;
            _statusLabel.style.flexShrink = 1;
            actionRow.Add(_statusLabel);

            _host.Add(actionRow);

            var listTitle = new Label("Active patches");
            listTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            listTitle.style.color = new StyleColor(new Color(0.8f, 0.8f, 0.8f));
            listTitle.style.fontSize = 10;
            listTitle.style.marginTop = 2;
            listTitle.style.marginBottom = 1;
            listTitle.style.flexShrink = 0;
            _host.Add(listTitle);

            // Active patches: own vertical ScrollView with a fixed
            // max-height share so a long list doesn't push the body
            // editor offscreen. flex-shrink=0 so the column layout
            // doesn't squeeze it to zero when the body is large.
            var listScroll = new ScrollView(ScrollViewMode.Vertical);
            listScroll.style.flexShrink = 0;
            listScroll.style.minHeight = 80;
            listScroll.style.maxHeight = 160;
            listScroll.style.backgroundColor = new StyleColor(new Color(0.14f, 0.14f, 0.14f));

            _activeListContainer = new VisualElement();
            listScroll.Add(_activeListContainer);
            _host.Add(listScroll);

            RebuildActiveList();
        }

        private void OnPullOriginalClicked()
        {
            var spec = new MethodPatchSpec
            {
                TargetTypeName = _targetField.value?.Trim() ?? string.Empty,
                MethodName     = _methodField.value?.Trim() ?? string.Empty,
                ParameterTypes = _paramsField.value?.Trim() ?? string.Empty,
            };
            if (string.IsNullOrEmpty(spec.TargetTypeName) || string.IsNullOrEmpty(spec.MethodName))
            {
                SetStatus("Target type and method name are required to pull source.", error: true);
                return;
            }

            var method = PatchEngine.TryResolveTargetMethod(spec, out var resolveErr);
            if (method == null)
            {
                SetStatus($"Pull failed — {resolveErr}", error: true);
                return;
            }

            var pulled = PatchSourcePuller.TryPullMethodBody(method);
            if (!pulled.Success)
            {
                SetStatus($"Pull failed — {pulled.Error}", error: true);
                return;
            }

            // Don't silently overwrite a non-default body — the user may
            // already be mid-edit. The default starter snippet is
            // recognizable; anything else gets a confirm dialog. (No
            // Editor dialog support inside ShowUtility-style panels?
            // EditorUtility.DisplayDialog works from the main editor
            // window's context, which is where this view lives.)
            bool currentIsDefault = string.IsNullOrEmpty(_bodyField.value)
                                 || _bodyField.value == DefaultBody
                                 || _bodyField.value.TrimStart().StartsWith("// Phase A MVP scope: void instance methods.");
            if (!currentIsDefault)
            {
                bool overwrite = UnityEditor.EditorUtility.DisplayDialog(
                    "Pull Original",
                    "The patch body is non-empty. Overwrite it with the original method body from " + pulled.SourcePath + "?",
                    "Overwrite",
                    "Cancel");
                if (!overwrite) { SetStatus("Pull cancelled — body unchanged.", error: false); return; }
            }

            _bodyField.SetValueWithoutNotify(pulled.Body);
            SetStatus($"Pulled from {pulled.SourcePath} ({pulled.Body?.Length ?? 0} chars).", error: false);
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
                // PatchEngine.Apply now preserves the previously-applied
                // detour when the new body fails to compile (engine fix
                // landed earlier in this PR). The old upsert here would
                // *still* overwrite the registry's live spec with the
                // broken body and a Failed status — the UI would then
                // lie: red "Failed" row in the active list while the
                // method is still being detoured by the original
                // working prefix. Suppress the upsert when an active
                // spec already lives at this key; just surface the
                // compile error as transient status. The form keeps
                // the user's edit so they can fix it and retry.
                var existing = PatchRegistry.Find(
                    spec.TargetTypeName, spec.MethodName, spec.ParameterTypes);
                if (existing != null && existing.Status == PatchStatus.Active)
                {
                    SetStatus($"compile failed; previous patch still active. {ex.Message}", error: true);
                }
                else
                {
                    // No prior active patch — record the failed attempt
                    // so the user can read the error from the active
                    // list row and iterate.
                    spec.Status = PatchStatus.Failed;
                    spec.LastError = ex.Message;
                    PatchRegistry.AddOrUpdate(spec);
                    SetStatus(ex.Message, error: true);
                }
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
            // Phase B contract: Revert flips the spec to Inactive but
            // *keeps* it in the registry as a draft. Removing here
            // would delete the persisted body — the README promises
            // users can `Load` an Inactive spec back into the form
            // later, so the body has to survive. PatchEngine.Revert
            // already updates spec.Status = Inactive and re-persists.
            PatchEngine.Revert(spec);
            SetStatus($"Reverted: {spec.TargetTypeName}.{spec.MethodName} (draft kept)", error: false);
        }

        private void OnRevertAllClicked()
        {
            // Same contract as OnRevertClicked — drop every Harmony
            // detour but keep the specs in the registry as Inactive
            // drafts the user can reapply.
            int n = PatchEngine.RevertAll();
            SetStatus($"Reverted {n} patch{(n == 1 ? "" : "es")} (drafts kept).", error: false);
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
                // Block wraps the main row plus an optional error
                // sub-line so the bottom border draws cleanly under
                // both — moving the border from the row to the
                // block keeps the visual divider where users expect
                // it (between specs, not between the spec and its
                // error message).
                var block = new VisualElement();
                block.style.flexDirection = FlexDirection.Column;
                block.style.borderBottomWidth = 1;
                block.style.borderBottomColor = new StyleColor(new Color(0.25f, 0.25f, 0.25f));

                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.paddingLeft = 6;
                row.style.paddingRight = 4;
                row.style.paddingTop = 3;
                row.style.paddingBottom = 3;

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
                    // Per-row Revert keeps the spec in the registry as
                    // a draft, matching OnRevertClicked. To delete the
                    // draft entirely the user can hit Apply with an
                    // empty body and then Revert (or use Reset Project
                    // Data for a clean slate). A dedicated "Delete"
                    // affordance can land in a later phase if drafts
                    // become heavy.
                    PatchEngine.Revert(s);
                })
                { text = "Revert" };
                revertBtn.style.minWidth = 60;
                row.Add(revertBtn);

                block.Add(row);

                // Phase B4: surface LastError as a persistent secondary
                // line under the row so users don't have to hover for
                // the tooltip to know why a row is red. The most common
                // cause in Phase B is "Auto-reapply failed: …" — i.e.
                // a domain reload happened and the patch couldn't be
                // re-installed. Hovering still shows the full text;
                // the inline preview is the first line only so a 100-
                // char compile diagnostic doesn't push the row layout
                // around.
                if (s.Status == PatchStatus.Failed && !string.IsNullOrEmpty(s.LastError))
                {
                    var firstLine = s.LastError.Split('\n')[0];
                    var errLabel = new Label("↳ " + firstLine);
                    errLabel.style.color = new StyleColor(new Color(0.95f, 0.65f, 0.65f));
                    errLabel.style.fontSize = 10;
                    errLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
                    errLabel.style.paddingLeft = 22;
                    errLabel.style.paddingRight = 6;
                    errLabel.style.paddingBottom = 3;
                    errLabel.style.whiteSpace = WhiteSpace.NoWrap;
                    errLabel.style.overflow = Overflow.Hidden;
                    errLabel.style.textOverflow = TextOverflow.Ellipsis;
                    errLabel.tooltip = s.LastError;
                    block.Add(errLabel);
                }

                _activeListContainer.Add(block);
            }
        }
    }
}
