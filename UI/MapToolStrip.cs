using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DataverseProcessMapper.Models;

namespace DataverseProcessMapper.UI
{
    /// <summary>
    /// The thin strip above one map: find, layout, expand / collapse, fit and
    /// zoom, full screen. It drives its <see cref="DiagramPanel"/> directly and
    /// follows the panel's events, so the labels also track the mouse (Ctrl+wheel,
    /// clicks on matches). The layout is the exception: it is one setting for
    /// both tabs, so a choice is only requested (<see cref="LayoutModeRequested"/>)
    /// and the host reflects it back with <see cref="SetLayoutMode"/>.
    /// </summary>
    public sealed class MapToolStrip : ToolStrip
    {
        private const int FindDelayMs = 200; // incremental search waits for a pause in typing
        private const float ZoomFactor = 1.2f;

        // A cue banner needs visual styles (comctl32 v6), which XrmToolBox enables.
        private const string FindCue = "Find step… (Ctrl+F)";
        private const string FindTip = "Find a step by its name or text (Ctrl+F). " +
                                       "Enter = next match, Shift+Enter = previous, Esc clears.";

        private readonly DiagramPanel _panel;
        private readonly int _iconPx;
        private readonly Timer _findTimer;

        private readonly ToolStripTextBox _findBox;
        private readonly ToolStripDropDownButton _findOptions;
        private readonly ToolStripMenuItem _includeDetails;
        private readonly ToolStripButton _previous;
        private readonly ToolStripButton _next;
        private readonly ToolStripLabel _matchLabel;
        private readonly ToolStripDropDownButton _layout;
        private readonly ToolStripMenuItem _nested;
        private readonly ToolStripMenuItem _flat;
        private readonly ToolStripButton _expandAll;
        private readonly ToolStripButton _collapseAll;
        private readonly ToolStripSplitButton _fit;
        private readonly ToolStripButton _zoomOut;
        private readonly ToolStripLabel _zoomLabel;
        private readonly ToolStripButton _zoomIn;
        private readonly ToolStripButton _fullScreen;
        private readonly ToolStripSeparator _afterFind;
        private readonly ToolStripSeparator _afterCollapse;
        private readonly ToolStripSeparator _afterZoom;

        // On a narrow map these go to the overflow menu a group at a time, in this
        // order: the layout (a setting) and expand / collapse first, then the zoom
        // (Ctrl+wheel and Ctrl+Plus / Minus zoom too), then Fit; Full screen only
        // when the find controls alone fill the strip, so it is never out of reach.
        private readonly ToolStripItem[][] _overflowGroups;
        // Each item's width on the strip, measured while it was there.
        private readonly Dictionary<ToolStripItem, int> _mainWidths = new Dictionary<ToolStripItem, int>();

        private LayoutMode _layoutMode = ProcessMapBuilder.DefaultMode;
        private bool _settingTerm;      // SetFindTerm writes the box and option without a debounced search
        private string _searchedTerm;   // what the panel's current matches are for; null when none
        private bool _searchedDetails;

        /// <summary>Raised when the user picks a layout; the host applies it everywhere and calls <see cref="SetLayoutMode"/>.</summary>
        public event Action<LayoutMode> LayoutModeRequested;

