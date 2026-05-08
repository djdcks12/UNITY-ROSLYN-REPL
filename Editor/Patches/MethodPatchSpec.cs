using System;
using System.Collections.Generic;

namespace RoslynRepl.Editor.Patches
{
    /// <summary>
    /// Status of a runtime method patch — whether it's currently applied,
    /// pending an apply, or last failed for some reason. Tracked
    /// separately from the spec data so a Failed → Inactive transition
    /// (user clicks Revert on a busted patch) doesn't lose the patch
    /// body or the diagnostic that caused the failure.
    /// </summary>
    public enum PatchStatus
    {
        Inactive,
        Active,
        Failed,
    }

    /// <summary>
    /// What the UI should render for a spec right now. Computed by
    /// <see cref="PatchRegistry.GetDisplayState"/> from the persisted
    /// <see cref="MethodPatchSpec.Status"/>, the live
    /// <see cref="PatchEngine.IsApplied"/> map, and the
    /// session-only dormancy set. Centralizing this decision in one
    /// helper means UI surfaces — toolbar badge, active-list row,
    /// form status line, Apply failure handling — never reach into
    /// the raw fields and assemble their own variant of "is this
    /// row really live right now". Reviewer-driven separation
    /// (issue #22 rounds three / four PR review): UI must not
    /// interpret <c>spec.Status == Active</c> directly because
    /// auto-off-dormant specs keep that field at Active for
    /// persistence reasons.
    /// </summary>
    public enum PatchDisplayState
    {
        /// <summary>Persisted draft, no detour expected.</summary>
        Inactive,

        /// <summary>Persisted Active and a Harmony detour really is installed right now.</summary>
        Active,

        /// <summary>Persisted Active intent kept on disk, but no detour is installed for the current session — auto-reapply is off, or the spec hasn't been Apply'd in this session yet. UI should render this distinct from Active so a user can tell "the row I see won't intercept calls right now".</summary>
        DormantAutoOff,

        /// <summary>Last Apply attempt failed; <see cref="MethodPatchSpec.LastError"/> carries the diagnostic.</summary>
        Failed,
    }

    /// <summary>
    /// Identification + content of a single method patch.
    ///
    /// The triple (TargetTypeName, MethodName, ParameterTypes) uniquely
    /// identifies a patchable method in the AppDomain. ParameterTypes is
    /// a comma-joined list of full type names — necessary for disambig-
    /// uation when the target has overloads, and stable enough across
    /// editor sessions to be a safe persistence key.
    ///
    /// PatchBody is the user-edited replacement body (everything between
    /// the method's outer braces). OriginalBody is the snapshot pulled
    /// from the .cs source at the time the patch was created — used by
    /// the diff view and source export and to render the editor's starting content.
    /// Initial drafts leave OriginalBody empty until the source-pull pipeline
    /// lands; A3 treats an empty OriginalBody as "user is writing from
    /// scratch".
    /// </summary>
    [Serializable]
    public class MethodPatchSpec
    {
        public string TargetTypeName;
        public string MethodName;
        public string ParameterTypes;
        public string OriginalBody;
        public string PatchBody;
        public PatchStatus Status;
        public string LastError;

        public string Key => Keyed(TargetTypeName, MethodName, ParameterTypes);

        public static string Keyed(string typeName, string methodName, string parameterTypes)
        {
            return $"{typeName ?? string.Empty}::{methodName ?? string.Empty}::{parameterTypes ?? string.Empty}";
        }
    }

    /// <summary>
    /// In-memory registry of every method patch the user has defined this
    /// session. the engine is intentionally *not* persistent — the user
    /// authoring a patch and the engine applying it are different
    /// concerns from "survive a domain reload". the persistence layer adds
    /// EditorPrefs (or asset) persistence on top of this same API.
    ///
    /// Looked-up by (typeName, methodName, parameterTypes); duplicates
    /// are handled as upsert so the editor UI can call AddOrUpdate
    /// freely as the user types in the body without first checking
    /// whether the spec already exists.
    /// </summary>
    public static class PatchRegistry
    {
        /// <summary>Raised whenever any add / update / remove / clear commits.</summary>
        public static event Action Changed;

        private static readonly Dictionary<string, MethodPatchSpec> _byKey = new();

