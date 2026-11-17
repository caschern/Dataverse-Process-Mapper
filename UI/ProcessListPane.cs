using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DataverseProcessMapper.Models;
using DataverseProcessMapper.Parsing;

namespace DataverseProcessMapper.UI
{
    /// <summary>Which processes a <see cref="ProcessListPane"/> lists; decides its columns and wording.</summary>
    public enum ProcessListKind { Flows, ClassicWorkflows }

    /// <summary>What the pane's filter box searches.</summary>
    public enum ListSearchMode { Names, Definitions }

    /// <summary>
    /// One tab's process list: a filter box with a names / definitions search
    /// mode above a sortable, checkable list. Selecting a row (debounced) asks
    /// the host for a preview; the checks only scope "Export all". The pane owns
    /// what is shown and in which order; the host owns loading, searching
    /// definitions, links and exports, which it is asked for through events.
    /// </summary>
    public sealed class ProcessListPane : UserControl
    {
        // Arrow-key browsing must not start a definition download per row.
        private const int SelectionDelayMs = 250;

        private const int EM_SETCUEBANNER = 0x1501;
        private const int WM_KEYDOWN = 0x0100, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203;

        private const string SortUp = " ▲", SortDown = " ▼";

        private readonly ProcessListKind _kind;
        private readonly string _noun;            // "flow" / "workflow"
        private readonly List<ListColumn> _columns;

        private readonly FilterBox _filterBox;
        private readonly ToolStrip _modeStrip;
        private readonly ToolStripDropDownButton _modeButton;
        private readonly ToolStripMenuItem _namesModeItem;
        private readonly ToolStripMenuItem _definitionsModeItem;
        private readonly ProcessListView _list;
        private readonly ToolTip _toolTip = new ToolTip();
        private readonly Timer _selectionTimer = new Timer { Interval = SelectionDelayMs };

        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem _openItem;
        private readonly ToolStripMenuItem _copyLinkItem;
        private readonly ToolStripMenuItem _copyNameItem;
        private readonly ToolStripMenuItem _copyIdItem;
        private readonly ToolStripMenuItem _refreshItem;
        private readonly ToolStripMenuItem _exportItem;
        private readonly ToolStripMenuItem _checkAllItem;
        private readonly ToolStripMenuItem _uncheckAllItem;

        // Everything the host gave us, in its order, and the rows built for it.
        // Rows are cached by Id so refiltering and re-sorting 5,000 processes only
        // re-adds existing ListViewItems instead of building them again.
        private List<ProcessItem> _all = new List<ProcessItem>();
        private readonly Dictionary<Guid, int> _indexById = new Dictionary<Guid, int>();
        private readonly Dictionary<Guid, ListViewItem> _rows = new Dictionary<Guid, ListViewItem>();
        private readonly HashSet<Guid> _checked = new HashSet<Guid>();
        private List<ProcessItem> _visible = new List<ProcessItem>();

        private HashSet<Guid> _solutionIds;       // null = every solution
        private HashSet<Guid> _matches;           // definition search results; null = none
        private string _matchTerm;
        private bool _definitionSearchActive;     // a search was asked for and not yet cleared
        private ListSearchMode _mode = ListSearchMode.Names;
        private int _sortColumn;
        private bool _sortDescending;

        private int _quiet;                       // > 0 while the pane changes the list itself
        private int _quietText;                   // > 0 while the pane changes the filter text itself
        private Guid? _raisedSelection;           // what SelectionChanged last reported
        private ProcessItem _menuTarget;
        private ProcessItem _mouseTarget;
        private bool _menuByMouse;

        /// <summary>Raised ~250 ms after the selection settles, with the selected process or null.</summary>
        public event Action<ProcessItem> SelectionChanged;

        /// <summary>Raised when the user checks or unchecks processes, or when the visible checked set changes.</summary>
        public event Action CheckedChanged;

        /// <summary>Raised when <see cref="VisibleItems"/> changes: content or order.</summary>
        public event Action VisibleItemsChanged;

        /// <summary>Raised on Enter in definitions mode with the trimmed search term.</summary>
        public event Action<string> DefinitionSearchRequested;

        /// <summary>Raised when the user ends a definition search (Esc, empty box, back to names mode).</summary>
        public event Action DefinitionSearchCleared;

        /// <summary>Context menu requests, for the process the menu was opened on.</summary>
        public event Action<ProcessItem> OpenRequested, CopyLinkRequested, RefreshRequested;

        /// <summary>Context menu Export ▸ request, for the process the menu was opened on.</summary>
        public event Action<ProcessItem, ExportFormat> ExportRequested;

        /// <summary>
        /// Optional: why "Open" / "Copy link" cannot work for a process (e.g. no
        /// environment id), shown as the disabled items' tooltip; null = available.
        /// </summary>
        public Func<ProcessItem, string> LinkUnavailableReason { get; set; }

        /// <summary>Raised when the user sorts by a column header (for the host to remember it).</summary>
        public event Action SortChanged;

        /// <summary>The clipboard behind Copy name / Copy ID; tests replace it.</summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public DesktopServices Services { get; set; } = DesktopServices.Default;

