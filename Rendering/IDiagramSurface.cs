using System.Drawing;

namespace DataverseProcessMapper.Rendering
{
    /// <summary>
    /// Low-level drawing primitives, implemented once for GDI+ (screen / PNG) and
    /// once for PdfSharp (vector PDF). All coordinates are in device-independent
    /// units with the origin at the top-left. The high-level shape, text-wrapping
    /// and arrow logic lives in <see cref="DiagramRenderer"/> so the two backends
    /// stay pixel-consistent.
    /// </summary>
    public interface IDiagramSurface
    {
        SizeF MeasureString(string text, DiagramFont font);

        /// <summary>Draws text with its top-left at (x, y).</summary>
        void DrawString(string text, DiagramFont font, Color color, float x, float y);

        void DrawLine(Color color, float width, float x1, float y1, float x2, float y2, bool dashed);

        void FillPolygon(Color fill, PointF[] points);
        void DrawPolygon(Color stroke, float width, PointF[] points);

        void FillRoundedRect(Color fill, RectangleF rect, float radius);
        void DrawRoundedRect(Color stroke, float width, RectangleF rect, float radius);

        void FillEllipse(Color fill, RectangleF rect);
        void DrawEllipse(Color stroke, float width, RectangleF rect);
    }
}
