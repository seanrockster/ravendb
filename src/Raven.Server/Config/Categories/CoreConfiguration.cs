using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Raven.Server.Config.Attributes;
using Raven.Server.Documents;
using Raven.Server.Utils;

namespace Raven.Server.Config.Categories
{
    public class CoreConfiguration : ConfigurationCategory
    {
        private bool runInMemory;
        private string workingDirectory;
        private string dataDirectory;

        [Description("Whatever the database should run purely in memory. When running in memory, nothing is written to disk and if the server is restarted all data will be lost. This is mostly useful for testing.")]
        [DefaultValue(false)]
        [ConfigurationEntry("Raven/RunInMemory")]
        public bool RunInMemory
        {
            get { return runInMemory; }
            set
            {
                runInMemory = value;
            }
        }

        [DefaultValue(@"~\")]
        [ConfigurationEntry("Raven/WorkingDir")]
        public string WorkingDirectory
        {
            get { return workingDirectory; }
            set { workingDirectory = CalculateWorkingDirectory(value); }
        }

        [Description("The directory for the RavenDB database. You can use the ~\\ prefix to refer to RavenDB's base directory.")]
        [DefaultValue(@"~\Databases\System")]
        [ConfigurationEntry("Raven/DataDir")]
        public string DataDirectory
        {
            get { return dataDirectory; }
            set { dataDirectory = value == null ? null : FilePathTools.ApplyWorkingDirectoryToPathAndMakeSureThatItEndsWithSlash(WorkingDirectory, value); }
        }

        public override void Initialize(NameValueCollection settings)
        {
            base.Initialize(settings);
        }

        private static string CalculateWorkingDirectory(string workingDirectory)
        {
            if (string.IsNullOrEmpty(workingDirectory))
                workingDirectory = @"~\";

            if (workingDirectory.StartsWith("APPDRIVE:", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("AppDomain.CurrentDomain.BaseDirectory is not exist in DNX");
                var baseDirectory = "";
                var rootPath = Path.GetPathRoot(baseDirectory);
                if (string.IsNullOrEmpty(rootPath) == false)
                    workingDirectory = Regex.Replace(workingDirectory, "APPDRIVE:", rootPath.TrimEnd('\\'), RegexOptions.IgnoreCase);
            }

            return FilePathTools.MakeSureEndsWithSlash(workingDirectory.ToFullPath());
        }
    }
}