using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using DataverseProcessMapper.Models;
using DataverseProcessMapper.Rendering;

namespace DataverseProcessMapper.Layout
{
    /// <summary>
    /// Word-wraps each node's label and computes its box size using an offscreen
    /// GDI+ graphics context. Sizes are device-independent and reused for the
    /// on-screen, PDF and HTML renderings.
    /// </summary>
    public static class NodeSizer
    {
        public static void MeasureAll(ProcessGraph graph)
        {
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                foreach (var node in graph.Nodes)
                    Measure(g, node);
            }
        }

        private static void Measure(Graphics g, ProcessNode node)
        {
            var labelFont = DiagramStyle.LabelFont;
            bool diamond = node.Shape == NodeShape.Diamond;

            // Diamonds fit less text horizontally, so wrap to a narrower width.
            float wrapWidth = diamond ? DiagramStyle.NodeMaxWidth * 0.62f : DiagramStyle.NodeMaxWidth;

            node.Lines = WrapText(g, node.Label, labelFont, wrapWidth);

            float textWidth = 0f;
            foreach (var line in node.Lines)
            {
                var size = MeasureString(g, line, labelFont);
                if (size.Width > textWidth) textWidth = size.Width;
            }

            if (!string.IsNullOrEmpty(node.Subtitle))
            {
                var subSize = MeasureString(g, node.Subtitle, DiagramStyle.SubtitleFont);
                if (subSize.Width > textWidth) textWidth = subSize.Width;
            }

            float width = textWidth + 2 * DiagramStyle.NodePadX;
            float lineCount = node.Lines.Count + (string.IsNullOrEmpty(node.Subtitle) ? 0 : 1);
            float height = lineCount * DiagramStyle.LineHeight + 2 * DiagramStyle.NodePadY;

            if (diamond)
            {
                // A diamond needs ~1.8x the bounding box to keep text inside the rhombus.
                width *= 1.7f;
                height *= 1.7f;
            }

            width = Clamp(width, DiagramStyle.NodeMinWidth, diamond ? DiagramStyle.NodeMaxWidth * 1.6f : DiagramStyle.NodeMaxWidth);
            height = System.Math.Max(height, 38f);

            node.Bounds = new RectangleF(0, 0, width, height);
        }

        private static List<string> WrapText(Graphics g, string text, DiagramFont font, float maxWidth)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) { lines.Add(""); return lines; }

            foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
            {
                var words = rawLine.Split(' ');
                var current = "";
                foreach (var word in words)
                {
                    var candidate = current.Length == 0 ? word : current + " " + word;
                    if (MeasureString(g, candidate, font).Width > maxWidth && current.Length > 0)
                    {
                        lines.Add(current);
                        current = word;
                    }
                    else
                    {
                        current = candidate;
                    }
                }
                lines.Add(current);
            }

            // Cap at 6 lines to keep boxes sane.
            if (lines.Count > 6)
            {
                lines = lines.GetRange(0, 6);
                lines[5] = Truncate(lines[5]) + "…";
            }
            return lines;
        }

        private static string Truncate(string s) => s.Length > 24 ? s.Substring(0, 24) : s;

        private static SizeF MeasureString(Graphics g, string s, DiagramFont font)
        {
            using (var f = new Font(font.Family, font.Size, font.Bold ? FontStyle.Bold : FontStyle.Regular))
            {
                return g.MeasureString(s, f, 10000, StringFormat.GenericTypographic);
            }
        }

        private static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
    }
}
