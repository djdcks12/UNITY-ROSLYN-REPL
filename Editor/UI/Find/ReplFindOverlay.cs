using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace RoslynRepl.Editor.UI.Find
{
    /// <summary>
    /// The Ctrl+F search bar that sits above the main pane area. A
    /// thin horizontal strip with: a text input, an N/M counter, Prev
    /// / Next arrows, and a Close button. Self-contained — the host
    /// just calls <see cref="Show"/> on Ctrl+F and the overlay drives
    /// the rest via <see cref="ReplFindController"/>.
    ///
    /// Keyboard contract (bound on the input field):
    /// <list type="bullet">
    /// <item><c>Enter</c> / <c>F3</c> → Next</item>
    /// <item><c>Shift+Enter</c> / <c>Shift+F3</c> → Prev</item>
    /// <item><c>Esc</c> → Close</item>
    /// </list>
    /// </summary>
    internal sealed class ReplFindOverlay
    {
        private readonly ReplFindController _controller;
        private VisualElement _root;
        private TextField _input;
        private Label _counter;
        private Button _prevBtn;
        private Button _nextBtn;
        private Button _closeBtn;

        public VisualElement Root => _root;

        public ReplFindOverlay(ReplFindController controller)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            Build();
            _controller.StateChanged += RefreshCounterAndButtons;
        }

        public void Dispose()
        {
            if (_controller != null) _controller.StateChanged -= RefreshCounterAndButtons;
        }

        /// <summary>Show the overlay and put the caret in the input
        /// field. If the overlay was already visible, the existing
        /// query is selected so the user can type-replace without an
        /// extra Ctrl+A.</summary>
        public void Show()
        {
            _root.style.display = DisplayStyle.Flex;
            _controller.Open();

            // Defer the focus + select-all into a schedule.Execute so
            // it lands after UI Toolkit finishes the display-flip
            // layout pass. Calling Focus / SelectAll directly on the
            // same frame the element becomes visible doesn't reliably
            // move the caret on every Editor version.
            _input.schedule.Execute(() =>
            {
                _input.Focus();
                _input.SelectAll();
            }).StartingIn(0);

            // Publish the input refocus hook so hits that briefly
            // steal focus (Patches body / form fields call
            // Focus() + SelectRange() to navigate the TextField's
            // internal scroll view to the match) can pop focus
            // back to the Find overlay on the same call stack.
            // Without this the body editor would keep keyboard
            // focus and the user couldn't continue typing.
            ReplFindHighlight.RequestRefocusInput = RefocusInput;

            RefreshCounterAndButtons();
        }

        public void Hide()
        {
            _root.style.display = DisplayStyle.None;
            // Drop the refocus hook so any stray ScrollIntoView fired
            // after the overlay closed doesn't reach for a hidden
            // input.
            if (ReplFindHighlight.RequestRefocusInput == (Action)RefocusInput)
                ReplFindHighlight.RequestRefocusInput = null;
            _controller.Close();
        }

        /// <summary>Re-focus the search input. Public so the static
        /// <see cref="ReplFindHighlight.RequestRefocusInput"/> hook
        /// can point at it without exposing private state.</summary>
        public void RefocusInput()
        {
            if (_input == null) return;
            // Don't steal SelectAll behaviour from the user — they
            // may be mid-edit on the query. Plain Focus() preserves
            // the caret position so typing resumes seamlessly.
            try { _input.Focus(); } catch { /* shouldn't throw, defensive */ }
        }

        private void Build()
        {
            _root = new VisualElement();
            _root.AddToClassList("rr-find-bar");
            _root.style.display = DisplayStyle.None;

            var icon = new Label("⌕");
            icon.AddToClassList("rr-find-icon");
            _root.Add(icon);

            _input = new TextField();
            _input.AddToClassList("rr-find-input");
            _input.tooltip = "Find in Output / Watch / Patches — Enter (Next), Shift+Enter (Prev), Esc (Close)";
            _input.RegisterValueChangedCallback(evt =>
            {
                _controller.Query = evt.newValue;
            });
            _input.RegisterCallback<KeyDownEvent>(OnInputKeyDown, TrickleDown.TrickleDown);
            _root.Add(_input);

            _counter = new Label();
            _counter.AddToClassList("rr-find-counter");
            _root.Add(_counter);

            _prevBtn = new Button(() => _controller.Prev()) { text = "‹" };
            _prevBtn.AddToClassList("rr-find-nav");
            _prevBtn.tooltip = "Previous match (Shift+Enter / Shift+F3)";
            _root.Add(_prevBtn);

            _nextBtn = new Button(() => _controller.Next()) { text = "›" };
            _nextBtn.AddToClassList("rr-find-nav");
            _nextBtn.tooltip = "Next match (Enter / F3)";
            _root.Add(_nextBtn);

            _closeBtn = new Button(Hide) { text = "✕" };
            _closeBtn.AddToClassList("rr-find-close");
            _closeBtn.tooltip = "Close find (Esc)";
            _root.Add(_closeBtn);
        }

        private void OnInputKeyDown(KeyDownEvent evt)
        {
            // Esc closes the overlay regardless of modifier state.
            if (evt.keyCode == KeyCode.Escape)
            {
                Hide();
                evt.StopPropagation();
                return;
            }

            // F3 / Enter navigate without the user having to reach for
            // the buttons. The shift modifier flips direction so a
            // user can repeat-tap Enter to walk forward and
            // Shift+Enter to walk back.
            bool isEnter = evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter;
            bool isF3 = evt.keyCode == KeyCode.F3;
            if (isEnter || isF3)
            {
                if (evt.shiftKey) _controller.Prev();
                else _controller.Next();
                evt.StopPropagation();
                evt.PreventDefault();
            }
        }

        private void RefreshCounterAndButtons()
        {
            if (_counter == null) return;
            int total = _controller.HitCount;
            int cur = _controller.CurrentIndex;

            if (string.IsNullOrEmpty(_controller.Query))
            {
                _counter.text = string.Empty;
                _counter.RemoveFromClassList("rr-find-counter--none");
            }
            else if (total == 0)
            {
                _counter.text = "no matches";
                _counter.AddToClassList("rr-find-counter--none");
            }
            else if (cur < 0)
            {
                // User typed a query but hasn't pressed Enter yet —
                // show the total count alone. Once they navigate,
                // the counter switches to "{cur+1} / {total}". The
                // hint reminds them Enter is the trigger.
                _counter.text = total == 1 ? "1 match — Enter" : $"{total} matches — Enter";
                _counter.RemoveFromClassList("rr-find-counter--none");
            }
            else
            {
                // Display the 1-based index so the counter reads like
                // a user would expect ("3 of 12") rather than the
                // controller's internal 0-based index.
                _counter.text = $"{cur + 1} / {total}";
                _counter.RemoveFromClassList("rr-find-counter--none");
            }

            bool hasHits = total > 0;
            _prevBtn?.SetEnabled(hasHits);
            _nextBtn?.SetEnabled(hasHits);
        }
    }
}
