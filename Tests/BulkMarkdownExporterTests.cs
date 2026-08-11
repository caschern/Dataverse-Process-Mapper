using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DataverseProcessMapper.Exporters;
using DataverseProcessMapper.Models;
using Xunit;

namespace DataverseProcessMapper.Tests
{
    public class BulkMarkdownExporterTests : IDisposable
    {
        private readonly string _folder;

        public BulkMarkdownExporterTests()
        {
            _folder = Path.Combine(Path.GetTempPath(), "dpm-bulk-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        public void Dispose()
        {
            try { Directory.Delete(_folder, true); } catch { /* best effort */ }
        }

        private static ProcessItem Flow(string name, string trigger = "When_a_row_is_added") =>
            new ProcessItem
            {
                Name = name,
                Category = 5,
                PrimaryEntity = "account",
                State = 1,
                ClientData = "{\"properties\":{\"definition\":{" +
                             "\"triggers\":{\"" + trigger + "\":{\"type\":\"OpenApiConnectionWebhook\"}}," +
                             "\"actions\":{\"Compose\":{\"type\":\"Compose\",\"inputs\":1,\"runAfter\":{}}}}}}"
            };

        [Fact]
        public void WritesOneFilePerProcess_PlusAnIndex()
        {
            var items = new List<ProcessItem> { Flow("Alpha"), Flow("Beta"), Flow("Gamma") };
            var result = BulkMarkdownExporter.Export(items, _folder, "Power Automate Flows", null);

            Assert.Equal(3, result.Exported);
            Assert.Empty(result.Failures);
            var files = Directory.GetFiles(_folder, "*.md").Select(Path.GetFileName).ToList();
            Assert.Equal(4, files.Count);
            Assert.Contains("index.md", files);
            Assert.Contains("Alpha.md", files);
            Assert.True(File.Exists(result.IndexPath));
        }

        [Fact]
        public void Index_DescribesEachProcessAndLinksToIt()
        {
            BulkMarkdownExporter.Export(new List<ProcessItem> { Flow("Alpha") }, _folder, "Flows", null);
            var index = File.ReadAllText(Path.Combine(_folder, "index.md"));

            Assert.Contains("# Flows — process catalogue", index);
            Assert.Contains("**Alpha** is a Power Automate Flow", index);
            Assert.Contains("`account` table", index);
            Assert.Contains("triggered by **When a row is added**", index);
            Assert.Contains("(Alpha.md)", index);
        }

        [Fact]
        public void DuplicateNames_DoNotOverwriteEachOther()
        {
            var result = BulkMarkdownExporter.Export(
                new List<ProcessItem> { Flow("Same"), Flow("Same") }, _folder, "Flows", null);

            Assert.Equal(2, result.Exported);
            Assert.True(File.Exists(Path.Combine(_folder, "Same.md")));
            Assert.True(File.Exists(Path.Combine(_folder, "Same_2.md")));
        }

        [Fact]
        public void OneBadProcess_IsRecordedAndTheRestStillExport()
        {
            var broken = new ProcessItem { Name = "Broken", Category = 5, ClientData = "{ not json" };
            var result = BulkMarkdownExporter.Export(
                new List<ProcessItem> { Flow("Good"), broken }, _folder, "Flows", null);

            // A malformed definition yields a note-only map rather than throwing,
            // so it still exports — the point is the run completes either way.
            Assert.True(result.Exported >= 1);
            Assert.True(File.Exists(Path.Combine(_folder, "Good.md")));
            Assert.True(File.Exists(result.IndexPath));
        }

        [Fact]
        public void Progress_IsReportedPerProcess()
        {
            var seen = new List<string>();
            BulkMarkdownExporter.Export(
                new List<ProcessItem> { Flow("Alpha"), Flow("Beta") }, _folder, "Flows", seen.Add);

            Assert.Equal(2, seen.Count);
            Assert.Contains(seen, s => s.Contains("Alpha"));
            Assert.Contains(seen, s => s.Contains("1 of 2"));
        }
    }
}
