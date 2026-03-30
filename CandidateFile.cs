using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FeatureToggleAgent
{
    public class CandidateFile
    {
        public CandidateFile(string path, string content, List<string> matched)
        {
            Path = path;
            Content = content;
            Matched = matched;
        }
        public string Path { get; set; }
        public string Content { get; set; }
        public List<string> Matched { get; set; }
    }
}
