using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DataverseProcessMapper.Models;

namespace DataverseProcessMapper.Exporters
{
    /// <summary>
    /// Exports a set of processes to one Markdown file each, plus an index,
    /// ready to upload as an AI knowledge source.
    ///
    /// The index is written to be genuinely useful to a retrieval system rather
    /// than just a table of contents: it names every process with its trigger,
    /// table and size, so an agent asked "which flow handles arrest records?"
    /// can find the right document before reading it. Individual failures are
    /// recorded and skipped, never fatal.
    /// </summary>
    public static class BulkMarkdownExporter
    {
        /// <summary>Copilot Studio accepts at most this many files per agent.</summary>
        public const int KnowledgeFileLimit = 500;

        private class Entry
        {
            public ProcessItem Item;
            public string File;
            public int Steps;
            public string Trigger;
            public int ParallelGroups;
        }

        public static BulkExportResult Export(IList<ProcessItem> items, string folder, string setLabel,
            Action<string> progress)
        {
            var result = new BulkExportResult();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<Entry>();

            int i = 0;
            foreach (var item in items)
            {
                i++;
                progress?.Invoke($"Exporting {i} of {items.Count}: {item.Name}");
                try
                {
                    var map = ProcessMapBuilder.Build(item);
                    var file = ExportNaming.UniqueName(usedNames,
                        ExportNaming.SafeFileName(item.Name)) + ".md";
                    MarkdownExporter.Save(map, Path.Combine(folder, file));

                    var trigger = map.Graph.Nodes.FirstOrDefault(n => n.Kind == NodeKind.Trigger);
                    entries.Add(new Entry
                    {
                        Item = item,
                        File = file,
                        Steps = map.Graph.Nodes.Count,
                        Trigger = trigger?.Label,
                        ParallelGroups = map.ViewGraph?.ParallelBlocks?.Count ?? 0
                    });
                    result.Exported++;
                }
                catch (Exception ex)
                {
                    result.Failures.Add($"{item.Name}: {ex.Message}");
                }
            }

            result.IndexPath = Path.Combine(folder, "index.md");
            File.WriteAllText(result.IndexPath, BuildIndex(entries, result.Failures, setLabel),
                new UTF8Encoding(false));
            return result;
        }

        private static string BuildIndex(List<Entry> entries, List<string> failures, string setLabel)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# " + setLabel + " — process catalogue");
            sb.AppendLine();
            sb.AppendLine("This catalogue lists " + entries.Count + " automated " +
                          (entries.Count == 1 ? "process" : "processes") +
                          " documented on " + DateTime.Now.ToString("yyyy-MM-dd") +
                          ". Each has its own document describing every step, what each step runs " +
                          "after, and which steps run at the same time.");
            sb.AppendLine();

            var totalSteps = entries.Sum(e => e.Steps);
            if (entries.Count > 0)
            {
                sb.AppendLine("Together they contain " + totalSteps + " steps. The largest is **" +
                              Clean(entries.OrderByDescending(e => e.Steps).First().Item.Name) +
                              "** with " + entries.Max(e => e.Steps) + " steps.");
                sb.AppendLine();
            }

            sb.AppendLine("## Processes");
            sb.AppendLine();
            foreach (var e in entries.OrderBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine("### " + Clean(e.Item.Name));
                sb.AppendLine();

                var facts = new List<string> { "a " + e.Item.CategoryLabel };
                if (!string.IsNullOrEmpty(e.Item.PrimaryEntity))
                    facts.Add("running against the `" + e.Item.PrimaryEntity + "` table");
                facts.Add("currently " + e.Item.StateLabel.ToLowerInvariant());
                facts.Add("made up of " + e.Steps + " steps");

                sb.AppendLine("**" + Clean(e.Item.Name) + "** is " + string.Join(", ", facts) + ".");
                if (!string.IsNullOrEmpty(e.Trigger))
                    sb.AppendLine("It is triggered by **" + Clean(e.Trigger) + "**.");
                if (e.ParallelGroups > 0)
                    sb.AppendLine("It has " + e.ParallelGroups + " group" +
                                  (e.ParallelGroups == 1 ? "" : "s") + " of steps that run at the same time.");
                sb.AppendLine();
                sb.AppendLine("Full documentation: [" + Clean(e.Item.Name) + "](" +
                              Uri.EscapeDataString(e.File) + ")");
                sb.AppendLine();
            }

            if (failures.Count > 0)
            {
                sb.AppendLine("## Not exported");
                sb.AppendLine();
                sb.AppendLine(failures.Count + " process" + (failures.Count == 1 ? "" : "es") +
                              " could not be documented:");
                sb.AppendLine();
                foreach (var f in failures)
                    sb.AppendLine("- " + Clean(f));
                sb.AppendLine();
            }

            // The index counts against the agent's file allowance too.
            int files = entries.Count + 1;
            if (files > KnowledgeFileLimit)
            {
                sb.AppendLine("> Note: this pack contains " + files + " files, more than the " +
                              KnowledgeFileLimit + " a single Copilot Studio agent accepts as " +
                              "knowledge. Split it across agents, or narrow the list before exporting.");
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("Generated by Dataverse Process Mapper for XrmToolBox.");
            return sb.ToString();
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
            return s.Replace("|", "\\|").Replace("*", "\\*");
        }
    }
}
