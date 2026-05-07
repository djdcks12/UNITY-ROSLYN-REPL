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
    /// Identification + content of a single method patch.
    ///
    /// The triple (TargetTypeName, MethodName, ParameterTypes) uniquely
    /// identifies a patchable method in the AppDomain. ParameterTypes is
    /// a comma-joined list of full type names — necessary for disambig-
    /// uation when the target has overloads, and stable enough across
    /// editor sessions to be a safe persistence key for Phase B.
    ///
    /// PatchBody is the user-edited replacement body (everything between
    /// the method's outer braces). OriginalBody is the snapshot pulled
    /// from the .cs source at the time the patch was created — used by
    /// Phase C diff/export and to render the editor's starting content.
    /// MVP leaves OriginalBody empty until the source-pull pipeline
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
    /// session. Phase A is intentionally *not* persistent — the user
    /// authoring a patch and the engine applying it are different
    /// concerns from "survive a domain reload". Phase B will add
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

        public static IReadOnlyCollection<MethodPatchSpec> Specs => _byKey.Values;

        public static int Count => _byKey.Count;

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
            Changed?.Invoke();
        }

        public static bool Remove(string typeName, string methodName, string parameterTypes)
        {
            var key = MethodPatchSpec.Keyed(typeName, methodName, parameterTypes);
            if (_byKey.Remove(key))
            {
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
            if (_byKey.Count == 0) return;
            _byKey.Clear();
            Changed?.Invoke();
        }
    }
}
