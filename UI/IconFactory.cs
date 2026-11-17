using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace DataverseProcessMapper.UI
{
    /// <summary>The icon keys the toolbars and menus ask <see cref="IconFactory"/> for.</summary>
    public static class IconNames
    {
        public const string Reload = "reload", Export = "export", ExportAll = "export-all", Pdf = "pdf", Html = "html",
            Svg = "svg", Png = "png", Markdown = "markdown", Open = "open", Link = "link", Copy = "copy",
            RefreshOne = "refresh-one", Solution = "solution", SearchDefinitions = "search-definitions", Find = "find",
            Previous = "previous", Next = "next", Layout = "layout", ExpandAll = "expand-all", CollapseAll = "collapse-all",
            Fit = "fit", ZoomIn = "zoom-in", ZoomOut = "zoom-out", FullScreen = "full-screen",
            ExitFullScreen = "exit-full-screen", CheckAll = "check-all", UncheckAll = "uncheck-all", Filter = "filter",
            Info = "info";
    }

    /// <summary>
    /// Toolbar and menu icons drawn at run time from the Fluent UI System Icons
    /// path data embedded in UI/Icons/fluent-icons.txt. Drawing the vector
    /// paths (rather than shipping bitmaps) keeps them crisp at any DPI and
    /// lets them follow the system text colour (high contrast).
    /// </summary>
    public static class IconFactory
    {
        private const string ResourceName = "DataverseProcessMapper.UI.Icons.fluent-icons.txt";

        // name -> design size (16 / 20) -> path data
        private static readonly Lazy<Dictionary<string, Dictionary<int, string>>> Paths =
            new Lazy<Dictionary<string, Dictionary<int, string>>>(LoadPaths, LazyThreadSafetyMode.ExecutionAndPublication);

        private static readonly Dictionary<string, Image> Cache = new Dictionary<string, Image>(StringComparer.Ordinal);
        private static readonly object CacheLock = new object();

        /// <summary>Every icon name the resource holds.</summary>
        public static IReadOnlyCollection<string> Names =>
            new ReadOnlyCollection<string>(Paths.Value.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList());

        /// <summary>The icon in the system text colour. Cached: callers must NOT dispose it.</summary>
        public static Image Get(string name, int px) => Get(name, px, SystemColors.ControlText);

        /// <summary>The icon in <paramref name="color"/>. Cached: callers must NOT dispose it.</summary>
        public static Image Get(string name, int px, Color color)
        {
            Dictionary<int, string> designs;
            if (name == null || !Paths.Value.TryGetValue(name, out designs))
                throw new ArgumentException("Unknown icon '" + name + "'.", nameof(name));
            if (px <= 0)
                throw new ArgumentOutOfRangeException(nameof(px), px, "Icon size must be positive.");

            // ToArgb resolves system colours, so a theme change gets new images.
            var key = name + "|" + px.ToString(CultureInfo.InvariantCulture) + "|" + color.ToArgb().ToString("X8", CultureInfo.InvariantCulture);
            lock (CacheLock)
            {
                Image image;
                if (!Cache.TryGetValue(key, out image))
                {
                    image = Render(designs, px, Color.FromArgb(color.ToArgb()));
                    Cache[key] = image;
                }
                return image;
            }
        }

        /// <summary>The icon size for <paramref name="control"/>: 16 at 96 DPI, scaled by its DeviceDpi.</summary>
        public static int SizeFor(Control control)
        {
            if (control == null) throw new ArgumentNullException(nameof(control));
            var dpi = control.DeviceDpi > 0 ? control.DeviceDpi : 96;
            return Math.Max(16, (int)Math.Round(16.0 * dpi / 96.0, MidpointRounding.AwayFromZero));
        }

        private static Image Render(Dictionary<int, string> designs, int px, Color color)
        {
            // Each Fluent size is drawn for its own grid; use the nearer one
            // (ties go to the larger, more detailed design) and scale it.
            int design = designs.Keys.OrderBy(s => Math.Abs(s - px)).ThenByDescending(s => s).First();
            var bitmap = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bitmap))
            using (var path = SvgPathParser.Parse(designs[design]))
            using (var brush = new SolidBrush(color))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // HighQuality puts pixel edges on whole coordinates, as SVG does,
                // so the designs' pixel-aligned strokes stay sharp.
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.ScaleTransform(px / (float)design, px / (float)design);
                g.FillPath(brush, path);
            }
            return bitmap;
        }

        private static Dictionary<string, Dictionary<int, string>> LoadPaths()
        {
            var result = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
            using (var stream = typeof(IconFactory).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("The icon resource " + ResourceName + " is missing from the assembly.");
                using (var reader = new StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (line.Length == 0 || line[0] == '#') continue;
                        var parts = line.Split(new[] { '|' }, 3);
                        int size;
                        if (parts.Length != 3 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out size) || size <= 0)
                            throw new InvalidDataException("Bad icon line: " + Head(line));
                        Dictionary<int, string> designs;
                        if (!result.TryGetValue(parts[0], out designs))
                            result[parts[0]] = designs = new Dictionary<int, string>();
                        designs[size] = parts[2];
                    }
                }
            }
            return result;
        }

        private static string Head(string s) => s.Length <= 60 ? s : s.Substring(0, 60) + "...";
    }

    /// <summary>
    /// Parses SVG path data (the <c>d</c> attribute) into a GDI+ path: absolute
    /// and relative M/L/H/V/C/S/Q/T/A/Z, implicit repeated arguments, exponents,
    /// and comma or space separators. Throws <see cref="FormatException"/> on bad data.
    /// </summary>
    public static class SvgPathParser
    {
        private const byte Start = 0, Line = 1, Bezier = 3, Close = 0x80;

        /// <summary>A new <see cref="FillMode.Winding"/> path (SVG's default nonzero fill rule); the caller disposes it.</summary>
        public static GraphicsPath Parse(string d)
        {
            var b = new Builder();
            var s = new Scanner(d ?? "");
            char cmd = '\0';
            while (true)
            {
                s.SkipSeparators();
                if (s.AtEnd) break;
                char c = s.Peek;
                if (IsCommand(c))
                {
                    cmd = c;
                    s.Advance();
                }
                else if (cmd == '\0' || cmd == 'Z' || cmd == 'z' || !s.AtNumber)
                    throw s.Error("unexpected '" + c + "'");
                else if (cmd == 'M')
                    cmd = 'L'; // extra pairs after a moveto are linetos
                else if (cmd == 'm')
                    cmd = 'l';
                b.Execute(cmd, s);
            }
            return b.ToPath();
        }

        private static bool IsCommand(char c) => "MmLlHhVvCcSsQqTtAaZz".IndexOf(c) >= 0;

        private sealed class Scanner
        {
            private readonly string _s;
            private int _i;

            public Scanner(string s) { _s = s; }

            public bool AtEnd => _i >= _s.Length;
            public char Peek => _s[_i];
            public void Advance() => _i++;

            public bool AtNumber
            {
                get
                {
                    if (AtEnd) return false;
                    char c = _s[_i];
                    return char.IsDigit(c) || c == '.' || c == '-' || c == '+';
                }
            }

            public void SkipSeparators()
            {
                while (_i < _s.Length && (char.IsWhiteSpace(_s[_i]) || _s[_i] == ',')) _i++;
            }

            public FormatException Error(string what) =>
                new FormatException("Invalid SVG path data at position " + _i + ": " + what + ".");

            public float Number()
            {
                SkipSeparators();
                int start = _i, digits = 0;
                if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                while (_i < _s.Length && char.IsDigit(_s[_i])) { _i++; digits++; }
                // "0.5.5" is two numbers: a second dot starts the next one.
                if (_i < _s.Length && _s[_i] == '.')
                {
                    _i++;
                    while (_i < _s.Length && char.IsDigit(_s[_i])) { _i++; digits++; }
                }
                if (digits == 0)
                {
                    _i = start;
                    throw Error("number expected");
                }
                if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    int e = _i + 1;
                    if (e < _s.Length && (_s[e] == '+' || _s[e] == '-')) e++;
                    if (e < _s.Length && char.IsDigit(_s[e]))
                    {
                        _i = e;
                        while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                    }
                }
                return float.Parse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            // Arc flags are single digits and may be packed ("a1 1 0 01 5 5").
            public bool Flag()
            {
                SkipSeparators();
                if (_i < _s.Length && (_s[_i] == '0' || _s[_i] == '1')) return _s[_i++] == '1';
                throw Error("arc flag (0 or 1) expected");
            }
        }

        private sealed class Builder
        {
            private readonly List<PointF> _points = new List<PointF>();
            private readonly List<byte> _types = new List<byte>();
            private int _figureStart = -1;
            private PointF _current, _subpathStart;
            private PointF _lastControl;   // for S / T reflection
            private char _lastKind;        // 'C' after a cubic, 'Q' after a quadratic, else '\0'

            public void Execute(char cmd, Scanner s)
            {
                bool rel = char.IsLower(cmd);
                PointF o = rel ? _current : PointF.Empty;
                char kind = '\0';
                switch (char.ToUpperInvariant(cmd))
                {
                    case 'M':
                        MoveTo(Pt(s, o));
                        break;
                    case 'L':
                        LineTo(Pt(s, o));
                        break;
                    case 'H':
                        LineTo(new PointF(s.Number() + o.X, _current.Y));
                        break;
                    case 'V':
                        LineTo(new PointF(_current.X, s.Number() + o.Y));
                        break;
                    case 'C':
                    {
                        var c1 = Pt(s, o); var c2 = Pt(s, o); var p = Pt(s, o);
                        CubicTo(c1, c2, p);
                        kind = 'C';
                        break;
                    }
                    case 'S':
                    {
                        var c1 = _lastKind == 'C' ? Reflect(_lastControl) : _current;
                        var c2 = Pt(s, o); var p = Pt(s, o);
                        CubicTo(c1, c2, p);
                        kind = 'C';
                        break;
                    }
                    case 'Q':
                    {
                        var q = Pt(s, o); var p = Pt(s, o);
                        QuadTo(q, p);
                        kind = 'Q';
                        break;
                    }
                    case 'T':
                    {
                        var q = _lastKind == 'Q' ? Reflect(_lastControl) : _current;
                        QuadTo(q, Pt(s, o));
                        kind = 'Q';
                        break;
                    }
                    case 'A':
                    {
                        float rx = s.Number(), ry = s.Number(), angle = s.Number();
                        bool large = s.Flag(), sweep = s.Flag();
                        ArcTo(rx, ry, angle, large, sweep, Pt(s, o));
                        break;
                    }
                    case 'Z':
                        ClosePath();
                        break;
                }
                _lastKind = kind;
            }

            private static PointF Pt(Scanner s, PointF origin)
            {
                float x = s.Number(), y = s.Number();
                return new PointF(x + origin.X, y + origin.Y);
            }

            private PointF Reflect(PointF control) => new PointF(2 * _current.X - control.X, 2 * _current.Y - control.Y);

            private void MoveTo(PointF p)
            {
                DropLoneStart();
                _points.Add(p);
                _types.Add(Start);
                _figureStart = _points.Count - 1;
                _current = _subpathStart = p;
            }

            // A drawing command after Z (or with no moveto) continues from the current point.
            private void EnsureFigure()
            {
                if (_figureStart >= 0) return;
                _points.Add(_current);
                _types.Add(Start);
                _figureStart = _points.Count - 1;
                _subpathStart = _current;
            }

            private void LineTo(PointF p)
            {
                EnsureFigure();
                _points.Add(p);
                _types.Add(Line);
                _current = p;
            }

            private void CubicTo(PointF c1, PointF c2, PointF p)
            {
                EnsureFigure();
                _points.Add(c1); _points.Add(c2); _points.Add(p);
                _types.Add(Bezier); _types.Add(Bezier); _types.Add(Bezier);
                _lastControl = c2;
                _current = p;
            }

            private void QuadTo(PointF q, PointF p)
            {
                // A quadratic is the cubic whose controls sit 2/3 of the way to q.
                var p0 = _current;
                CubicTo(new PointF(p0.X + 2f / 3f * (q.X - p0.X), p0.Y + 2f / 3f * (q.Y - p0.Y)),
                        new PointF(p.X + 2f / 3f * (q.X - p.X), p.Y + 2f / 3f * (q.Y - p.Y)), p);
                _lastControl = q;
            }

            // SVG 1.1 implementation notes F.6.5/F.6.6: endpoint to centre
            // parameterisation, then cubic segments of at most 90 degrees.
            private void ArcTo(float rx, float ry, float angleDeg, bool large, bool sweep, PointF p)
            {
                var p0 = _current;
                if (p0 == p) return;
                double rX = Math.Abs(rx), rY = Math.Abs(ry);
                if (rX == 0 || rY == 0) { LineTo(p); return; }

                double phi = angleDeg * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
                double dx = (p0.X - p.X) / 2.0, dy = (p0.Y - p.Y) / 2.0;
                double x1 = cos * dx + sin * dy, y1 = -sin * dx + cos * dy;
                double lambda = x1 * x1 / (rX * rX) + y1 * y1 / (rY * rY);
                if (lambda > 1) { rX *= Math.Sqrt(lambda); rY *= Math.Sqrt(lambda); }

                double num = rX * rX * rY * rY - rX * rX * y1 * y1 - rY * rY * x1 * x1;
                double den = rX * rX * y1 * y1 + rY * rY * x1 * x1;
                double coef = Math.Sqrt(Math.Max(0, num / den)) * (large == sweep ? -1 : 1);
                double cx1 = coef * rX * y1 / rY, cy1 = -coef * rY * x1 / rX;
                double cx = cos * cx1 - sin * cy1 + (p0.X + p.X) / 2.0;
                double cy = sin * cx1 + cos * cy1 + (p0.Y + p.Y) / 2.0;

                double theta1 = Angle(1, 0, (x1 - cx1) / rX, (y1 - cy1) / rY);
                double delta = Angle((x1 - cx1) / rX, (y1 - cy1) / rY, (-x1 - cx1) / rX, (-y1 - cy1) / rY);
                if (!sweep && delta > 0) delta -= 2 * Math.PI;
                else if (sweep && delta < 0) delta += 2 * Math.PI;

                int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-9));
                double step = delta / segments, k = 4.0 / 3.0 * Math.Tan(step / 4);
                Func<double, double, PointF> map = (ux, uy) => new PointF(
                    (float)(cx + cos * rX * ux - sin * rY * uy),
                    (float)(cy + sin * rX * ux + cos * rY * uy));
                double t = theta1;
                for (int i = 0; i < segments; i++)
                {
                    double t2 = t + step;
                    double c1 = Math.Cos(t), s1 = Math.Sin(t), c2 = Math.Cos(t2), s2 = Math.Sin(t2);
                    var end = i == segments - 1 ? p : map(c2, s2); // land exactly on the endpoint
                    CubicTo(map(c1 - k * s1, s1 + k * c1), map(c2 + k * s2, s2 - k * c2), end);
                    t = t2;
                }
            }

            private static double Angle(double ux, double uy, double vx, double vy)
            {
                double a = Math.Atan2(uy, ux), b = Math.Atan2(vy, vx), d = b - a;
                while (d > Math.PI) d -= 2 * Math.PI;
                while (d <= -Math.PI) d += 2 * Math.PI;
                return d;
            }

            private void ClosePath()
            {
                if (_figureStart >= 0)
                {
                    if (_points.Count - 1 > _figureStart)
                        _types[_types.Count - 1] |= Close;
                    else
                        DropLoneStart();
                    _figureStart = -1;
                }
                _current = _subpathStart; // per SVG, the next subpath starts here
            }

            // GDI+ rejects a figure that is only a start point (M followed by M or Z).
            private void DropLoneStart()
            {
                if (_figureStart >= 0 && _figureStart == _points.Count - 1)
                {
                    _points.RemoveAt(_figureStart);
                    _types.RemoveAt(_figureStart);
                }
                _figureStart = -1;
            }

            public GraphicsPath ToPath()
            {
                DropLoneStart();
                return _points.Count == 0
                    ? new GraphicsPath(FillMode.Winding)
                    : new GraphicsPath(_points.ToArray(), _types.ToArray(), FillMode.Winding);
            }
        }
    }
}
