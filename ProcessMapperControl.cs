using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DataverseProcessMapper.Data;
using DataverseProcessMapper.Exporters;
using DataverseProcessMapper.Models;
using DataverseProcessMapper.UI;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Tooling.Connector;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Args;
using XrmToolBox.Extensibility.Interfaces;
using SolutionInfo = DataverseProcessMapper.Data.SolutionInfo;

namespace DataverseProcessMapper
{
    public partial class ProcessMapperControl : PluginControlBase, IMessageBusHost, IGitHubPlugin, IHelpPlugin,
        IAboutPlugin, IStatusBarMessenger
    {
        // --- IMessageBusHost: lets this tool hand data to other XrmToolBox tools ---

        public event EventHandler<MessageBusEventArgs> OnOutgoingMessage;

        public void OnIncomingMessage(MessageBusEventArgs message)
        {
            // This tool doesn't accept incoming payloads (yet).
        }

        /// <summary>Opens (or focuses) FetchXML Builder with the given query loaded.</summary>
        private void OpenInFetchXmlBuilder(string fetchXml)
        {
            if (string.IsNullOrWhiteSpace(fetchXml)) return;
            OnOutgoingMessage?.Invoke(this, new MessageBusEventArgs("FetchXML Builder")
            {
                TargetArgument = fetchXml
            });
        }

        // --- IGitHubPlugin, IHelpPlugin, IAboutPlugin: XrmToolBox checks the control, not the plugin class ---

        public string RepositoryName => "Dataverse-Process-Mapper";
        public string UserName => "caschern";
        public string HelpUrl => AboutDialog.RepositoryUrl + "#readme";

        public void ShowAboutDialog()
        {
            using (var dialog = new AboutDialog(Services, _logo)) dialog.ShowDialog(this);
        }

        // --- IStatusBarMessenger: counts and progress go to XrmToolBox's status bar ---

        public event EventHandler<StatusBarMessageEventArgs> SendMessageToStatusBar;

        // ---------------------------------------------------------------- fields

        private const string AllSolutions = "All solutions";
        private const int DefaultListWidth = 460;
        private const float ZoomFactor = 1.2f; // the map strip's zoom buttons use the same step

        private ToolStrip _toolbar;
        private ToolStripButton _loadButton;
        private ToolStripLabel _solutionLabel;
        private ToolStripComboBox _solutionBox;
        private ToolStripDropDownButton _exportButton;
        private readonly Dictionary<ExportFormat, ToolStripMenuItem> _exportItems = new Dictionary<ExportFormat, ToolStripMenuItem>();
        private ToolStripDropDownButton _exportAllButton;
        private ToolStripMenuItem _exportAllHtmlItem;
        private ToolStripMenuItem _exportAllMarkdownItem;
        private ToolStripSplitButton _openButton;
        private ToolStripMenuItem _copyLinkItem;

        private TabControl _tabs;
        private ProcessListPane _flowList;
        private ProcessListPane _workflowList;
        private DiagramPanel _flowPanel;
        private DiagramPanel _workflowPanel;
        private MapToolStrip _flowStrip;
        private MapToolStrip _workflowStrip;
        private NodeDetailsPane _flowDetails;
        private NodeDetailsPane _workflowDetails;
        private SplitContainer _flowSplit;
        private SplitContainer _workflowSplit;
        private Tab _flowTab;
        private Tab _workflowTab;

        private LayoutMode _layoutMode = ProcessMapBuilder.DefaultMode;

        private readonly ISettingsStore _settingsStore;
        private ProcessMapperSettings _settings;
        private ProcessMapperSettings _savedSettings;

        private DesktopServices _services = DesktopServices.Default;
        private readonly Bitmap _logo;

        /// <summary>One tab: its list, map, strip and details, and its definition search.</summary>
        private sealed class Tab
        {
            public ProcessListKind Kind;
            public string Noun;                 // "flow" / "workflow"
            public string SetLabel;             // the tab's title, for export packs
            public ProcessListPane List;
            public DiagramPanel Panel;
            public MapToolStrip Strip;
            public NodeDetailsPane Details;
            public SplitContainer Split;

            // Master (unfiltered) list; the pane filters it.
            public List<ProcessItem> All = new List<ProcessItem>();

            // Definition search: the term searched for (running or with results shown), null = none.
            public string SearchTerm;
            public bool SearchRunning;
            public int SearchId;                // bumped per search; a late result of an older one is dropped
            public CancellationTokenSource SearchCancel;
            public int SearchDone, SearchTotal;
            public string FindTermSet;          // what this tool put in the map strip's find box for the search
            public bool DetailsBeforeSearch;    // the find box's details option before this tool turned it on

            public string Nouns(int count) => count == 1 ? Noun : Noun + "s";
        }

        public ProcessMapperControl() : this(null)
        {
        }

        /// <param name="settingsStore">Where settings are kept; null = XrmToolBox's settings folder.</param>
        public ProcessMapperControl(ISettingsStore settingsStore)
        {
            _settingsStore = settingsStore ?? new XrmToolBoxSettingsStore(GetType());
            _settings = LoadSettings();
            _savedSettings = _settings.Clone();
            _layoutMode = _settings.LayoutMode;

            _logo = LoadLogo();
            TabIcon = _logo;
            PluginIcon = IconOf(_logo);

            BuildUi();
            ConnectionUpdated += (s, e) => OnConnectionChanged();
            OnWorkAsync += (s, e) => _backgroundJobsStarted++;
        }

        /// <summary>
        /// The browser, clipboard, files and dialogs the tool uses; tests replace
        /// them so nothing opens on the desktop.
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public DesktopServices Services
        {
            get { return _services; }
            set
            {
                _services = value ?? DesktopServices.Default;
                if (_flowList != null) _flowList.Services = _services;
                if (_workflowList != null) _workflowList.Services = _services;
                if (_flowDetails != null) _flowDetails.Services = _services;
                if (_workflowDetails != null) _workflowDetails.Services = _services;
            }
        }

        /// <summary>Counts XrmToolBox background jobs (WorkAsync calls) started by this tool.</summary>
        private int _backgroundJobsStarted;

        // ---------------------------------------------------------------- UI

        private void BuildUi()
        {
            Dock = DockStyle.Fill;
            int px = IconFactory.SizeFor(this);

            _toolbar = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                ImageScalingSize = new Size(px, px),
                ShowItemToolTips = true
            };

            _loadButton = ToolbarButton(new ToolStripButton("Load processes"), IconNames.Reload, px);
            _loadButton.Click += (s, e) => ExecuteMethod(LoadProcesses);

            _solutionLabel = new ToolStripLabel("Solution:");
            _solutionBox = new ToolStripComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems,
                AutoSize = false,
                Size = new Size(220, 23),
                MaxDropDownItems = 20,
                AccessibleName = "Solution"
            };
            // A click in the drop-down applies the solution. Arrow keys only move through
            // the names (each would filter both lists, and read the solution's processes):
            // like typed text, that choice applies on Enter or on leaving the box.
            _solutionBox.SelectedIndexChanged += (s, e) =>
            {
                bool keyed = _solutionKeyed;
                _solutionKeyed = false;
                if (_fillingSolutions == 0 && !keyed && !_solutionBox.DroppedDown) OnSolutionPicked();
            };
            _solutionBox.DropDownClosed += (s, e) => { if (_fillingSolutions == 0) OnSolutionPicked(); };
            _solutionBox.KeyDown += (s, e) =>
            {
                switch (e.KeyCode)
                {
                    case Keys.Up:
                    case Keys.Down:
                    case Keys.PageUp:
                    case Keys.PageDown:
                    case Keys.Home:
                    case Keys.End:
                        _solutionKeyed = !e.Alt && !_solutionBox.DroppedDown;
                        return;
                    case Keys.Enter:
                        e.Handled = e.SuppressKeyPress = true; // no beep
                        CommitSolutionText();
                        return;
                }
            };
            _solutionBox.KeyUp += (s, e) => _solutionKeyed = false;
            _solutionBox.Leave += (s, e) => CommitSolutionText();
            FillSolutionBox(null);

            _exportButton = ToolbarButton(new ToolStripDropDownButton("Export"), IconNames.Export, px);
            AddExportItem(ExportFormat.Pdf, "PDF document…", IconNames.Pdf, "Save the map as a PDF document, every step expanded", px);
            AddExportItem(ExportFormat.Html, "Web page (HTML)…", IconNames.Html,
                "Save the map as a web page with the step details beside it", px);
            AddExportItem(ExportFormat.Svg, "Vector image (SVG, for Visio)…", IconNames.Svg,
                "Vector image for Visio, Mural, Lucidchart, draw.io…", px);
            AddExportItem(ExportFormat.Png, "Image (PNG)…", IconNames.Png, "High-resolution image for chat, email and slides", px);
            AddExportItem(ExportFormat.Markdown, "Markdown for AI knowledge…", IconNames.Markdown,
                "Structured text for AI knowledge bases (Copilot Studio, RAG) — states what each step " +
                "runs after and branches into, which a diagram cannot", px);