        // Session-only dormancy bookkeeping (issue #22 follow-up). The
        // auto-reapply opt-out path needs a way to say "treat this
        // spec as Inactive for the current process" without ever
        // touching <see cref="MethodPatchSpec.Status"/> or
        // persistence — because the same spec instance lives in
        // _byKey and Persist serializes _byKey.Values, mutating
        // Status would leak into the next save the *moment* any
        // other operation in the session calls AddOrUpdate / Remove
        // / Clear and triggers a Persist over every value. Storing
        // the dormancy state in a separate set keyed on
        // <see cref="MethodPatchSpec.Key"/> guarantees the live
        // process can shadow the persisted desired status without
        // losing it.
        private static readonly HashSet<string> _sessionDormantKeys = new();

        public static IReadOnlyCollection<MethodPatchSpec> Specs => _byKey.Values;

        public static int Count => _byKey.Count;

        /// <summary>True when the spec with this key is shown as
        /// dormant in the live process — auto-reapply opted out
        /// for this session — even though its persisted Status is
        /// still Active. Most UI code should prefer
        /// <see cref="GetDisplayState"/>, which folds dormancy and
        /// installed-detour state into a single enum so callers
        /// don't have to compose the rules themselves.</summary>
        public static bool IsSessionDormant(string key) =>
            !string.IsNullOrEmpty(key) && _sessionDormantKeys.Contains(key);

        /// <summary>
        /// Single source of truth for what the UI should render for
        /// this spec right now. Combines the persisted
        /// <see cref="MethodPatchSpec.Status"/> with the live
        /// <see cref="PatchEngine.IsApplied"/> map and the session
        /// dormancy set so callers never re-derive the rules
        /// (and never miss a case when the rules change).
        ///
        /// Mapping:
        ///   • Status=Failed                                 → Failed
        ///   • Status=Inactive                               → Inactive
        ///   • Status=Active && IsSessionDormant             → DormantAutoOff
        ///   • Status=Active && PatchEngine.IsApplied        → Active
        ///   • Status=Active && !IsApplied && !dormant       → DormantAutoOff
        ///         (the persisted intent is Active but no detour
        ///          is installed — usually a transient state right
        ///          before Apply runs, or a manual registry mutation
        ///          from outside the engine. Treating it like the
        ///          dormant case gives the user the same install
        ///          affordance instead of falsely claiming the row
        ///          is live.)
        /// </summary>
        public static PatchDisplayState GetDisplayState(MethodPatchSpec spec)
        {
            if (spec == null) return PatchDisplayState.Inactive;
            switch (spec.Status)
            {
                case PatchStatus.Failed:   return PatchDisplayState.Failed;
                case PatchStatus.Inactive: return PatchDisplayState.Inactive;
                case PatchStatus.Active:
                    if (IsSessionDormant(spec.Key))    return PatchDisplayState.DormantAutoOff;
                    if (PatchEngine.IsApplied(spec))   return PatchDisplayState.Active;
                    return PatchDisplayState.DormantAutoOff;
                default:                   return PatchDisplayState.Inactive;
            }
        }

        /// <summary>Mark the spec with this key dormant for the
        /// current process. Persistence is not touched. Caller is
        /// expected to follow up with
        /// <see cref="NotifyInMemoryMutation"/> so subscribers
        /// re-render against the updated dormancy view.</summary>
        public static void MarkSessionDormant(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            _sessionDormantKeys.Add(key);
        }

        /// <summary>Drop every dormancy mark — used when the user
        /// flips auto-reapply back on (so a follow-up reload sees
        /// no dormancy carryover) and during Clear.</summary>
        public static void ClearSessionDormancy()
        {
            if (_sessionDormantKeys.Count == 0) return;
            _sessionDormantKeys.Clear();
            Changed?.Invoke();
        }

        public static MethodPatchSpec Find(string typeName, string methodName, string parameterTypes)
        {
            var key = MethodPatchSpec.Keyed(typeName, methodName, parameterTypes);
            return _byKey.TryGetValue(key, out var spec) ? spec : null;
        }

