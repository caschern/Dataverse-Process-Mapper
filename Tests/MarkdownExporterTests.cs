using System;
using System.IO;
using System.Linq;
using DataverseProcessMapper;
using DataverseProcessMapper.Exporters;
using DataverseProcessMapper.Models;
using Xunit;

namespace DataverseProcessMapper.Tests
{
    public class MarkdownExporterTests
    {
        private const string Definition = @"{
          ""triggers"": { ""When_a_row_is_added"": { ""type"": ""OpenApiConnectionWebhook"" } },
          ""actions"": {
            ""Compose_-_Parallelize"": { ""type"": ""Compose"", ""inputs"": true, ""runAfter"": {} },
            ""List_rows_-_A"": { ""type"": ""OpenApiConnection"", ""runAfter"": { ""Compose_-_Parallelize"": [""Succeeded""] } },
            ""List_rows_-_B"": { ""type"": ""OpenApiConnection"", ""runAfter"": { ""Compose_-_Parallelize"": [""Succeeded""] } },
            ""List_rows_-_C"": { ""type"": ""OpenApiConnection"", ""runAfter"": { ""Compose_-_Parallelize"": [""Succeeded""] } },
            ""Check_it"": {
              ""type"": ""If"",
              ""runAfter"": { ""List_rows_-_A"": [""Succeeded""], ""List_rows_-_B"": [""Succeeded""], ""List_rows_-_C"": [""Succeeded""] },
              ""actions"": { ""Send_an_email"": { ""type"": ""OpenApiConnection"", ""runAfter"": {} } },
              ""else"": { ""actions"": {} }
            }
          }}";

        private static string Export() => MarkdownExporter.Build(ProcessMapBuilder.Build(new ProcessItem
        {
            Name = "Sample Flow",
            Category = 5,
            PrimaryEntity = "account",
            State = 1,
            ClientData = "{\"properties\":{\"definition\":" + Definition + "}}"
        }));

        [Fact]
        public void Header_DescribesTheProcessInProse()
        {
            var md = Export();
            Assert.StartsWith("# Sample Flow", md);
            Assert.Contains("Power Automate Flow", md);
            Assert.Contains("`account` table", md);
            Assert.Contains("activated", md);
        }

        [Fact]
        public void Overview_NamesTheTriggerAndTheParallelBranches()
        {
            var md = Export();
            Assert.Contains("triggered by **When a row is added**", md);
            Assert.Contains("Steps that run at the same time", md);
            Assert.Contains("3 branches run at the same time", md);
            Assert.Contains("must finish before **Check it**", md);
        }

        [Fact]
        public void Relationships_AreStatedAsText_NotLeftToGeometry()
        {
            var md = Export();
            // The thing a diagram cannot give a retrieval system.
            Assert.Contains("Runs after **Compose - Parallelize**", md);
            Assert.Contains("If **Yes**, continues to **Send an email**", md);
        }

        [Fact]
        public void NestedSteps_AreNestedHeadings()
        {
            var md = Export();
            var lines = md.Split('\n');
            var parent = Array.FindIndex(lines, l => l.StartsWith("### Check it"));
            var child = Array.FindIndex(lines, l => l.StartsWith("#### Send an email"));
            Assert.True(parent >= 0 && child > parent, "child heading should sit under its parent");
            Assert.Contains("Contains 2 steps", md);
        }

        [Fact]
        public void MultiLineValues_GoInFencedBlocks_AndFencesStayBalanced()
        {
            var md = MarkdownExporter.Build(ProcessMapBuilder.Build(new ProcessItem
            {
                Name = "Fetch Flow",
                Category = 5,
                ClientData = "{\"properties\":{\"definition\":{\"triggers\":{},\"actions\":{" +
                             "\"List_rows\":{\"type\":\"OpenApiConnection\",\"runAfter\":{},\"inputs\":{\"parameters\":{" +
                             "\"fetchXml\":\"<fetch><entity name=\\\"contact\\\" /></fetch>\"}}}}}}}"
            }));
            int fences = md.Split(new[] { "```" }, StringSplitOptions.None).Length - 1;
            Assert.True(fences > 0, "the fetchXml should have produced a fenced block");
            Assert.True(fences % 2 == 0, $"{fences} fences — every block must be closed");
        }

        [RealFlowFact]
        public void RealFlow_ExportsWithoutLosingSteps()
        {
            var map = ProcessMapBuilder.Build(new ProcessItem
            {
                Name = "Integration - LAPD",
                Category = 5,
                ClientData = File.ReadAllText(RealFlowFactAttribute.FixturePath)
            });
            var md = MarkdownExporter.Build(map);

            // Every step gets a heading of its own.
            int headings = md.Split('\n').Count(l => l.StartsWith("###"));
            Assert.True(headings >= map.Graph.Nodes.Count,
                $"{headings} headings for {map.Graph.Nodes.Count} steps");
            Assert.Contains("8 branches run at the same time", md);
            Assert.DoesNotContain("\n####### ", md);

            // Logical names must survive verbatim or the index stops matching
            // what anyone would actually search for.
            Assert.Contains("jn_integrationlog", md);
            Assert.DoesNotContain("jn\\_", md);
        }
    }
}
