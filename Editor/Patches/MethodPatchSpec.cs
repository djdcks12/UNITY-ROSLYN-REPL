using System;
using System.Collections.Generic;
using System.Linq;

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
    /// a <see cref="ParamSeparator"/>-joined list of full type names —
    /// necessary for disambiguation when the target has overloads, and
    /// stable enough across editor sessions to be a safe persistence key.
    ///
    /// Issue #41 (v0.7.2): the historical separator was <c>','</c>, which
    /// silently broke for generic parameter types because closed-generic
    /// <see cref="Type.FullName"/> embeds commas (assembly-qualified inner
    /// type list, <c>List`1[[System.Int32, mscorlib, …]]</c>). Splitting
    /// on <c>','</c> shredded such entries into garbage. The current
    /// separator is <c>;</c> — illegal in CLR full type names, so it
    /// can't collide with anything inside a single parameter's name
    /// while still being typeable in the form field. <see cref="JoinParamTypes"/>
    /// always emits the new form; <see cref="SplitParamTypes"/> falls
    /// back to comma-splitting only when the value contains no <c>;</c>
    /// AND no <c>[</c> (so legacy non-generic specs keep loading, but
    /// legacy generic specs surface a clear "couldn't resolve" error
    /// instead of silently picking the wrong overload).
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

        /// <summary>Separator used inside <see cref="ParameterTypes"/>
        /// to delimit individual parameter type names. <c>;</c> is illegal
        /// in CLR <see cref="Type.FullName"/> output, so it cannot collide
        /// with embedded commas inside a closed-generic assembly-qualified
        /// inner type list.</summary>
        public const char ParamSeparator = ';';

        public string Key => Keyed(TargetTypeName, MethodName, ParameterTypes);

        /// <summary>Build the registry key for a (typeName, methodName,
        /// parameterTypes) triple. The parameterTypes value is run
        /// through <see cref="NormalizeParamTypes"/> first so a legacy
        /// comma-joined spec persisted on 0.7.1 ends up at the same
        /// key as a freshly Browse-picked semicolon-joined spec for
        /// the same method — without it the two would map to
        /// different registry slots and `Apply` would install a
        /// second Harmony prefix instead of replacing the first.
        /// </summary>
        public static string Keyed(string typeName, string methodName, string parameterTypes)
        {
            var canonical = NormalizeParamTypes(parameterTypes);
            return $"{typeName ?? string.Empty}::{methodName ?? string.Empty}::{canonical}";
        }

        /// <summary>Canonicalize a ParameterTypes string into the
        /// current <see cref="ParamSeparator"/> form. Idempotent —
        /// already-canonical strings round-trip unchanged. Used by
        /// <see cref="Keyed"/> and by the registry's load / mutate
        /// surfaces so legacy comma-joined data and current
        /// semicolon-joined data don't fork into separate entries
        /// for the same physical method.</summary>
        public static string NormalizeParamTypes(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            return JoinParamTypes(SplitParamTypes(raw));
        }

        /// <summary>Build the persisted ParameterTypes value from a
        /// structured list of per-parameter full type names. Always
        /// emits the current <see cref="ParamSeparator"/> form.</summary>
        public static string JoinParamTypes(IEnumerable<string> paramTypeNames)
        {
            if (paramTypeNames == null) return string.Empty;
            return string.Join(ParamSeparator.ToString(),
                paramTypeNames
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Select(s => s.Trim()));
        }

        /// <summary>Split a persisted ParameterTypes value back into
        /// individual parameter type names. Prefers the current
        /// <see cref="ParamSeparator"/>; falls back to <c>','</c> only
        /// when the value contains no <c>;</c> AND no <c>[</c> — the
        /// legacy non-generic shape — so old saved specs keep loading.
        /// Legacy generic specs (commas + brackets) used to silently
        /// resolve to the wrong overload; now they surface a clean
        /// "type not found" upstream because the malformed slice gets
        /// passed straight to the type resolver.</summary>
        public static string[] SplitParamTypes(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return Array.Empty<string>();
            char delimiter;
            if (raw.IndexOf(ParamSeparator) >= 0)
            {
                delimiter = ParamSeparator;
            }
            else if (raw.IndexOf('[') < 0)
            {
                // Legacy non-generic form: every entry is a flat
                // FullName with no embedded commas, so the historic
                // comma split is still safe.
                delimiter = ',';
            }
            else
            {
                // Single closed-generic FullName with no separator —
                // treat the whole string as one entry rather than
                // shredding the assembly-qualified inner list.
                return new[] { raw.Trim() };
            }
            return raw.Split(delimiter)
                      .Select(s => s.Trim())
                      .Where(s => s.Length > 0)
                      .ToArray();
        }
    }

    /// <summary>
    /// In-memory registry of every method patch the user has defined this
    /// session. The engine is intentionally *not* persistent on its own —
    /// the user authoring a patch and the engine applying it are
    /// different concerns from "survive a domain reload".
    /// <see cref="PatchPersistence"/> layers a project-local JSON file
    /// (<c>UserSettings/RoslynRepl/patches.json</c>) on top of this
    /// same API.
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
            // Issue #41 P2: rewrite to the canonical separator form so
            // the in-memory ParameterTypes value matches what `Keyed`
            // computes the lookup key from. Without this a Browse
            // → Apply for a method whose legacy spec was loaded with
            // commas would store `Type;Type` while the previous spec
            // sat under `Type,Type`, leaving both entries live.
            spec.ParameterTypes = MethodPatchSpec.NormalizeParamTypes(spec.ParameterTypes);
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
        /// Raise <see cref="Changed"/> for callers that updated some
        /// in-memory view of the registry without going through
        /// <see cref="AddOrUpdate"/>. The driving use case is the
        /// auto-reapply opt-out path: after Bootstrap calls
        /// <see cref="MarkSessionDormant"/> on every Active spec,
        /// the UI needs to redraw against the new dormancy view
        /// even though no spec field changed. Persistence is left
        /// untouched on purpose — the persisted Status stays Active
        /// so the toggle remains a reload policy, not a one-way
        /// deactivation, and the inline-toggle setter installs
        /// every dormant spec immediately on its OFF→ON edge.
        ///
        /// Callers must have mutated either the dormancy set or a
        /// spec instance already in <see cref="Specs"/>; this
        /// method only fires the event.
        /// </summary>
        public static void NotifyInMemoryMutation() => Changed?.Invoke();

        public static bool Remove(string typeName, string methodName, string parameterTypes)
            => Remove(typeName, methodName, parameterTypes, out _);

        /// <summary>Remove the spec with the given key. Returns
        /// <c>true</c> iff the spec was present in the in-memory
        /// registry. <paramref name="persistedOk"/> reports whether
        /// the on-disk file now matches the post-removal state —
        /// <c>false</c> means the in-memory entry is gone but
        /// <c>patches.json</c> couldn't be deleted (locked /
        /// read-only) and a domain reload would resurrect the
        /// draft. UI callers (the per-row Delete button) can
        /// surface this as a partial-failure status. PR-review
        /// followup on #52.</summary>
        public static bool Remove(string typeName, string methodName, string parameterTypes, out bool persistedOk)
        {
            persistedOk = true;
            var key = MethodPatchSpec.Keyed(typeName, methodName, parameterTypes);
            if (!_byKey.ContainsKey(key)) return false;

            // PR-review followup on #52: build the post-removal
            // snapshot first, then call Save. If Save throws on a
            // hard write failure (permissions, disk full,
            // atomic-replace contention) the exception bubbles out
            // before we mutate _byKey, so the registry stays
            // consistent with the on-disk file. The earlier shape
            // (Remove → Persist) mutated first and only then
            // wrote the file; on a multi-row delete + write
            // failure the registry was missing a key the file
            // still had, and a domain reload would resurrect the
            // supposedly-deleted spec.
            var snapshot = _byKey.Values
                .Where(s => !string.Equals(s.Key, key, System.StringComparison.Ordinal))
                .ToList();
            persistedOk = PatchPersistence.Save(snapshot);

            // Save returned cleanly (either OK or with persistedOk
            // = false for the soft last-row Delete-failed case
            // where the empty-list write succeeded as a no-op but
            // UserSettingsStorage.Delete couldn't drop the file).
            // Commit the in-memory removal — the user's intent was
            // "this row goes away now", and the persistedOk = false
            // signal carries the "file still on disk, will
            // resurrect on reload — act now" warning up to the UI.
            _byKey.Remove(key);
            _sessionDormantKeys.Remove(key);
            Changed?.Invoke();
            return true;
        }

        public static bool Remove(MethodPatchSpec spec)
            => Remove(spec, out _);

        public static bool Remove(MethodPatchSpec spec, out bool persistedOk)
        {
            persistedOk = true;
            if (spec == null) return false;
            return Remove(spec.TargetTypeName, spec.MethodName, spec.ParameterTypes, out persistedOk);
        }

        /// <summary>Wipe both the in-memory registry and the on-disk
        /// patch file. Returns the success flag from
        /// <see cref="PatchPersistence.Clear"/> so Reset Project Data
        /// can aggregate file-deletion failures across every store and
        /// surface a partial-failure dialog. PR-review followup on
        /// #27.</summary>
        public static bool Clear()
        {
            // Two independent buckets to consider:
            //   • the live in-memory dictionary,
            //   • the persisted JSON file.
            // The dictionary can be empty after LoadFromPersistence
            // even when the file still exists on disk (e.g. a manual
            // file edit made every entry undecodable, so Load returned
            // an empty list). Routing through HasAny catches that case
            // so the README's "Reset removes every package-owned
            // persistence slot" promise survives.
            bool hadInMemory  = _byKey.Count > 0;
            bool hadPersisted = PatchPersistence.HasAny();
            if (!hadInMemory && !hadPersisted) return true;

            _byKey.Clear();
            _sessionDormantKeys.Clear();
            bool ok = PatchPersistence.Clear();
            Changed?.Invoke();
            return ok;
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
                // Issue #41 P2: legacy specs persisted with the historic
                // comma separator land here verbatim from PatchPersistence.
                // Canonicalise on load so the in-memory ParameterTypes
                // value matches what Keyed / Find expect — otherwise a
                // legacy entry and a freshly-Browsed entry for the same
                // method end up in two different registry slots.
                s.ParameterTypes = MethodPatchSpec.NormalizeParamTypes(s.ParameterTypes);
                _byKey[s.Key] = s;
            }
            Changed?.Invoke();
        }

        // Returns the success flag from PatchPersistence.Save so
        // callers that care about the soft "could not delete the
        // (now-empty) file" branch can surface it. AddOrUpdate
        // ignores the bool — its non-empty list path can't return
        // false (only throw on a hard write failure). Remove uses
        // the bool to populate its `out persistedOk` overload.
        // PR-review followup on #52.
        private static bool Persist()
        {
            return PatchPersistence.Save(_byKey.Values);
        }
    }
}
