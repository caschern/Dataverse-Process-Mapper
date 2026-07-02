using System.Xml.Linq;

namespace DataverseProcessMapper.Parsing
{
    /// <summary>
    /// Shared cleanup for detail values: XML-shaped strings (fetchXml, layoutXml…)
    /// are pretty-printed with indentation; everything else is collapsed to a
    /// single line. Both are capped so a pathological blob can't flood the UI.
    /// </summary>
    internal static class DetailText
    {
        private const int MaxLength = 4000;

        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            var pretty = TryFormatXml(s);
            if (pretty != null) return Cap(pretty);

            return Cap(s.Replace("\r", " ").Replace("\n", " "));
        }

        /// <summary>Indented multi-line rendering when the value parses as XML, else null.</summary>
        private static string TryFormatXml(string s)
        {
            var trimmed = s.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '<') return null;
            try
            {
                return XElement.Parse(s).ToString(); // ToString() indents
            }
            catch
            {
                return null; // looked like XML but wasn't — leave as-is
            }
        }

        private static string Cap(string s)
            => s.Length <= MaxLength ? s : s.Substring(0, MaxLength - 3) + "…";
    }
}
