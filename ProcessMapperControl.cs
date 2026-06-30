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
        private ToolStripButton _fitButton;
        private ToolStripLabel _status;

        private TabControl _tabs;
        private ListView _flowList;
        private ListView _workflowList;
        private DiagramPanel _flowPanel;
        private DiagramPanel _workflowPanel;

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
                _pdfButton, _htmlButton, new ToolStripSeparator(),
                _fitButton, new ToolStripSeparator(),
                _status, closeButton
            });

            _tabs = new TabControl { Dock = DockStyle.Fill };

            var flowTab = new TabPage("Power Automate Flows");
            _flowList = CreateList();
            _flowPanel = new DiagramPanel { Dock = DockStyle.Fill };
            flowTab.Controls.Add(CreateSplit(_flowList, _flowPanel));

            var wfTab = new TabPage("Classic Workflows");
            _workflowList = CreateList();
            _workflowPanel = new DiagramPanel { Dock = DockStyle.Fill };
            wfTab.Controls.Add(CreateSplit(_workflowList, _workflowPanel));

            _flowList.SelectedIndexChanged += (s, e) => PreviewSelection(_flowList, _flowPanel);
            _workflowList.SelectedIndexChanged += (s, e) => PreviewSelection(_workflowList, _workflowPanel);
            _tabs.SelectedIndexChanged += (s, e) => UpdateButtons();

            _tabs.TabPages.Add(flowTab);
            _tabs.TabPages.Add(wfTab);

            Controls.Add(_tabs);
            Controls.Add(toolbar);
        }

        private static ListView CreateList()
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
            list.Columns.Add("Name", 280);
            list.Columns.Add("Status", 80);
            list.Columns.Add("Table", 140);
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
            var flows = items.Where(i => i.Category == WorkflowRepository.CategoryModernFlow)
                             .OrderBy(i => i.Name).ToList();
            var workflows = items.Where(i => i.Category == WorkflowRepository.CategoryClassicWorkflow)
                                 .OrderBy(i => i.Name).ToList();

            Fill(_flowList, flows);
            Fill(_workflowList, workflows);

            _status.Text = $"{flows.Count} flows · {workflows.Count} workflows";
            _status.ForeColor = Color.DimGray;
            UpdateButtons();
        }

        private static void Fill(ListView list, List<ProcessItem> items)
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var item in items)
            {
                var lvi = new ListViewItem(item.Name ?? "(unnamed)") { Tag = item };
                lvi.SubItems.Add(item.StateLabel);
                lvi.SubItems.Add(item.PrimaryEntity ?? "");
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

        private enum ExportFormat { Pdf, Html }

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
                if (format == ExportFormat.Pdf)
                {
                    dialog.Filter = "PDF document (*.pdf)|*.pdf";
                    dialog.FileName = safeName + ".pdf";
                }
                else
                {
                    dialog.Filter = "HTML document (*.html)|*.html";
                    dialog.FileName = safeName + ".html";
                }

                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    Cursor = Cursors.WaitCursor;
                    if (format == ExportFormat.Pdf)
                        PdfExporter.Save(map, dialog.FileName);
                    else
                        HtmlExporter.Save(map, dialog.FileName);

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