        public ProcessListPane(ProcessListKind kind)
        {
            _kind = kind;
            _noun = kind == ProcessListKind.Flows ? "flow" : "workflow";
            _columns = kind == ProcessListKind.Flows ? FlowColumns() : ClassicColumns();
            int px = IconFactory.SizeFor(this);

            // ---- top row: filter box + search mode
            _filterBox = new FilterBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 1, 2, 1) };
            _filterBox.HandleCreated += (s, e) => UpdateCue();
            _filterBox.TextChanged += (s, e) => OnFilterTextChanged();
            _filterBox.KeyDown += OnFilterKeyDown;

            _namesModeItem = new ToolStripMenuItem("Search names", IconFactory.Get(IconNames.Filter, px), (s, e) => SearchMode = ListSearchMode.Names)
            {
                ImageScaling = ToolStripItemImageScaling.None,
                ToolTipText = "Show only the " + _noun + "s whose name contains the text, as you type"
            };
            _definitionsModeItem = new ToolStripMenuItem("Search inside definitions", IconFactory.Get(IconNames.SearchDefinitions, px),
                (s, e) => SearchMode = ListSearchMode.Definitions)
            {
                ImageScaling = ToolStripItemImageScaling.None,
                ToolTipText = "Find the " + _noun + "s whose definition contains the text (Enter searches, Esc clears)"
            };
            _modeButton = new ToolStripDropDownButton
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ToolTipText = "Choose what the box searches: " + _noun + " names as you type, or the text inside every definition on Enter"
            };
            _modeButton.DropDownItems.Add(_namesModeItem);
            _modeButton.DropDownItems.Add(_definitionsModeItem);
            ((ToolStripDropDownMenu)_modeButton.DropDown).ShowItemToolTips = true;
            ((ToolStripDropDownMenu)_modeButton.DropDown).ImageScalingSize = new Size(px, px);

            _modeStrip = new ToolStrip
            {
                Dock = DockStyle.None,
                AutoSize = true,
                GripStyle = ToolStripGripStyle.Hidden,
                CanOverflow = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                ImageScalingSize = new Size(px, px),
                Renderer = new BorderlessRenderer(),
                BackColor = SystemColors.Control
            };
            _modeStrip.Items.Add(_modeButton);

            var top = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(0, 0, 0, 2)
            };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.Controls.Add(_filterBox, 0, 0);
            top.Controls.Add(_modeStrip, 1, 0);

            // ---- the list
            _list = new ProcessListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                GridLines = false,
                ShowItemToolTips = true
            };
            foreach (var column in _columns)
                _list.Columns.Add(column.Header, column.Width);
            _list.ColumnClick += (s, e) => OnColumnClick(e.Column);
            _list.SelectedIndexChanged += (s, e) => { if (_quiet == 0) RestartSelectionTimer(); };
            _list.ItemChecked += OnItemChecked;
            _list.MouseDown += OnListMouseDown;
            _list.KeyDown += (s, e) => _menuByMouse = false; // the menu key opens it for the selection

            _selectionTimer.Tick += (s, e) => RaiseSelectionIfChanged();

            // ---- context menu
            _openItem = CreateMenuItem(kind == ProcessListKind.Flows ? "Open in Power Automate" : "Open in workflow designer",
                IconNames.Open, px, () => OpenRequested?.Invoke(_menuTarget));
            _copyLinkItem = CreateMenuItem("Copy link", IconNames.Link, px, () => CopyLinkRequested?.Invoke(_menuTarget));
            _copyNameItem = CreateMenuItem("Copy name", IconNames.Copy, px, () => CopyToClipboard(_menuTarget?.Name ?? ""));
            _copyIdItem = CreateMenuItem("Copy ID", IconNames.Copy, px, () => CopyToClipboard(_menuTarget?.Id.ToString("D") ?? ""));
            _refreshItem = CreateMenuItem("Refresh this process", IconNames.RefreshOne, px, () => RefreshRequested?.Invoke(_menuTarget));
            _exportItem = CreateMenuItem("Export", IconNames.Export, px, null);
            _exportItem.DropDown.ImageScalingSize = new Size(px, px);
            ((ToolStripDropDownMenu)_exportItem.DropDown).ShowItemToolTips = true;
            AddExport(ExportFormat.Pdf, "PDF document…", IconNames.Pdf, "a PDF document", px);
            AddExport(ExportFormat.Html, "Web page (HTML)…", IconNames.Html, "a web page (HTML)", px);
            AddExport(ExportFormat.Svg, "Vector image (SVG, for Visio)…", IconNames.Svg, "a vector image (SVG) that Visio can open", px);
            AddExport(ExportFormat.Png, "Image (PNG)…", IconNames.Png, "an image (PNG)", px);
            AddExport(ExportFormat.Markdown, "Markdown for AI knowledge…", IconNames.Markdown, "Markdown for AI knowledge sources", px);
            _checkAllItem = CreateMenuItem("Check all visible", IconNames.CheckAll, px, CheckAllVisible);
            _uncheckAllItem = CreateMenuItem("Uncheck all", IconNames.UncheckAll, px, UncheckAll);

            _menu.ShowItemToolTips = true;
            _menu.ImageScalingSize = new Size(px, px);
            _menu.Items.AddRange(new ToolStripItem[]
            {
                _openItem, _copyLinkItem, _copyNameItem, _copyIdItem, _refreshItem, _exportItem,
                new ToolStripSeparator(), _checkAllItem, _uncheckAllItem
            });
            _menu.Opening += OnMenuOpening;
            _list.ContextMenuStrip = _menu;

            Controls.Add(_list);
            Controls.Add(top);
            _list.BringToFront(); // Fill takes the space under the Top-docked row

            UpdateModeUi();
            UpdateHeaders();
        }

        // ------------------------------------------------------------ public API

        /// <summary>
        /// Replaces this tab's processes. The sort, filter text, checks (by Id)
        /// and the selected process (by Id) survive when still present; an
        /// unchanged selection raises no <see cref="SelectionChanged"/>.
        /// </summary>
        public void SetItems(IList<ProcessItem> items)
        {
            var view = CaptureView();
            ClearListView();

            var next = new List<ProcessItem>(items?.Count ?? 0);
            _indexById.Clear();
            foreach (var item in items ?? (IList<ProcessItem>)new ProcessItem[0])
            {
                if (item == null || _indexById.ContainsKey(item.Id)) continue;
                _indexById[item.Id] = next.Count;
                next.Add(item);
            }
            _all = next;

            // Reuse the rows of processes still listed (their cells may have changed).
            foreach (var id in _rows.Keys.Where(id => !_indexById.ContainsKey(id)).ToList())
                _rows.Remove(id);
            _checked.IntersectWith(_indexById.Keys);
            foreach (var item in _all)
            {
                ListViewItem row;
                if (_rows.TryGetValue(item.Id, out row))
                {
                    row.Tag = item;
                    UpdateCells(row, item);
                }
                else
                {
                    _rows[item.Id] = CreateRow(item);
                }
            }

            Refill(view);
        }

        /// <summary>Shows only the processes with these workflow ids; null shows all.</summary>
        public void SetSolutionFilter(ICollection<Guid> workflowIds)
        {
            _solutionIds = workflowIds == null ? null : new HashSet<Guid>(workflowIds);
            Refill(CaptureView());
        }

        /// <summary>
        /// The host's definition search results for <paramref name="term"/>; in
        /// definitions mode the list narrows to them. Null clears them (no
        /// <see cref="DefinitionSearchCleared"/>: the host asked for it).
        /// Results that arrive in names mode are late (the search was ended) and dropped.
        /// </summary>
        public void SetDefinitionMatches(ICollection<Guid> ids, string term)
        {
            if (ids != null && _mode != ListSearchMode.Definitions) return;
            if (ids == null)
            {
                _matches = null;
                _matchTerm = null;
                _definitionSearchActive = false;
            }
            else
            {
                _matches = new HashSet<Guid>(ids);
                _matchTerm = term;
                _definitionSearchActive = true;
            }
            Refill(CaptureView());
        }

        /// <summary>
        /// Repaints the cells of these processes (e.g. the Trigger column once the
        /// background pass has read their definitions). Rows stay where they are,
        /// with the selection and scroll position, even when sorted by a changed column.
        /// </summary>
        public void RefreshRows(IEnumerable<ProcessItem> items)
        {
            if (items == null) return;
            foreach (var item in items)
            {
                int index;
                ListViewItem row;
                if (item == null || !_indexById.TryGetValue(item.Id, out index) || !_rows.TryGetValue(item.Id, out row)) continue;

                var old = _all[index];
                if (!ReferenceEquals(old, item))
                {
                    // A new instance for the same process: list and report the new one.
                    _all[index] = item;
                    int visibleIndex = _visible.IndexOf(old);
                    if (visibleIndex >= 0) _visible[visibleIndex] = item;
                    row.Tag = item;
                }
                UpdateCells(row, item);
            }
        }

        /// <summary>
        /// Empties the pane (connection change): processes, checks, solution
        /// filter and definition results; the filter text too when asked. Sort
        /// and search mode stay.
        /// </summary>
        public void Clear(bool clearFilterText)
        {
            _selectionTimer.Stop();
            ClearListView();
            bool hadVisible = _visible.Count > 0;
            bool hadChecked = _visible.Any(i => _checked.Contains(i.Id));

            _all = new List<ProcessItem>();
            _visible = new List<ProcessItem>();
            _indexById.Clear();
            _rows.Clear();
            _checked.Clear();
            _solutionIds = null;
            _matches = null;
            _matchTerm = null;
            _definitionSearchActive = false;
            if (clearFilterText) SetFilterTextQuietly("");
            UpdateFilterToolTip();

            if (_raisedSelection != null)
            {
                _raisedSelection = null;
                SelectionChanged?.Invoke(null);
            }
            if (hadVisible) VisibleItemsChanged?.Invoke();
            if (hadChecked) CheckedChanged?.Invoke();
        }

        /// <summary>The listed processes in display order (a snapshot).</summary>
        public IList<ProcessItem> VisibleItems => new List<ProcessItem>(_visible);

        /// <summary>
        /// The checked processes that are visible, in display order. Checks on
        /// processes the filters hide are remembered, but not exported: Export
        /// all only ever acts on what the list shows.
        /// </summary>
        public IList<ProcessItem> CheckedItems => _visible.Where(i => _checked.Contains(i.Id)).ToList();

        /// <summary>The selected process, now (not debounced); null when none.</summary>
        public ProcessItem SelectedItem => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as ProcessItem : null;

        public ListSearchMode SearchMode
        {
            get { return _mode; }
            set
            {
                if (value == _mode) return;
                bool endsSearch = _mode == ListSearchMode.Definitions && (_matches != null || _definitionSearchActive);
                _mode = value;
                if (endsSearch)
                {
                    _matches = null;
                    _matchTerm = null;
                    _definitionSearchActive = false;
                }
                UpdateModeUi();
                Refill(CaptureView()); // the same text now filters (names) or waits for Enter (definitions)
                if (endsSearch) DefinitionSearchCleared?.Invoke();
            }
        }

        /// <summary>The filter box's text, as typed.</summary>
        public string FilterText => _filterBox.Text;

        /// <summary>The sorted column's index (columns as listed for the kind); invalid values sort by name.</summary>
        public int SortColumn
        {
            get { return _sortColumn; }
            set
            {
                int column = value >= 0 && value < _columns.Count ? value : 0;
                if (column == _sortColumn) return;
                _sortColumn = column;
                UpdateHeaders();
                Refill(CaptureView(), keepScroll: false);
            }
        }

        public bool SortDescending
        {
            get { return _sortDescending; }
            set
            {
                if (value == _sortDescending) return;
                _sortDescending = value;
                UpdateHeaders();
                Refill(CaptureView(), keepScroll: false);
            }
        }

        protected override void WndProc(ref Message m)
        {
            // While the pane sets checks itself (refills, Check all), each row reports its
            // check box changing, and WinForms turns each report into ItemCheck and
            // ItemChecked events the pane ignores anyway: ~0.4 ms a row, 2 s for 5,000
            // rows. Those reports are dropped here, before the list sees them.
            if (m.Msg == WM_NOTIFY && _quiet > 0 && IsCheckBoxOnlyChange(m)) return; // Result 0: allow it
            base.WndProc(ref m);
        }

        private bool IsCheckBoxOnlyChange(Message m)
        {
            if (!_list.IsHandleCreated) return false;
            var header = (NMHDR)Marshal.PtrToStructure(m.LParam, typeof(NMHDR));
            if (header.hwndFrom != _list.Handle || (header.code != LVN_ITEMCHANGING && header.code != LVN_ITEMCHANGED)) return false;
            var change = (NMLISTVIEW)Marshal.PtrToStructure(m.LParam, typeof(NMLISTVIEW));
            // Only the state image (the check box): selection and focus changes still go through.
            return change.uChanged == LVIF_STATE && ((change.uNewState ^ change.uOldState) & ~LVIS_STATEIMAGEMASK) == 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _selectionTimer.Dispose();
                _toolTip.Dispose();
                _menu.Dispose();
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------ columns

        private enum ColumnKind { Text, Date }

        private sealed class ListColumn
        {
            public string Header;
            public int Width;
            public ColumnKind Kind;
            public Func<ProcessItem, string> Text;
            public Func<ProcessItem, DateTime?> Date;   // local time, for Date columns
        }

        private static ListColumn TextColumn(string header, int width, Func<ProcessItem, string> text) =>
            new ListColumn { Header = header, Width = width, Kind = ColumnKind.Text, Text = i => text(i) ?? "" };

        private static ListColumn DateColumn(string header, int width, Func<ProcessItem, DateTime?> utc) =>
            new ListColumn
            {
                Header = header,
                Width = width,
                Kind = ColumnKind.Date,
                Date = i => utc(i)?.ToLocalTime(),
                Text = i => utc(i)?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""
            };

        // Default widths: the columns up to Trigger fit the list at its default 460 px
        // (check box and vertical scroll bar included); the rest scroll into view.
        private static ListColumn NameColumn(int width) => TextColumn("Name", width, i => i.Name ?? "(unnamed)");
        private static ListColumn StatusColumn() => TextColumn("Status", 70, i => i.StateLabel);

        private static List<ListColumn> FlowColumns() => new List<ListColumn>
        {
            NameColumn(180),
            StatusColumn(),
            // Null until the background pass has read the flow's definition.
            TextColumn("Trigger", 180, i => i.TriggerSummary),
            DateColumn("Modified On", 90, i => i.ModifiedOn),
            TextColumn("Modified By", 130, i => i.ModifiedBy),
            TextColumn("Owner", 130, i => i.Owner),
            DateColumn("Created On", 90, i => i.CreatedOn),
            TextColumn("Created By", 130, i => i.CreatedBy),
            TextColumn("Scope", 100, i => i.Scope)
        };

        private static List<ListColumn> ClassicColumns() => new List<ListColumn>
        {
            NameColumn(140),
            StatusColumn(),
            TextColumn("Table", 80, i => i.PrimaryEntity),
            // A classic workflow's trigger is in its list columns, so no definition is needed.
            TextColumn("Trigger", 150, i => string.IsNullOrEmpty(i.TriggerSummary) ? TriggerSummary.ForClassic(i) : i.TriggerSummary),
            TextColumn("Mode", 85, TriggerSummary.ModeLabel),
            DateColumn("Modified On", 90, i => i.ModifiedOn),
            TextColumn("Owner", 130, i => i.Owner)
        };

        private ListViewItem CreateRow(ProcessItem item)
        {
            var texts = new string[_columns.Count];
            for (int i = 0; i < texts.Length; i++) texts[i] = _columns[i].Text(item);
            // An explicit unchecked state image: a row without one makes a checkbox list
            // resolve it per row on insert, which is ~7x slower (2 s for 5,000 rows).
            return new ListViewItem(texts) { Tag = item, ToolTipText = RowToolTip(item, texts), StateImageIndex = 0 };
        }

        private void UpdateCells(ListViewItem row, ProcessItem item)
        {
            // Only changed cells: each assignment repaints the cell of a listed row.
            for (int i = 0; i < _columns.Count; i++)
            {
                var text = _columns[i].Text(item);
                if (row.SubItems[i].Text != text) row.SubItems[i].Text = text;
            }
            var tip = RowToolTip(item, null);
            if (row.ToolTipText != tip) row.ToolTipText = tip;
        }

        private string RowToolTip(ProcessItem item, string[] texts)
        {
            // The name can be cut off by the column; the description is in no column.
            var lines = new List<string> { item.Name ?? "(unnamed)" };
            var trigger = TriggerText(item, texts);
            if (!string.IsNullOrEmpty(trigger))
                lines.Add("Trigger: " + trigger + (trigger == "Child flow"
                    ? " (worked out from the trigger: a manual flow that answers a Power App reads the same)"
                    : ""));
            var description = (item.Description ?? "").Trim();
            if (description.Length > 300) description = description.Substring(0, 300) + "…";
            if (description.Length > 0) lines.Add(description);
            return string.Join("\n", lines);
        }

        private string TriggerText(ProcessItem item, string[] texts)
        {
            int index = _columns.FindIndex(c => c.Header == "Trigger");
            return texts != null ? texts[index] : _columns[index].Text(item);
        }

        private void UpdateHeaders()
        {
            for (int i = 0; i < _columns.Count; i++)
            {
                var text = _columns[i].Header + (i == _sortColumn ? (_sortDescending ? SortDown : SortUp) : "");
                if (_list.Columns[i].Text != text) _list.Columns[i].Text = text;
            }
        }

        // ------------------------------------------------------------ filling

        /// <summary>What the user is looking at, so a refill can put it back.</summary>
        private sealed class ViewState
        {
            public Guid? Selected;
            public Guid? Top;
            public HashSet<Guid> CheckedVisible;
        }

        private ViewState CaptureView()
        {
            ListViewItem top = null;
            try
            {
                if (_list.IsHandleCreated && _list.Items.Count > 0) top = _list.TopItem;
            }
            catch (InvalidOperationException)
            {
                // TopItem is only valid in some views and states; the scroll position is a nicety.
            }
            return new ViewState
            {
                Selected = SelectedItem?.Id,
                Top = (top?.Tag as ProcessItem)?.Id,
                CheckedVisible = new HashSet<Guid>(_visible.Where(i => _checked.Contains(i.Id)).Select(i => i.Id))
            };
        }

        private void ClearListView()
        {
            _quiet++;
            _list.BeginUpdate();
            try { _list.Items.Clear(); }
            finally
            {
                _list.EndUpdate();
                _quiet--;
            }
        }

        private void Refill(ViewState view, bool keepScroll = true)
        {
            var visible = Sort(Filter());

            _quiet++;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                var rows = new ListViewItem[visible.Count];
                var checkedIndexes = new List<int>();
                for (int i = 0; i < rows.Length; i++)
                {
                    var row = _rows[visible[i].Id];
                    // In unchecked (see CreateRow): a row inserted checked costs ~1 ms in
                    // WinForms, so the checks are set natively once the rows are in.
                    row.StateImageIndex = 0;
                    if (_checked.Contains(visible[i].Id)) checkedIndexes.Add(i);
                    // Items.Clear saved each row's native selected state; a row hidden
                    // while selected would come back selected (and be previewed again).
                    row.Selected = false;
                    rows[i] = row;
                }
                _list.Items.AddRange(rows);
                SetListedChecks(checkedIndexes, rows);

                // Re-select without a new SelectionChanged: the host is already showing it.
                ListViewItem selected;
                if (view.Selected != null && _rows.TryGetValue(view.Selected.Value, out selected) && selected.ListView == _list)
                {
                    selected.Selected = true;
                    selected.Focused = true;
                }
            }
            finally
            {
                _list.EndUpdate();
                _quiet--;
            }
            RestoreScroll(view, keepScroll);

            bool visibleChanged = !SameOrder(_visible, visible);
            _visible = visible;
            bool checkedChanged = !view.CheckedVisible.SetEquals(_visible.Where(i => _checked.Contains(i.Id)).Select(i => i.Id));
            UpdateFilterToolTip(); // it counts what is shown

            if (SelectedItem?.Id != _raisedSelection) RestartSelectionTimer();
            if (visibleChanged) VisibleItemsChanged?.Invoke();
            if (checkedChanged) CheckedChanged?.Invoke();
        }

        private void RestoreScroll(ViewState view, bool keepScroll)
        {
            if (!_list.IsHandleCreated || _list.Items.Count == 0) return;
            try
            {
                ListViewItem row;
                if (keepScroll && view.Top != null && _rows.TryGetValue(view.Top.Value, out row) && row.ListView == _list)
                    _list.TopItem = row;
                else if (!keepScroll)
                    _list.TopItem = _list.Items[0];
                if (_list.SelectedItems.Count > 0) _list.SelectedItems[0].EnsureVisible();
            }
            catch (InvalidOperationException)
            {
                // See CaptureView.
            }
        }

        private static bool SameOrder(List<ProcessItem> a, List<ProcessItem> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Id != b[i].Id) return false;
            return true;
        }

        /// <summary>Visible = items ∩ solution filter ∩ (name contains the text | definition matches).</summary>
        private List<ProcessItem> Filter()
        {
            var term = _mode == ListSearchMode.Names ? (_filterBox.Text ?? "").Trim() : "";
            var result = new List<ProcessItem>(_all.Count);
            foreach (var item in _all)
            {
                if (_solutionIds != null && !_solutionIds.Contains(item.Id)) continue;
                if (_mode == ListSearchMode.Definitions)
                {
                    if (_matches != null && !_matches.Contains(item.Id)) continue;
                }
                else if (term.Length > 0 && (item.Name ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                result.Add(item);
            }
            return result;
        }

        private List<ProcessItem> Sort(List<ProcessItem> items)
        {
            // Keys once per item, not once per comparison (5,000 rows, culture-aware compares).
            var column = _columns[_sortColumn];
            var nameColumn = _columns[0];   // ties by the name as shown ("(unnamed)" for none)
            int n = items.Count;
            var texts = new string[n];
            var dates = new DateTime?[n];
            var names = new string[n];
            for (int i = 0; i < n; i++)
            {
                if (column.Kind == ColumnKind.Date) dates[i] = column.Date(items[i]);
                else texts[i] = column.Text(items[i]);
                names[i] = nameColumn.Text(items[i]);
            }

            var compare = CultureInfo.CurrentCulture.CompareInfo;
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            int sign = _sortDescending ? -1 : 1;
            Array.Sort(order, (a, b) =>
            {
                int c = column.Kind == ColumnKind.Date
                    ? CompareEmptyLast(dates[a] == null, dates[b] == null, () => dates[a].Value.CompareTo(dates[b].Value), sign)
                    : CompareEmptyLast(texts[a].Length == 0, texts[b].Length == 0,
                        () => compare.Compare(texts[a], texts[b], CompareOptions.IgnoreCase), sign);
                if (c != 0) return c;
                // Ties by name, then id: the order is total, so refills never shuffle equal rows.
                c = compare.Compare(names[a], names[b], CompareOptions.IgnoreCase);
                return c != 0 ? c : items[a].Id.CompareTo(items[b].Id);
            });

            var sorted = new List<ProcessItem>(n);
            foreach (var i in order) sorted.Add(items[i]);
            return sorted;
        }

        /// <summary>Blanks (no date, no trigger read yet) sort last in both directions.</summary>
        private static int CompareEmptyLast(bool aEmpty, bool bEmpty, Func<int> compare, int sign)
        {
            if (aEmpty || bEmpty) return aEmpty == bEmpty ? 0 : (aEmpty ? 1 : -1);
            return sign * compare();
        }

        private void OnColumnClick(int column)
        {
            if (column == _sortColumn) _sortDescending = !_sortDescending;
            else
            {
                _sortColumn = column;
                _sortDescending = false;
            }
            UpdateHeaders();
            Refill(CaptureView(), keepScroll: false);
            SortChanged?.Invoke();
        }

        // ------------------------------------------------------------ filter box

        private void OnFilterTextChanged()
        {
            if (_quietText > 0) return;
            if (_mode == ListSearchMode.Names)
            {
                Refill(CaptureView());
            }
            else if (_filterBox.Text.Trim().Length == 0 && (_matches != null || _definitionSearchActive))
            {
                // Emptying the box ends the search, as Esc does.
                EndDefinitionSearch();
            }
        }

        private void OnFilterKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control || e.Alt) return;
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = e.SuppressKeyPress = true;
                SetFilterTextQuietly("");
                if (_mode == ListSearchMode.Definitions) EndDefinitionSearch();
                else Refill(CaptureView());
            }
            else if (e.KeyCode == Keys.Enter)
            {
                e.Handled = e.SuppressKeyPress = true; // no beep from a single-line box
                if (_mode != ListSearchMode.Definitions) return;
                var term = _filterBox.Text.Trim();
                if (term.Length == 0)
                {
                    if (_matches != null || _definitionSearchActive) EndDefinitionSearch();
                    return;
                }
                _definitionSearchActive = true;
                DefinitionSearchRequested?.Invoke(term);
            }
            else if (e.KeyCode == Keys.Down && _list.Items.Count > 0)
            {
                // From the box straight into the list.
                e.Handled = e.SuppressKeyPress = true;
                _list.Focus();
                if (_list.SelectedItems.Count == 0)
                {
                    _list.Items[0].Selected = true;
                    _list.Items[0].Focused = true;
                }
            }
        }

        private void EndDefinitionSearch()
        {
            _matches = null;
            _matchTerm = null;
            _definitionSearchActive = false;
            Refill(CaptureView());
            DefinitionSearchCleared?.Invoke();
        }

        private void SetFilterTextQuietly(string text)
        {
            _quietText++;
            try { _filterBox.Text = text; }
            finally { _quietText--; }
        }

        private void UpdateModeUi()
        {
            bool names = _mode == ListSearchMode.Names;
            _namesModeItem.Checked = names;
            _definitionsModeItem.Checked = !names;
            _modeButton.Text = names ? "Names" : "Definitions";
            UpdateCue();
            UpdateFilterToolTip();
        }

        private string CueText => _mode == ListSearchMode.Names ? "Filter by name…" : "Search inside definitions… (Enter)";

        private void UpdateCue()
        {
            // Native cue banner ("watermark"), shown while the box is empty, even when focused.
            if (_filterBox.IsHandleCreated)
                SendMessage(_filterBox.Handle, EM_SETCUEBANNER, (IntPtr)1, CueText);
        }

        private void UpdateFilterToolTip()
        {
            string tip;
            if (_mode == ListSearchMode.Names)
                tip = "Show only the " + _noun + "s whose name contains this text (Esc clears)";
            else if (_matches != null)
                tip = (_visible.Count == 1 ? "Showing the " + _noun + " whose definition contains '"
                          : "Showing the " + _visible.Count + " " + _noun + "s whose definition contains '")
                      + _matchTerm + "' (Esc clears)";
            else
                tip = "Type text and press Enter to find the " + _noun + "s whose definition contains it (Esc clears)";
            _toolTip.SetToolTip(_filterBox, tip);
        }

        // ------------------------------------------------------------ selection and checks

        private void RestartSelectionTimer()
        {
            _selectionTimer.Stop();
            _selectionTimer.Start();
        }

        private void RaiseSelectionIfChanged()
        {
            _selectionTimer.Stop();
            var item = SelectedItem;
            if (item?.Id == _raisedSelection) return;
            _raisedSelection = item?.Id;
            SelectionChanged?.Invoke(item);
        }

        private void OnItemChecked(object sender, ItemCheckedEventArgs e)
        {
            // A handle being made again (docking can re-parent the tab) re-inserts every
            // row and reports its check off and on again: not the user's doing.
            if (_quiet > 0 || _list.RebuildingHandle) return;
            var item = e.Item?.Tag as ProcessItem;
            if (item == null) return;
            bool changed = e.Item.Checked ? _checked.Add(item.Id) : _checked.Remove(item.Id);
            if (changed) CheckedChanged?.Invoke();
        }

        private void CheckAllVisible()
        {
            bool changed = false;
            foreach (var item in _visible) changed |= _checked.Add(item.Id);
            if (!changed) return;
            Quietly(() => SetNativeCheck(-1, true));
            CheckedChanged?.Invoke();
        }

        private void UncheckAll()
        {
            // Also the checks the filters hide, so none comes back on its own.
            if (_checked.Count == 0) return;
            _checked.Clear();
            Quietly(() => SetNativeCheck(-1, false));
            CheckedChanged?.Invoke();
        }

        private void Quietly(Action change)
        {
            _quiet++;
            _list.BeginUpdate();
            try { change(); }
            finally
            {
                _list.EndUpdate();
                _quiet--;
            }
        }

        /// <summary>Checks these just-inserted rows (by display index); called while quiet.</summary>
        private void SetListedChecks(List<int> indexes, ListViewItem[] rows)
        {
            if (indexes.Count == 0) return;
            if (!_list.IsHandleCreated)
            {
                foreach (var i in indexes) rows[i].Checked = true;
            }
            else if (indexes.Count == rows.Length)
            {
                SetNativeCheck(-1, true);
            }
            else
            {
                foreach (var i in indexes) SetNativeCheck(i, true);
            }
        }

        /// <summary>
        /// A row's check box (-1: every listed row in one call) set natively, not
        /// through ListViewItem.Checked; while quiet, WndProc drops the reports it causes.
        /// </summary>
        private void SetNativeCheck(int index, bool check)
        {
            if (!_list.IsHandleCreated)
            {
                foreach (ListViewItem row in _list.Items)
                    if (index < 0 || row.Index == index) row.Checked = check;
                return;
            }
            var state = new LVITEM { stateMask = LVIS_STATEIMAGEMASK, state = (check ? 2u : 1u) << 12 };
            SendMessage(_list.Handle, LVM_SETITEMSTATE, (IntPtr)index, ref state);
        }

        // ------------------------------------------------------------ context menu

        private void OnListMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
            {
                _menuByMouse = false;
                return;
            }
            // Right-click selects the row under the cursor; the menu then acts on it.
            var row = _list.HitTest(e.Location).Item;
            _mouseTarget = row?.Tag as ProcessItem;
            _menuByMouse = true;
            if (row != null && !row.Selected)
            {
                row.Selected = true;
                row.Focused = true;
            }
        }

        private void OnMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // A right-click on empty space has no target; the menu key acts on the selection.
            _menuTarget = _menuByMouse ? _mouseTarget : SelectedItem;
            _menuByMouse = false;
            var target = _menuTarget;
            if (target == null && _visible.Count == 0 && _checked.Count == 0)
            {
                e.Cancel = true; // nothing it could do
                return;
            }

            string none = "Right-click a " + _noun + " to use this";
            string linkReason = target == null ? none : LinkUnavailableReason?.Invoke(target);
            bool flows = _kind == ProcessListKind.Flows;

            SetState(_openItem, linkReason, flows
                ? "Open this flow in Power Automate in your browser"
                : "Open this workflow in the classic workflow designer in your browser");
            SetState(_copyLinkItem, linkReason, flows
                ? "Copy the link to this flow in Power Automate"
                : "Copy the link to this workflow in the classic workflow designer");
            SetState(_copyNameItem, target == null ? none : null, "Copy this " + _noun + "'s name to the clipboard");
            SetState(_copyIdItem, target == null ? none : null, "Copy this " + _noun + "'s ID (workflowid) to the clipboard");
            SetState(_refreshItem, target == null ? none : null, "Download this " + _noun + "'s definition again and redraw its map");
            SetState(_exportItem, target == null ? none : null, "Save this " + _noun + "'s map to a file");

            int uncheckedCount = _visible.Count(i => !_checked.Contains(i.Id));
            SetState(_checkAllItem,
                _visible.Count == 0 ? "The list is empty" : uncheckedCount == 0 ? "Every " + _noun + " in the list is already checked" : null,
                "Check every " + _noun + " in the list (Export all exports the checked " + _noun + "s)");
            SetState(_uncheckAllItem, _checked.Count == 0 ? "No " + _noun + "s are checked" : null,
                "Uncheck every " + _noun + ", including any the filter hides");
        }

        private static void SetState(ToolStripMenuItem item, string disabledReason, string tip)
        {
            item.Enabled = disabledReason == null;
            item.ToolTipText = disabledReason ?? tip;
        }

        private static ToolStripMenuItem CreateMenuItem(string text, string icon, int px, Action click)
        {
            var item = new ToolStripMenuItem(text, IconFactory.Get(icon, px)) { ImageScaling = ToolStripItemImageScaling.None };
            if (click != null) item.Click += (s, e) => click();
            return item;
        }

        private void AddExport(ExportFormat format, string text, string icon, string what, int px)
        {
            var item = CreateMenuItem(text, icon, px, () =>
            {
                if (_menuTarget != null) ExportRequested?.Invoke(_menuTarget, format);
            });
            item.ToolTipText = "Save this " + _noun + "'s map as " + what;
            item.Tag = format;
            _exportItem.DropDownItems.Add(item);
        }

        private void CopyToClipboard(string text)
        {
            if (_menuTarget == null || string.IsNullOrEmpty(text)) return;
            var desktop = Services ?? DesktopServices.Default;
            try
            {
                desktop.SetClipboardText(text);
            }
            catch (ExternalException)
            {
                desktop.ShowMessage(this, "Could not copy to the clipboard: another program is using it. Try again.",
                    "Copy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ------------------------------------------------------------ helper controls

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref LVITEM lParam);

        private const int WM_NOTIFY = 0x004E;
        private const int LVM_SETITEMSTATE = 0x102B;
        private const int LVN_ITEMCHANGING = -100, LVN_ITEMCHANGED = -101;
        private const uint LVIF_STATE = 0x0008, LVIS_STATEIMAGEMASK = 0xF000;

        [StructLayout(LayoutKind.Sequential)]
        private struct LVITEM
        {
            public uint mask;
            public int iItem;
            public int iSubItem;
            public uint state;
            public uint stateMask;
            public IntPtr pszText;
            public int cchTextMax;
            public int iImage;
            public IntPtr lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NMHDR
        {
            public IntPtr hwndFrom;
            public IntPtr idFrom;
            public int code;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NMLISTVIEW
        {
            public NMHDR hdr;
            public int iItem;
            public int iSubItem;
            public uint uNewState;
            public uint uOldState;
            public uint uChanged;
            public Point ptAction;
            public IntPtr lParam;
        }

        /// <summary>A filter box that keeps Enter and Esc from the form's accept / cancel buttons.</summary>
        private sealed class FilterBox : TextBox
        {
            protected override bool IsInputKey(Keys keyData)
            {
                var key = keyData & Keys.KeyCode;
                if ((key == Keys.Enter || key == Keys.Escape) && (keyData & (Keys.Control | Keys.Alt)) == 0) return true;
                return base.IsInputKey(keyData);
            }
        }

        /// <summary>
        /// Double-buffered (no flicker while filling or resizing), and a
        /// double-click on a row no longer toggles its check box: a double-click
        /// means "this one", not "add it to Export all".
        /// </summary>
        private sealed class ProcessListView : ListView
        {
            private bool _inDoubleClick;
            private int _handleChanges;

            public ProcessListView()
            {
                DoubleBuffered = true; // LVS_EX_DOUBLEBUFFER
            }

            /// <summary>True while the native list is being made or torn down, when it re-reports every row's check.</summary>
            public bool RebuildingHandle => _handleChanges > 0 || RecreatingHandle;

            protected override void OnHandleCreated(EventArgs e)
            {
                _handleChanges++;
                try { base.OnHandleCreated(e); }
                finally { _handleChanges--; }
            }

            protected override void OnHandleDestroyed(EventArgs e)
            {
                _handleChanges++;
                try { base.OnHandleDestroyed(e); }
                finally { _handleChanges--; }
            }

            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case WM_KEYDOWN:
                    case WM_LBUTTONDOWN:
                        _inDoubleClick = false; // a new click or key: the check box works again
                        break;
                    case WM_LBUTTONDBLCLK:
                        // The native list toggles the check of a double-clicked row
                        // somewhere between this message and the button release.
                        long lParam = m.LParam.ToInt64();
                        var point = new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
                        _inDoubleClick = CheckBoxes && HitTest(point).Location != ListViewHitTestLocations.StateImage;
                        break;
                    case WM_LBUTTONUP:
                        try { base.WndProc(ref m); }
                        finally { _inDoubleClick = false; }
                        return;
                }
                base.WndProc(ref m);
            }

            protected override void OnItemCheck(ItemCheckEventArgs ice)
            {
                if (_inDoubleClick) ice.NewValue = ice.CurrentValue;
                base.OnItemCheck(ice);
            }
        }

        /// <summary>The system look without the strip's bottom border, so the button sits flush beside the box.</summary>
        private sealed class BorderlessRenderer : ToolStripSystemRenderer
        {
            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
            }
        }
    }
}