            _exportAllButton = ToolbarButton(new ToolStripDropDownButton("Export all"), IconNames.ExportAll, px);
            _exportAllHtmlItem = MenuItem("HTML documentation pack…", IconNames.Html, px, () => ExportAll(bulkMarkdown: false));
            _exportAllHtmlItem.ToolTipText = "One web page per process in a folder, with an index page linking them";
            _exportAllMarkdownItem = MenuItem("Markdown knowledge pack…", IconNames.Markdown, px, () => ExportAll(bulkMarkdown: true));
            _exportAllMarkdownItem.ToolTipText = "One Markdown file per process, ready to upload as an AI knowledge source";
            _exportAllButton.DropDownItems.AddRange(new ToolStripItem[] { _exportAllHtmlItem, _exportAllMarkdownItem });
            ConfigureDropDown(_exportAllButton.DropDown, px);

            _openButton = ToolbarButton(new ToolStripSplitButton("Open in Power Automate"), IconNames.Open, px);
            _openButton.ButtonClick += (s, e) => OpenProcess(CurrentTab(), CurrentTab().List.SelectedItem);
            _copyLinkItem = MenuItem("Copy link", IconNames.Link, px, () => CopyLink(CurrentTab(), CurrentTab().List.SelectedItem));
            _openButton.DropDownItems.Add(_copyLinkItem);
            ConfigureDropDown(_openButton.DropDown, px);

            _toolbar.Items.AddRange(new ToolStripItem[]
            {
                _loadButton, new ToolStripSeparator(),
                _solutionLabel, _solutionBox, new ToolStripSeparator(),
                _exportButton, _exportAllButton, new ToolStripSeparator(),
                _openButton
            });

            _tabs = new TabControl { Dock = DockStyle.Fill };
            _flowTab = CreateTab(ProcessListKind.Flows, "Power Automate flows", "flow");
            _workflowTab = CreateTab(ProcessListKind.ClassicWorkflows, "Classic workflows", "workflow");

            _flowList = _flowTab.List;
            _flowPanel = _flowTab.Panel;
            _flowStrip = _flowTab.Strip;
            _flowDetails = _flowTab.Details;
            _flowSplit = _flowTab.Split;
            _workflowList = _workflowTab.List;
            _workflowPanel = _workflowTab.Panel;
            _workflowStrip = _workflowTab.Strip;
            _workflowDetails = _workflowTab.Details;
            _workflowSplit = _workflowTab.Split;

            _tabs.SelectedIndexChanged += (s, e) =>
            {
                UpdateButtons();
                UpdateStatusBar();
            };

            Controls.Add(_tabs);
            Controls.Add(_toolbar);

