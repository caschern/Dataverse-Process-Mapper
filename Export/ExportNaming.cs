using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using DataverseProcessMapper.Parsing;

namespace DataverseProcessMapper.Exporters
{
    /// <summary>File naming shared by the bulk exporters.</summary>
    internal static class ExportNaming
    {
        /// <summary>
        /// A file name for a process. Unpaired surrogates (a bad escape in the
        /// source) are replaced like the other invalid characters: the bulk
        /// index links by percent-encoding the name, which cannot represent one
        /// and throws, losing the whole index.
        /// </summary>
        public static string SafeFileName(string name)
        {
            name = name ?? "process";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            var safe = DetailText.Head(name.Trim(), 80).ToCharArray();
            for (int i = 0; i < safe.Length; i++)
            {
                if (char.IsHighSurrogate(safe[i]) && i + 1 < safe.Length && char.IsLowSurrogate(safe[i + 1]))
                    i++; // a proper pair: keep both halves
                else if (char.IsSurrogate(safe[i]))
                    safe[i] = '_';
            }
            var result = new string(safe);
            if (result.Length == 0)
                return "process"; // ".html" alone would be a hidden dot-file
            // Windows reserves device names whatever the extension ("CON.html",
            // "nul.txt.html"); before Windows 11 writing one fails.
            return ReservedDeviceName.IsMatch(result) ? "_" + result : result;
        }

        private static readonly Regex ReservedDeviceName = new Regex(
            @"^(CON|PRN|AUX|NUL|COM[0-9\u00B9\u00B2\u00B3]|LPT[0-9\u00B9\u00B2\u00B3]) *(\.|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Returns a name not yet in <paramref name="used"/>, adding it. Two
        /// processes may legitimately share a name in Dataverse, so collisions
        /// get a numeric suffix rather than silently overwriting.
        /// </summary>
        public static string UniqueName(HashSet<string> used, string baseName)
        {
            var candidate = baseName;
            int n = 2;
            while (!used.Add(candidate))
                candidate = baseName + "_" + n++;
            return candidate;
        }
    }
}
