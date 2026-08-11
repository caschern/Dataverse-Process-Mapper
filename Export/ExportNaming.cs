using System;
using System.Collections.Generic;
using System.IO;

namespace DataverseProcessMapper.Exporters
{
    /// <summary>File naming shared by the bulk exporters.</summary>
    internal static class ExportNaming
    {
        public static string SafeFileName(string name)
        {
            name = name ?? "process";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim();
            return name.Length > 80 ? name.Substring(0, 80) : name;
        }

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
