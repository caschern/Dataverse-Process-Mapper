using System;
using DataverseProcessMapper.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DataverseProcessMapper.Exporters
{
    /// <summary>Exports a process map to a vector PDF using PdfSharp.</summary>
    public static class PdfExporter
    {
        public static void Save(ProcessMap map, string path)
        {
            using (var document = new PdfDocument())
            {
                document.Info.Title = map.Graph.Title ?? "Process Map";
                document.Info.Subject = map.Graph.Subtitle ?? "";
                document.Info.Creator = "Dataverse Process Mapper (XrmToolBox)";

                var page = document.AddPage();

                // Size the page to the diagram (1 unit == 1 point), with a small margin.
                double w = map.CanvasSize.Width + 16;
                double h = map.CanvasSize.Height + 16;
                page.Width = XUnit.FromPoint(w);
                page.Height = XUnit.FromPoint(h);

                using (var gfx = XGraphics.FromPdfPage(page))
                {
                    gfx.DrawRectangle(XBrushes.White, 0, 0, w, h);
                    gfx.TranslateTransform(8, 8);
                    var surface = new PdfDiagramSurface(gfx);
                    DiagramRenderer.Render(surface, map.Graph, map.CanvasSize);
                }

                document.Save(path);
            }
        }
    }
}
