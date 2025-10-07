using System;
using System.Collections.Generic;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace cms_api.Models
{
    public class News : Identity
    {

        public News()
        {
            imageUrl = "";
            imageUrlCreateBy = "";
            view = 0;
            totalLv = 0;
            app = "khubdeedlt";
        }

        public string imageUrl { get; set; }
        public string imageUrlCreateBy { get; set; }
        public int view { get; set; }
        public int totalLv { get; set; }
        public string app { get; set; }
    }
}
