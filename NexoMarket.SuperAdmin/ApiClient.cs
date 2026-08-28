using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace NexoMarket.SuperAdmin
{
    internal sealed class ApiClient
    {
        public string BaseUrl { get; set; }
        public string AdminKey { get; set; }

        private string Request(string path, string method, Dictionary<string,string> data)
        {
            string url = (BaseUrl ?? "").Trim().TrimEnd('/') + path;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            req.Headers["X-Nexo-Admin-Key"] = AdminKey ?? "";
            if (method == "POST")
            {
                string body = Encode(data);
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                req.ContentType = "application/x-www-form-urlencoded; charset=UTF-8";
                req.ContentLength = bytes.Length;
                using (Stream s = req.GetRequestStream()) s.Write(bytes,0,bytes.Length);
            }
            try
            {
                using (HttpWebResponse res=(HttpWebResponse)req.GetResponse())
                using (StreamReader r=new StreamReader(res.GetResponseStream(),Encoding.UTF8)) return r.ReadToEnd();
            }
            catch(WebException ex)
            {
                if(ex.Response!=null) using(StreamReader r=new StreamReader(ex.Response.GetResponseStream(),Encoding.UTF8)) return r.ReadToEnd();
                return "ERROR|"+ex.Message;
            }
        }
        private static string Encode(Dictionary<string,string> data)
        {
            if(data==null)return ""; StringBuilder b=new StringBuilder();
            foreach(KeyValuePair<string,string> p in data){if(b.Length>0)b.Append('&');b.Append(Uri.EscapeDataString(p.Key??""));b.Append('=');b.Append(Uri.EscapeDataString(p.Value??""));}
            return b.ToString();
        }
        public string Overview(){return Request("/api/admin/overview","GET",null);}
        public string Stores(){return Request("/api/admin/stores","GET",null);}
        public string Accounts(){return Request("/api/admin/accounts","GET",null);}
        public string CreateStore(Dictionary<string,string> d){return Request("/api/admin/store/create","POST",d);}
        public string DeleteStore(string id){return Request("/api/admin/store/delete","POST",new Dictionary<string,string>{{"storeId",id}});}
        public string SetStoreActive(string id,bool active){return Request("/api/admin/store/active","POST",new Dictionary<string,string>{{"storeId",id},{"active",active?"1":"0"}});}
        public string SetStoreFeatured(string id,bool featured){return Request("/api/admin/store/featured","POST",new Dictionary<string,string>{{"storeId",id},{"featured",featured?"1":"0"}});}
        public string StoreMedia(Dictionary<string,string> d){return Request("/api/admin/store/media","POST",d);}
        public string SetStorePlus(string id,bool storePlus){return Request("/api/admin/store/store-plus","POST",new Dictionary<string,string>{{"storeId",id},{"storePlus",storePlus?"1":"0"}});}
        public string SetTrial(string email,int days){return Request("/api/admin/account/trial","POST",new Dictionary<string,string>{{"email",email},{"days",days.ToString()}});}
        public string SetAccountActive(string email,bool active){return Request("/api/admin/account/active","POST",new Dictionary<string,string>{{"email",email},{"active",active?"1":"0"}});}
        public string DeleteAccount(string email){return Request("/api/admin/account/delete","POST",new Dictionary<string,string>{{"email",email}});}
        public string FactoryReset(){return Request("/api/admin/factory-reset","POST",new Dictionary<string,string>{{"confirm","NEXO-FACTORY-RESET"}});}
        public string Audit(string storeId,int limit){return Request("/api/audit?storeId="+Uri.EscapeDataString(storeId??"")+"&limit="+limit.ToString(),"GET",null);}
    }
}
