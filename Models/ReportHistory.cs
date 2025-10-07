using System;
namespace cms_api.Models
{
	public class ReportHistory : Identity
    {
		public ReportHistory()
		{
            reportStatus = "";
            officer = "";
        }

        public string reportStatus { get; set; }
        public string officer { get; set; }
    }
}

