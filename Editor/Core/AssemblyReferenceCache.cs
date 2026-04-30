using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.CodeAnalysis;
using UnityEditor;
using UnityEngine;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// Caches Roslyn MetadataReferences for all loaded, file-backed assemblies in
    /// the current AppDomain. Built lazily on first use; invalidated whenever a
    /// new assembly is loaded so subsequent compilations see fresh references.
    /// </summary>
    [InitializeOnLoad]
    public static class AssemblyReferenceCache
    {
        private static MetadataReference[] _cached;
        private static readonly object _lock = new();

        static AssemblyReferenceCache()
        {
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            // Ignore dynamic / in-memory assemblies (e.g. our own ReplEngine emit
            // output). Without this guard every Execute() invalidates the cache
            // and the next call pays the full reference-build cost again.
            var asm = args.LoadedAssembly;
            if (asm == null || asm.IsDynamic) return;

            string location;
            try { location = asm.Location; }
            catch { return; }

            if (string.IsNullOrEmpty(location)) return;

            Invalidate();
        }

        public static MetadataReference[] GetReferences()
        {
            lock (_lock)
            {
                if (_cached != null) return _cached;
                _cached = BuildReferences();
                return _cached;
            }
        }

        public static void Invalidate()
        {
            lock (_lock) _cached = null;
        }

        public static int CountOrZero
        {
            get { lock (_lock) return _cached?.Length ?? 0; }
        }

        private static MetadataReference[] BuildReferences()
        {
            var list = new List<MetadataReference>(256);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;

                string location;
                try { location = asm.Location; }
                catch { continue; }

                if (string.IsNullOrEmpty(location)) continue;
                if (!File.Exists(location)) continue;

                try
                {
                    list.Add(MetadataReference.CreateFromFile(location));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Roslyn REPL] Failed to load metadata for {SafeName(asm)}: {ex.Message}");
                }
            }
            return list.ToArray();
        }

        private static string SafeName(Assembly asm)
        {
            try { return asm.GetName().Name; }
            catch { return "<unknown>"; }
        }
    }
}
