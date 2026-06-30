using System.Collections.Generic;
using DataverseProcessMapper.Models;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DataverseProcessMapper.Data
{
    /// <summary>
    /// Reads process definitions from the Dataverse <c>workflow</c> table.
    /// </summary>
    public static class WorkflowRepository
    {
        // workflow.type option set: 1 = Definition, 2 = Activation, 3 = Template.
        private const int TypeDefinition = 1;

        // Categories we know how to visualise.
        public const int CategoryClassicWorkflow = 0;
        public const int CategoryModernFlow = 5;

        /// <summary>
        /// Retrieves all classic workflow and modern flow <em>definitions</em>.
        /// Uses paging so environments with many processes are fully returned.
        /// </summary>
        public static List<ProcessItem> RetrieveProcesses(IOrganizationService service)
        {
            var query = new QueryExpression("workflow")
            {
                ColumnSet = new ColumnSet(
                    "workflowid", "name", "category", "type",
                    "xaml", "clientdata", "primaryentity", "statecode", "modifiedon"),
                Criteria = new FilterExpression(LogicalOperator.And),
                PageInfo = new PagingInfo { Count = 500, PageNumber = 1 },
                Orders = { new OrderExpression("name", OrderType.Ascending) }
            };

            query.Criteria.AddCondition("type", ConditionOperator.Equal, TypeDefinition);
            query.Criteria.AddCondition("category", ConditionOperator.In,
                CategoryClassicWorkflow, CategoryModernFlow);

            var results = new List<ProcessItem>();

            while (true)
            {
                var page = service.RetrieveMultiple(query);
                foreach (var e in page.Entities)
                {
                    results.Add(Map(e));
                }

                if (!page.MoreRecords) break;
                query.PageInfo.PageNumber++;
                query.PageInfo.PagingCookie = page.PagingCookie;
            }

            return results;
        }

        private static ProcessItem Map(Entity e)
        {
            return new ProcessItem
            {
                Id = e.Id,
                Name = e.GetAttributeValue<string>("name"),
                Category = GetOptionSet(e, "category"),
                Xaml = e.GetAttributeValue<string>("xaml"),
                ClientData = e.GetAttributeValue<string>("clientdata"),
                PrimaryEntity = e.GetAttributeValue<string>("primaryentity"),
                State = GetOptionSet(e, "statecode"),
                ModifiedOn = e.Contains("modifiedon")
                    ? e.GetAttributeValue<System.DateTime>("modifiedon")
                    : (System.DateTime?)null
            };
        }

        private static int GetOptionSet(Entity e, string attr)
        {
            var v = e.GetAttributeValue<OptionSetValue>(attr);
            return v?.Value ?? -1;
        }
    }
}
