using System.Collections.Generic;

namespace RoslynRepl.Editor.Core
{
    public class ReplOptions
    {
        public IReadOnlyList<string> Usings { get; set; } = DefaultUsings;

        public static IReadOnlyList<string> DefaultUsings { get; } = new[]
        {
            "System",
            "System.Collections.Generic",
            "System.Linq",
            "UnityEngine",
            "UnityEditor",
        };
    }
}
