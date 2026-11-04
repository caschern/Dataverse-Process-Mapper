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
        private const int MaxXmlLength = 20000; // XML must stay complete to be reusable (e.g. sent to FetchXML Builder)

        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            var pretty = TryFormatXml(s);
            if (pretty != null)
                return pretty.Length <= MaxXmlLength ? pretty : Head(pretty, MaxXmlLength - 3) + "…";

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
            => s.Length <= MaxLength ? s : Head(s, MaxLength - 3) + "…";

        // Every cut of user text goes through Head/Tail: a plain Substring can
        // split an emoji's surrogate pair, and the lone half left behind becomes
        // U+FFFD in SVG/HTML/Markdown and makes Uri.EscapeDataString throw on a
        // file name.

        /// <summary>At most the first <paramref name="length"/> chars, one fewer rather than half an emoji.</summary>
        public static string Head(string s, int length)
        {
            if (s.Length <= length) return s;
            if (length <= 0) return "";
            if (char.IsHighSurrogate(s[length - 1])) length--;
            return s.Substring(0, length);
        }

        /// <summary>At most the last <paramref name="length"/> chars, one fewer rather than half an emoji.</summary>
        public static string Tail(string s, int length)
        {
            if (s.Length <= length) return s;
            if (length <= 0) return ""; // s[s.Length] below would be out of range
            int start = s.Length - length;
            if (char.IsLowSurrogate(s[start])) start++;
            return s.Substring(start);
        }
    }
}
