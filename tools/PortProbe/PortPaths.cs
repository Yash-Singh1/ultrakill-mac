using System;
using System.IO;
namespace ULTRAKILL.MacPort
{
    public static class PortPaths
    {
        public static string Diagnostics(string applicationDataPath)
        {
            string selected = Environment.GetEnvironmentVariable("ULTRAKILL_MAC_DIAGNOSTICS_PATH");
            if (!string.IsNullOrEmpty(selected)) return selected;
            for (var directory = new DirectoryInfo(applicationDataPath); directory != null; directory = directory.Parent)
                if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && directory.Parent != null)
                    return Path.Combine(directory.Parent.FullName, "diagnostics");
            throw new InvalidOperationException("No Mac application bundle in data path.");
        }
    }
}