        public MapToolStrip(DiagramPanel panel)
        {
            _panel = panel ?? throw new ArgumentNullException(nameof(panel));
            _iconPx = IconFactory.SizeFor(this);

            GripStyle = ToolStripGripStyle.Hidden;
            ImageScalingSize = new Size(_iconPx, _iconPx);
            // The find controls never overflow (a text box in the chevron menu is
            // awkward), and Full screen, what a narrow map needs, goes last; the other
            // commands go there least used first (see FitOverflow).
            LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow;
            CanOverflow = true;
            ShowItemToolTips = true;
            Padding = new Padding(3, 0, 1, 0); // keeps the find box off the splitter

            _findTimer = new Timer { Interval = FindDelayMs };
            _findTimer.Tick += (s, e) => RunSearch();

            // AutoSize off: an auto-sized ToolStripTextBox falls back to 100 px once parented.
            _findBox = new ToolStripTextBox("Find step")
            {
                AutoSize = false,
                Size = new Size(150, 23),
                Overflow = ToolStripItemOverflow.Never,
                AccessibleName = "Find step"
            };
            _findBox.TextBox.HandleCreated += (s, e) =>
                SendMessage(_findBox.TextBox.Handle, EM_SETCUEBANNER, (IntPtr)1, FindCue);
            _findBox.TextChanged += (s, e) => OnFindTextChanged();

            _includeDetails = new ToolStripMenuItem("Include step details")
            {
                CheckOnClick = true,
                ToolTipText = "Also match the details of each step: inputs, expressions, recipients…"
            };
            _includeDetails.CheckedChanged += (s, e) => { if (!_settingTerm) RunSearch(); };
            _findOptions = new ToolStripDropDownButton
            {
                DisplayStyle = ToolStripItemDisplayStyle.None, // just the arrow, next to the box
                AccessibleName = "Find options",
                AutoToolTip = false,
                Overflow = ToolStripItemOverflow.Never
            };
            _findOptions.DropDownItems.Add(_includeDetails);
            _findOptions.DropDown.ShowItemToolTips = true;

            _previous = IconButton("Previous match", IconNames.Previous, (s, e) => FindPreviousMatch());
            _next = IconButton("Next match", IconNames.Next, (s, e) => FindNextMatch());
            _previous.Overflow = _next.Overflow = ToolStripItemOverflow.Never;

            // Fixed widths, so nothing to the right jumps about while typing or zooming.
            _matchLabel = new ToolStripLabel
            {
                AutoSize = false,
                Size = new Size(66, 22),
                Overflow = ToolStripItemOverflow.Never,
                ToolTipText = "Steps in this map that match the find box"
            };

            _nested = new ToolStripMenuItem("Nested")
            {
                ToolTipText = "Each scope, condition, case and loop is a frame around its steps, " +
                              "with the next step below the frame"
            };
            _nested.Click += (s, e) => RequestLayout(LayoutMode.Nested);
            _flat = new ToolStripMenuItem("Flat")
            {
                ToolTipText = "One flowchart: containers are headers with their steps hanging below"
            };
            _flat.Click += (s, e) => RequestLayout(LayoutMode.Flat);
            _layout = new ToolStripDropDownButton("Layout", IconFactory.Get(IconNames.Layout, _iconPx))
            {
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                ImageScaling = ToolStripItemImageScaling.None,
                AutoToolTip = false
            };
            _layout.DropDownItems.AddRange(new ToolStripItem[] { _nested, _flat });
            _layout.DropDown.ShowItemToolTips = true;

            _expandAll = IconButton("Expand all", IconNames.ExpandAll, (s, e) => _panel.SetAllCollapsed(false));
            _collapseAll = IconButton("Collapse all", IconNames.CollapseAll, (s, e) => _panel.SetAllCollapsed(true));

            var fitWidth = new ToolStripMenuItem("Fit width", null, (s, e) => _panel.ZoomToFit())
            {
                ToolTipText = "Fit the map's width to the window; it scrolls down for the rest (Ctrl+0)"
            };
            var fitWhole = new ToolStripMenuItem("Fit whole map", null, (s, e) => _panel.FitWholeMap())
            {
                ToolTipText = "Zoom out until the whole map fits in the window"
            };
            var actualSize = new ToolStripMenuItem("Actual size (100%)", null, (s, e) => _panel.ActualSize())
            {
                ToolTipText = "Show the map at its natural size"
            };
            _fit = new ToolStripSplitButton("Fit", IconFactory.Get(IconNames.Fit, _iconPx))
            {
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                ImageScaling = ToolStripItemImageScaling.None,
                AutoToolTip = false
            };
            _fit.ButtonClick += (s, e) => _panel.ZoomToFit();
            _fit.DropDownItems.AddRange(new ToolStripItem[] { fitWidth, fitWhole, actualSize });
            _fit.DropDown.ShowItemToolTips = true;

            _zoomOut = IconButton("Zoom out", IconNames.ZoomOut, (s, e) => _panel.ZoomStep(1f / ZoomFactor));
            _zoomLabel = new ToolStripLabel
            {
                AutoSize = false,
                Size = new Size(42, 22),
                ToolTipText = "Zoom level (Ctrl+wheel also zooms)"
            };
            _zoomIn = IconButton("Zoom in", IconNames.ZoomIn, (s, e) => _panel.ZoomStep(ZoomFactor));

            _fullScreen = IconButton("Full screen", IconNames.FullScreen, (s, e) => _panel.ToggleFullScreen());

            _afterFind = new ToolStripSeparator();
            _afterCollapse = new ToolStripSeparator();
            _afterZoom = new ToolStripSeparator();
            Items.AddRange(new ToolStripItem[]
            {
                _findBox, _findOptions, _previous, _next, _matchLabel, _afterFind,
                _layout, _expandAll, _collapseAll, _afterCollapse,
                _fit, _zoomOut, _zoomLabel, _zoomIn, _afterZoom,
                _fullScreen
            });
            _overflowGroups = new[]
            {
                new ToolStripItem[] { _layout, _expandAll, _collapseAll, _afterCollapse },
                new ToolStripItem[] { _zoomOut, _zoomLabel, _zoomIn },
                new ToolStripItem[] { _fit, _afterZoom },
                new ToolStripItem[] { _fullScreen, _afterFind }
            };
            // FitOverflow decides what overflows; the stock layout would take the right-hand end first.
            foreach (ToolStripItem item in Items) item.Overflow = ToolStripItemOverflow.Never;

            _panel.MapChanged += OnMapChanged;
            _panel.ZoomChanged += UpdateZoom;
            _panel.FindChanged += UpdateFind;
            _panel.FullScreenChanged += OnFullScreenChanged;

            SetLayoutMode(_layoutMode);
            UpdateState();
        }

