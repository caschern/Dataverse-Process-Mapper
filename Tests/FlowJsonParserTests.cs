using System.Linq;
using DataverseProcessMapper.Layout;
using DataverseProcessMapper.Models;
using DataverseProcessMapper.Parsing;
using Xunit;

namespace DataverseProcessMapper.Tests
{
    public class FlowJsonParserTests
    {
        /// <summary>A small but representative flow definition: scalar-input
        /// Compose (the JValue regression), a Scope with a nested connector
        /// action carrying fetchXml, and an If with both branches.</summary>
        internal const string SampleDefinition = @"{
          ""triggers"": { ""When_a_row_is_added"": { ""type"": ""OpenApiConnectionWebhook"" } },
          ""actions"": {
            ""Compose"": { ""type"": ""Compose"", ""inputs"": ""@variables('x')"", ""runAfter"": {} },
            ""Scope_1"": { ""type"": ""Scope"", ""runAfter"": { ""Compose"": [""Succeeded""] }, ""actions"": {
                ""Inner_List"": { ""type"": ""OpenApiConnection"", ""runAfter"": {}, ""inputs"": {
                    ""host"": { ""operationId"": ""ListRecords"", ""apiId"": ""/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps"" },
                    ""parameters"": { ""entityName"": ""accounts"", ""fetchXml"": ""<fetch><entity name=\""account\"" /></fetch>"" } } } } },
            ""Condition"": { ""type"": ""If"", ""expression"": { ""equals"": [1, 1] },
                ""runAfter"": { ""Scope_1"": [""Succeeded""] },
                ""actions"": { ""Yes_Step"": { ""type"": ""Compose"", ""inputs"": ""1"", ""runAfter"": {} } },
                ""else"": { ""actions"": { ""No_Step"": { ""type"": ""Compose"", ""inputs"": ""2"", ""runAfter"": {} } } } }
          }}";

        internal static ProcessItem SampleFlow() => new ProcessItem
        {
            Name = "Test Flow",
            Category = 5,
            ClientData = "{\"properties\":{\"definition\":" + SampleDefinition + "}}"
        };

        private static ProcessGraph Parse() => new FlowJsonParser().Parse(SampleFlow());

        private static ProcessNode ByLabel(ProcessGraph g, string label)
            => g.Nodes.Single(n => n.Label == label);

        [Fact]
        public void Parse_BuildsExpectedNodes()
        {
            var g = Parse();
            // trigger + Compose + Scope + Inner + Condition + Yes + No = 7
            Assert.Equal(7, g.Nodes.Count);
        }

        [Fact]
        public void ScalarInputs_DoNotThrow_AndSurfaceAsDetail()
        {
            var g = Parse(); // would throw before the JValue guard
            var compose = ByLabel(g, "Compose");
            Assert.Contains(compose.Details, d => d.Key == "Inputs" && d.Value.Contains("@variables('x')"));
        }

        [Fact]
        public void NestedAction_GetsParentId()
        {
            var g = Parse();
            var scope = ByLabel(g, "Scope 1");
            var inner = ByLabel(g, "Inner List");
            Assert.Equal(scope.Id, inner.ParentId);
            Assert.Null(scope.ParentId);
        }

        [Fact]
        public void IfBranches_AreLabeledYesNo()
        {
            var g = Parse();
            var condition = ByLabel(g, "Condition");
            var labels = g.Edges.Where(e => e.FromId == condition.Id).Select(e => e.Label).ToList();
            Assert.Contains("Yes", labels);
            Assert.Contains("No", labels);
        }

