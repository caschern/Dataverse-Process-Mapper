using System;
using System.Drawing;
using System.Windows.Forms;
using DataverseProcessMapper.Models;

namespace DataverseProcessMapper.UI
{
    /// <summary>
    /// Right-hand panel showing the selected node's details (inputs,
    /// expressions, run-after). Long values word-wrap onto multiple lines and
    /// the pane widens itself (within limits) to fit the content.
    /// </summary>
    public class NodeDetailsPane : Panel
    {
        private const int MinWidth = 260;
        private const int MaxWidthCap = 520;

        private readonly Label _title;
        private readonly Label _subtitle;
        private readonly DataGridView _grid;
        private readonly Label _hint;
        private readonly Font _monoFont = new Font("Consolas", 8.25f);

        public NodeDetailsPane()
        {
            BackColor = Color.White;
            Padding = new Padding(8, 8, 8, 8);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowUserToOrderColumns = false,
                RowHeadersVisible = false,
                BorderStyle = BorderStyle.None,
                BackgroundColor = Color.White,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                GridColor = Color.FromArgb(233, 235, 238),
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
                ScrollBars = ScrollBars.Vertical,
                EnableHeadersVisualStyles = true
            };

            var colProp = new DataGridViewTextBoxColumn
            {
                HeaderText = "Property",
                Width = 92,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            colProp.DefaultCellStyle.ForeColor = Color.DimGray;
            colProp.DefaultCellStyle.WrapMode = DataGridViewTriState.True;

            var colValue = new DataGridViewTextBoxColumn
            {
                HeaderText = "Value",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            colValue.DefaultCellStyle.WrapMode = DataGridViewTriState.True;

            _grid.Columns.Add(colProp);
            _grid.Columns.Add(colValue);

            _hint = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Select a step in the diagram\nto see its details.",
                ForeColor = Color.Gray,
                TextAlign = ContentAlignment.MiddleCenter
            };

            _subtitle = new Label
            {
                Dock = DockStyle.Top,
                ForeColor = Color.Gray,
                AutoEllipsis = true,
                Height = 18
            };

            _title = new Label
            {
                Dock = DockStyle.Top,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                AutoEllipsis = true,
                Height = 22
            };

            Controls.Add(_grid);
            Controls.Add(_hint);
            Controls.Add(_subtitle);
            Controls.Add(_title);

            SetNode(null);
        }

        public void SetNode(ProcessNode node)
        {
            if (node == null)
            {
                _title.Text = "Details";
                _subtitle.Text = "";
                _grid.Rows.Clear();
                _grid.Visible = false;
                _hint.Visible = true;
                return;
            }

            _title.Text = node.Label?.Replace("\n", " ") ?? "";
            _subtitle.Text = string.IsNullOrEmpty(node.Subtitle) ? node.Kind.ToString() : node.Subtitle;

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            if (node.Details != null)
            {
                foreach (var kv in node.Details)
                {
                    int row = _grid.Rows.Add(kv.Key, kv.Value ?? "");
                    // Pretty-printed XML (multi-line) reads better in monospace.
                    if (kv.Value != null && kv.Value.IndexOf('\n') >= 0)
                        _grid.Rows[row].Cells[1].Style.Font = _monoFont;
                }
            }
            if (_grid.Rows.Count == 0)
            {
                _grid.Rows.Add("No details", "This step has no recorded inputs.");
            }
            _grid.ResumeLayout();
            _grid.ClearSelection();

            _hint.Visible = false;
            _grid.Visible = true;

            AutoFitWidth(node);
        }

        /// <summary>
        /// Sizes the property column to the longest key and widens the pane to
        /// fit the longest value line (so most values need no wrapping), bounded
        /// by MinWidth and roughly half the parent's width.
        /// </summary>
        private void AutoFitWidth(ProcessNode node)
        {
            if (node?.Details == null || Parent == null) return;

            int longestKey = 0;
            int longestValue = 0;
            foreach (var kv in node.Details)
            {
                if (!string.IsNullOrEmpty(kv.Key))
                {
                    int kw = TextRenderer.MeasureText(kv.Key, _grid.Font).Width;
                    if (kw > longestKey) longestKey = kw;
                }
                if (!string.IsNullOrEmpty(kv.Value))
                {
                    // Multi-line values (pretty-printed XML): the widest LINE is
                    // what matters, not the whole string laid out end to end.
                    bool multiline = kv.Value.IndexOf('\n') >= 0;
                    var font = multiline ? _monoFont : _grid.Font;
                    foreach (var line in kv.Value.Split('\n'))
                    {
                        int vw = TextRenderer.MeasureText(line.TrimEnd('\r'), font).Width;
                        if (vw > longestValue) longestValue = vw;
                    }
                }
            }

            // Property column: fit the longest key, capped at 210px (wraps beyond).
            int propWidth = Math.Max(70, Math.Min(longestKey + 14, 210));
            _grid.Columns[0].Width = propWidth;

            int chrome = Padding.Horizontal + propWidth +
                         SystemInformation.VerticalScrollBarWidth + 16;
            int desired = longestValue + chrome;

            int maxAllowed = Math.Min(MaxWidthCap, Parent.ClientSize.Width / 2);
            Width = Math.Max(MinWidth, Math.Min(desired, maxAllowed));
        }
    }
}