        public static void AddOrUpdate(MethodPatchSpec spec)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            if (string.IsNullOrEmpty(spec.TargetTypeName)) throw new ArgumentException("TargetTypeName is required", nameof(spec));
            if (string.IsNullOrEmpty(spec.MethodName))     throw new ArgumentException("MethodName is required",     nameof(spec));
            spec.ParameterTypes ??= string.Empty;
            _byKey[spec.Key] = spec;
            // Any explicit registry write — Apply, Revert, save a
            // draft from the form — is the user's intent for this
            // spec, so the session-only dormancy mark gets cleared.
            // Apply leaves the spec at Active and we want the
            // toolbar badge / Patches list to recognize it
            // immediately; Revert leaves the spec at Inactive
            // (regardless of the dormancy state) and dropping the
            // mark keeps the registry view consistent with the
            // persisted desired status.
            _sessionDormantKeys.Remove(spec.Key);
            Persist();
            Changed?.Invoke();
        }

        /// <summary>
        /// Raise <see cref="Changed"/> for callers that intentionally
        /// mutated a live spec object in place without going through
        /// <see cref="AddOrUpdate"/>. The use case is the auto-reapply
        /// opt-out path, which needs to hide Active specs from the
        /// current process (mark them Inactive in memory so the UI
        /// shows them as off and the toolbar badge drops to zero)
        /// while keeping the persisted desired status intact, so
        /// flipping the menu back on re-installs the patches on the
        /// next reload. <see cref="AddOrUpdate"/> can't do this — its
        /// Persist call is exactly what we want to skip.
        ///
        /// Callers must have mutated a spec that's already in
        /// <see cref="Specs"/>; this method only fires the event.
        /// Persistence is left untouched.
        /// </summary>
        public static void NotifyInMemoryMutation() => Changed?.Invoke();

        public static bool Remove(string typeName, string methodName, string parameterTypes)
        {
            var key = MethodPatchSpec.Keyed(typeName, methodName, parameterTypes);
            if (_byKey.Remove(key))
            {
                _sessionDormantKeys.Remove(key);
                Persist();
                Changed?.Invoke();
                return true;
            }
            return false;
        }

        public static bool Remove(MethodPatchSpec spec)
        {
            if (spec == null) return false;
            return Remove(spec.TargetTypeName, spec.MethodName, spec.ParameterTypes);
        }

        public static void Clear()
        {
            // Two independent buckets to consider:
            //   • the live in-memory dictionary,
            //   • the persisted EditorPrefs key.
            // An older package version that wrote SetString(key, "")
            // could leave the second bucket non-empty even when the
            // first is empty after LoadFromPersistence. Bailing on
            // _byKey.Count == 0 alone would skip the DeleteKey on
            // those upgraded projects and break the README's
            // "Reset removes every package-owned EditorPrefs key"
            // promise.
            bool hadInMemory  = _byKey.Count > 0;
            bool hadPersisted = PatchPersistence.HasAny();
            if (!hadInMemory && !hadPersisted) return;

            _byKey.Clear();
            _sessionDormantKeys.Clear();
            // Use the dedicated DeleteKey path instead of Persist()'s
            // SetString-with-empty-list. SetString("") leaves an empty
            // EditorPrefs key on disk, which contradicts the README's
            // "Reset removes every package-owned EditorPrefs key"
            // promise. Match the behavior the other stores ship
            // (UsingsStore.Clear, RunHistoryStore.Clear, etc. all call
            // DeleteKey directly).
            PatchPersistence.Clear();
            Changed?.Invoke();
        }

        /// <summary>
        /// Pull the persisted spec list back into the live registry.
        /// Intended for the [InitializeOnLoad] boot path — the bootstrap path
        /// calls this once per domain reload, then walks the loaded
        /// specs and re-applies the Active ones.
        ///
        /// Specs already in the live registry are overwritten by their
        /// persisted counterparts (the live state during a recompile
        /// pause is whatever was already set; the persisted state is
        /// the most recently committed view, so it wins). Fires
        /// <see cref="Changed"/> once at the end so the UI rebuilds in
        /// a single pass instead of per-row.
        /// </summary>
        public static void LoadFromPersistence()
        {
            var persisted = PatchPersistence.Load();
            foreach (var s in persisted)
            {
                if (s == null || string.IsNullOrEmpty(s.TargetTypeName) || string.IsNullOrEmpty(s.MethodName))
                    continue;
                s.ParameterTypes ??= string.Empty;
                _byKey[s.Key] = s;
            }
            Changed?.Invoke();
        }

        private static void Persist()
        {
            PatchPersistence.Save(_byKey.Values);
        }
    }
}
