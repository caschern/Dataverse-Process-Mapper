using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DataverseProcessMapper.Data;
using DataverseProcessMapper.Exporters;
using DataverseProcessMapper.Models;
using DataverseProcessMapper.UI;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;

namespace DataverseProcessMapper
{
    public partial class ProcessMapperControl : PluginControlBase, IMessageBusHost
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

        private ToolStripButton _loadButton;
        private ToolStripButton _pdfButton;
        private ToolStripButton _htmlButton;
        private ToolStripButton _svgButton;
        private ToolStripButton _pngButton;
        private ToolStripButton _markdownButton;
        private ToolStripButton _exportAllButton;
        private ToolStripButton _exportAllMarkdownButton;
        private ToolStripButton _fitButton;
        private ToolStripTextBox _findBox;
        private ToolStripLabel _status;

        private TabControl _tabs;
        private ListView _flowList;
        private ListView _workflowList;
        private TextBox _flowSearch;
        private TextBox _workflowSearch;
        private DiagramPanel _flowPanel;
        private DiagramPanel _workflowPanel;
        private NodeDetailsPane _flowDetails;
        private NodeDetailsPane _workflowDetails;
        private SplitContainer _flowSplit;
        private SplitContainer _workflowSplit;

        // Master (unfiltered) lists backing the search boxes.
        private List<ProcessItem> _allFlows = new List<ProcessItem>();
        private List<ProcessItem> _allWorkflows = new List<ProcessItem>();

        public ProcessMapperControl()
        {
            BuildUi();
        }

        // ---------------------------------------------------------------- UI

        private void BuildUi()
        {
            Dock = DockStyle.Fill;

            var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };

