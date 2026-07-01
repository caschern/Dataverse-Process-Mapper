using System.Drawing;
using DataverseProcessMapper.Models;

namespace DataverseProcessMapper.Rendering
{
    /// <summary>A font specification independent of GDI+/PdfSharp.</summary>
    public struct DiagramFont
    {
        public string Family;
        public float Size;
        public bool Bold;

        public DiagramFont(string family, float size, bool bold)
        {
            Family = family;
            Size = size;
            Bold = bold;
        }
    }

    /// <summary>Resolved visual style for a node kind.</summary>
    public struct NodeStyle
    {
        public Color Fill;
        public Color Border;
        public Color Text;
        public Color SubtitleText;
    }

    /// <summary>
    /// Central palette and sizing constants so the on-screen, PDF and HTML
    /// renderings stay visually consistent.
    /// </summary>
    public static class DiagramStyle
    {
        public const string FontFamily = "Segoe UI";

        public static DiagramFont LabelFont => new DiagramFont(FontFamily, 9f, true);
        public static DiagramFont SubtitleFont => new DiagramFont(FontFamily, 7.5f, false);
        public static DiagramFont EdgeLabelFont => new DiagramFont(FontFamily, 8.5f, false);
        public static DiagramFont TitleFont => new DiagramFont(FontFamily, 13f, true);

        // Layout constants (device-independent units == pixels == points)
        public const float NodeMinWidth = 130f;
        public const float NodeMaxWidth = 210f;
        public const float NodePadX = 14f;
        public const float NodePadY = 10f;
        public const float LineHeight = 14f;
        public const float SubtitleLineHeight = 12f;
        public const float HorizontalGap = 34f;
        public const float VerticalGap = 46f;
        public const float Margin = 30f;
        public const float TitleBandHeight = 46f;
        public const float CornerRadius = 10f;

        public static readonly Color CanvasBackground = Color.White;
        public static readonly Color EdgeColor = Color.FromArgb(110, 118, 129);
        public static readonly Color EdgeLabelColor = Color.FromArgb(55, 62, 70);
        public static readonly Color TitleColor = Color.FromArgb(33, 41, 54);
        public static readonly Color SubtitleColor = Color.FromArgb(110, 118, 129);

        public static NodeStyle For(NodeKind kind)
        {
            switch (kind)
            {
                case NodeKind.Start:
                    return Solid(Color.FromArgb(56, 142, 60), Color.White);
                case NodeKind.End:
                    return Solid(Color.FromArgb(97, 97, 97), Color.White);
                case NodeKind.Trigger:
                    return Solid(Color.FromArgb(0, 137, 123), Color.White);
                case NodeKind.Terminate:
                    return Solid(Color.FromArgb(198, 40, 40), Color.White);
                case NodeKind.Condition:
                    return Tint(Color.FromArgb(255, 248, 225), Color.FromArgb(245, 159, 0));
                case NodeKind.Switch:
                    return Tint(Color.FromArgb(255, 243, 224), Color.FromArgb(239, 108, 0));
                case NodeKind.Loop:
                    return Tint(Color.FromArgb(243, 229, 245), Color.FromArgb(142, 36, 170));
                case NodeKind.Note:
                    return Tint(Color.FromArgb(245, 245, 245), Color.FromArgb(158, 158, 158));
                case NodeKind.Action:
                default:
                    return Tint(Color.FromArgb(227, 242, 253), Color.FromArgb(25, 118, 210));
            }
        }

        private static NodeStyle Solid(Color fill, Color text)
        {
            return new NodeStyle
            {
                Fill = fill,
                Border = Darken(fill, 0.85f),
                Text = text,
                SubtitleText = Color.FromArgb(220, text)
            };
        }

        private static NodeStyle Tint(Color fill, Color border)
        {
            return new NodeStyle
            {
                Fill = fill,
                Border = border,
                Text = Color.FromArgb(38, 50, 56),
                SubtitleText = Color.FromArgb(90, 100, 110)
            };
        }

        private static Color Darken(Color c, float f)
        {
            return Color.FromArgb(c.A, (int)(c.R * f), (int)(c.G * f), (int)(c.B * f));
        }
    }
}
