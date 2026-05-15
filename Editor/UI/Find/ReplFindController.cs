using System;
using System.Collections.Generic;

namespace RoslynRepl.Editor.UI.Find
{
    /// <summary>
    /// State + orchestration for the Ctrl+F overlay search across the
    /// three REPL panes (Output, Watch, Patches). Owns the current
    /// query, the flat hit list, and the active hit index; lets the
    /// overlay UI subscribe to <see cref="StateChanged"/> so the
    /// counter / button enable state stays in sync.
    ///
    /// Lifecycle:
    /// <list type="number">
    /// <item><see cref="RegisterSource"/> from each
    /// <see cref="IReplFindable"/> at window CreateGUI time.</item>
    /// <item><see cref="Open"/> when the user hits Ctrl+F.</item>
    /// <item>Overlay sets <see cref="Query"/> on every keystroke; the
    /// controller recomputes hits and focuses the first one.</item>
    /// <item><see cref="Next"/> / <see cref="Prev"/> cycle through hits
    /// (modulo, so navigation wraps at the ends).</item>
    /// <item><see cref="Close"/> drops current-hit styling and lets the
    /// overlay hide.</item>
    /// </list>
    ///
    /// The controller also watches each source's
    /// <see cref="IReplFindable.ContentRebuilt"/> event while
    /// <see cref="IsActive"/>. When any pane rebuilds (a Run finished,
    /// the Watch panel refreshed, the patch list mutated) the hit list
    /// would point at disposed VisualElements; subscribing rebuilds the
    /// hit list against the new state and re-focuses what's now at the
    /// same index (or, if the list shrank, the nearest valid index).
    /// Without that the user would type a query, click Run, and find
    /// next / prev would crash on dead references.
    /// </summary>
    internal sealed class ReplFindController
    {
        private readonly List<IReplFindable> _sources = new();
        private readonly List<ReplFindHit> _hits = new();
        private string _query = string.Empty;
        private int _currentIndex = -1;

        /// <summary>Raised after every state mutation so the overlay
        /// (and any other view) can refresh its counter, enable
        /// state, etc. without polling.</summary>
        public event Action StateChanged;

        public bool IsActive { get; private set; }

        public string Query
        {
            get => _query;
            set
            {
                value ??= string.Empty;
                if (value == _query) return;
                _query = value;
                Recompute();
            }
        }

        public int HitCount => _hits.Count;

        /// <summary>Zero-based index of the focused hit. -1 means no
        /// hit is focused (either the query is empty / produced no
        /// matches, or the overlay is closed).</summary>
        public int CurrentIndex => _currentIndex;

        public ReplFindHit CurrentHit
            => _currentIndex >= 0 && _currentIndex < _hits.Count
                ? _hits[_currentIndex]
                : null;

        public void RegisterSource(IReplFindable source)
        {
            if (source == null) return;
            if (_sources.Contains(source)) return;
            _sources.Add(source);
            source.ContentRebuilt += OnSourceRebuilt;
        }

        public void UnregisterAllSources()
        {
            foreach (var s in _sources) s.ContentRebuilt -= OnSourceRebuilt;
            _sources.Clear();
        }

        public void Open()
        {
            IsActive = true;
            Recompute();
        }

        public void Close()
        {
            UnsetCurrentHighlight();
            _hits.Clear();
            _currentIndex = -1;
            IsActive = false;
            // Clear the global highlight query so character-level
            // rich-text wrapping on every Label / tree cell stops.
            ReplFindHighlight.SetActiveQuery(null);
            StateChanged?.Invoke();
        }

        public void Next()
        {
            if (_hits.Count == 0) return;
            MoveTo((_currentIndex + 1) % _hits.Count);
        }

        public void Prev()
        {
            if (_hits.Count == 0) return;
            // Modular wrap that handles _currentIndex == 0 → last.
            int n = _hits.Count;
            MoveTo((_currentIndex - 1 + n) % n);
        }

        /// <summary>Force a recompute even if the query string is
        /// unchanged. Used by external callers (panel rebuilds, the
        /// Run pipeline) so the hit list doesn't drift away from the
        /// underlying data.</summary>
        public void Refresh()
        {
            if (!IsActive) return;
            Recompute();
        }

        private void OnSourceRebuilt()
        {
            if (!IsActive) return;
            Recompute();
        }

        private void Recompute()
        {
            // Remember which hit was current so we can try to keep the
            // user near "the same place" across recomputes. The source
            // panels build fresh ReplFindHit instances each pass, so a
            // pointer comparison would always miss; index-based recall
            // is the best we can do without a stable hit identity.
            int oldIndex = _currentIndex;
            UnsetCurrentHighlight();

            // Push the query down to the global highlight machinery
            // before walking sources — bind-cell closures in
            // virtualized trees read this string on every cell bind,
            // so the order matters: query first, then collect hits
            // (each panel calls RefreshItems / re-decorates via the
            // event the setter fires).
            ReplFindHighlight.SetActiveQuery(IsActive ? _query : null);

            _hits.Clear();
            if (IsActive && !string.IsNullOrEmpty(_query))
            {
                foreach (var s in _sources)
                {
                    try { s.CollectMatches(_query, _hits); }
                    catch (Exception ex)
                    {
                        // A panel's collector throwing shouldn't take
                        // down the overlay. Log so the diagnostic is
                        // visible without breaking the search bar.
                        UnityEngine.Debug.LogWarning(
                            $"[Roslyn REPL] Find: source {s.GetType().Name} threw during CollectMatches: " +
                            $"{ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            if (_hits.Count == 0)
            {
                _currentIndex = -1;
            }
            else
            {
                // Clamp the old index into the new range. Anchoring
                // at 0 on every recompute would jerk the focus back
                // to the top each time the user typed a character; a
                // clamp keeps them within reach of where they were.
                _currentIndex = oldIndex < 0
                    ? 0
                    : System.Math.Min(oldIndex, _hits.Count - 1);
                ApplyCurrent();
            }

            StateChanged?.Invoke();
        }

        private void MoveTo(int index)
        {
            UnsetCurrentHighlight();
            _currentIndex = index;
            ApplyCurrent();
            StateChanged?.Invoke();
        }

        private void ApplyCurrent()
        {
            var hit = CurrentHit;
            if (hit == null) return;
            // Order: scroll first, then set the current-class. A few
            // panels rely on schedule.Execute to flip the highlight
            // *after* the next layout pass; scrolling first means the
            // row is already in the right ScrollView position by the
            // time the class lands.
            hit.ScrollIntoView?.Invoke();
            hit.SetCurrent?.Invoke();
        }

        private void UnsetCurrentHighlight()
        {
            CurrentHit?.UnsetCurrent?.Invoke();
        }
    }
}
