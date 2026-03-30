using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FeatureToggleAgent
{
    public class AzureDevOpsConfigOptions
    {
        public const string Section = "AzureDevops";
        public string OrganizationUrl { get; set; }
        public string Project { get; set; }
        public string Repository { get; set; }
        public string PersonalAccessToken { get; set; }
    }
}