            _loadButton = new ToolStripButton("Load Processes")
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text
            };
            _loadButton.Click += (s, e) => ExecuteMethod(LoadProcesses);

            _pdfButton = new ToolStripButton("Generate PDF")
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                Enabled = false
            };
            _pdfButton.Click += (s, e) => Export(ExportFormat.Pdf);

            _htmlButton = new ToolStripButton("Generate HTML")
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                Enabled = false
            };
            _htmlButton.Click += (s, e) => Export(ExportFormat.Html);

            _svgButton = new ToolStripButton("Generate SVG")
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                Enabled = false,
                ToolTipText = "Vector image for Visio, Mural, Lucidchart, draw.io…"
            };
            _svgButton.Click += (s, e) => Export(ExportFormat.Svg);

            _pngButton = new ToolStripButton("Generate PNG")
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                Enabled = false,
                ToolTipText = "High-resolution image for chat, email and slides"
            };
            _pngButton.Click += (s, e) => Export(ExportFormat.Png);

            _markdownButton = new ToolStripButton("Generate Markdown")
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                Enabled = false,
                ToolTipText = "Structured text for AI knowledge bases (Copilot Studio, RAG) — " +
                              "states what each step runs after and branches into, which a diagram cannot"
            };
            _markdownButton.Click += (s, e) => Export(ExportFormat.Markdown);

            _exportAllButton = new ToolStripButton("Export All HTML")
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                Enabled = false,
                ToolTipText = "Export every process in the current list to a folder with an index page"
            };
            _exportAllButton.Click += (s, e) => ExportAll(bulkMarkdown: false);

            _exportAllMarkdownButton = new ToolStripButton("Export All Markdown")
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                Enabled = false,
                ToolTipText = "Export every process in the current list as Markdown, " +
                              "ready to upload as an AI knowledge source"
            };
            _exportAllMarkdownButton.Click += (s, e) => ExportAll(bulkMarkdown: true);

            _fitButton = new ToolStripButton("Zoom to Fit")
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                Enabled = false
            };
            _fitButton.Click += (s, e) => CurrentPanel()?.ZoomToFit();

            _findBox = new ToolStripTextBox { Width = 170, ToolTipText = "Find a step by name (Enter = next match)" };
            _findBox.TextBox.HandleCreated += (s, e) =>
                SendMessage(_findBox.TextBox.Handle, EM_SETCUEBANNER, (IntPtr)1, "Find step…");
            _findBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    if (CurrentPanel()?.FindNext(_findBox.Text) != true)
                        System.Media.SystemSounds.Asterisk.Play(); // no match
                }
            };

            var closeButton = new ToolStripButton("Close")
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                Alignment = ToolStripItemAlignment.Right
            };
            closeButton.Click += (s, e) => CloseTool();

            _status = new ToolStripLabel("Not loaded") { ForeColor = Color.Gray };

            toolbar.Items.AddRange(new ToolStripItem[]
            {
                _loadButton, new ToolStripSeparator(),
                _pdfButton, _htmlButton, _svgButton, _pngButton, _markdownButton, new ToolStripSeparator(),
                _exportAllButton, _exportAllMarkdownButton, new ToolStripSeparator(),
                _fitButton, _findBox, new ToolStripSeparator(),
                _status, closeButton
            });

            _tabs = new TabControl { Dock = DockStyle.Fill };

            var flowTab = new TabPage("Power Automate Flows");
            _flowList = CreateList(flowColumns: true);
            _flowSearch = CreateSearchBox();
            _flowPanel = new DiagramPanel { Dock = DockStyle.Fill };
            _flowDetails = new NodeDetailsPane();
            _flowSplit = CreateSplit(WrapWithSearch(_flowSearch, _flowList),
                WrapWithDetails(_flowPanel, _flowDetails));
            flowTab.Controls.Add(_flowSplit);

            var wfTab = new TabPage("Classic Workflows");
            _workflowList = CreateList(flowColumns: false);
            _workflowSearch = CreateSearchBox();
            _workflowPanel = new DiagramPanel { Dock = DockStyle.Fill };
            _workflowDetails = new NodeDetailsPane();
            _workflowSplit = CreateSplit(WrapWithSearch(_workflowSearch, _workflowList),
                WrapWithDetails(_workflowPanel, _workflowDetails));
            wfTab.Controls.Add(_workflowSplit);

            _flowPanel.NodeSelected += n => _flowDetails.SetNode(n);
            _workflowPanel.NodeSelected += n => _workflowDetails.SetNode(n);
            _flowDetails.FetchXmlRequested += OpenInFetchXmlBuilder;
            _workflowDetails.FetchXmlRequested += OpenInFetchXmlBuilder;

            // Full screen: hide the process list so the map + details own the width.
            _flowPanel.FullScreenChanged += full => _flowSplit.Panel1Collapsed = full;
            _workflowPanel.FullScreenChanged += full => _workflowSplit.Panel1Collapsed = full;

            _flowList.SelectedIndexChanged += (s, e) => PreviewSelection(_flowList, _flowPanel);
            _workflowList.SelectedIndexChanged += (s, e) => PreviewSelection(_workflowList, _workflowPanel);
            _flowSearch.TextChanged += (s, e) => ApplyFilter(_flowList, _allFlows, _flowSearch, flowColumns: true);
            _workflowSearch.TextChanged += (s, e) => ApplyFilter(_workflowList, _allWorkflows, _workflowSearch, flowColumns: false);
            _tabs.SelectedIndexChanged += (s, e) => UpdateButtons();

            _tabs.TabPages.Add(flowTab);
            _tabs.TabPages.Add(wfTab);

            Controls.Add(_tabs);
            Controls.Add(toolbar);
        }

        private static TextBox CreateSearchBox()
        {
            var box = new TextBox { Dock = DockStyle.Top };
            // Native cue banner ("watermark") text; shown while the box is empty.
            box.HandleCreated += (s, e) =>
                SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, "Filter by name…");
            box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    box.Clear();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            return box;
        }

        private static Control WrapWithDetails(DiagramPanel diagram, NodeDetailsPane details)
        {
            var host = new Panel { Dock = DockStyle.Fill };
            details.Dock = DockStyle.Right;
            details.Width = 260;
            var splitter = new Splitter { Dock = DockStyle.Right, Width = 5 };

            // The button lives in a NON-scrolling wrapper around the diagram, so
            // it stays pinned to the top-right corner while the map scrolls.
            var mapArea = new Panel { Dock = DockStyle.Fill };

            Button OverlayButton(string text)
            {
                var b = new Button
                {
                    Text = text,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.White,
                    UseVisualStyleBackColor = false,
                    TabStop = false
                };
                b.FlatAppearance.BorderColor = Color.FromArgb(200, 205, 212);
                return b;
            }

            var fullScreenButton = OverlayButton("Full screen");
            fullScreenButton.Click += (s, e) => diagram.ToggleFullScreen();
            diagram.FullScreenChanged += full =>
                fullScreenButton.Text = full ? "Exit full screen" : "Full screen";

            var zoomInButton = OverlayButton("+");
            zoomInButton.Click += (s, e) => diagram.ZoomStep(1.2f);
            var zoomOutButton = OverlayButton("−");
            zoomOutButton.Click += (s, e) => diagram.ZoomStep(1f / 1.2f);

            var expandAllButton = OverlayButton("Expand all");
            expandAllButton.Click += (s, e) => diagram.SetAllCollapsed(false);
            var collapseAllButton = OverlayButton("Collapse all");
            collapseAllButton.Click += (s, e) => diagram.SetAllCollapsed(true);

            void PositionButtons()
            {
                int right = mapArea.ClientSize.Width - 8 - SystemInformation.VerticalScrollBarWidth;
                fullScreenButton.Location = new Point(right - fullScreenButton.Width, 8);
                zoomInButton.Location = new Point(fullScreenButton.Left - zoomInButton.Width - 6, 8);
                zoomOutButton.Location = new Point(zoomInButton.Left - zoomOutButton.Width - 2, 8);
                expandAllButton.Location = new Point(zoomOutButton.Left - expandAllButton.Width - 6, 8);
                collapseAllButton.Location = new Point(expandAllButton.Left - collapseAllButton.Width - 2, 8);
            }
            mapArea.Resize += (s, e) => PositionButtons();
            fullScreenButton.SizeChanged += (s, e) => PositionButtons();

            mapArea.Controls.Add(fullScreenButton);
            mapArea.Controls.Add(zoomInButton);
            mapArea.Controls.Add(zoomOutButton);
            mapArea.Controls.Add(expandAllButton);
            mapArea.Controls.Add(collapseAllButton);
            mapArea.Controls.Add(diagram);
            fullScreenButton.BringToFront();
            zoomInButton.BringToFront();
            zoomOutButton.BringToFront();
            expandAllButton.BringToFront();
            collapseAllButton.BringToFront();
            PositionButtons();

            host.Controls.Add(mapArea);
            host.Controls.Add(splitter);
            host.Controls.Add(details);
            return host;
        }

        private static Control WrapWithSearch(TextBox search, ListView list)
        {
            var host = new Panel { Dock = DockStyle.Fill };
            host.Controls.Add(list);
            host.Controls.Add(search);
            list.BringToFront(); // Fill occupies the space under the Top-docked box
            return host;
        }

        private const int EM_SETCUEBANNER = 0x1501;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private static ListView CreateList(bool flowColumns)
        {
            var list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                GridLines = false
            };
            list.Columns.Add("Name", 260);
            list.Columns.Add("Status", 75);
            if (flowColumns)
            {
                // Flow-relevant metadata; flows rarely have a meaningful primary table.
                list.Columns.Add("Created On", 100);
                list.Columns.Add("Created By", 130);
                list.Columns.Add("Modified By", 130);
                list.Columns.Add("Owner", 130);
                list.Columns.Add("Scope", 100);
            }
            else
            {
                list.Columns.Add("Table", 140);
            }
            return list;
        }

        private static SplitContainer CreateSplit(Control left, Control right)
        {
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 320
            };
            split.Panel1.Controls.Add(left);
            split.Panel2.Controls.Add(right);
            return split;
        }

        // ----------------------------------------------------------- loading

        private void LoadProcesses()
        {
            WorkAsync(new WorkAsyncInfo
            {
                Message = "Loading workflows and flows from Dataverse...",
                Work = (worker, args) =>
                {
                    args.Result = WorkflowRepository.RetrieveProcesses(Service);
                },
                PostWorkCallBack = args =>
                {
                    if (args.Error != null)
                    {
                        MessageBox.Show(this, args.Error.Message, "Load failed",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    var items = (List<ProcessItem>)args.Result;
                    PopulateLists(items);
                }
            });
        }

        private void PopulateLists(List<ProcessItem> items)
        {
            _allFlows = items.Where(i => i.Category == WorkflowRepository.CategoryModernFlow)
                             .OrderBy(i => i.Name).ToList();
            _allWorkflows = items.Where(i => i.Category == WorkflowRepository.CategoryClassicWorkflow)
                                 .OrderBy(i => i.Name).ToList();

            Fill(_flowList, Filter(_allFlows, _flowSearch.Text), flowColumns: true);
            Fill(_workflowList, Filter(_allWorkflows, _workflowSearch.Text), flowColumns: false);

            _status.ForeColor = Color.DimGray;
            UpdateStatus();
            UpdateButtons();
        }

        private void ApplyFilter(ListView list, List<ProcessItem> all, TextBox search, bool flowColumns)
        {
            Fill(list, Filter(all, search.Text), flowColumns);
            UpdateStatus();
            UpdateButtons();
        }

        private static List<ProcessItem> Filter(List<ProcessItem> items, string term)
        {
            if (string.IsNullOrWhiteSpace(term)) return items;
            term = term.Trim();
            return items
                .Where(i => (i.Name ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }

        private void UpdateStatus()
        {
            if (_allFlows.Count == 0 && _allWorkflows.Count == 0) return; // nothing loaded yet

            string flows = _flowList.Items.Count == _allFlows.Count
                ? $"{_allFlows.Count} flows"
                : $"{_flowList.Items.Count} of {_allFlows.Count} flows";
            string workflows = _workflowList.Items.Count == _allWorkflows.Count
                ? $"{_allWorkflows.Count} workflows"
                : $"{_workflowList.Items.Count} of {_allWorkflows.Count} workflows";
            _status.Text = flows + " · " + workflows;
        }

        private static void Fill(ListView list, List<ProcessItem> items, bool flowColumns)
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var item in items)
            {
                var lvi = new ListViewItem(item.Name ?? "(unnamed)") { Tag = item };
                lvi.SubItems.Add(item.StateLabel);
                if (flowColumns)
                {
                    lvi.SubItems.Add(item.CreatedOn?.ToLocalTime().ToString("yyyy-MM-dd") ?? "");
                    lvi.SubItems.Add(item.CreatedBy ?? "");
                    lvi.SubItems.Add(item.ModifiedBy ?? "");
                    lvi.SubItems.Add(item.Owner ?? "");
                    lvi.SubItems.Add(item.Scope ?? "");
                }
                else
                {
                    lvi.SubItems.Add(item.PrimaryEntity ?? "");
                }
                list.Items.Add(lvi);
            }
            list.EndUpdate();
        }

        // -------------------------------------------------------- previewing

        private void PreviewSelection(ListView list, DiagramPanel panel)
        {
            var item = SelectedItem(list);
            if (item == null) { UpdateButtons(); return; }

            try
            {
                Cursor = Cursors.WaitCursor;
                var map = ProcessMapBuilder.Build(item);
                panel.SetMap(map);
                panel.ZoomToFit();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not render this process:\n\n" + ex.Message,
                    "Render error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                Cursor = Cursors.Default;
                UpdateButtons();
            }
        }

        // ---------------------------------------------------------- exporting

        private enum ExportFormat { Pdf, Html, Svg, Png, Markdown }

        private void Export(ExportFormat format)
        {
            var panel = CurrentPanel();
            var map = panel?.Map;
            if (map == null)
            {
                MessageBox.Show(this, "Select a process to export first.", "Nothing selected",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dialog = new SaveFileDialog())
            {
                var safeName = MakeSafeFileName(map.Graph.Title ?? "process-map");
                switch (format)
                {
                    case ExportFormat.Pdf:
                        dialog.Filter = "PDF document (*.pdf)|*.pdf";
                        dialog.FileName = safeName + ".pdf";
                        break;
                    case ExportFormat.Svg:
                        dialog.Filter = "SVG image (*.svg)|*.svg";
                        dialog.FileName = safeName + ".svg";
                        break;
                    case ExportFormat.Png:
                        dialog.Filter = "PNG image (*.png)|*.png";
                        dialog.FileName = safeName + ".png";
                        break;
                    case ExportFormat.Markdown:
                        dialog.Filter = "Markdown document (*.md)|*.md";
                        dialog.FileName = safeName + ".md";
                        break;
                    default:
                        dialog.Filter = "HTML document (*.html)|*.html";
                        dialog.FileName = safeName + ".html";
                        break;
                }

                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    Cursor = Cursors.WaitCursor;
                    switch (format)
                    {
                        case ExportFormat.Pdf:
                            PdfExporter.Save(map, dialog.FileName);
                            break;
                        case ExportFormat.Svg:
                            SvgExporter.Save(map, dialog.FileName);
                            break;
                        case ExportFormat.Png:
                            using (var bmp = ProcessMapBuilder.RenderToBitmap(map, 2f))
                                bmp.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Png);
                            break;
                        case ExportFormat.Markdown:
                            MarkdownExporter.Save(map, dialog.FileName);
                            break;
                        default:
                            HtmlExporter.Save(map, dialog.FileName);
                            break;
                    }

                    if (MessageBox.Show(this, "Export complete. Open the file now?", "Done",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    {
                        System.Diagnostics.Process.Start(dialog.FileName);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Export failed:\n\n" + ex.Message, "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    Cursor = Cursors.Default;
                }
            }
        }

        /// <summary>
        /// Exports every process in the current tab's (filtered) list to one
        /// HTML file each, plus an index.html linking them.
        /// </summary>
        private void ExportAll(bool bulkMarkdown)
        {
            var kind = bulkMarkdown ? "Markdown" : "HTML";
            var items = CurrentList().Items.Cast<ListViewItem>()
                .Select(l => l.Tag as ProcessItem)
                .Where(p => p != null)
                .ToList();
            if (items.Count == 0)
            {
                MessageBox.Show(this, "There are no processes in the current list to export.",
                    "Nothing to export", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // The pack is one file per process plus the index; warn before doing
            // the work rather than after, since the limit is per Copilot agent.
            if (bulkMarkdown && items.Count + 1 > BulkMarkdownExporter.KnowledgeFileLimit)
            {
                var proceed = MessageBox.Show(this,
                    $"This will produce {items.Count + 1} files, more than the " +
                    $"{BulkMarkdownExporter.KnowledgeFileLimit} a single Copilot Studio agent accepts " +
                    "as knowledge.\n\nYou can still export and split the pack, or filter the list first." +
                    "\n\nExport anyway?",
                    "More files than one agent accepts",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (proceed != DialogResult.Yes) return;
            }

            string folder;
            using (var dlg = new FolderBrowserDialog
            {
                Description = $"Choose a folder for the {items.Count} exported {kind} files"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                folder = dlg.SelectedPath;
            }

            var setLabel = _tabs.SelectedIndex == 0 ? "Power Automate Flows" : "Classic Workflows";

            WorkAsync(new WorkAsyncInfo
            {
                Message = $"Exporting {items.Count} processes to {kind}...",
                Work = (worker, args) =>
                {
                    Action<string> report = s => worker.ReportProgress(0, s);
                    args.Result = bulkMarkdown
                        ? BulkMarkdownExporter.Export(items, folder, setLabel, report)
                        : BulkHtmlExporter.Export(items, folder, setLabel, report);
                },
                ProgressChanged = args => SetWorkingMessage(args.UserState?.ToString()),
                PostWorkCallBack = args =>
                {
                    if (args.Error != null)
                    {
                        MessageBox.Show(this, "Export failed:\n\n" + args.Error.Message, "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    var result = (BulkExportResult)args.Result;
                    var text = $"Exported {result.Exported} of {items.Count} processes.";
                    if (result.Failures.Count > 0)
                        text += $"\n{result.Failures.Count} failed (listed at the bottom of the index page).";
                    text += "\n\nOpen the index now?";

                    if (MessageBox.Show(this, text, "Export complete",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    {
                        System.Diagnostics.Process.Start(result.IndexPath);
                    }
                }
            });
        }

        // ------------------------------------------------------------ helpers

        private DiagramPanel CurrentPanel()
            => _tabs.SelectedIndex == 0 ? _flowPanel : _workflowPanel;

        private ListView CurrentList()
            => _tabs.SelectedIndex == 0 ? _flowList : _workflowList;

        private static ProcessItem SelectedItem(ListView list)
            => list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as ProcessItem : null;

        private void UpdateButtons()
        {
            bool hasMap = CurrentPanel()?.Map != null;
            _pdfButton.Enabled = hasMap;
            _htmlButton.Enabled = hasMap;
            _svgButton.Enabled = hasMap;
            _pngButton.Enabled = hasMap;
            _markdownButton.Enabled = hasMap;
            _fitButton.Enabled = hasMap;
            bool hasList = CurrentList()?.Items.Count > 0;
            _exportAllButton.Enabled = hasList;
            _exportAllMarkdownButton.Enabled = hasList;
        }

        private static string MakeSafeFileName(string name)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Length > 80 ? name.Substring(0, 80) : name;
        }
    }
}
