using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using RoslynRepl.Editor.Patches;

namespace RoslynRepl.Editor.UI
{
    /// <summary>
    /// In-window panel for the Runtime Method Patch feature.
    /// Mounts inside the main REPL window's shared bottom area —
    /// same physical space as the Output panel, switched via the
    /// Output / Patches mode tab in the pane header.
    ///
    /// Engine constraints surfaced here:
    ///   • void instance methods only
    ///   • private members are reachable through natural source
    ///     (the syntax rewriter routes inaccessible names through
    ///     reflection helpers) or via the explicit `__get` / `__set`
    ///     / `__call` helpers when the rewriter picks the wrong
    ///     overload.
    /// </summary>
    public class MethodPatchView
    {
        private const string DefaultBody =
@"// Write the patch body the same way you'd write the source —
// `hp -= 10`, `Singleton.Instance.PrivateField`, `base.OnEnable()`
// all work. The rewriter routes inaccessible names through
// reflection helpers automatically.
UnityEngine.Debug.Log(""[patched] "" + __instance.GetType().Name);";

        private readonly VisualElement _host;
        private TextField _targetField;
        private TextField _methodField;
        private TextField _paramsField;
        private TextField _bodyField;

        // Diff section. _diffLines is the scrollable colored line
        // view, _diffSummary shows "+N -M" in the header, the two
        // buttons act on the current vs.-snapshot pair. Disabled
        // when no source snapshot is available.
        private VisualElement _diffContainer;
        private VisualElement _diffLines;
        private Label _diffSummary;
        private ScrollView _diffScroll;
        private Button _copyDiffBtn;
        private Button _applyToFileBtn;
        private Label _statusLabel;
        private VisualElement _activeListContainer;

        // Pull Original drops the source body into the editor, but
        // the user then edits it before Apply. To keep
        // spec.OriginalBody (the unedited snapshot the diff view
        // relies on) accurate, remember the last successful pull
        // keyed by the form's identity at pull time. If the form
        // still
        // matches that key when Apply runs, the snapshot ships into
        // the spec; if the form drifted (user changed Type / Method /
        // Params, picked a different method via Browse, etc.) the
        // snapshot is stale and we leave OriginalBody empty rather
        // than persist a body that doesn't belong to the target
        // method.
        private string _lastPulledKey;
        private string _lastPulledOriginal;

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
            _paramsField.style.marginRight = 4;
            formRow.Add(_paramsField);

            // Browse Methods — open a popup that lists every patchable
            // method on the current Type, click to fill Method/Params.
            // Saves users from typing exact method signatures with
            // overload-disambiguating parameter type lists.
            var browseBtn = new Button(OnBrowseMethodsClicked) { text = "Browse" };
            browseBtn.tooltip =
                "List patchable methods on the current target type and fill\n" +
                "Method + Params from the chosen one. Type field must be set.";
            browseBtn.style.minWidth = 70;
            formRow.Add(browseBtn);

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
                "Use as the starting point for an in-place edit. Supports void instance methods,\n" +
                "block bodies (`{ … }`); source must live in Assets/ or Packages/.";
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

            // ─── Diff section ────────────────────────────────────
            // Shows the line diff between the source snapshot Pull
            // Original captured (or spec.OriginalBody for a stored
            // patch) and the current edited body. Buttons act on
            // the snapshot/current pair: "Copy diff" pastes a
            // unified-diff text blob to the clipboard; "Apply to
            // file" splices the *user-edited* form back into the
            // target method's source file. The auto-rewrite that
            // routes inaccessible names through reflection helpers
            // never touches spec.PatchBody itself, so the body
            // is always clean source — natural `hp -= 10`, never
            // the wrapper-only `__set("hp", __get<int>("hp") - 10)`.
            //
            // Disabled when no snapshot exists — typically a
            // hand-typed patch that was never Pulled.
            _diffContainer = new VisualElement();
            _diffContainer.style.flexShrink = 0;
            _diffContainer.style.marginTop = 4;
            _diffContainer.style.marginBottom = 4;
            _diffContainer.style.borderTopWidth = 1;
            _diffContainer.style.borderTopColor = new StyleColor(new Color(0.3f, 0.3f, 0.3f));
            _diffContainer.style.paddingTop = 4;

