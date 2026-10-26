using System.Collections.Generic;

namespace cms_api.Models
{
    public class AiWebChatRequest
    {
        public string message { get; set; }
        public string knowledgeGroup { get; set; }
        public string province { get; set; }
        public string district { get; set; }
    }

    public class AiWebChatResponse
    {
        public string status { get; set; }
        public string message { get; set; }
        public string answer { get; set; }
        public string model { get; set; }
        public List<AiWebChatSource> sources { get; set; }
    }

    public class AiWebChatSource
    {
        public string label { get; set; }
        public string url { get; set; }
        public string docId { get; set; }
        public string referenceId { get; set; }
        public string filePath { get; set; }
    }
}
