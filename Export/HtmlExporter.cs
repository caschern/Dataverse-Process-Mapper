using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using DataverseProcessMapper.Models;
using DataverseProcessMapper.Rendering;

namespace DataverseProcessMapper.Exporters
{
    /// <summary>
    /// Exports a process map to a self-contained HTML file: the diagram is
    /// embedded as an inline vector SVG (crisp at any zoom) and is accompanied
    /// by a metadata header and a collapsible step tree with per-step details.
    /// </summary>
    public static class HtmlExporter
    {
        public static void Save(ProcessMap map, string path)
        {
            File.WriteAllText(path, BuildHtml(map, BuildSvg(map)), Encoding.UTF8);
        }

        /// <summary>Renders the diagram to an inline-embeddable SVG element.</summary>
        private static string BuildSvg(ProcessMap map)
        {
            float w = map.CanvasSize.Width + 16;
            float h = map.CanvasSize.Height + 16;

            string elements;
            using (var surface = new SvgDiagramSurface())
            {
                // Diagram = what's on screen (collapsed containers stay collapsed);
                // the step tree below always documents the FULL graph.
                DiagramRenderer.Render(surface, map.ViewGraph, map.CanvasSize);
                elements = surface.GetElements();
            }

            var sb = new StringBuilder();
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\"")
              .Append(" width=\"100%\" height=\"100%\" preserveAspectRatio=\"xMidYMid meet\"")
              .Append(" viewBox=\"0 0 ").Append(F(w)).Append(' ').Append(F(h)).AppendLine("\">")
              .Append("<rect width=\"").Append(F(w)).Append("\" height=\"").Append(F(h))
              .AppendLine("\" fill=\"#FFFFFF\"/>")
              .AppendLine("<g transform=\"translate(8,8)\">")
              .Append(elements)
              .AppendLine("</g></svg>");
            return sb.ToString();
        }

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static string BuildHtml(ProcessMap map, string svg)
        {
            var item = map.Source;
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\"><head>");
            sb.AppendLine("<meta charset=\"utf-8\">");
            sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.AppendLine($"<title>{E(map.Graph.Title)} – Process Map</title>");
            sb.AppendLine("<style>");
            sb.AppendLine(Css());
            sb.AppendLine("</style></head><body>");

