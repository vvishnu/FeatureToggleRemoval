using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FeatureToggleAgent
{
    public class RemovalResult
    {
        public string FeatureName { get; set; }
        public RemovalStatus Status { get; set; }
        public string BranchName { get; set; }
        public int FilesIdentified { get; set; }
        public int FilesModified { get; set; }
        public List<string> ModifiedFiles { get; set; } = new();
        public string CommitHash { get; set; }
        public int? PullRequestNumber { get; set; }
        public string PullRequestUrl { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime CompletedTime { get; set; }
        public string ErrorMessage { get; set; }
        public List<FileError> FailedFiles { get; set; } = new();

        public TimeSpan ExecutionTime => CompletedTime - StartTime;
    }
}
