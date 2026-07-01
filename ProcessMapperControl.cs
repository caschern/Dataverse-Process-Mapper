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

namespace DataverseProcessMapper
{
    public partial class ProcessMapperControl : PluginControlBase
    {
        private ToolStripButton _loadButton;
        private ToolStripButton _pdfButton;
        private ToolStripButton _htmlButton;
        private ToolStripButton _svgButton;
        private ToolStripButton _fitButton;
        private ToolStripLabel _status;

        private TabControl _tabs;
        private ListView _flowList;
        private ListView _workflowList;
        private TextBox _flowSearch;
        private TextBox _workflowSearch;
        private DiagramPanel _flowPanel;
        private DiagramPanel _workflowPanel;

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

            _fitButton = new ToolStripButton("Zoom to Fit")
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                Enabled = false
            };
            _fitButton.Click += (s, e) => CurrentPanel()?.ZoomToFit();

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
                _pdfButton, _htmlButton, _svgButton, new ToolStripSeparator(),
                _fitButton, new ToolStripSeparator(),
                _status, closeButton
            });

            _tabs = new TabControl { Dock = DockStyle.Fill };

            var flowTab = new TabPage("Power Automate Flows");
            _flowList = CreateList(flowColumns: true);
            _flowSearch = CreateSearchBox();
            _flowPanel = new DiagramPanel { Dock = DockStyle.Fill };
            flowTab.Controls.Add(CreateSplit(WrapWithSearch(_flowSearch, _flowList), _flowPanel));

            var wfTab = new TabPage("Classic Workflows");
            _workflowList = CreateList(flowColumns: false);
            _workflowSearch = CreateSearchBox();
            _workflowPanel = new DiagramPanel { Dock = DockStyle.Fill };
            wfTab.Controls.Add(CreateSplit(WrapWithSearch(_workflowSearch, _workflowList), _workflowPanel));

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

        private enum ExportFormat { Pdf, Html, Svg }

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
            _fitButton.Enabled = hasMap;
        }

        private static string MakeSafeFileName(string name)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Length > 80 ? name.Substring(0, 80) : name;
        }
    }
}
