using System;
using System.IO;
using Xunit;

namespace DataverseProcessMapper.Tests
{
    /// <summary>
    /// A test that needs the real large-flow benchmark. The benchmark is a client's
    /// production flow definition, so it is kept out of the public repository
    /// (see .gitignore); on a clone without it these tests report as skipped
    /// rather than failing.
    /// </summary>
    public sealed class RealFlowFactAttribute : FactAttribute
    {
        public const string FileName = "lapd-clientdata.json";

        /// <summary>Where the copied benchmark sits next to the test assembly.</summary>
        public static string FixturePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);

        public RealFlowFactAttribute()
        {
            if (!File.Exists(FixturePath))
                Skip = "Large-flow benchmark not present. Save a flow's clientdata as " +
                       "Tests/" + FileName + " to run it (the file is git-ignored).";
        }
    }
}
