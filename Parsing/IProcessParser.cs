using DataverseProcessMapper.Models;

namespace DataverseProcessMapper.Parsing
{
    public interface IProcessParser
    {
        /// <summary>
        /// Builds a process graph from a process definition. Implementations
        /// should never throw for malformed input — return a graph containing a
        /// single Note node describing the problem instead.
        /// </summary>
        ProcessGraph Parse(ProcessItem item);
    }
}