            var diffHeaderRow = new VisualElement();
            diffHeaderRow.style.flexDirection = FlexDirection.Row;
            diffHeaderRow.style.alignItems = Align.Center;
            diffHeaderRow.style.marginBottom = 2;
            var diffTitle = new Label("Diff (original → current)");
            diffTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            diffTitle.style.fontSize = 10;
            diffTitle.style.color = new StyleColor(new Color(0.7f, 0.7f, 0.7f));
            diffTitle.style.flexGrow = 1;
            diffHeaderRow.Add(diffTitle);
            _diffSummary = new Label("(no snapshot)");
            _diffSummary.style.fontSize = 10;
            _diffSummary.style.color = new StyleColor(new Color(0.55f, 0.55f, 0.55f));
            diffHeaderRow.Add(_diffSummary);
            _diffContainer.Add(diffHeaderRow);

            _diffScroll = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
            _diffScroll.style.maxHeight = 140;
            _diffScroll.style.minHeight = 40;
            _diffScroll.style.backgroundColor = new StyleColor(new Color(0.12f, 0.12f, 0.12f));
            _diffLines = new VisualElement();
            _diffScroll.Add(_diffLines);
            _diffContainer.Add(_diffScroll);

            var diffActions = new VisualElement();
            diffActions.style.flexDirection = FlexDirection.Row;
            diffActions.style.marginTop = 2;
            _copyDiffBtn = new Button(OnCopyDiffClicked) { text = "Copy diff" };
            _copyDiffBtn.style.fontSize = 10;
            _copyDiffBtn.style.marginRight = 4;
            _copyDiffBtn.tooltip = "Copy a unified-diff text blob to the system clipboard.";
            diffActions.Add(_copyDiffBtn);
            _applyToFileBtn = new Button(OnApplyToFileClicked) { text = "Apply to file" };
            _applyToFileBtn.style.fontSize = 10;
            _applyToFileBtn.tooltip =
                "Write the current patch body back into the target method's .cs file.\n" +
                "Backs up the original to <source>.bak before touching it.\n" +
                "The auto-rewrite that routes inaccessible names through\n" +
                "reflection helpers is wrapper-only, so the body written stays\n" +
                "clean source (`hp -= 10`, not `__set/__get` calls).";
            diffActions.Add(_applyToFileBtn);
            _diffContainer.Add(diffActions);

            _host.Add(_diffContainer);

            // Refresh diff whenever any of (Type, Method, Params,
            // body) changes — the snapshot is keyed off the form
            // identity, so a swap should rebuild against the new
            // spec's stored OriginalBody (or no snapshot, if none).
            _bodyField.RegisterValueChangedCallback(_ => RefreshDiff());
            _targetField.RegisterValueChangedCallback(_ => RefreshDiff());
            _methodField.RegisterValueChangedCallback(_ => RefreshDiff());
            _paramsField.RegisterValueChangedCallback(_ => RefreshDiff());

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
            RefreshDiff();
        }

        // ─── Diff helpers ─────────────────────────────────────────

