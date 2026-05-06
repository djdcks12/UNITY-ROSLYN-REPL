using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace RoslynRepl.Editor.UI
{
    /// <summary>
    /// Multiline code input wrapped with a line-number gutter and a caret-
    /// position indicator. The gutter is a sibling Label kept in sync with
    /// the input's text content; both sit inside a common scrollable row so
    /// vertical alignment stays one-to-one without having to chase the
    /// TextField's internal scroller.
    ///
    /// Phase 4b extends the gutter with per-line error markers (red dot +
    /// tooltip). Markers are addressable by 1-based line number and are
    /// cleared on the next text change so stale diagnostics never linger.
    /// </summary>
    public class CodeEditorView : VisualElement
    {
        private readonly TextField _input;
        private readonly VisualElement _gutter;
        private readonly Label _caretLabel;
        private readonly List<GutterRow> _gutterRows = new();
        private readonly Dictionary<int, string> _lineErrors = new();
        private string _lastText = string.Empty;

        public TextField Input => _input;
        public Label CaretLabel => _caretLabel;

        public string value
        {
            get => _input.value;
            set
            {
                if (_input.value == value) return;
                _input.value = value;
            }
        }

        public event Action<string> TextChanged;

        public CodeEditorView()
        {
            AddToClassList("rr-code-editor");
            style.flexDirection = FlexDirection.Column;
            style.flexGrow = 1;

            // Row hosting [gutter | input]. Both children share the row's
            // height so each gutter line aligns with its input row.
            var row = new VisualElement();
            row.AddToClassList("rr-code-row");
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexGrow = 1;

            _gutter = new VisualElement();
            _gutter.AddToClassList("rr-code-gutter");
            _gutter.style.flexDirection = FlexDirection.Column;

            _input = new TextField();
            _input.multiline = true;
            _input.AddToClassList("rr-code-input");
            _input.style.flexGrow = 1;

            row.Add(_gutter);
            row.Add(_input);
            Add(row);

            _caretLabel = new Label("Ln 1, Col 1");
            _caretLabel.AddToClassList("rr-code-caret");
            Add(_caretLabel);

            _input.RegisterValueChangedCallback(evt =>
            {
                _lastText = evt.newValue ?? string.Empty;
                // Stale diagnostics referenced lines from the previous
                // compile; nothing about the new buffer matches them, so
                // drop them rather than risk pointing at the wrong place.
                if (_lineErrors.Count > 0) _lineErrors.Clear();
                RefreshGutter();
                UpdateCaret();
                TextChanged?.Invoke(_lastText);
            });

            _input.RegisterCallback<KeyUpEvent>(_ => UpdateCaret());
            _input.RegisterCallback<MouseUpEvent>(_ => UpdateCaret());
            _input.RegisterCallback<FocusInEvent>(_ => UpdateCaret());

            RefreshGutter();
            UpdateCaret();
        }

        /// <summary>
        /// Set per-line error markers. Replaces any previously displayed
        /// markers. <c>line</c> is 1-based to match Roslyn diagnostics; lines
        /// outside the current buffer are silently clamped/dropped.
        /// </summary>
        public void SetErrorMarkers(IEnumerable<(int line, string message)> errors)
        {
            _lineErrors.Clear();
            if (errors != null)
            {
                foreach (var (line, message) in errors)
                {
                    if (line < 1) continue;
                    if (_lineErrors.TryGetValue(line, out var existing))
                        _lineErrors[line] = existing + "\n" + message;
                    else
                        _lineErrors[line] = message ?? string.Empty;
                }
            }
            RefreshGutter();
        }

        public void ClearErrorMarkers()
        {
            if (_lineErrors.Count == 0) return;
            _lineErrors.Clear();
            RefreshGutter();
        }

        private void RefreshGutter()
        {
            int targetLines = Math.Max(1, CountLines(_lastText));

            // Grow row pool if needed; reuse existing rows otherwise so we
            // don't churn VisualElements on every keystroke.
            while (_gutterRows.Count < targetLines)
            {
                int lineNumber = _gutterRows.Count + 1;
                var rowEl = BuildGutterRow(lineNumber);
                _gutterRows.Add(rowEl);
                _gutter.Add(rowEl.Container);
            }
            for (int i = _gutterRows.Count - 1; i >= targetLines; i--)
            {
                _gutter.Remove(_gutterRows[i].Container);
                _gutterRows.RemoveAt(i);
            }

            for (int i = 0; i < targetLines; i++)
            {
                var rowEl = _gutterRows[i];
                int lineNumber = i + 1;
                rowEl.Number.text = lineNumber.ToString();
                bool hasError = _lineErrors.TryGetValue(lineNumber, out var msg);
                rowEl.Marker.visible = hasError;
                rowEl.Container.tooltip = hasError ? msg : null;
                rowEl.Container.EnableInClassList("rr-code-gutter-row--error", hasError);
            }
        }

        private static GutterRow BuildGutterRow(int _)
        {
            var container = new VisualElement();
            container.AddToClassList("rr-code-gutter-row");
            container.style.flexDirection = FlexDirection.Row;

            var marker = new VisualElement();
            marker.AddToClassList("rr-code-gutter-marker");
            marker.visible = false;

            var number = new Label();
            number.AddToClassList("rr-code-gutter-number");

            container.Add(marker);
            container.Add(number);
            return new GutterRow { Container = container, Marker = marker, Number = number };
        }

        private void UpdateCaret()
        {
            int idx = _input.cursorIndex;
            var (line, col) = OffsetToLineCol(_lastText, idx);
            _caretLabel.text = $"Ln {line}, Col {col}";
        }

        private static int CountLines(string s)
        {
            if (string.IsNullOrEmpty(s)) return 1;
            int count = 1;
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '\n') count++;
            return count;
        }

        private static (int line, int col) OffsetToLineCol(string s, int offset)
        {
            if (string.IsNullOrEmpty(s)) return (1, 1);
            if (offset < 0) offset = 0;
            if (offset > s.Length) offset = s.Length;
            int line = 1, col = 1;
            for (int i = 0; i < offset; i++)
            {
                if (s[i] == '\n') { line++; col = 1; }
                else col++;
            }
            return (line, col);
        }

        private sealed class GutterRow
        {
            public VisualElement Container;
            public VisualElement Marker;
            public Label Number;
        }
    }
}
