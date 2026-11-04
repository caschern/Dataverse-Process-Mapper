using System;
using DataverseProcessMapper.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DataverseProcessMapper.Exporters
{
    /// <summary>Exports a process map to a vector PDF using PdfSharp.</summary>
    public static class PdfExporter
    {
        /// <summary>
        /// The longest page side the PDF spec's implementation limits allow
        /// (Acrobat's 200 inches); viewers refuse or clip a larger page.
        /// </summary>
        private const double MaxPageSide = 14400;

        public static void Save(ProcessMap map, string path)
        {
            map = ProcessMapBuilder.Expanded(map); // every step, whatever is collapsed on screen
            using (var document = new PdfDocument())
            {
                document.Info.Title = map.Graph.Title ?? "Process Map";
                document.Info.Subject = map.Graph.Subtitle ?? "";
                document.Info.Creator = "Dataverse Process Mapper (XrmToolBox)";

                var page = document.AddPage();

                // Size the page to the diagram (1 unit == 1 point), with a small margin.
                double w = map.CanvasSize.Width + 16;
                double h = map.CanvasSize.Height + 16;

                // A map too long for one page is scaled down whole rather than
                // split: it stays one vector page, so zooming in recovers it.
                double scale = Math.Min(1.0, MaxPageSide / Math.Max(w, h));
                page.Width = XUnit.FromPoint(Math.Min(w * scale, MaxPageSide));
                page.Height = XUnit.FromPoint(Math.Min(h * scale, MaxPageSide));

                using (var gfx = XGraphics.FromPdfPage(page))
                {
                    if (scale < 1.0) gfx.ScaleTransform(scale);
                    gfx.DrawRectangle(XBrushes.White, 0, 0, w, h);
                    gfx.TranslateTransform(8, 8);
                    var surface = new PdfDiagramSurface(gfx);
                    DiagramRenderer.Render(surface, map.ViewGraph, map.CanvasSize);
                }

                document.Save(path);
            }
        }
    }
}