            UpdateButtons();
        }

        private static T ToolbarButton<T>(T item, string icon, int px) where T : ToolStripItem
        {
            item.Image = IconFactory.Get(icon, px);
            item.ImageScaling = ToolStripItemImageScaling.None;
            item.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            item.AutoToolTip = false;
            var dropDown = item as ToolStripDropDownItem;
            if (dropDown != null) ConfigureDropDown(dropDown.DropDown, px);
            return item;
        }

        private static void ConfigureDropDown(ToolStripDropDown dropDown, int px)
        {
            dropDown.ImageScalingSize = new Size(px, px);
            var menu = dropDown as ToolStripDropDownMenu;
            if (menu != null) menu.ShowItemToolTips = true;
        }

        private static ToolStripMenuItem MenuItem(string text, string icon, int px, Action click)
        {
            var item = new ToolStripMenuItem(text, IconFactory.Get(icon, px)) { ImageScaling = ToolStripItemImageScaling.None };
            item.Click += (s, e) => click();
            return item;
        }

        private void AddExportItem(ExportFormat format, string text, string icon, string tip, int px)
        {
            var item = MenuItem(text, icon, px, () => ExportCurrent(format));
            item.ToolTipText = tip;
            item.Tag = format;
            _exportItems[format] = item;
            _exportButton.DropDownItems.Add(item);
        }

        private Tab CreateTab(ProcessListKind kind, string title, string noun)
        {
            var tab = new Tab { Kind = kind, Noun = noun, SetLabel = title };
            tab.List = new ProcessListPane(kind) { Dock = DockStyle.Fill, Services = _services };
            tab.List.LinkUnavailableReason = item =>
            {
                string reason;
                LinkFor(tab, item, out reason);
                return reason;
            };
            if (kind == ProcessListKind.Flows)
            {
                tab.List.SortColumn = _settings.FlowSortColumn;
                tab.List.SortDescending = _settings.FlowSortDescending;
            }
            else
            {
                tab.List.SortColumn = _settings.WorkflowSortColumn;
                tab.List.SortDescending = _settings.WorkflowSortDescending;
            }

            tab.Panel = new DiagramPanel { Dock = DockStyle.Fill };
            tab.Strip = new MapToolStrip(tab.Panel) { Dock = DockStyle.Top };
            tab.Strip.SetLayoutMode(_layoutMode);
            tab.Details = new NodeDetailsPane { Services = _services };
            tab.Split = CreateSplit(tab.List, WrapWithDetails(tab.Strip, tab.Panel, tab.Details), tab.Details);

            var page = new TabPage(title);
            page.Controls.Add(tab.Split);
            _tabs.TabPages.Add(page);

            tab.Panel.NodeSelected += n =>
            {
                // The pane widens to fit the node's details: into spare room only (CreateSplit).
                _detailsAutoSizing = true;
                try { tab.Details.SetNode(n); }
                finally { _detailsAutoSizing = false; }
            };
            tab.Details.FetchXmlRequested += OpenInFetchXmlBuilder;
            // Full screen: hide the process list so the map + details own the width.
            tab.Panel.FullScreenChanged += full => tab.Split.Panel1Collapsed = full;
            tab.Panel.MapChanged += UpdateButtons;
            tab.Strip.LayoutModeRequested += SetLayoutMode;

            tab.List.SelectionChanged += item => OnSelectionChanged(tab, item);
            tab.List.CheckedChanged += () =>
            {
                UpdateButtons();
                UpdateStatusBar();
            };
            tab.List.VisibleItemsChanged += () =>
            {
                UpdateButtons();
                UpdateStatusBar();
            };
            tab.List.DefinitionSearchRequested += term => StartDefinitionSearch(tab, term);
            tab.List.DefinitionSearchCleared += () => EndDefinitionSearch(tab);
            tab.List.OpenRequested += item => OpenProcess(tab, item);
            tab.List.CopyLinkRequested += item => CopyLink(tab, item);
            tab.List.RefreshRequested += item => RefreshProcess(tab, item);
            tab.List.ExportRequested += (item, format) => ExportProcess(tab, item, format);
            tab.List.SortChanged += () =>
            {
                if (kind == ProcessListKind.Flows)
                {
                    _settings.FlowSortColumn = tab.List.SortColumn;
                    _settings.FlowSortDescending = tab.List.SortDescending;
                }
                else
                {
                    _settings.WorkflowSortColumn = tab.List.SortColumn;
                    _settings.WorkflowSortDescending = tab.List.SortDescending;
                }
                SaveSettings();
            };
            return tab;
        }

        /// <summary>
        /// The keyboard shortcuts, wherever focus is in the tool (the strip's
        /// buttons and the list take focus, so the map cannot rely on having it).
        /// Each acts only when the matching command is available.
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var tab = CurrentTab();
            bool hasMap = tab.Panel.Map != null;
            switch (keyData)
            {
                case Keys.F5:
                    if (!_loadButton.Enabled) break;
                    _loadButton.PerformClick();
                    return true;
                case Keys.Control | Keys.F:
                    if (!hasMap) break;
                    tab.Strip.FocusFind();
                    return true;
                case Keys.F3:
                    if (!hasMap) break;
                    tab.Strip.FindNextMatch();
                    return true;
                case Keys.Shift | Keys.F3:
                    if (!hasMap) break;
                    tab.Strip.FindPreviousMatch();
                    return true;
                case Keys.Control | Keys.D0:
                case Keys.Control | Keys.NumPad0:
                    if (!hasMap) break;
                    tab.Panel.ZoomToFit();
                    return true;
                case Keys.Control | Keys.Oemplus:
                case Keys.Control | Keys.Shift | Keys.Oemplus: // "+" is Shift+= on many layouts
                case Keys.Control | Keys.Add:
                    if (!hasMap) break;
                    tab.Panel.ZoomStep(ZoomFactor);
                    return true;
                case Keys.Control | Keys.OemMinus:
                case Keys.Control | Keys.Subtract:
                    if (!hasMap) break;
                    tab.Panel.ZoomStep(1f / ZoomFactor);
                    return true;
                case Keys.Escape:
                    // Esc leaves full screen, unless a text box wants it: this runs before
                    // the list's filter box (Esc clears it) and the find box ever see the key.
                    if (!tab.Panel.IsFullScreen || EscapeBelongsToFocusedBox()) break;
                    tab.Panel.ToggleFullScreen();
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>True when the focused control is a list's filter box, or a text box with text to clear.</summary>
        private static bool EscapeBelongsToFocusedBox()
        {
            var box = FromHandle(GetFocus()) as TextBoxBase;
            if (box == null) return false;
            for (var parent = box.Parent; parent != null; parent = parent.Parent)
                if (parent is ProcessListPane) return true;
            return box.TextLength > 0;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        /// <summary>The map area: the map strip above the map, the details pane on the right.</summary>
        private static Control WrapWithDetails(MapToolStrip strip, DiagramPanel diagram, NodeDetailsPane details)
        {
            var host = new Panel { Dock = DockStyle.Fill };
            details.Dock = DockStyle.Right;
            details.Width = DefaultDetailsWidth;
            var splitter = new Splitter { Dock = DockStyle.Right, Width = 5 };

            var mapArea = new Panel { Dock = DockStyle.Fill };
            mapArea.Controls.Add(diagram);
            mapArea.Controls.Add(strip);
            diagram.BringToFront(); // Fill occupies the space under the Top-docked strip

            host.Controls.Add(mapArea);
            host.Controls.Add(splitter);
            host.Controls.Add(details);
            return host;
        }

        private SplitContainer CreateSplit(Control left, Control right, NodeDetailsPane details)
        {
            int initial = _settings.ListWidth > 0 ? _settings.ListWidth : DefaultListWidth;
            var split = new SplitContainer
            {
                // A new SplitContainer is only 150 px wide, which clamps the
                // distance; size it first. With Panel1 fixed, the list keeps its
                // width however the host sizes the tool and the map gets the rest.
                Size = new Size(Math.Max(1000, initial + 400), 600),
                FixedPanel = FixedPanel.Panel1,
                Orientation = Orientation.Vertical,
                SplitterDistance = initial,
                Dock = DockStyle.Fill
            };
            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(right);

            // A fixed list would leave the splitter off screen when the window
            // narrows (map and its strip gone, nothing left to drag). Remember
            // the width the list wants and lend part of it to the map only while
            // the window is too narrow, so it comes back when the window widens.
            int listWidth = split.SplitterDistance;
            int fittedWidth = -1;
            bool fitting = false;
            bool dragging = false;
            // Only the user's own drags are remembered between sessions: scaling
            // moves the splitter too, and a scaled width saved would grow every run.
            split.SplitterMoving += (s, e) => dragging = true;
            split.SplitterMoved += (s, e) =>
            {
                if (fitting) return;
                listWidth = split.SplitterDistance;
                if (!dragging) return;
                dragging = false;
                _settings.ListWidth = Math.Max(1, (int)Math.Round(listWidth / _mapStripScale));
                SaveSettings();
            };
            // A drag cancelled with Esc moves nothing and raises no SplitterMoved: forget it once the
            // mouse is released (later, so a completed drag's SplitterMoved has seen it first).
            split.MouseCaptureChanged += (s, e) =>
            {
                if (dragging && split.IsHandleCreated) split.BeginInvoke(new Action(() => dragging = false));
            };
            Action fit = () =>
            {
                // The map side needs the details pane as it is now (it widens to
                // its content, scales with DPI and can be dragged wider), the
                // pane's splitter, and a strip of map for its toolbar.
                var detailsSplitter = details.Parent?.Controls.OfType<Splitter>().FirstOrDefault();
                int reserve = details.Width + (detailsSplitter?.Width ?? 0)
                              + (int)Math.Round(MinMapStrip * _mapStripScale);
                int room = split.Width - split.SplitterWidth - reserve;
                int target = Math.Max(split.Panel1MinSize, Math.Min(listWidth, room));
                // No room for a splitter at all (minimised, or mid-construction).
                if (target > split.Width - split.SplitterWidth - split.Panel2MinSize) return;
                if (target == split.SplitterDistance) return;
                fitting = true;
                try { split.SplitterDistance = target; }
                finally { fitting = false; }
            };
            // Only a width change alters what fits; a height-only resize must
            // not snap back a list the user dragged very wide.
            int detailsWidth = details.Width;
            split.Resize += (s, e) =>
            {
                if (split.Width == fittedWidth) return;
                fittedWidth = split.Width;
                fit();
            };
            details.SizeChanged += (s, e) =>
            {
                if (details.Width == detailsWidth) return;
                if (_detailsAutoSizing)
                {
                    // Widened to fit a node's details, the pane takes only the room the
                    // list (at the width it wants) and the whole map strip leave; a pane
                    // the user drags wider still takes width from the list.
                    var detailsSplitter = details.Parent?.Controls.OfType<Splitter>().FirstOrDefault();
                    int spare = split.Width - split.SplitterWidth - (split.Panel1Collapsed ? 0 : listWidth)
                                - (detailsSplitter?.Width ?? 0) - (int)Math.Round(FullMapStrip * _mapStripScale);
                    int cap = Math.Max((int)Math.Round(DefaultDetailsWidth * _mapStripScale), spare);
                    if (details.Width > cap)
                    {
                        details.Width = cap; // raises this again, at the width allowed
                        return;
                    }
                }
                detailsWidth = details.Width;
                fit();
            };
            _splitFits.Add(fit);
            return split;
        }

        // The narrowest map kept beside the details pane (at 100%): room for the map
        // strip's find box, previous / next, "n of m", Fit and Full screen, with the
        // overflow menu (layout, expand / collapse, zoom) beside them. The list gives
        // up width first.
        private const int MinMapStrip = 370;
        // The map strip with every command on it (at 100%), and the details pane's own width.
        private const int FullMapStrip = 520;
        private const int DefaultDetailsWidth = 260;
        private bool _detailsAutoSizing; // a details pane is sizing itself to a node's details
        private float _mapStripScale = 1f;
        private readonly List<Action> _splitFits = new List<Action>();

        /// <summary>
        /// DPI/font scaling widens the details pane before this control learns
        /// the factor (children scale first), so refit once the strip scales too.
        /// </summary>
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
            base.ScaleControl(factor, specified);
            if ((specified & BoundsSpecified.Width) != 0 && factor.Width > 0) _mapStripScale *= factor.Width;
            foreach (var fit in _splitFits) fit();
        }

        // ----------------------------------------------------------- loading

        // Every load belongs to the connection it was started on. XrmToolBox
        // connects in the background without blocking the tool, so a Load can be
        // clicked while a switch is still in progress, and a load can still be
        // running when the switch completes. Bumped on every connection change;
        // a result from an earlier connection is discarded.
        private int _connectionVersion;
        private int _loadingVersion = -1; // the connection a running load is for; -1 = none running
        private bool _listsLoaded;
        // Bumped whenever the lists are replaced or cleared: work asked for on an older list
        // must not put that list's items back into the panes.
        private int _listGeneration;

        // Kept per connection: the definitions read so far, the solutions, the
        // processes of each solution picked, and the environment id for links.
        private DefinitionStore _store;
        private List<SolutionInfo> _solutions;
        private Exception _solutionsError;
        private readonly Dictionary<Guid, HashSet<Guid>> _solutionMembers = new Dictionary<Guid, HashSet<Guid>>();
        private SolutionInfo _selectedSolution;
        private HashSet<Guid> _solutionFilter;
        private int _solutionRequest;
        private int _fillingSolutions;
        private bool _solutionKeyed; // the selection is being moved with the arrow keys
        private string _environmentId;
        private int _environmentVersion = -1;

        // Processes re-read with "Refresh this process", by id: newer than the list's copy.
        private readonly Dictionary<Guid, ProcessItem> _refreshed = new Dictionary<Guid, ProcessItem>();
        private readonly HashSet<Guid> _refreshPending = new HashSet<Guid>();

        private sealed class LoadResult
        {
            public List<ProcessItem> Processes;
            public List<SolutionInfo> Solutions;
            public Exception SolutionsError;
            public HashSet<Guid> SolutionMembers;   // of the solution picked when the load started
            public string EnvironmentId;
        }

        private void LoadProcesses()
        {
            if (_loadingVersion == _connectionVersion) return; // already loading from this connection

            var service = Service; // read here, not on the worker: the connection this load is for
            var detail = Connection;
            int version = _loadingVersion = _connectionVersion;
            bool resolveEnvironment = _environmentVersion != version;
            var solution = _selectedSolution;

            // The lists are about to be replaced: what reads the current ones stops.
            CancelTriggerPass();
            SuspendDefinitionSearches();
            try
            {
                WorkAsync(new WorkAsyncInfo
                {
                    Message = "Loading workflows and flows from Dataverse…",
                    Work = (worker, args) =>
                    {
                        var result = new LoadResult { Processes = WorkflowRepository.RetrieveProcesses(service) };
                        try
                        {
                            result.Solutions = SolutionRepository.RetrieveSolutions(service);
                            if (solution != null && result.Solutions.Any(s => s.Id == solution.Id))
                                result.SolutionMembers = SolutionRepository.RetrieveProcessIds(service, solution.Id);
                        }
                        catch (Exception ex)
                        {
                            // The lists still load; only the solution filter is unavailable (and says why).
                            result.SolutionsError = ex;
                        }
                        if (resolveEnvironment) result.EnvironmentId = MakerLinks.ResolveEnvironmentId(service, detail);
                        args.Result = result;
                    },
                    PostWorkCallBack = args =>
                    {
                        if (IsClosed) return;
                        if (_loadingVersion == version) _loadingVersion = -1;
                        UpdateButtons();
                        if (version != _connectionVersion) return; // the previous environment's processes

                        if (args.Error != null)
                        {
                            // What was listed stays, and so does what was reading it.
                            StartTriggerPass();
                            ResumeDefinitionSearches();
                            Services.ShowError(this, args.Error, "Load failed",
                                "Loading the workflow table's flows and classic workflows.");
                            return;
                        }
                        PopulateLists((LoadResult)args.Result, version, solution);
                        ResumeDefinitionSearches();
                    }
                });
            }
            catch
            {
                _loadingVersion = -1; // never leave Load stuck as "already running"
                UpdateButtons();
                throw;
            }
            UpdateButtons();
        }

        /// <param name="solution">The solution picked when the load started, whose processes the load read.</param>
        private void PopulateLists(LoadResult result, int version, SolutionInfo solution)
        {
            // Definitions are read again for the new list (a reload is asked for to see changes).
            _store = new DefinitionStore();
            _listGeneration++;
            _refreshed.Clear();
            _refreshPending.Clear();
            ForgetRecentDefinitions();

            _flowTab.All = result.Processes.Where(i => i.Category == WorkflowRepository.CategoryModernFlow)
                                           .OrderBy(i => i.Name).ToList();
            _workflowTab.All = result.Processes.Where(i => i.Category == WorkflowRepository.CategoryClassicWorkflow)
                                               .OrderBy(i => i.Name).ToList();
            _triggerPending = _flowTab.All.Where(i => i.TriggerSummary == null).ToList();

            // A null id may be a passing failure: the next load asks again.
            if (result.EnvironmentId != null)
            {
                _environmentId = result.EnvironmentId;
                _environmentVersion = version;
            }

            // The solution picked now (it may have been changed while the lists loaded)
            // stays picked when it is still there, with its processes read again.
            var current = _selectedSolution;
            HashSet<Guid> interim;
            if (current == null || !_solutionMembers.TryGetValue(current.Id, out interim)) interim = null;
            _solutions = result.Solutions;
            _solutionsError = result.SolutionsError;
            _solutionMembers.Clear();
            _solutionRequest++; // a pick's read still running belongs to the old list
            var kept = current != null ? _solutions?.FirstOrDefault(s => s.Id == current.Id) : null;
            bool readByLoad = kept != null && solution != null && solution.Id == kept.Id && result.SolutionMembers != null;
            if (readByLoad) _solutionMembers[kept.Id] = result.SolutionMembers;
            _selectedSolution = kept;
            // Picked during the load: filtered as before until its processes are read again below.
            _solutionFilter = kept == null ? null : readByLoad ? result.SolutionMembers : interim;
            FillSolutionBox(kept);

            foreach (var tab in Tabs)
            {
                tab.List.SetSolutionFilter(_solutionFilter);
                tab.List.SetItems(tab.All);
            }
            _listsLoaded = true;

            if (kept != null && !readByLoad) SelectSolution(kept);
            StartTriggerPass();
            UpdateButtons();
            UpdateStatusBar();
        }

        /// <summary>
        /// Raised by XrmToolBox (on the UI thread) whenever this tool's connection
        /// changes, before it runs any action that was waiting for a connection.
        /// A throw here would skip that action, so nothing here may throw.
        ///
        /// Lists that were loaded — or were loading — come from the previous
        /// environment: they are cleared and reloaded from the new one, which also
        /// rescues a Load clicked while XrmToolBox was still connecting (that load
        /// ran against the old environment). A tool with nothing loaded yet stays
        /// empty; if a Load triggered this connection, XrmToolBox starts it next.
        /// </summary>
        private void OnConnectionChanged()
        {
            bool hadProcesses = _listsLoaded || _loadingVersion != -1;
            ClearLoadedProcesses(clearFilters: hadProcesses);
            if (hadProcesses && Service != null) LoadProcesses();
        }

        /// <summary>
        /// Forgets everything loaded from the previous connection: the lists, the
        /// maps, the solutions and environment id, any load, preview, trigger pass
        /// or search still running for it, and — when there was something to
        /// filter — the filter text, since names typed for the previous environment
        /// would hide the new one's processes. Full screen is left, so the list the
        /// next load fills is actually visible.
        /// </summary>
        private void ClearLoadedProcesses(bool clearFilters)
        {
            _connectionVersion++;
            _latestPreview.Clear(); // drops every preview still running
            CancelTriggerPass();
            foreach (var tab in Tabs) CancelDefinitionSearch(tab);
            _listsLoaded = false;
            _store = null;
            _listGeneration++;
            _refreshed.Clear();
            _refreshPending.Clear();
            ForgetRecentDefinitions();
            _triggerPending = new List<ProcessItem>();
            _environmentId = null;
            _solutions = null;
            _solutionsError = null;
            _solutionMembers.Clear();
            _selectedSolution = null;
            _solutionFilter = null;
            _solutionRequest++;
            FillSolutionBox(null);

            foreach (var tab in Tabs)
            {
                tab.All = new List<ProcessItem>();
                ClearFindTermFromSearch(tab);
                if (tab.Panel.IsFullScreen) tab.Panel.ToggleFullScreen();
                tab.List.Clear(clearFilters);
                tab.Panel.SetMap(null);
            }
            UpdateButtons();
            UpdateStatusBar();
        }

        // ---------------------------------------------------- solution filter

        /// <summary>Lists "All solutions" and the loaded solutions, selecting <paramref name="selected"/> (null = all).</summary>
        private void FillSolutionBox(SolutionInfo selected)
        {
            _fillingSolutions++;
            try
            {
                _solutionBox.BeginUpdate();
                _solutionBox.Items.Clear();
                _solutionBox.Items.Add(AllSolutions);
                if (_solutions != null)
                    foreach (var s in _solutions) _solutionBox.Items.Add(s);
                _solutionBox.SelectedIndex = selected == null ? 0 : Math.Max(0, _solutionBox.Items.IndexOf(selected));
                _solutionBox.EndUpdate();

                // Long friendly names stay readable in the drop-down.
                int widest = _solutionBox.Items.Cast<object>()
                    .Select(o => TextRenderer.MeasureText(o.ToString(), _solutionBox.Font).Width)
                    .DefaultIfEmpty(0).Max() + SystemInformation.VerticalScrollBarWidth + 8;
                _solutionBox.DropDownWidth = Math.Min(Math.Max(widest, _solutionBox.Width), 520);
            }
            finally
            {
                _fillingSolutions--;
            }
        }

        private void OnSolutionPicked()
        {
            var picked = _solutionBox.SelectedItem as SolutionInfo;
            if (picked?.Id == _selectedSolution?.Id) return;
            SelectSolution(picked);
        }

        /// <summary>Typed text (Enter, or leaving the box) picks the solution of that name, or goes back to the one picked.</summary>
        private void CommitSolutionText()
        {
            var text = (_solutionBox.Text ?? "").Trim();
            var match = _solutionBox.Items.Cast<object>()
                .FirstOrDefault(o => string.Equals(o.ToString(), text, StringComparison.CurrentCultureIgnoreCase));
            if (match == null)
            {
                _fillingSolutions++;
                try { _solutionBox.SelectedItem = (object)_selectedSolution ?? AllSolutions; }
                finally { _fillingSolutions--; }
                _solutionBox.Text = (_selectedSolution?.ToString()) ?? AllSolutions;
                return;
            }
            if (!ReferenceEquals(_solutionBox.SelectedItem, match)) _solutionBox.SelectedItem = match; // raises the pick
            else OnSolutionPicked();
        }

        /// <summary>Filters both tabs to the solution's processes (null = all), reading them once per solution.</summary>
        private void SelectSolution(SolutionInfo solution)
        {
            _selectedSolution = solution;
            int request = ++_solutionRequest;
            if (solution == null)
            {
                ApplySolutionFilter(null);
                return;
            }

            HashSet<Guid> members;
            if (_solutionMembers.TryGetValue(solution.Id, out members))
            {
                ApplySolutionFilter(members);
                return;
            }

            var service = Service;
            int version = _connectionVersion;
            WorkAsync(new WorkAsyncInfo
            {
                Message = "Reading the processes in " + solution + "…",
                Work = (worker, args) => args.Result = SolutionRepository.RetrieveProcessIds(service, solution.Id),
                PostWorkCallBack = args =>
                {
                    if (IsClosed || version != _connectionVersion) return;
                    if (args.Error != null)
                    {
                        if (request != _solutionRequest) return;
                        // Back to every solution, so the box never names a filter that is not applied.
                        _selectedSolution = null;
                        FillSolutionBox(null);
                        ApplySolutionFilter(null);
                        Services.ShowError(this, args.Error, "Could not read the solution's processes", solution.UniqueName);
                        return;
                    }
                    _solutionMembers[solution.Id] = (HashSet<Guid>)args.Result;
                    if (request != _solutionRequest) return; // another solution was picked meanwhile
                    ApplySolutionFilter((HashSet<Guid>)args.Result);
                }
            });
        }

        private void ApplySolutionFilter(HashSet<Guid> members)
        {
            _solutionFilter = members;
            foreach (var tab in Tabs) tab.List.SetSolutionFilter(members);
            // A definition search covers the processes in the filter: search the new set.
            foreach (var tab in Tabs)
                if (tab.SearchTerm != null) StartDefinitionSearch(tab, tab.SearchTerm);
            UpdateButtons();
            UpdateStatusBar();
        }

        // ------------------------------------------------- flow trigger pass

        // Flows' triggers are in their definitions, which the list is loaded
        // without. After each load they are read in the background, a batch at a
        // time, filling the Trigger column as they arrive. Not through WorkAsync:
        // that shows XrmToolBox's working overlay over the tool and would hold up
        // the previews behind it.
        private bool _readTriggersInBackground = true;
        private CancellationTokenSource _triggerCancel;
        private int _triggerPassId;
        private bool _triggerPassRunning;
        private int _triggerDone, _triggerTotal;
        // The current list's flows whose row shows no trigger yet. The pass, a search
        // and a preview can each read a trigger; ShowReadTriggers repaints their rows.
        private List<ProcessItem> _triggerPending = new List<ProcessItem>();

        private void StartTriggerPass()
        {
            CancelTriggerPass();
            ShowReadTriggers(); // what a cancelled pass or a search read before this
            var store = _store;
            if (!_readTriggersInBackground || store == null) return;
            var pending = _flowTab.All.Where(i => i.TriggerSummary == null).ToList();
            if (pending.Count == 0) return;

            var service = Service;
            int version = _connectionVersion;
            int id = ++_triggerPassId;
            var cancel = _triggerCancel = new CancellationTokenSource();
            // Read here: the source can be cancelled and disposed before the task starts.
            var token = cancel.Token;
            _triggerPassRunning = true;
            _triggerDone = 0;
            _triggerTotal = pending.Count;
            UpdateStatusBar();

            Task.Factory.StartNew(() =>
            {
                Exception failure = null;
                IOrganizationService own = null;
                try
                {
                    // A connection of its own when the service can make one, so the pass never
                    // shares a channel with the previews and exports running meanwhile.
                    own = CloneOf(service);
                    store.EnsureLoaded(own ?? service, pending,
                        (done, total) => PostToUi(() => OnTriggerProgress(id, version, done)), token);
                }
                catch (OperationCanceledException)
                {
                    return; // cancelled: whoever cancelled it has moved on
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    (own as IDisposable)?.Dispose();
                }
                PostToUi(() => OnTriggerPassEnded(id, version, failure));
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>A copy of a CrmServiceClient for a background thread; null to use the service as it is.</summary>
        private static IOrganizationService CloneOf(IOrganizationService service)
        {
            var client = service as CrmServiceClient;
            if (client == null) return null;
            try
            {
                var clone = client.Clone();
                if (clone != null && clone.IsReady) return clone;
                clone?.Dispose();
            }
            catch (Exception)
            {
                // Some sign-in types cannot be cloned; the shared client still works.
            }
            return null;
        }

        private void OnTriggerProgress(int id, int version, int done)
        {
            if (id != _triggerPassId || version != _connectionVersion) return;
            _triggerDone = done;
            ShowReadTriggers();
            UpdateStatusBar();
        }

        private void OnTriggerPassEnded(int id, int version, Exception failure)
        {
            if (id != _triggerPassId || version != _connectionVersion) return;
            _triggerPassRunning = false;
            ShowReadTriggers();
            UpdateStatusBar();
            if (failure != null)
                SendStatus(null, "Could not read every flow trigger: " + failure.Message);
        }

        /// <summary>Repaints the rows whose trigger has been read since the last time.</summary>
        private void ShowReadTriggers()
        {
            var read = _triggerPending.Where(i => i.TriggerSummary != null).ToList();
            if (read.Count == 0) return;
            _triggerPending = _triggerPending.Where(i => i.TriggerSummary == null).ToList();
            _flowList.RefreshRows(read);
        }

        private void CancelTriggerPass()
        {
            if (_triggerCancel != null)
            {
                _triggerCancel.Cancel();
                _triggerCancel.Dispose(); // the pass only polls the token, which still reads as cancelled
                _triggerCancel = null;
            }
            _triggerPassId++; // whatever it still reports is dropped
            if (!_triggerPassRunning) return;
            _triggerPassRunning = false;
            UpdateStatusBar();
        }

        // ------------------------------------------------- definition search

        private void StartDefinitionSearch(Tab tab, string term)
        {
            CancelDefinitionSearch(tab);
            if (string.IsNullOrWhiteSpace(term)) return;

            var store = _store;
            var items = tab.All.Where(i => _solutionFilter == null || _solutionFilter.Contains(i.Id)).ToList();
            tab.SearchTerm = term;
            if (store == null)
            {
                tab.List.SetDefinitionMatches(new Guid[0], term); // nothing loaded, nothing found
                UpdateStatusBar();
                return;
            }

            var service = Service;
            int version = _connectionVersion;
            int id = ++tab.SearchId;
            var cancel = tab.SearchCancel = new CancellationTokenSource();
            var token = cancel.Token; // as in the trigger pass: read before the source can be disposed
            tab.SearchRunning = true;
            tab.SearchDone = 0;
            tab.SearchTotal = items.Count;
            UpdateStatusBar();

            Task.Factory.StartNew(() =>
            {
                List<ProcessItem> found = null;
                Exception failure = null;
                IOrganizationService own = null;
                try
                {
                    // Like the trigger pass: a connection of its own, not the one previews and exports use.
                    own = CloneOf(service);
                    found = store.Search(own ?? service, items, term,
                        (done, total) => PostToUi(() => OnSearchProgress(tab, id, version, done)), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    (own as IDisposable)?.Dispose();
                }
                PostToUi(() => OnSearchEnded(tab, id, version, term, found, failure));
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void OnSearchProgress(Tab tab, int id, int version, int done)
        {
            if (id != tab.SearchId || version != _connectionVersion) return;
            tab.SearchDone = done;
            ShowReadTriggers(); // a flow search reads triggers on its way
            if (tab == CurrentTab()) UpdateStatusBar();
        }

        private void OnSearchEnded(Tab tab, int id, int version, string term, List<ProcessItem> found, Exception failure)
        {
            if (id != tab.SearchId || version != _connectionVersion) return; // a newer search, or another environment
            tab.SearchRunning = false;
            DisposeSearchCancel(tab);
            ShowReadTriggers();
            if (failure != null)
            {
                tab.SearchTerm = null;
                tab.List.SetDefinitionMatches(null, null);
                UpdateStatusBar();
                Services.ShowError(this, failure, "Search failed", "Searching the " + tab.Noun + " definitions for '" + term + "'.");
                return;
            }
            if (tab.List.SearchMode != ListSearchMode.Definitions)
            {
                tab.SearchTerm = null; // the search was ended meanwhile
                UpdateStatusBar();
                return;
            }

            tab.List.SetDefinitionMatches(found.Select(i => i.Id).ToList(), term);
            // The map on screen, if it is one of them, shows where the term is.
            var shown = tab.Panel.Map?.Source;
            if (shown != null && found.Any(i => i.Id == shown.Id)) SetFindTermFromSearch(tab);
            UpdateButtons();
            UpdateStatusBar();
        }

        /// <summary>The user ended the search (Esc, empty box, names mode): stop it and forget its results.</summary>
        private void EndDefinitionSearch(Tab tab)
        {
            CancelDefinitionSearch(tab);
            ClearFindTermFromSearch(tab);
            UpdateButtons();
            UpdateStatusBar();
        }

        private void CancelDefinitionSearch(Tab tab)
        {
            StopSearch(tab);
            tab.SearchTerm = null;
        }

        /// <summary>Stops the tab's running search; its late results are dropped.</summary>
        private static void StopSearch(Tab tab)
        {
            tab.SearchCancel?.Cancel();
            DisposeSearchCancel(tab);
            tab.SearchId++;
            tab.SearchRunning = false;
        }

        private static void DisposeSearchCancel(Tab tab)
        {
            tab.SearchCancel?.Dispose(); // the search only polls the token, which still reads as cancelled
            tab.SearchCancel = null;
        }

        /// <summary>
        /// Stops the searches for a reload. Their terms stay, and finished searches
        /// keep their results on screen until the new list arrives.
        /// </summary>
        private void SuspendDefinitionSearches()
        {
            foreach (var tab in Tabs.Where(t => t.SearchTerm != null)) StopSearch(tab);
        }

        /// <summary>
        /// After a reload (or a failed one): each tab's search as it is now — the
        /// one running when the reload started, or one started meanwhile against
        /// the old list — runs again on the list now shown.
        /// </summary>
        private void ResumeDefinitionSearches()
        {
            foreach (var tab in Tabs)
                if (tab.SearchTerm != null && tab.List.SearchMode == ListSearchMode.Definitions)
                    StartDefinitionSearch(tab, tab.SearchTerm);
        }

        private void SetFindTermFromSearch(Tab tab)
        {
            if (tab.SearchTerm == null || tab.SearchRunning) return;
            // The details option as the user had it, for when the search ends.
            if (tab.FindTermSet == null || !SameTerm(tab.Strip.FindTerm, tab.FindTermSet))
                tab.DetailsBeforeSearch = tab.Strip.IncludeDetails;
            tab.Strip.SetFindTerm(tab.SearchTerm, includeDetails: true);
            tab.FindTermSet = tab.SearchTerm;
        }

        /// <summary>
        /// Empties the find box when this tool filled it for a search that is over,
        /// and the box still holds that term: one typed there since stays.
        /// </summary>
        private void ClearFindTermFromSearch(Tab tab)
        {
            if (tab.FindTermSet == null) return;
            var set = tab.FindTermSet;
            tab.FindTermSet = null;
            if (SameTerm(tab.Strip.FindTerm, set)) tab.Strip.SetFindTerm("", tab.DetailsBeforeSearch);
        }

        private static bool SameTerm(string a, string b)
            => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        // -------------------------------------------------------- previewing

        // Bumped per preview request; a result that arrives after a newer request
        // for the same panel started is stale and dropped, so quick arrow-key
        // browsing can never land an older map on top of the one now selected.
        // Kept per panel: a preview on the other tab must not drop this one.
        private int _previewRequest;
        private readonly Dictionary<DiagramPanel, int> _latestPreview = new Dictionary<DiagramPanel, int>();

        private void OnSelectionChanged(Tab tab, ProcessItem item)
        {
            UpdateButtons();
            if (item != null) Preview(tab, item);
        }

        /// <summary>"Refresh this process": its definition is read again and the map redrawn.</summary>
        private void RefreshProcess(Tab tab, ProcessItem item)
        {
            if (item == null) return;
            // Any preview of it until the fresh copy arrives reads it again too.
            _refreshPending.Add(item.Id);
            Preview(tab, item);
        }

        private void Preview(Tab tab, ProcessItem item)
        {
            var panel = tab.Panel;
            // Fetching the definition and laying out a large map both take time,
            // so neither runs on the UI thread.
            int request = _latestPreview[panel] = ++_previewRequest;
            int version = _connectionVersion;
            var mode = _layoutMode;
            var service = Service; // the environment the listed item came from
            int generation = _listGeneration;
            bool reread = _refreshPending.Contains(item.Id);
            var source = reread ? null : Snapshot(item);
            WorkAsync(new WorkAsyncInfo
            {
                Message = source != null ? "Building the map…" : "Loading the definition…",
                Work = (worker, args) =>
                {
                    var copy = source ?? Download(service, item);
                    var map = ProcessMapBuilder.Build(copy, mode);
                    ProcessMapBuilder.CollapseToFit(map); // large maps open readable
                    args.Result = map;
                },
                PostWorkCallBack = args =>
                {
                    if (IsClosed || version != _connectionVersion) return;
                    var built = args.Error == null ? (ProcessMap)args.Result : null;
                    // What it read belongs to the list it was asked from: after a reload the
                    // item is the old list's, and its row has been replaced.
                    if (built != null && generation == _listGeneration)
                    {
                        if (source == null && !reread) RememberDefinition(built.Source);
                        if (reread || item.TriggerSummary == null) Absorb(tab, item, built.Source, reread);
                    }

                    int latest;
                    if (!_latestPreview.TryGetValue(panel, out latest) || request != latest) return;
                    if (args.Error != null)
                    {
                        // The previous process must not stay on screen, or be
                        // exported, under the one now selected.
                        panel.SetMap(null);
                        Services.ShowMessage(this, "Could not render this process:\n\n" + args.Error.Message,
                            "Render error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    else
                    {
                        panel.SetMap(built);
                        // The layout may have been changed while this map was built.
                        panel.SetLayoutMode(_layoutMode);
                        panel.ZoomToFit();
                        // A definition search is on: show where its term is in this map.
                        SetFindTermFromSearch(tab);
                    }
                    UpdateButtons();
                }
            });
        }

        /// <summary>What a preview just read: the fresh copy of a refreshed process, and a flow's trigger.</summary>
        private void Absorb(Tab tab, ProcessItem item, ProcessItem read, bool reread)
        {
            if (reread)
            {
                _refreshed[item.Id] = read;
                _refreshPending.Remove(item.Id);
            }
            if (!item.IsModernFlow || read.ClientData == null) return;
            var summary = Parsing.TriggerSummary.ForFlow(read.ClientData);
            if (summary == null) return;
            read.TriggerSummary = summary; // the copy's headers (exports) say the same
            if (summary == item.TriggerSummary) return;
            item.TriggerSummary = summary;
            tab.List.RefreshRows(new[] { item });
        }

        /// <summary>
        /// A copy of the process with its definition, when it is in memory; null
        /// when it has to be downloaded. A copy, because the definitions held for
        /// the trigger column and search can be dropped to stay within their memory
        /// budget at any time — a map and an export keep their own. UI thread only.
        /// </summary>
        private ProcessItem Snapshot(ProcessItem item)
        {
            ProcessItem refreshed;
            if (_refreshed.TryGetValue(item.Id, out refreshed)) return refreshed;
            var recent = RecentDefinition(item.Id);
            if (recent != null) return recent;

            // The flag first: dropping text clears the flag before the text, so text
            // read after the flag said loaded is either whole or missing.
            bool loaded = item.DefinitionLoaded;
            var clientData = item.ClientData;
            var xaml = item.Xaml;
            if (!loaded || (clientData == null && xaml == null)) return null;

            var copy = CopyOf(item);
            copy.ClientData = clientData;
            copy.Xaml = xaml;
            copy.DefinitionLoaded = true;
            return copy;
        }

        // The copies previews downloaded, most recently used last, so going back to a
        // process builds its map at once: the store only keeps what the trigger pass
        // and searches read (no classic workflows, for one). Bounded; cleared with the list.
        private const long RecentDefinitionChars = 4_000_000; // ≈ 8 MB of text
        private readonly LinkedList<ProcessItem> _recent = new LinkedList<ProcessItem>();
        private long _recentChars;

        private static long DefinitionChars(ProcessItem item) => (item.ClientData?.Length ?? 0) + (item.Xaml?.Length ?? 0);

        private ProcessItem RecentDefinition(Guid id)
        {
            for (var node = _recent.Last; node != null; node = node.Previous)
            {
                if (node.Value.Id != id) continue;
                _recent.Remove(node);
                _recent.AddLast(node);
                return node.Value;
            }
            return null;
        }

        private void RememberDefinition(ProcessItem copy)
        {
            long size = DefinitionChars(copy);
            if (size == 0) return; // nothing came back (deleted, or not readable): ask again next time
            var old = _recent.FirstOrDefault(i => i.Id == copy.Id);
            if (old != null)
            {
                _recent.Remove(old);
                _recentChars -= DefinitionChars(old);
            }
            _recent.AddLast(copy);
            _recentChars += size;
            // The oldest go first; the newest stays even when it alone is over the budget.
            while (_recentChars > RecentDefinitionChars && _recent.Count > 1)
            {
                _recentChars -= DefinitionChars(_recent.First.Value);
                _recent.RemoveFirst();
            }
        }

        private void ForgetRecentDefinitions()
        {
            _recent.Clear();
            _recentChars = 0;
        }

        /// <summary>A copy of the process with its definition read from Dataverse (background thread).</summary>
        private static ProcessItem Download(IOrganizationService service, ProcessItem item)
        {
            var copy = CopyOf(item);
            WorkflowRepository.LoadDefinitions(service, new[] { copy });
            return copy;
        }

        private static ProcessItem CopyOf(ProcessItem item) => new ProcessItem
        {
            Id = item.Id,
            Name = item.Name,
            Category = item.Category,
            PrimaryEntity = item.PrimaryEntity,
            State = item.State,
            ModifiedOn = item.ModifiedOn,
            CreatedOn = item.CreatedOn,
            CreatedBy = item.CreatedBy,
            ModifiedBy = item.ModifiedBy,
            Owner = item.Owner,
            Scope = item.Scope,
            WorkflowIdUnique = item.WorkflowIdUnique,
            IsManaged = item.IsManaged,
            Description = item.Description,
            TriggerOnCreate = item.TriggerOnCreate,
            TriggerOnDelete = item.TriggerOnDelete,
            TriggerOnUpdateAttributes = item.TriggerOnUpdateAttributes,
            OnDemand = item.OnDemand,
            IsChild = item.IsChild,
            Mode = item.Mode,
            RunAs = item.RunAs,
            TriggerSummary = item.TriggerSummary
        };

        // ---------------------------------------------------------- links

        /// <summary>Where the process is edited, or null with the reason no link can be built.</summary>
        private string LinkFor(Tab tab, ProcessItem item, out string reason)
        {
            reason = null;
            bool flows = tab.Kind == ProcessListKind.Flows;
            if (item == null)
            {
                reason = !_listsLoaded
                    ? (flows ? "Load processes to open a flow in Power Automate" : "Load processes to open a workflow in the workflow designer")
                    : (flows ? "Select a flow to open it in Power Automate" : "Select a workflow to open it in the workflow designer");
                return null;
            }

            var orgUrl = OrganizationUrl;
            if (flows)
            {
                if (_environmentVersion != _connectionVersion || string.IsNullOrEmpty(_environmentId))
                {
                    // Also on-premises and older servers, which have none.
                    reason = "The environment id of this connection could not be read, so no Power Automate link can be built";
                    return null;
                }
                if (!item.WorkflowIdUnique.HasValue || item.WorkflowIdUnique.Value == Guid.Empty)
                {
                    reason = "This flow has no Power Automate id (workflowidunique), so no link can be built";
                    return null;
                }
                return MakerLinks.FlowUrl(MakerLinks.MakerHost(orgUrl), _environmentId, item.WorkflowIdUnique.Value);
            }

            if (string.IsNullOrWhiteSpace(orgUrl))
            {
                reason = "The connection has no web address, so no workflow designer link can be built";
                return null;
            }
            return MakerLinks.ClassicWorkflowUrl(orgUrl, item.Id);
        }

        /// <summary>
        /// The connection's details. Read by reflection: PluginControlBase declares the property
        /// against McTools.Xrm.Connection 1.2025.9.64, newer than the copy the package graph gives
        /// this project (CS1705); at run time there is only the host's copy, so the type is the same.
        /// </summary>
        private McTools.Xrm.Connection.ConnectionDetail Connection =>
            ConnectionDetailProperty?.GetValue(this) as McTools.Xrm.Connection.ConnectionDetail;

        private static readonly System.Reflection.PropertyInfo ConnectionDetailProperty =
            typeof(PluginControlBase).GetProperty("ConnectionDetail");

        /// <summary>The environment's web address, e.g. https://contoso.crm9.dynamics.com.</summary>
        private string OrganizationUrl
        {
            get
            {
                var detail = Connection;
                if (detail == null) return null;
                return !string.IsNullOrWhiteSpace(detail.WebApplicationUrl) ? detail.WebApplicationUrl : detail.OriginalUrl;
            }
        }

        private void OpenProcess(Tab tab, ProcessItem item)
        {
            string reason;
            var url = LinkFor(tab, item, out reason);
            if (url == null) return;
            try
            {
                Services.OpenUrl(Connection, url);
            }
            catch (Exception ex)
            {
                Services.ShowMessage(this, "Could not open the browser:\n\n" + ex.Message + "\n\n" + url,
                    "Open", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void CopyLink(Tab tab, ProcessItem item)
        {
            string reason;
            var url = LinkFor(tab, item, out reason);
            if (url == null) return;
            try
            {
                Services.SetClipboardText(url);
                SendStatus(null, "Copied the link to '" + (item.Name ?? "(unnamed)") + "'");
            }
            catch (ExternalException)
            {
                Services.ShowMessage(this, "Could not copy to the clipboard: another program is using it. Try again.",
                    "Copy link", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ---------------------------------------------------------- exporting

        /// <summary>Export ▾: the current tab's map.</summary>
        private void ExportCurrent(ExportFormat format)
        {
            var map = CurrentTab().Panel.Map;
            if (map?.Source == null)
            {
                Services.ShowMessage(this, "Select a process to export first.", "Nothing selected",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Export(map.Source, true, map.Mode, format, map.Graph.Title); // a map's source is a copy with its definition
        }

        /// <summary>The list's Export ▸: that row's process, whether or not its map is the one shown.</summary>
        private void ExportProcess(Tab tab, ProcessItem item, ExportFormat format)
        {
            if (item == null) return;
            var map = tab.Panel.Map;
            if (map?.Source != null && map.Source.Id == item.Id)
            {
                Export(map.Source, true, map.Mode, format, map.Graph.Title);
                return;
            }
            var copy = Snapshot(item);
            Export(copy ?? item, copy != null, _layoutMode, format, item.Name);
        }

        /// <summary>
        /// Saves a process's map as a file. The file name is asked first; the map
        /// is then built and saved off the UI thread, always fully expanded (a
        /// reader cannot expand a PDF), from <paramref name="source"/>'s definition
        /// — downloaded first when it is not a copy that has it.
        /// </summary>
        private void Export(ProcessItem source, bool hasDefinition, LayoutMode mode, ExportFormat format, string title)
        {
            string filter, extension, what;
            switch (format)
            {
                case ExportFormat.Pdf: filter = "PDF document (*.pdf)|*.pdf"; extension = ".pdf"; what = "PDF"; break;
                case ExportFormat.Svg: filter = "SVG image (*.svg)|*.svg"; extension = ".svg"; what = "SVG"; break;
                case ExportFormat.Png: filter = "PNG image (*.png)|*.png"; extension = ".png"; what = "PNG"; break;
                case ExportFormat.Markdown: filter = "Markdown document (*.md)|*.md"; extension = ".md"; what = "Markdown"; break;
                default: filter = "HTML document (*.html)|*.html"; extension = ".html"; what = "HTML"; break;
            }
            var safeName = ExportNaming.SafeFileName(string.IsNullOrWhiteSpace(title) ? (source.Name ?? "process-map") : title);
            // The environment this process came from — read now, before the dialog:
            // a connection switch can complete while it is open.
            var service = Service;
            int connection = _connectionVersion;
            var path = Services.ChooseSaveFile(this, filter, safeName + extension, _settings.LastExportFolder);
            if (string.IsNullOrEmpty(path)) return;
            if (!hasDefinition && connection != _connectionVersion)
            {
                Services.ShowMessage(this,
                    "The connection changed while the file was being chosen, and the lists have been " +
                    "reloaded from the new environment. Select the process there and export it again.",
                    "Connection changed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            RememberExportFolder(Path.GetDirectoryName(path));

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Exporting to " + what + "…",
                Work = (worker, args) =>
                {
                    var item = hasDefinition ? source : Download(service, source);
                    // A map of its own: the one on screen may be collapsed, and the
                    // user can expand or re-lay it out while this one is drawn.
                    var full = ProcessMapBuilder.Build(item, mode);
                    switch (format)
                    {
                        case ExportFormat.Pdf: PdfExporter.Save(full, path); break;
                        case ExportFormat.Svg: SvgExporter.Save(full, path); break;
                        case ExportFormat.Png: args.Result = ProcessMapBuilder.SavePng(full, path); return;
                        case ExportFormat.Markdown: MarkdownExporter.Save(full, path); break;
                        default: HtmlExporter.Save(full, path); break;
                    }
                    args.Result = true;
                },
                PostWorkCallBack = args =>
                {
                    if (IsClosed) return;
                    if (args.Error != null)
                    {
                        Services.ShowError(this, args.Error, "Export failed", "Exporting '" + source.Name + "' to " + what + ".");
                        return;
                    }
                    if (!(bool)args.Result)
                    {
                        Services.ShowMessage(this,
                            "This map is too large for a PNG image. Export it as SVG or PDF instead; " +
                            "both keep every step sharp at any size.",
                            "Map too large", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    if (Services.ShowMessage(this, "Export complete. Open the file now?", "Done",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    {
                        OpenFile(path);
                    }
                }
            });
        }

        /// <summary>
        /// What Export all exports on the current tab: the checked processes, or
        /// every process the list shows (after its name, definition and solution
        /// filters) when none is checked.
        /// </summary>
        private List<ProcessItem> ExportAllScope(Tab tab, out bool isChecked)
        {
            var scope = tab.List.CheckedItems.ToList();
            isChecked = scope.Count > 0;
            return isChecked ? scope : tab.List.VisibleItems.ToList();
        }

        /// <summary>
        /// Exports the Export all scope to one file each plus an index, in a folder.
        /// </summary>
        private void ExportAll(bool bulkMarkdown)
        {
            var kind = bulkMarkdown ? "Markdown" : "HTML";
            var tab = CurrentTab();
            bool isChecked;
            var items = ExportAllScope(tab, out isChecked);
            // The environment these listed processes came from — read now, before
            // the dialogs below: a connection switch can complete while they are open.
            var service = Service;
            int connection = _connectionVersion;
            if (items.Count == 0)
            {
                Services.ShowMessage(this, "There are no processes in the current list to export.",
                    "Nothing to export", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // The pack is one file per process plus the index; warn before doing
            // the work rather than after, since the limit is per Copilot agent.
            if (bulkMarkdown && items.Count + 1 > BulkMarkdownExporter.KnowledgeFileLimit)
            {
                var proceed = Services.ShowMessage(this,
                    $"This will produce {items.Count + 1} files, more than the " +
                    $"{BulkMarkdownExporter.KnowledgeFileLimit} a single Copilot Studio agent accepts " +
                    "as knowledge.\n\nYou can still export and split the pack, or check fewer processes first." +
                    "\n\nExport anyway?",
                    "More files than one agent accepts",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (proceed != DialogResult.Yes) return;
            }

            var folder = Services.ChooseFolder(this,
                $"Choose a folder for the {items.Count} exported {kind} files", _settings.LastExportFolder);
            if (string.IsNullOrEmpty(folder)) return;

            if (connection != _connectionVersion)
            {
                Services.ShowMessage(this,
                    "The connection changed while the export was being set up, and the list has been " +
                    "reloaded from the new environment. Run the export again to export that list.",
                    "Connection changed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            RememberExportFolder(folder);

            var setLabel = tab.SetLabel;
            var mode = _layoutMode; // the diagrams match what is on screen
            // Copies: the trigger pass may drop the list's definition text while this runs.
            var copies = items.Select(i => Snapshot(i) ?? CopyOf(i)).ToList();

            // XrmToolBox shows progress on whichever background job started last,
            // so progress is only reported while this export is that job; a list
            // reload or preview started meanwhile would otherwise have its panel
            // re-created after it finished, and left on screen for good.
            int thisJob = -1;
            WorkAsync(new WorkAsyncInfo
            {
                Message = $"Exporting {items.Count} processes to {kind}…",
                Work = (worker, args) =>
                {
                    Action<string> report = s => worker.ReportProgress(0, s);

                    // The list was loaded without definitions; fetch the missing ones.
                    WorkflowRepository.LoadDefinitions(service, copies,
                        (done, total) => report($"Loading definitions ({done} of {total})…"));

                    args.Result = bulkMarkdown
                        ? BulkMarkdownExporter.Export(copies, folder, setLabel, report)
                        : BulkHtmlExporter.Export(copies, folder, setLabel, report, mode);
                },
                ProgressChanged = args =>
                {
                    if (IsClosed) return;
                    if (_backgroundJobsStarted == thisJob) SetWorkingMessage(args.UserState?.ToString());
                },
                PostWorkCallBack = args =>
                {
                    if (IsClosed) return;
                    if (args.Error != null)
                    {
                        Services.ShowError(this, args.Error, "Export failed",
                            $"Exporting {items.Count} processes to {kind} in {folder}.");
                        return;
                    }

                    var result = (BulkExportResult)args.Result;
                    var text = $"Exported {result.Exported} of {items.Count} processes.";
                    if (result.Failures.Count > 0)
                        text += $"\n{result.Failures.Count} failed (listed at the bottom of the index page).";
                    text += "\n\nOpen the index now?";

                    if (Services.ShowMessage(this, text, "Export complete",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    {
                        OpenFile(result.IndexPath);
                    }
                }
            });
            thisJob = _backgroundJobsStarted; // progress arrives only after this handler returns
        }

        private void OpenFile(string path)
        {
            try
            {
                Services.OpenFile(path);
            }
            catch (Exception ex)
            {
                Services.ShowMessage(this, "Could not open the file:\n\n" + ex.Message, "Open",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void RememberExportFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            _settings.LastExportFolder = folder;
            SaveSettings();
        }

        // ---------------------------------------------------------- layout

        /// <summary>The layout is one setting for both tabs: both strips show it, both maps use it.</summary>
        private void SetLayoutMode(LayoutMode mode)
        {
            _layoutMode = mode;
            foreach (var tab in Tabs)
            {
                tab.Strip.SetLayoutMode(mode);
                tab.Panel.SetLayoutMode(mode);
            }
            _settings.LayoutMode = mode;
            SaveSettings();
        }

        // ---------------------------------------------------------- settings

        private ProcessMapperSettings LoadSettings()
        {
            ProcessMapperSettings settings = null;
            try
            {
                settings = _settingsStore.Load();
            }
            catch (Exception)
            {
                // Unreadable settings are no reason not to open: the defaults are used.
            }
            settings = settings ?? new ProcessMapperSettings();
            if (!Enum.IsDefined(typeof(LayoutMode), settings.LayoutMode)) settings.LayoutMode = ProcessMapBuilder.DefaultMode;
            if (settings.ListWidth < 0) settings.ListWidth = 0;
            return settings;
        }

        /// <summary>Saves the settings when they changed since the last save.</summary>
        private void SaveSettings()
        {
            if (_settings.SameAs(_savedSettings)) return;
            var snapshot = _settings.Clone();
            try
            {
                _settingsStore.Save(snapshot);
                _savedSettings = snapshot;
            }
            catch (Exception)
            {
                // A settings file that cannot be written must not break the tool.
            }
        }

        public override void ClosingPlugin(PluginCloseInfo info)
        {
            base.ClosingPlugin(info);
            if (info.Cancel) return;
            CancelBackgroundWork();
            SendStatus(null, ""); // no progress left behind in the host's status bar
            SaveSettings();
        }

        private void CancelBackgroundWork()
        {
            CancelTriggerPass();
            foreach (var tab in Tabs) CancelDefinitionSearch(tab);
        }

        protected override void Dispose(bool disposing)
        {
            // The icons stay: XrmToolBox's tab and window may still draw them while they close.
            if (disposing) CancelBackgroundWork();
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------ status

        private string _statusMessage;
        private int? _statusProgress;

        /// <summary>
        /// The status bar: a running search's or trigger pass's progress, else
        /// the current tab's counts. Empty (hidden) until something is loaded.
        /// </summary>
        private void UpdateStatusBar()
        {
            var tab = CurrentTab();
            if (tab.SearchRunning)
            {
                SendStatus(Percent(tab.SearchDone, tab.SearchTotal),
                    $"Searching {tab.Noun} definitions for '{tab.SearchTerm}': {tab.SearchDone} of {tab.SearchTotal}");
                return;
            }
            if (_triggerPassRunning && tab == _flowTab) // the classic tab keeps its counts
            {
                SendStatus(Percent(_triggerDone, _triggerTotal), $"Reading flow triggers {_triggerDone} of {_triggerTotal}");
                return;
            }
            SendStatus(null, _listsLoaded ? Counts(tab) : "");
        }

        /// <summary>"12 of 42 flows · Solution: Contoso · 3 checked", or what a definition search found.</summary>
        private string Counts(Tab tab)
        {
            int total = tab.All.Count;
            var visible = tab.List.VisibleItems;
            string text;
            if (tab.SearchTerm != null)
                text = $"{visible.Count} {tab.Nouns(visible.Count)} {(visible.Count == 1 ? "contains" : "contain")} '{tab.SearchTerm}'";
            else if (visible.Count == total)
                text = $"{total} {tab.Nouns(total)}";
            else
                text = $"{visible.Count} of {total} {tab.Nouns(total)}";

            if (_selectedSolution != null) text += " · Solution: " + _selectedSolution;
            int checkedCount = tab.List.CheckedItems.Count;
            if (checkedCount > 0) text += $" · {checkedCount} checked";
            return text;
        }

        private static int Percent(int done, int total)
            => total <= 0 ? 0 : Math.Max(0, Math.Min(100, (int)(100L * done / total)));

        private void SendStatus(int? progress, string message)
        {
            if (progress.HasValue) progress = Math.Max(0, Math.Min(100, progress.Value)); // the host rejects anything else
            if (message == _statusMessage && progress == _statusProgress) return;
            _statusMessage = message;
            _statusProgress = progress;
            SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs(progress, message));
        }

        // ------------------------------------------------------------ helpers

        /// <summary>
        /// True once the tool has been closed. XrmToolBox still delivers a
        /// background job's result after that, and a disposed control cannot
        /// own a message box.
        /// </summary>
        private bool IsClosed => IsDisposed || Disposing;

        /// <summary>Runs on the UI thread, later; dropped once the tool is closed.</summary>
        private void PostToUi(Action action)
        {
            if (IsClosed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (!IsClosed) action();
                }));
            }
            catch (InvalidOperationException)
            {
                // The handle went away meanwhile: closed.
            }
        }

        private IEnumerable<Tab> Tabs
        {
            get
            {
                yield return _flowTab;
                yield return _workflowTab;
            }
        }

        private Tab CurrentTab() => _tabs.SelectedIndex == 1 ? _workflowTab : _flowTab;

        private void UpdateButtons()
        {
            if (_flowTab == null || _workflowTab == null) return; // still being built
            var tab = CurrentTab();

            bool loading = _loadingVersion != -1 && _loadingVersion == _connectionVersion;
            _loadButton.Text = _listsLoaded ? "Reload" : "Load processes";
            Available(_loadButton, !loading,
                _listsLoaded ? "Load the flows and classic workflows again from Dataverse (F5)"
                             : "Load the flows and classic workflows of this environment (F5)",
                "Loading the processes…");

            bool hasSolutions = _solutions != null && _solutions.Count > 0;
            string solutionReason = !_listsLoaded ? "Load processes to filter them by solution"
                : _solutionsError != null ? "The solutions could not be read: " + _solutionsError.Message
                : "This environment has no visible solutions";
            Available(_solutionBox, hasSolutions,
                "Show only the processes in this solution, on both tabs (type to search the names)", solutionReason);
            _solutionLabel.Enabled = hasSolutions;
            _solutionLabel.ToolTipText = _solutionBox.ToolTipText;

            var map = tab.Panel.Map;
            Available(_exportButton, map != null,
                map == null ? "" : "Save the map of '" + (map.Graph.Title ?? map.Source?.Name) + "' to a file, every step expanded",
                _listsLoaded ? "Select a process to export it" : "Load processes to export a map");

            bool isChecked;
            var scope = ExportAllScope(tab, out isChecked);
            string suffix = isChecked ? $" ({scope.Count} checked)…"
                : scope.Count == 1 ? " (1 in list)…" : $" (all {scope.Count} in list)…";
            _exportAllHtmlItem.Text = "HTML documentation pack" + suffix;
            _exportAllMarkdownItem.Text = "Markdown knowledge pack" + suffix;
            Available(_exportAllButton, scope.Count > 0,
                isChecked ? $"Export the {scope.Count} checked {tab.Nouns(scope.Count)} to a folder, one file each"
                          : (scope.Count == 1 ? $"Export the 1 {tab.Noun} in the list to a folder"
                                              : $"Export all {scope.Count} {tab.Nouns(scope.Count)} in the list to a folder, one file each") +
                            $" (check {tab.Noun}s to export only those)",
                !_listsLoaded ? "Load processes to export them" : "The list is empty: there is nothing to export");

            bool flows = tab.Kind == ProcessListKind.Flows;
            _openButton.Text = flows ? "Open in Power Automate" : "Open in workflow designer";
            var selected = tab.List.SelectedItem;
            string reason;
            var url = LinkFor(tab, selected, out reason);
            var name = "'" + (selected?.Name ?? "(unnamed)") + "'";
            Available(_openButton, url != null,
                flows ? "Open " + name + " in Power Automate in your browser"
                      : "Open " + name + " in the classic workflow designer in your browser",
                reason);
            _copyLinkItem.ToolTipText = url != null
                ? (flows ? "Copy the link to " + name + " in Power Automate" : "Copy the link to " + name + " in the workflow designer")
                : selected == null && _listsLoaded ? (flows ? "Select a flow to copy its link" : "Select a workflow to copy its link")
                : reason;
        }

        /// <summary>Enables or disables an item; a disabled item's tooltip says why.</summary>
        private static void Available(ToolStripItem item, bool enabled, string tip, string reason)
        {
            item.Enabled = enabled;
            item.ToolTipText = enabled ? tip : reason;
        }

        // ------------------------------------------------------------- icons

        private static Bitmap LoadLogo()
        {
            using (var stream = new MemoryStream(Convert.FromBase64String(ProcessMapperPlugin.SmallIconBase64)))
            using (var image = Image.FromStream(stream))
                return new Bitmap(image); // a copy: an Image keeps reading its stream
        }

        private static Icon IconOf(Bitmap bitmap)
        {
            var handle = bitmap.GetHicon();
            try
            {
                using (var icon = Icon.FromHandle(handle)) return (Icon)icon.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);
    }
}