        private ToolStripButton IconButton(string text, string icon, EventHandler click)
        {
            var button = new ToolStripButton(text, IconFactory.Get(icon, _iconPx), click)
            {
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                ImageScaling = ToolStripItemImageScaling.None,
                AutoToolTip = false
            };
            return button;
        }

        // ---------- public API ----------

        /// <summary>Shows the layout the host has applied; raises nothing.</summary>
        public void SetLayoutMode(LayoutMode mode)
        {
            _layoutMode = mode;
            _nested.Checked = mode == LayoutMode.Nested;
            _flat.Checked = mode == LayoutMode.Flat;
            _layout.ToolTipText = "Map layout: " + (mode == LayoutMode.Nested ? "nested" : "flat");
        }

        /// <summary>The find box's text.</summary>
        public string FindTerm => _findBox.Text;

        /// <summary>Whether "Include step details" is on.</summary>
        public bool IncludeDetails => _includeDetails.Checked;

        /// <summary>Puts the cursor in the find box with its text selected (Ctrl+F).</summary>
        public void FocusFind()
        {
            if (!_findBox.Enabled) return;
            _findBox.Focus();
            _findBox.SelectAll();
        }

        /// <summary>
        /// Moves to the next match (F3, Enter). A term still waiting for the
        /// typing pause, or one the map has not been searched for, is searched
        /// first, which lands on its first match.
        /// </summary>
        public void FindNextMatch()
        {
            if (_panel.Map == null) return;
            if (SearchIfStale()) return;
            _panel.FindNext();
        }

        /// <summary>Moves to the previous match (Shift+F3, Shift+Enter); a fresh search starts from the last match.</summary>
        public void FindPreviousMatch()
        {
            if (_panel.Map == null) return;
            SearchIfStale();
            _panel.FindPrevious();
        }

        /// <summary>
        /// Fills the find box and searches straight away (no typing pause), e.g.
        /// with the term of a definition search. A map shown later is searched
        /// for it too.
        /// </summary>
        public void SetFindTerm(string term, bool includeDetails)
        {
            _settingTerm = true;
            try
            {
                _includeDetails.Checked = includeDetails;
                _findBox.Text = term ?? "";
            }
            finally
            {
                _settingTerm = false;
            }
            RunSearch();
        }

