using System;
using DataverseProcessMapper.Models;
using XrmToolBox.Extensibility;

namespace DataverseProcessMapper
{
    /// <summary>
    /// What the tool remembers between sessions. XML-serialised by XrmToolBox's
    /// SettingsManager, so it keeps public settable properties and a public
    /// parameterless constructor; a value missing from an older file keeps its default.
    /// </summary>
    public class ProcessMapperSettings
    {
        /// <summary>The map layout, one setting for both tabs.</summary>
        public LayoutMode LayoutMode { get; set; } = ProcessMapBuilder.DefaultMode;

        /// <summary>Where the last export was saved; the next export's dialogs start there.</summary>
        public string LastExportFolder { get; set; }

        public int FlowSortColumn { get; set; }
        public bool FlowSortDescending { get; set; }
        public int WorkflowSortColumn { get; set; }
        public bool WorkflowSortDescending { get; set; }

        /// <summary>The process list's width in pixels as last dragged; 0 = the default.</summary>
        public int ListWidth { get; set; }

        public ProcessMapperSettings Clone() => (ProcessMapperSettings)MemberwiseClone();

        public bool SameAs(ProcessMapperSettings other) =>
            other != null &&
            LayoutMode == other.LayoutMode &&
            string.Equals(LastExportFolder, other.LastExportFolder, StringComparison.Ordinal) &&
            FlowSortColumn == other.FlowSortColumn && FlowSortDescending == other.FlowSortDescending &&
            WorkflowSortColumn == other.WorkflowSortColumn && WorkflowSortDescending == other.WorkflowSortDescending &&
            ListWidth == other.ListWidth;
    }

    /// <summary>Where <see cref="ProcessMapperSettings"/> are kept; tests use one in memory.</summary>
    public interface ISettingsStore
    {
        /// <summary>The saved settings, or null when there are none (or they cannot be read).</summary>
        ProcessMapperSettings Load();

        void Save(ProcessMapperSettings settings);
    }

    /// <summary>XrmToolBox's settings folder, in a file named after the tool's assembly.</summary>
    public sealed class XrmToolBoxSettingsStore : ISettingsStore
    {
        private readonly Type _pluginType;

        public XrmToolBoxSettingsStore(Type pluginType)
        {
            _pluginType = pluginType ?? throw new ArgumentNullException(nameof(pluginType));
        }

        public ProcessMapperSettings Load()
        {
            ProcessMapperSettings settings;
            return SettingsManager.Instance.TryLoad(_pluginType, out settings) ? settings : null;
        }

        public void Save(ProcessMapperSettings settings) => SettingsManager.Instance.Save(_pluginType, settings);
    }
}
