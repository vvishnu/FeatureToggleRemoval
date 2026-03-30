using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FeatureToggleAgent
{
    public enum RemovalStatus
    {
        Success,
        Failed,
        NoFilesFound,
        PartialSuccess
    }
}
