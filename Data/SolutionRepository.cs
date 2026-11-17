using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DataverseProcessMapper.Data
{
    /// <summary>A solution offered by the solution filter.</summary>
    public sealed class SolutionInfo
    {
        public Guid Id;
        public string UniqueName;
        public string FriendlyName;
        public bool IsManaged;

        /// <summary>What the filter's drop-down shows.</summary>
        public override string ToString()
            => (string.IsNullOrWhiteSpace(FriendlyName) ? UniqueName : FriendlyName) + (IsManaged ? " (managed)" : "");
    }

    /// <summary>
    /// Reads solutions and the processes they contain, for filtering the
    /// process lists by solution.
    /// </summary>
    public static class SolutionRepository
    {
        // solutioncomponent.componenttype for a process (workflow table row).
        private const int ComponentTypeWorkflow = 29;

        private const int PageSize = 5000;

        /// <summary>The visible solutions (hidden system ones left out), sorted by friendly name.</summary>
        public static List<SolutionInfo> RetrieveSolutions(IOrganizationService service)
        {
            var query = new QueryExpression("solution")
            {
                ColumnSet = new ColumnSet("solutionid", "uniquename", "friendlyname", "ismanaged"),
                Criteria = new FilterExpression(LogicalOperator.And),
                Orders = { new OrderExpression("friendlyname", OrderType.Ascending) }
            };
            query.Criteria.AddCondition("isvisible", ConditionOperator.Equal, true);

            var solutions = RetrieveAll(service, query).Select(e => new SolutionInfo
            {
                Id = e.Id,
                UniqueName = e.GetAttributeValue<string>("uniquename"),
                FriendlyName = e.GetAttributeValue<string>("friendlyname"),
                IsManaged = e.GetAttributeValue<bool>("ismanaged")
            });

            // Sorted here too: the server's collation need not match the user's.
            return solutions
                .OrderBy(s => (string.IsNullOrWhiteSpace(s.FriendlyName) ? s.UniqueName : s.FriendlyName) ?? "",
                    StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(s => s.UniqueName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>The workflowids of every process in the solution.</summary>
        public static HashSet<Guid> RetrieveProcessIds(IOrganizationService service, Guid solutionId)
        {
            var query = new QueryExpression("solutioncomponent")
            {
                ColumnSet = new ColumnSet("objectid"),
                Criteria = new FilterExpression(LogicalOperator.And)
            };
            query.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solutionId);
            query.Criteria.AddCondition("componenttype", ConditionOperator.Equal, ComponentTypeWorkflow);

            var ids = new HashSet<Guid>();
            foreach (var e in RetrieveAll(service, query))
            {
                var id = e.GetAttributeValue<Guid?>("objectid");
                if (id.HasValue) ids.Add(id.Value);
            }
            return ids;
        }

        /// <summary>Every row of the query, page by page.</summary>
        private static IEnumerable<Entity> RetrieveAll(IOrganizationService service, QueryExpression query)
        {
            query.PageInfo = new PagingInfo { Count = PageSize, PageNumber = 1 };
            while (true)
            {
                var page = service.RetrieveMultiple(query);
                foreach (var e in page.Entities) yield return e;

                if (!page.MoreRecords) yield break;
                query.PageInfo.PageNumber++;
                query.PageInfo.PagingCookie = page.PagingCookie;
            }
        }
    }
}
