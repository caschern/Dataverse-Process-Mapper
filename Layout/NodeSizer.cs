using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using DataverseProcessMapper.Models;
using DataverseProcessMapper.Parsing;
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
            using (var m = new TextMeasurer())
            {
                foreach (var node in graph.Nodes)
                    Measure(m, node);
            }
        }

        private static void Measure(TextMeasurer m, ProcessNode node)
        {
            var labelFont = DiagramStyle.LabelFont;
            bool diamond = node.Shape == NodeShape.Diamond;

            // Wrap to the width the finished box can actually hold. A plain box is
            // clamped to NodeMaxWidth after padding is added, so the text has to
            // leave room for that padding or it renders outside the shape. The
            // diamond path scales its box up by 1.7 afterwards, so 0.62 already
            // leaves it head-room.
            float wrapWidth = diamond
                ? DiagramStyle.NodeMaxWidth * 0.62f
                : DiagramStyle.NodeMaxWidth - 2 * DiagramStyle.NodePadX;

            node.Lines = WrapText(m, node.Label, labelFont, wrapWidth);

            float textWidth = 0f;
            foreach (var line in node.Lines)
            {
                var size = m.Measure(line, labelFont);
                if (size.Width > textWidth) textWidth = size.Width;
            }

            var subtitle = node.DisplaySubtitle;
            if (!string.IsNullOrEmpty(subtitle))
            {
                var subSize = m.Measure(subtitle, DiagramStyle.SubtitleFont);
                if (subSize.Width > textWidth) textWidth = subSize.Width;
            }

            float width = textWidth + 2 * DiagramStyle.NodePadX;
            float lineCount = node.Lines.Count + (string.IsNullOrEmpty(subtitle) ? 0 : 1);
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

        private static List<string> WrapText(TextMeasurer m, string text, DiagramFont font, float maxWidth)
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
                    if (m.Measure(candidate, font).Width > maxWidth && current.Length > 0)
                    {
                        lines.Add(current);
                        current = word;
                    }
                    else
                    {
                        current = candidate;
                    }

                    // A single token with nowhere to break — a GUID case value, a
                    // URL, a spaceless expression — still overflows the line above.
                    // Split it by character so it stays inside the shape.
                    while (m.Measure(current, font).Width > maxWidth)
                    {
                        var head = LongestPrefixThatFits(m, current, font, maxWidth);
                        if (head.Length == 0 || head.Length == current.Length) break;
                        lines.Add(head);
                        current = current.Substring(head.Length);
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

        /// <summary>Longest leading run of <paramref name="s"/> that fits the width (at least one character).</summary>
        private static string LongestPrefixThatFits(TextMeasurer m, string s, DiagramFont font, float maxWidth)
        {
            // Binary search: a prefix only gets wider as it grows.
            int lo = 1, hi = s.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (m.Measure(s.Substring(0, mid), font).Width > maxWidth) hi = mid - 1;
                else lo = mid;
            }
            return s.Substring(0, lo);
        }

        private static string Truncate(string s) => DetailText.Head(s, 24);

        private static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);

        private static readonly object LabelLock = new object();
        private static readonly Dictionary<string, float> LabelWidths = new Dictionary<string, float>();

        /// <summary>
        /// Width of an edge label's text in the font the renderer draws it with.
        /// Cached: a process uses a handful of distinct labels ("Yes", "No",
        /// "always", "Failed"…). Locked because maps are built on background threads.
        /// </summary>
        public static float EdgeLabelWidth(string label)
        {
            if (string.IsNullOrEmpty(label)) return 0f;
            lock (LabelLock)
            {
                if (LabelWidths.TryGetValue(label, out var width)) return width;
                using (var m = new TextMeasurer())
                    width = m.Measure(label, DiagramStyle.EdgeLabelFont).Width;
                LabelWidths[label] = width;
                return width;
            }
        }

        /// <summary>
        /// An offscreen measuring context that creates each font once per pass
        /// rather than once per measurement — wrapping measures the same few
        /// fonts hundreds of times on a large map.
        /// </summary>
        private sealed class TextMeasurer : IDisposable
        {
            private readonly Bitmap _bmp = new Bitmap(1, 1);
            private readonly Graphics _g;
            private readonly Dictionary<string, Font> _fonts = new Dictionary<string, Font>();

            public TextMeasurer()
            {
                _g = Graphics.FromImage(_bmp);
                _g.TextRenderingHint = TextRenderingHint.AntiAlias;
            }

            public SizeF Measure(string s, DiagramFont font)
            {
                var key = font.Family + "|" + font.Size + "|" + font.Bold;
                Font f;
                if (!_fonts.TryGetValue(key, out f))
                    _fonts[key] = f = new Font(font.Family, font.Size, font.Bold ? FontStyle.Bold : FontStyle.Regular);
                return _g.MeasureString(s, f, 10000, StringFormat.GenericTypographic);
            }

            public void Dispose()
            {
                foreach (var f in _fonts.Values) f.Dispose();
                _g.Dispose();
                _bmp.Dispose();
            }
        }
    }
}