        // Snapshot resolution priority:
        //   1. _lastPulledOriginal — populated by Pull Original /
        //      LoadIntoForm when the form's identity matches a
        //      previously-captured body. Verified against the
        //      *current* form key before we trust it: the user can
        //      Pull for A.Foo, edit Type/Method/Params to point at
        //      B.Bar, and the cached body would still belong to
        //      A.Foo. Without the key check we'd offer (and Apply
        //      to file write!) a stale snapshot against the wrong
        //      target.
        //   2. spec.OriginalBody from the registry, looked up by
        //      the form's current identity. Covers the case where
        //      the user reopened the window after the cached
        //      snapshot was lost (domain reload) but a stored spec
        //      still carries one.
        //   3. null — no snapshot available, diff section disables.
        //
        // null vs "" distinction matters: a method declared as
        // `void Foo() {}` pulls as an empty string body, which is
        // a valid snapshot — the user should be able to add code
        // through the editor and Apply it back. Returning null only
        // when no snapshot exists keeps the empty-body path open.
        // `_lastPulledKey != null` is the presence signal for the
        // cached pull; spec.OriginalBody is non-null when the
        // registry stored a snapshot, even if its content is
        // empty.
        private string ResolveSnapshot()
        {
            var typeName = _targetField?.value?.Trim();
            var methodName = _methodField?.value?.Trim();
            var paramsCsv = _paramsField?.value ?? string.Empty;
            if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(methodName)) return null;

            var formKey = MethodPatchSpec.Keyed(typeName, methodName, paramsCsv);

            if (_lastPulledKey == formKey && _lastPulledOriginal != null)
                return _lastPulledOriginal;