        // ---------- find ----------

        private void OnFindTextChanged()
        {
            if (_settingTerm) return;
            _findTimer.Stop();
            if (string.IsNullOrWhiteSpace(_findBox.Text)) RunSearch(); // clearing needs no pause
            else _findTimer.Start();
        }

        /// <summary>Searches the map for the box's term now (an empty box clears the search).</summary>
        private void RunSearch()
        {
            _findTimer.Stop();
            var term = _findBox.Text;
            if (_panel.Map == null || string.IsNullOrWhiteSpace(term))
            {
                _searchedTerm = null;
                _panel.ClearFind();
            }
            else
            {
                _searchedTerm = term.Trim();
                _searchedDetails = _includeDetails.Checked;
                _panel.Find(term, _includeDetails.Checked);
            }
            UpdateFind();
        }

        /// <summary>Runs the search when the panel's matches are not for the box's term; true when it did.</summary>
        private bool SearchIfStale()
        {
            bool current = !_findTimer.Enabled && _searchedTerm != null && _searchedDetails == _includeDetails.Checked &&
                           string.Equals(_searchedTerm, _findBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);
            if (current) return false;
            RunSearch();
            return true;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // The find box's keys are handled here, ahead of the host's shortcuts:
            // Esc in a box with text clears it rather than leaving full screen.
            if (_findBox.Focused)
            {
                switch (keyData)
                {
                    case Keys.Enter:
                        FindNextMatch();
                        return true;
                    case Keys.Shift | Keys.Enter:
                        FindPreviousMatch();
                        return true;
                    case Keys.Escape:
                        if (_findBox.TextLength == 0) break; // nothing to clear: let Esc leave full screen
                        _findBox.Clear();
                        return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------- overflow ----------

        protected override void OnLayout(LayoutEventArgs e)
        {
            // Changing an item's overflow asks for another layout; it runs once this
            // one is done and finds nothing more to change.
            FitOverflow();
            base.OnLayout(e);
            foreach (ToolStripItem item in Items)
                if (item.Placement == ToolStripItemPlacement.Main && item.Width > 0) _mainWidths[item] = item.Width;
        }

        /// <summary>
        /// Sends whole groups of commands to the overflow menu, least used first,
        /// until the rest fit beside the overflow button.
        /// </summary>
        private void FitOverflow()
        {
            int room = DisplayRectangle.Width;
            if (room <= 0 || _overflowGroups == null) return;

            int needed = 0;
            foreach (ToolStripItem item in Items) needed += MainWidth(item);
            // The overflow button has its size while hidden too (it has no preferred size).
            int chevron = (OverflowButton.Width > 0 ? OverflowButton.Width : 16) + OverflowButton.Margin.Horizontal;

            int overflowed = 0;
            while (overflowed < _overflowGroups.Length && needed > room - (overflowed > 0 ? chevron : 0))
            {
                foreach (var item in _overflowGroups[overflowed]) needed -= MainWidth(item);
                overflowed++;
            }

            for (int g = 0; g < _overflowGroups.Length; g++)
                foreach (var item in _overflowGroups[g])
                    item.Overflow = g < overflowed ? ToolStripItemOverflow.Always : ToolStripItemOverflow.Never;
            // Separators only between what is shown: none ending the menu, none doubled on the strip.
            _afterCollapse.Available = overflowed != 1;
            _afterZoom.Available = overflowed < 3;
            _afterFind.Available = overflowed < 4;
        }

        /// <summary>The item's width on the strip (whether or not it is there now), margins included.</summary>
        private int MainWidth(ToolStripItem item)
        {
            int width;
            if (!_mainWidths.TryGetValue(item, out width))
                width = item is ToolStripSeparator ? 6 : item.AutoSize ? item.GetPreferredSize(Size.Empty).Width : item.Width;
            return width + item.Margin.Horizontal;
        }

        // ---------- panel events ----------

        private void OnMapChanged()
        {
            _findTimer.Stop();
            _searchedTerm = null; // the new map has no matches yet
            // Keep looking for the same thing on the next process the user opens.
            if (_panel.Map != null && !string.IsNullOrWhiteSpace(_findBox.Text)) RunSearch();
            UpdateState();
        }

        private void OnFullScreenChanged(bool fullScreen) => UpdateFullScreen();

        // ---------- state ----------

        private void UpdateState()
        {
            bool hasMap = _panel.Map != null;
            Available(_findBox, hasMap, FindTip, NoMap("find its steps"));
            Available(_findOptions, hasMap, "Find options", NoMap("find its steps"));
            Available(_expandAll, hasMap, "Expand all containers", NoMap("expand its steps"));
            Available(_collapseAll, hasMap, "Collapse all containers", NoMap("collapse its steps"));
            Available(_fit, hasMap, "Fit the map to the window's width (Ctrl+0)", NoMap("fit its map to the window"));
            Available(_zoomOut, hasMap, "Zoom out (Ctrl+Minus)", NoMap("zoom its map"));
            Available(_zoomIn, hasMap, "Zoom in (Ctrl+Plus)", NoMap("zoom its map"));
            Available(_zoomLabel, hasMap, "Zoom level (Ctrl+wheel also zooms)", NoMap("zoom its map"));
            Available(_matchLabel, hasMap, "Steps in this map that match the find box", NoMap("find its steps"));
            UpdateFind();
            UpdateZoom();
            UpdateFullScreen();
        }

        private void UpdateFind()
        {
            bool hasMap = _panel.Map != null;
            int count = _panel.MatchCount;

            // The panel always has a current match while it has any (Find selects the first).
            _matchLabel.Text = !hasMap || _searchedTerm == null ? ""
                : count == 0 ? "No matches"
                : (Math.Max(_panel.MatchIndex, 0) + 1).ToString(CultureInfo.CurrentCulture) + " of " +
                  count.ToString(CultureInfo.CurrentCulture);

            string reason = !hasMap ? NoMap("find its steps")
                : string.IsNullOrWhiteSpace(_findBox.Text) ? "Type in the find box to find steps"
                : "No steps match the find box";
            Available(_previous, hasMap && count > 0, "Previous match (Shift+F3)", reason);
            Available(_next, hasMap && count > 0, "Next match (F3)", reason);
        }

        private void UpdateZoom()
        {
            // Blank without a map: a greyed "100%" would describe nothing.
            _zoomLabel.Text = _panel.Map == null ? ""
                : Math.Round(_panel.Zoom * 100f).ToString("0", CultureInfo.CurrentCulture) + "%";
        }

        private void UpdateFullScreen()
        {
            bool full = _panel.IsFullScreen;
            _fullScreen.Checked = full;
            _fullScreen.Image = IconFactory.Get(full ? IconNames.ExitFullScreen : IconNames.FullScreen, _iconPx);
            _fullScreen.Text = full ? "Exit full screen" : "Full screen";
            // Leaving full screen stays possible even if the map has just gone away.
            Available(_fullScreen, _panel.Map != null || full,
                full ? "Show the process list (Esc)" : "Hide the process list (Esc)",
                NoMap("view its map full screen"));
        }

        private void RequestLayout(LayoutMode mode)
        {
            if (mode == _layoutMode) return;
            LayoutModeRequested?.Invoke(mode);
        }

        /// <summary>Enables or disables an item; a disabled item's tooltip says why.</summary>
        private static void Available(ToolStripItem item, bool enabled, string tip, string reason)
        {
            item.Enabled = enabled;
            item.ToolTipText = enabled ? tip : reason;
        }

        private static string NoMap(string toDoWhat) => "Select a process to " + toDoWhat;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _panel.MapChanged -= OnMapChanged;
                _panel.ZoomChanged -= UpdateZoom;
                _panel.FindChanged -= UpdateFind;
                _panel.FullScreenChanged -= OnFullScreenChanged;
                _findTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
    }
}