        /// <summary>A switch with a populated case, a declared-but-empty case, and a
        /// default — plus an empty Scope and an If whose else branch has no actions.</summary>
        private const string BranchDefinition = @"{
          ""triggers"": { ""Manual"": { ""type"": ""Request"" } },
          ""actions"": {
            ""Check_status"": {
              ""type"": ""Switch"", ""expression"": ""@variables('Status')"", ""runAfter"": {},
              ""cases"": {
                ""Case"":   { ""case"": ""Approved"", ""actions"": { ""Send_email"": { ""type"": ""Compose"", ""inputs"": ""1"", ""runAfter"": {} } } },
                ""Case_2"": { ""case"": ""On hold"",  ""actions"": {} }
              },
              ""default"": { ""actions"": {} }
            },
            ""Empty_scope"": { ""type"": ""Scope"", ""runAfter"": { ""Check_status"": [""Succeeded""] }, ""actions"": {} },
            ""Half_if"": {
              ""type"": ""If"", ""runAfter"": { ""Empty_scope"": [""Succeeded""] },
              ""actions"": { ""Then_step"": { ""type"": ""Compose"", ""inputs"": ""2"", ""runAfter"": {} } },
              ""else"": { ""actions"": {} }
            }
          }}";

        private static ProcessGraph ParseBranches() => new FlowJsonParser().Parse(new ProcessItem
        {
            Name = "Branch Flow",
            Category = 5,
            ClientData = "{\"properties\":{\"definition\":" + BranchDefinition + "}}"
        });

        [Fact]
        public void SwitchCases_BecomeChipsLabeledWithMatchedValue()
        {
            var g = ParseBranches();
            var chips = g.Nodes.Where(n => n.Kind == NodeKind.Case).Select(n => n.Label).ToList();
            // These keys are untouched designer defaults ("Case", "Case_2"), which
            // say nothing, so the matched value labels them instead.
            Assert.Equal(new[] { "Approved", "On hold", "default" }, chips);
            Assert.DoesNotContain("Case 2", chips);
        }

        [Fact]
        public void SwitchChips_HangOffTheSwitch_AndOwnTheirActions()
        {
            var g = ParseBranches();
            var sw = ByLabel(g, "Check status");
            var approved = ByLabel(g, "Approved");
            Assert.Equal(sw.Id, approved.ParentId);
            Assert.Contains(g.Edges, e => e.FromId == sw.Id && e.ToId == approved.Id);
            Assert.Equal(approved.Id, ByLabel(g, "Send email").ParentId);
        }

        [Fact]
        public void EmptyCase_AndEmptyDefault_StayVisible()
        {
            var g = ParseBranches();
            // Previously both vanished, understating the switch's real branch count.
            foreach (var branch in new[] { "On hold", "default" })
            {
                var chip = ByLabel(g, branch);
                Assert.Contains(g.Nodes, n => n.Kind == NodeKind.Empty && n.ParentId == chip.Id);
            }
        }

        [Fact]
        public void EmptyScopeBody_AndEmptyElseBranch_AreMarked()
        {
            var g = ParseBranches();
            var scope = ByLabel(g, "Empty scope");
            Assert.Contains(g.Nodes, n => n.Kind == NodeKind.Empty && n.ParentId == scope.Id);

            var half = ByLabel(g, "Half if");
            var elseMarker = g.Nodes.Single(n => n.Kind == NodeKind.Empty && n.ParentId == half.Id);
            Assert.Contains(g.Edges, e => e.FromId == half.Id && e.ToId == elseMarker.Id && e.Label == "No");
        }

        [Fact]
        public void PopulatedBranches_GetNoEmptyMarker()
        {
            var g = Parse(); // the original sample: every branch has actions
            Assert.DoesNotContain(g.Nodes, n => n.Kind == NodeKind.Empty);
        }

        /// <summary>A switch on a lookup whose case values are record GUIDs, with
        /// cases renamed in the designer — plus one left at its default name.</summary>
        private const string TitledSwitchDefinition = @"{
          ""triggers"": { ""Manual"": { ""type"": ""Request"" } },
          ""actions"": {
            ""Switch"": {
              ""type"": ""Switch"", ""expression"": ""@triggerBody()?['outcome']"", ""runAfter"": {},
              ""cases"": {
                ""Case_-_ATI_Finished_and_Closed"": { ""case"": ""46ee7808-fb59-f111-bec6-7ced8dde196c"",
                  ""actions"": { ""Condition_-_Diversion_Program_is_ATI"": { ""type"": ""Compose"", ""inputs"": ""1"", ""runAfter"": {} } } },
                ""Case_-_Returned_to_Filer"": { ""case"": ""664dcdee-fa59-f111-bec6-7ced8dde196c"", ""actions"": {} },
                ""Case_3"": { ""case"": ""Pending"", ""actions"": {} }
              },
              ""default"": { ""actions"": {} }
            }
          }}";

        private static ProcessGraph ParseTitled() => new FlowJsonParser().Parse(new ProcessItem
        {
            Name = "Titled Switch",
            Category = 5,
            ClientData = "{\"properties\":{\"definition\":" + TitledSwitchDefinition + "}}"
        });

        [Fact]
        public void RenamedCases_UseTheDesignersTitle_NotTheGuid()
        {
            var chips = ParseTitled().Nodes.Where(n => n.Kind == NodeKind.Case).Select(n => n.Label).ToList();
            Assert.Contains("Case - ATI Finished and Closed", chips);
            Assert.Contains("Case - Returned to Filer", chips);
            Assert.DoesNotContain(chips, l => l.StartsWith("46ee7808"));
        }

        [Fact]
        public void UntouchedDefaultCaseNames_FallBackToTheMatchedValue()
        {
            var chips = ParseTitled().Nodes.Where(n => n.Kind == NodeKind.Case).Select(n => n.Label).ToList();
            Assert.Contains("Pending", chips);
            Assert.DoesNotContain("Case 3", chips);
        }

        [Fact]
        public void MatchedValue_IsKeptInFullInDetails_AndShortenedUnderTheTitle()
        {
            var g = ParseTitled();
            var ati = ByLabel(g, "Case - ATI Finished and Closed");
            Assert.Contains(ati.Details, d => d.Key == "Equals" && d.Value == "46ee7808-fb59-f111-bec6-7ced8dde196c");
            Assert.Equal("equals 46ee7808…8dde196c", ati.Subtitle);

            // When the value already is the title, it is not repeated underneath.
            Assert.Null(ByLabel(g, "Pending").Subtitle);
        }

        [Fact]
        public void DefaultBranch_SaysWhenItRuns()
        {
            var def = ByLabel(ParseTitled(), "default");
            Assert.Contains(def.Details, d => d.Key == "Runs when" && d.Value == "no other case matches");
        }

        [Fact]
        public void FetchXml_IsPrettyPrintedMultiline()
        {
            var g = Parse();
            var inner = ByLabel(g, "Inner List");
            var fetch = inner.Details.Single(d => d.Key == "fetchXml").Value;
            Assert.StartsWith("<fetch", fetch.TrimStart());
            Assert.Contains("\n", fetch); // indented by DetailText
        }

        [Fact]
        public void ConnectorDetails_IncludeOperationAndConnector()
        {
            var g = Parse();
            var inner = ByLabel(g, "Inner List");
            Assert.Contains(inner.Details, d => d.Key == "Operation" && d.Value == "ListRecords");
            Assert.Contains(inner.Details, d => d.Key == "Connector" && d.Value == "shared_commondataserviceforapps");
        }

        [Fact]
        public void TriggerConditions_AreSurfacedInDetails()
        {
            // "conditions" is a sibling of inputs on the trigger, not inside it.
            var item = new ProcessItem
            {
                Name = "Conditioned Flow",
                Category = 5,
                ClientData = @"{""properties"":{""definition"":{
                  ""triggers"": { ""When_updated"": {
                      ""type"": ""OpenApiConnectionWebhook"",
                      ""conditions"": [
                        { ""expression"": ""@equals(triggerOutputs()?['body/statecode'], 0)"" },
                        { ""expression"": ""@not(empty(triggerOutputs()?['body/name']))"" }
                      ],
                      ""splitOn"": ""@triggerOutputs()?['body/value']"" } },
                  ""actions"": { ""Step"": { ""type"": ""Compose"", ""inputs"": ""1"", ""runAfter"": {} } }
                }}}"
            };

            var g = new FlowJsonParser().Parse(item);
            var trigger = g.Nodes.Single(n => n.Label == "When updated");

            Assert.Contains(trigger.Details, d =>
                d.Key == "Trigger condition 1" && d.Value.Contains("statecode"));
            Assert.Contains(trigger.Details, d =>
                d.Key == "Trigger condition 2" && d.Value.Contains("empty"));
            Assert.Contains(trigger.Details, d =>
                d.Key == "Split on" && d.Value.Contains("body/value"));
        }

        [Fact]
        public void CollapsedScope_HidesDescendants_AndReanchorsEdges()
        {
            var g = Parse();
            var scope = ByLabel(g, "Scope 1");
            var inner = ByLabel(g, "Inner List");
            var condition = ByLabel(g, "Condition");

            scope.Collapsed = true;
            var view = GraphContraction.BuildView(g);

            Assert.Null(view[inner.Id]);                    // hidden
            Assert.Equal(1, scope.HiddenCount);             // counted
            Assert.NotNull(view[scope.Id]);                 // container stays
            Assert.Contains(view.Edges, e => e.FromId == scope.Id && e.ToId == condition.Id);
            Assert.DoesNotContain(view.Edges, e => e.ToId == inner.Id || e.FromId == inner.Id);
        }
    }

    public class XamlWorkflowParserTests
    {
        private static ProcessItem Workflow(string xaml) => new ProcessItem
        {
            Name = "Test Workflow",
            Category = 0,
            Xaml = xaml
        };

        [Fact]
        public void Parse_RecognizesStepActivity_WithAttributeDetails()
        {
            var g = new XamlWorkflowParser().Parse(Workflow(
                "<Activity><Sequence>" +
                "<CreateEntity DisplayName=\"Create record\" EntityName=\"account\" />" +
                "</Sequence></Activity>"));

            var step = g.Nodes.Single(n => n.Label == "Create record");
            Assert.Contains(step.Details, d => d.Key == "EntityName" && d.Value == "account");

            // Start -> step -> End
            Assert.Contains(g.Edges, e => e.ToId == step.Id);
            Assert.Contains(g.Edges, e => e.FromId == step.Id);
        }
    }
}