            var spec = PatchRegistry.Find(typeName, methodName, paramsCsv);
            return spec?.OriginalBody;
        }

        private void RefreshDiff()
        {
            if (_diffLines == null || _diffSummary == null) return;
            _diffLines.Clear();

            var original = ResolveSnapshot();
            var current = _bodyField?.value ?? string.Empty;
            if (original == null)
            {
                // null means "no snapshot taken" — the diff section
                // can't operate. An *empty* snapshot (`""`) is a
                // valid case (e.g., a `void Foo() {}` body that was
                // pulled successfully); we keep the section enabled
                // so the user can add code and apply it.
                _diffSummary.text = "(no snapshot — Pull Original to enable)";
                _diffSummary.style.color = new StyleColor(new Color(0.55f, 0.55f, 0.55f));
                _copyDiffBtn?.SetEnabled(false);
                _applyToFileBtn?.SetEnabled(false);
                return;
            }

            var diff = PatchSourceDiff.Compute(original, current);
            _diffSummary.text = $"+{diff.AddedCount} -{diff.RemovedCount}";
            _diffSummary.style.color = diff.HasChanges
                ? new StyleColor(new Color(0.85f, 0.85f, 0.55f))
                : new StyleColor(new Color(0.55f, 0.55f, 0.55f));

            foreach (var line in diff.Lines)
            {
                var lbl = new Label();
                lbl.style.fontSize = 11;
                lbl.style.whiteSpace = WhiteSpace.NoWrap;
                lbl.style.paddingLeft = 4;
                lbl.style.paddingRight = 4;
                switch (line.Kind)
                {
                    case PatchSourceDiff.LineKind.Same:
                        lbl.text = "  " + line.Text;
                        lbl.style.color = new StyleColor(new Color(0.55f, 0.55f, 0.55f));
                        break;
                    case PatchSourceDiff.LineKind.Added:
                        lbl.text = "+ " + line.Text;
                        lbl.style.color = new StyleColor(new Color(0.4f, 0.85f, 0.45f));
                        break;
                    case PatchSourceDiff.LineKind.Removed:
                        lbl.text = "- " + line.Text;
                        lbl.style.color = new StyleColor(new Color(0.95f, 0.45f, 0.45f));
                        break;
                }
                _diffLines.Add(lbl);
            }

            _copyDiffBtn?.SetEnabled(true);
            // Apply To File can run regardless of HasChanges —
            // sometimes the user wants to round-trip the unedited
            // body back into the file (no-op write that still
            // refreshes import). Keep enabled while a snapshot is
            // present.
            _applyToFileBtn?.SetEnabled(true);
        }

        private void OnCopyDiffClicked()
        {
            var original = ResolveSnapshot();
            if (original == null)
            {
                SetStatus("No source snapshot to diff against.", error: true);
                return;
            }
            var current = _bodyField?.value ?? string.Empty;
            var diff = PatchSourceDiff.Compute(original, current);
            var label = $"{_targetField?.value}.{_methodField?.value}";
            UnityEditor.EditorGUIUtility.systemCopyBuffer = PatchSourceDiff.FormatUnified(diff, label);
            SetStatus($"Diff copied (+{diff.AddedCount} -{diff.RemovedCount}).", error: false);
        }

        private void OnApplyToFileClicked()
        {
            var typeName = _targetField?.value?.Trim();
            var methodName = _methodField?.value?.Trim();
            var paramsCsv = _paramsField?.value ?? string.Empty;
            var body = _bodyField?.value ?? string.Empty;

            if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(methodName))
            {
                SetStatus("Type and Method are required to apply to file.", error: true);
                return;
            }

            // Reuse PatchEngine's resolver so the same scope rules
            // (instance void only, etc.) apply that Apply Patch
            // would enforce. If the target wouldn't even be
            // patchable, writing it to source is also wrong.
            var spec = new MethodPatchSpec
            {
                TargetTypeName = typeName,
                MethodName = methodName,
                ParameterTypes = paramsCsv,
                PatchBody = body,
            };
            var target = PatchEngine.TryResolveTargetMethod(spec, out var resolveError);
            if (target == null)
            {
                SetStatus("Apply to file failed: " + resolveError, error: true);
                return;
            }

            var declName = target.DeclaringType?.FullName ?? target.DeclaringType?.Name ?? "<unknown>";
            var ok = UnityEditor.EditorUtility.DisplayDialog(
                "Apply patch to source file?",
                $"Write the current patch body into\n\n  {declName}.{target.Name}\n\n" +
                $"A backup will be saved to <source>.bak.\n\n" +
                "The body is the user-edited form — the auto-rewrite that routes\n" +
                "inaccessible names through reflection helpers is wrapper-only.",
                "Apply",
                "Cancel");
            if (!ok) return;

            // Pass the current snapshot in so the writer can detect
            // a conflict — the file might have been edited (by the
            // user, an IDE, or source control) since Pull captured
            // the snapshot. Splicing a stale body would silently
            // overwrite the newer content. The writer aborts on
            // mismatch and ConflictDetected gives the UI a hook to
            // surface a "Pull again" hint instead of a generic
            // failure.
            var snapshot = ResolveSnapshot();
            var result = PatchSourceWriter.ApplyToFile(target, body, snapshot);
            if (result.Success)
            {
                SetStatus($"Wrote to {result.SourcePath} (backup: {result.BackupPath}).", error: false);
            }
            else if (result.ConflictDetected)
            {
                SetStatus("Apply to file aborted: " + result.Error, error: true);
            }
            else
            {
                SetStatus("Apply to file failed: " + result.Error, error: true);
            }
        }

        private void OnBrowseMethodsClicked()
        {
            var typeName = _targetField.value?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(typeName))
            {
                SetStatus("Type is required to browse methods.", error: true);
                return;
            }

            // Reuse the engine's resolver so type discovery rules (full
            // AppDomain walk, namespace handling) match Apply / Pull.
            var type = ResolveTypeByName(typeName);
            if (type == null)
            {
                SetStatus($"Type not found: {typeName}", error: true);
                return;
            }

            MethodPickerPopup.Open(type, picked =>
            {
                if (picked == null) return;
                FillFormFromMethod(picked);
                SetStatus($"Picked: {picked.DeclaringType?.Name}.{picked.Name}", error: false);
            });
        }

        public void FillFormFromMethod(System.Reflection.MethodInfo method)
        {
            if (method == null) return;
            var declType = method.DeclaringType;
            if (declType != null) _targetField.SetValueWithoutNotify(declType.FullName ?? declType.Name);
            _methodField.SetValueWithoutNotify(method.Name);
            var ps = method.GetParameters();
            _paramsField.SetValueWithoutNotify(ps.Length == 0
                ? string.Empty
                : string.Join(",", ps.Select(p => p.ParameterType.FullName ?? p.ParameterType.Name)));

            // Form just got rewritten to a different method — any
            // previous Pull's snapshot belongs to the *old* form
            // identity, so drop it.
            _lastPulledKey = null;
            _lastPulledOriginal = null;

            // Auto-Pull Original after Browse: the user just told
            // us exactly which method they want to patch, so the
            // next thing they need is the source body in the
            // editor. Without this they'd have to click Pull
            // Original immediately after every pick, which is what
            // the UX feedback asked us to remove. The Pull path
            // still owns the "non-default body — overwrite?"
            // confirm dialog, so an already-edited body is
            // protected.
            OnPullOriginalClicked();
        }

        private static System.Type ResolveTypeByName(string fullName)
        {
            var direct = System.Type.GetType(fullName, throwOnError: false);
            if (direct != null) return direct;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, throwOnError: false);
                if (t != null) return t;
            }
            return null;
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
                                 // legacy starter text — still treat as default so
                                 // historic projects don't get an unnecessary confirm
                                 || _bodyField.value.TrimStart().StartsWith("// Supported scope: void instance methods.")
                                 || _bodyField.value.TrimStart().StartsWith("// Write the patch body the same way");
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
            // Remember the snapshot keyed by *this* form so the next
            // Apply can ship it into spec.OriginalBody even after the
            // user edits the body. Stale on Type/Method/Params change
            // — handled in FillFormFromMethod / LoadIntoForm.
            _lastPulledKey = MethodPatchSpec.Keyed(spec.TargetTypeName, spec.MethodName, spec.ParameterTypes);
            _lastPulledOriginal = pulled.Body;
            SetStatus($"Pulled from {pulled.SourcePath} ({pulled.Body?.Length ?? 0} chars).", error: false);
            // SetValueWithoutNotify skips the registered changed
            // callback that drives the diff; refresh manually.
            RefreshDiff();
        }

        public void LoadIntoForm(MethodPatchSpec spec)
        {
            if (spec == null) return;
            _targetField.SetValueWithoutNotify(spec.TargetTypeName);
            _methodField.SetValueWithoutNotify(spec.MethodName);
            _paramsField.SetValueWithoutNotify(spec.ParameterTypes ?? string.Empty);
            _bodyField.SetValueWithoutNotify(spec.PatchBody ?? string.Empty);

            // Restore the OriginalBody snapshot so a subsequent Apply
            // re-persists it. The previous flow lost the snapshot the
            // moment the user clicked Load on a stored spec — Pull's
            // first run was permanent state, but every subsequent
            // round-trip dropped it.
            if (!string.IsNullOrEmpty(spec.OriginalBody))
            {
                _lastPulledKey = spec.Key;
                _lastPulledOriginal = spec.OriginalBody;
            }
            else
            {
                _lastPulledKey = null;
                _lastPulledOriginal = null;
            }

            UpdateStatusForCurrentForm();
            RefreshDiff();
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

            // Carry the most recent Pull's snapshot into the spec when
            // it still belongs to *this* form identity. Stale snapshots
            // (form changed since the last Pull) are dropped instead of
            // persisted against the wrong target. The diff view diffs
            // OriginalBody against PatchBody to surface the user's
            // actual edits; without this hook the diff would always be
            // "the entire patch body is new", which is wrong.
            if (!string.IsNullOrEmpty(_lastPulledKey)
                && _lastPulledKey == spec.Key
                && _lastPulledOriginal != null)
            {
                spec.OriginalBody = _lastPulledOriginal;
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
            // Revert flips the spec to Inactive but *keeps* it in
            // the registry as a draft. Removing here would delete
            // the persisted body — the README promises users can
            // `Load` an Inactive spec back into the form later, so
            // the body has to survive. PatchEngine.Revert already
            // updates spec.Status = Inactive and re-persists.
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

                // Surface LastError as a persistent secondary line
                // under the row so users don't have to hover for the
                // tooltip to know why a row is red. The most common
                // cause is "Auto-reapply failed: …" — i.e. a domain
                // reload happened and the patch couldn't be re-
                // installed. Hovering still shows the full text;
                // the inline preview is the first line only so a
                // 100-char compile diagnostic doesn't push the row
                // layout around.
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
