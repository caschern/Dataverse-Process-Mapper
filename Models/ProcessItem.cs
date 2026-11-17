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

        /// <summary>
        /// True once <see cref="Xaml"/> / <see cref="ClientData"/> have been
        /// fetched. The process list is loaded without them — they are by far the
        /// largest columns and most processes are never opened.
        /// </summary>
        public bool DefinitionLoaded { get; set; }

        /// <summary>Logical name of the table the process runs against, when applicable.</summary>
        public string PrimaryEntity { get; set; }

        /// <summary>statecode: 0 = Draft, 1 = Activated, 2 = Suspended.</summary>
        public int State { get; set; }

        public DateTime? ModifiedOn { get; set; }

        public DateTime? CreatedOn { get; set; }

        /// <summary>Display name of the <c>createdby</c> user.</summary>
        public string CreatedBy { get; set; }

        /// <summary>Display name of the <c>modifiedby</c> user.</summary>
        public string ModifiedBy { get; set; }

        /// <summary>Display name of the <c>ownerid</c> user or team.</summary>
        public string Owner { get; set; }

        /// <summary>Localized label of the <c>scope</c> option set (e.g. "Organization").</summary>
        public string Scope { get; set; }

        /// <summary>
        /// The <c>workflowidunique</c> column: the id the Power Automate portal uses
        /// in a flow's URL (it differs from <see cref="Id"/>). Null when not fetched.
        /// </summary>
        public Guid? WorkflowIdUnique { get; set; }

        public bool IsManaged { get; set; }

        public string Description { get; set; }

        /// <summary>Classic workflow starts when a record is created.</summary>
        public bool TriggerOnCreate { get; set; }

        /// <summary>Classic workflow starts when a record is deleted.</summary>
        public bool TriggerOnDelete { get; set; }

        /// <summary>
        /// The <c>triggeronupdateattributelist</c> column: comma-separated logical
        /// names whose change starts the workflow. Empty or null = not on update.
        /// </summary>
        public string TriggerOnUpdateAttributes { get; set; }

        /// <summary>Classic workflow can be run on demand.</summary>
        public bool OnDemand { get; set; }

        /// <summary>The <c>subprocess</c> column: the workflow can run as a child process.</summary>
        public bool IsChild { get; set; }

        /// <summary>The <c>mode</c> option set: 0 = Background, 1 = Real-time, -1 = unknown.</summary>
        public int Mode { get; set; } = -1;

        /// <summary>The <c>runas</c> option set: 0 = Owner, 1 = Calling user, -1 = unknown.</summary>
        public int RunAs { get; set; } = -1;

        /// <summary>
        /// One-line description of what starts the process ("Dataverse: account –
        /// Added", "Create, Delete · On demand"). Classic workflows get it from the
        /// list columns; flows only once their definition has been read, so it is
        /// null until then. It outlives the definition text (see DefinitionStore).
        /// </summary>
        public string TriggerSummary { get; set; }

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

        /// <summary>
        /// The statecode's label. Suspended is what a flow becomes when a DLP
        /// policy switches it off, so it must never read as Draft.
        /// </summary>
        public string StateLabel
        {
            get
            {
                switch (State)
                {
                    case 0: return "Draft";
                    case 1: return "Activated";
                    case 2: return "Suspended";
                    default: return "State " + State;
                }
            }
        }
    }
}
