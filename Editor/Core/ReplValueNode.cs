using System.Collections.Generic;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// One row in the result tree shown by the REPL output panel. The tree is
    /// produced eagerly by <see cref="SimpleObjectSerializer"/> with depth and
    /// collection-head caps so even large object graphs don't explode.
    /// </summary>
    public class ReplValueNode
    {
        public string Name;          // field/property name, "[index]" for collection items, or "(result)" for the root
        public string TypeName;      // short type name (e.g. "List<int>")
        public string Preview;       // 1-line value preview
        public bool IsExpandable;    // true iff Children may be non-empty
        public List<ReplValueNode> Children = new();
    }
}