            sb.AppendLine("<div class=\"wrap\">");
            sb.AppendLine($"<h1>{E(map.Graph.Title)}</h1>");
            sb.AppendLine("<table class=\"meta\"><tbody>");
            Meta(sb, "Type", item?.CategoryLabel);
            Meta(sb, "Primary table", string.IsNullOrEmpty(item?.PrimaryEntity) ? "—" : item.PrimaryEntity);
            Meta(sb, "Status", item?.StateLabel);
            Meta(sb, "Steps", map.Graph.Nodes.Count.ToString());
            Meta(sb, "Generated", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine("</tbody></table>");

            // The diagram is an interactive pan/zoom viewport: wheel zooms at the
            // cursor, drag pans, buttons give Fit / 100%.
            float viewH = map.CanvasSize.Height + 18f;
            sb.AppendLine($"<div class=\"diagram\" style=\"height:min(78vh, {F(viewH)}px)\">");
            sb.AppendLine("<div class=\"dgbar\">" +
                "<button type=\"button\" onclick=\"dgZoom(1.25)\">+</button>" +
                "<button type=\"button\" onclick=\"dgZoom(0.8)\">−</button>" +
                "<button type=\"button\" onclick=\"dgFit()\">Fit</button>" +
                "<button type=\"button\" onclick=\"dg100()\">100%</button>" +
                "<span id=\"dgpct\"></span></div>");
            sb.AppendLine(svg);
            sb.AppendLine("</div>");

            // Step tree: containers (scopes, conditions, loops) are collapsible.
            sb.AppendLine("<h2>Steps</h2>");
            sb.AppendLine("<div class=\"treebar\">" +
                "<button type=\"button\" onclick=\"document.querySelectorAll('.tree details').forEach(function(d){d.open=true})\">Expand all</button> " +
                "<button type=\"button\" onclick=\"document.querySelectorAll('.tree details').forEach(function(d){d.open=false})\">Collapse all</button>" +
                "</div>");
            sb.AppendLine("<div class=\"tree\">");
            AppendNodeTree(sb, map.Graph, null);
            sb.AppendLine("</div>");

            // Connectors with labels
            var labeled = map.Graph.Edges.Where(e => !string.IsNullOrEmpty(e.Label)).ToList();
            if (labeled.Count > 0)
            {
                sb.AppendLine("<h2>Branches</h2>");
                sb.AppendLine("<table class=\"steps\"><thead><tr><th>From</th><th>Condition</th><th>To</th></tr></thead><tbody>");
                foreach (var e in labeled)
                {
                    var from = map.Graph[e.FromId];
                    var to = map.Graph[e.ToId];
                    sb.AppendLine("<tr>" +
                        $"<td>{E(from?.Label.Replace("\n", " "))}</td>" +
                        $"<td><b>{E(e.Label)}</b></td>" +
                        $"<td>{E(to?.Label.Replace("\n", " "))}</td></tr>");
                }
                sb.AppendLine("</tbody></table>");
            }

            sb.AppendLine("<p class=\"footer\">Generated by Dataverse Process Mapper for XrmToolBox.</p>");
            sb.AppendLine("</div>");
            sb.AppendLine(ViewerScript());
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        private static void Meta(StringBuilder sb, string label, string value)
            => sb.AppendLine($"<tr><th>{E(label)}</th><td>{E(value)}</td></tr>");

        /// <summary>Self-contained pan/zoom for the inline SVG (viewBox based, no dependencies).</summary>
        private static string ViewerScript() => @"<script>
(function () {
  var svg = document.querySelector('.diagram svg');
  if (!svg) return;
  var v0 = svg.getAttribute('viewBox').split(' ').map(Number);
  var W = v0[2], H = v0[3];
  var vb = { x: 0, y: 0, w: W, h: H };
  var pct = document.getElementById('dgpct');
  function apply() {
    svg.setAttribute('viewBox', vb.x + ' ' + vb.y + ' ' + vb.w + ' ' + vb.h);
    if (pct) pct.textContent = Math.round(svg.getBoundingClientRect().width / vb.w * 100) + '%';
  }
  function zoom(f, cx, cy) {
    var nw = Math.min(Math.max(vb.w / f, W / 16), W * 3);
    f = vb.w / nw;
    vb.x = cx - (cx - vb.x) / f;
    vb.y = cy - (cy - vb.y) / f;
    vb.w = nw; vb.h = vb.h / f;
    apply();
  }
  function svgPoint(e) {
    var r = svg.getBoundingClientRect();
    return { x: vb.x + (e.clientX - r.left) / r.width * vb.w,
             y: vb.y + (e.clientY - r.top) / r.height * vb.h };
  }
  svg.addEventListener('wheel', function (e) {
    e.preventDefault();
    var p = svgPoint(e);
    zoom(e.deltaY < 0 ? 1.15 : 1 / 1.15, p.x, p.y);
  }, { passive: false });
  var drag = null;
  svg.addEventListener('mousedown', function (e) {
    drag = { x: e.clientX, y: e.clientY, vx: vb.x, vy: vb.y };
    svg.style.cursor = 'grabbing';
    e.preventDefault();
  });
  window.addEventListener('mousemove', function (e) {
    if (!drag) return;
    var r = svg.getBoundingClientRect();
    vb.x = drag.vx - (e.clientX - drag.x) / r.width * vb.w;
    vb.y = drag.vy - (e.clientY - drag.y) / r.height * vb.h;
    apply();
  });
  window.addEventListener('mouseup', function () { drag = null; svg.style.cursor = 'grab'; });
  window.dgZoom = function (f) { zoom(f, vb.x + vb.w / 2, vb.y + vb.h / 2); };
  window.dgFit = function () { vb = { x: 0, y: 0, w: W, h: H }; apply(); };
  window.dg100 = function () {
    var r = svg.getBoundingClientRect();
    var cx = vb.x + vb.w / 2, cy = vb.y + vb.h / 2;
    vb.w = r.width; vb.h = r.height;
    vb.x = cx - vb.w / 2; vb.y = cy - vb.h / 2;
    apply();
  };
  apply();
})();
</script>";

        /// <summary>Renders the children of <paramref name="parentId"/> (null = top level).</summary>
        private static void AppendNodeTree(StringBuilder sb, ProcessGraph graph, string parentId)
        {
            foreach (var n in graph.Nodes)
            {
                if (n.ParentId != parentId) continue;

                var header = $"<span class=\"dot {KindClass(n.Kind)}\"></span><b>{E(n.Label.Replace("\n", " "))}</b>" +
                             (string.IsNullOrEmpty(n.Subtitle) ? "" : $" <span class=\"muted\">· {E(n.Subtitle)}</span>");

                bool hasChildren = graph.Nodes.Any(c => c.ParentId == n.Id);
                if (hasChildren)
                {
                    int count = graph.Nodes.Count(c => c.ParentId == n.Id);
                    sb.AppendLine($"<details><summary>{header} <span class=\"muted\">({count} steps)</span></summary>");
                    AppendProps(sb, n);
                    sb.AppendLine("<div class=\"children\">");
                    AppendNodeTree(sb, graph, n.Id);
                    sb.AppendLine("</div></details>");
                }
                else
                {
                    sb.AppendLine($"<div class=\"leaf\">{header}");
                    AppendProps(sb, n);
                    sb.AppendLine("</div>");
                }
            }
        }

        private static void AppendProps(StringBuilder sb, ProcessNode n)
        {
            if (n.Details == null || n.Details.Count == 0) return;
            sb.AppendLine("<table class=\"props\">");
            foreach (var kv in n.Details)
            {
                string cell;
                if (kv.Value != null && kv.Value.IndexOf('\n') >= 0)
                    cell = $"<td><pre class=\"code\">{E(kv.Value)}</pre></td>";       // pretty-printed XML
                else if (kv.Value != null && kv.Value.StartsWith("@"))
                    cell = $"<td class=\"mono\">{E(kv.Value)}</td>";                  // flow expression
                else
                    cell = $"<td>{E(kv.Value)}</td>";
                sb.AppendLine($"<tr><th>{E(kv.Key)}</th>{cell}</tr>");
            }
            sb.AppendLine("</table>");
        }

        private static string KindClass(NodeKind kind)
        {
            switch (kind)
            {
                case NodeKind.Trigger: return "k-trigger";
                case NodeKind.Start: return "k-start";
                case NodeKind.End: return "k-end";
                case NodeKind.Condition:
                case NodeKind.Switch: return "k-cond";
                case NodeKind.Case: return "k-case";
                case NodeKind.Empty: return "k-empty";
                case NodeKind.Loop: return "k-loop";
                case NodeKind.Terminate: return "k-term";
                default: return "k-action";
            }
        }

        private static string E(string s) => string.IsNullOrEmpty(s) ? "" : WebUtility.HtmlEncode(s);

        private static string Css()
        {
            return @"
:root { --fg:#1f2936; --muted:#6b7280; --line:#e5e7eb; }
* { box-sizing: border-box; }
body { margin:0; background:#f3f4f6; color:var(--fg);
       font-family:'Segoe UI',Roboto,Helvetica,Arial,sans-serif; }
.wrap { max-width:1100px; margin:24px auto; background:#fff; padding:28px 34px;
        border-radius:12px; box-shadow:0 1px 4px rgba(0,0,0,.08); }
h1 { margin:0 0 4px; font-size:24px; }
h2 { margin:28px 0 8px; font-size:16px; border-bottom:1px solid var(--line); padding-bottom:6px; }
table.meta { border-collapse:collapse; margin:10px 0 4px; }
table.meta th { text-align:left; color:var(--muted); font-weight:600; padding:2px 16px 2px 0; }
table.meta td { padding:2px 0; }
.diagram { position:relative; margin:18px 0; border:1px solid var(--line); border-radius:8px; background:#fff; overflow:hidden; }
.diagram svg { display:block; width:100%; height:100%; cursor:grab; }
.dgbar { position:absolute; top:8px; right:8px; z-index:2; display:flex; gap:4px; align-items:center; background:rgba(255,255,255,.92); padding:4px 6px; border:1px solid var(--line); border-radius:6px; }
.dgbar button { font:13px 'Segoe UI',sans-serif; padding:2px 10px; border:1px solid var(--line); border-radius:5px; background:#fff; cursor:pointer; }
.dgbar button:hover { background:#f3f4f6; }
.dgbar span { font-size:12px; color:var(--muted); min-width:40px; text-align:right; }
table.steps { width:100%; border-collapse:collapse; font-size:14px; }
table.steps th, table.steps td { text-align:left; padding:7px 10px; border-bottom:1px solid var(--line); vertical-align:top; }
table.steps thead th { color:var(--muted); font-size:12px; text-transform:uppercase; letter-spacing:.04em; }
td.num { color:var(--muted); width:34px; }
.dot { display:inline-block; width:9px; height:9px; border-radius:50%; margin-right:8px; vertical-align:middle; }
.tree { font-size:14px; }
.tree details, .tree .leaf { margin:3px 0; }
.tree summary { cursor:pointer; padding:5px 8px; border-radius:6px; }
.tree summary:hover { background:#f3f4f6; }
.tree .leaf { padding:5px 8px; }
.tree .children { margin:2px 0 6px 14px; border-left:2px solid var(--line); padding-left:14px; }
.muted { color:var(--muted); font-weight:400; }
table.props { table-layout:fixed; width:calc(100% - 26px); border-collapse:collapse; margin:2px 0 8px 26px; font-size:12.5px; }
table.props th { width:140px; text-align:left; color:var(--muted); font-weight:600; padding:1px 12px 1px 0; vertical-align:top; overflow-wrap:break-word; }
table.props td { padding:1px 0; overflow-wrap:anywhere; white-space:pre-wrap; }
table.props pre.code { margin:2px 0; padding:7px 10px; background:#f6f8fa; border:1px solid var(--line); border-radius:6px; font:12px/1.5 Consolas,'Cascadia Mono',Menlo,monospace; white-space:pre; overflow-x:auto; max-width:100%; }
.mono { font-family:Consolas,'Cascadia Mono',Menlo,monospace; font-size:12.5px; }
.treebar { margin:2px 0 10px; }
.treebar button { font:13px 'Segoe UI',sans-serif; padding:4px 12px; border:1px solid var(--line); border-radius:6px; background:#fff; cursor:pointer; }
.treebar button:hover { background:#f3f4f6; }
.k-trigger{background:#00897b;} .k-start{background:#388e3c;} .k-end{background:#616161;}
.k-cond{background:#f59f00;} .k-loop{background:#8e24aa;} .k-term{background:#c62828;} .k-action{background:#1976d2;}
.k-case{background:#ef6c00;} .k-empty{background:#bdbdbd;}
.footer { margin-top:24px; color:var(--muted); font-size:12px; }
";
        }
    }
}
