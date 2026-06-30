using System;

namespace DataverseProcessMapper.Models
{
    /// <summary>
    /// Lightweight representation of a row from the Dataverse <c>workflow</c> table.
    /// </summary>
    public class ProcessItem
    {
        public Guid Id { get; set; }

        /// <summary>The <c>name</c> column — used as the display label in the lists.</summary>
        public string Name { get; set; }

        /// <summary>
        /// The <c>category</c> option set value.
        /// 0 = Classic Workflow, 2 = Business Rule, 3 = Action,
        /// 4 = Business Process Flow, 5 = Modern Flow (Power Automate).
        /// </summary>
        public int Category { get; set; }

        /// <summary>WF4 XAML definition (classic workflows / business rules).</summary>
        public string Xaml { get; set; }

        /// <summary>JSON definition (Power Automate / modern flows).</summary>
        public string ClientData { get; set; }

        /// <summary>Logical name of the table the process runs against, when applicable.</summary>
        public string PrimaryEntity { get; set; }

        /// <summary>statecode: 0 = Draft, 1 = Activated.</summary>
        public int State { get; set; }

        public DateTime? ModifiedOn { get; set; }

        public bool IsModernFlow => Category == 5;

        public string CategoryLabel
        {
            get
            {
                switch (Category)
                {
                    case 0: return "Classic Workflow";
                    case 1: return "Dialog";
                    case 2: return "Business Rule";
                    case 3: return "Action";
                    case 4: return "Business Process Flow";
                    case 5: return "Power Automate Flow";
                    case 6: return "Desktop Flow";
                    case 7: return "AI Flow";
                    default: return "Category " + Category;
                }
            }
        }

        public string StateLabel => State == 1 ? "Activated" : "Draft";
    }
}
