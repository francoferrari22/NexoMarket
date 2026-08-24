using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using NexoMarket.Admin.Models;

namespace NexoMarket.Admin.UI
{
    public sealed class CentralAccountService
    {
        private readonly string _baseUrl;
        public CentralAccountService(string baseUrl) { _baseUrl = (baseUrl ?? "").Trim().TrimEnd('/'); }
        public bool Enabled { get { return _baseUrl.Length > 0 && _baseUrl.IndexOf("tudominio.com", StringComparison.OrdinalIgnoreCase) < 0; } }

        public string Register(WebUser user)
        {
            if (!Enabled || user == null) return "";
            Dictionary<string,string> f = new Dictionary<string,string>();
            f["email"] = user.Email ?? ""; f["passwordHash"] = user.PasswordHash ?? ""; f["salt"] = user.Salt ?? "";
            f["name"] = user.Name ?? ""; f["phone"] = user.Phone ?? ""; f["role"] = user.Role ?? "buyer"; f["storeId"] = user.StoreId ?? "";
            return Post("/api/accounts/register", f);
        }

        public string Login(string email, string passwordHash, string salt, string role, string storeId)
        {
            if (!Enabled) return "";
            Dictionary<string,string> f = new Dictionary<string,string>();
            f["email"] = email ?? ""; f["passwordHash"] = passwordHash ?? ""; f["salt"] = salt ?? ""; f["role"] = role ?? ""; f["storeId"] = storeId ?? "";
            return Post("/api/accounts/login", f);
        }

        private string Post(string path, Dictionary<string,string> fields)
        {
            try
            {
                StringBuilder b = new StringBuilder();
                foreach (KeyValuePair<string,string> x in fields)
                {
                    if (b.Length > 0) b.Append('&');
                    b.Append(Uri.EscapeDataString(x.Key)); b.Append('='); b.Append(Uri.EscapeDataString(x.Value ?? ""));
                }
                byte[] data = Encoding.UTF8.GetBytes(b.ToString());
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(_baseUrl + path);
                req.Method = "POST"; req.ContentType = "application/x-www-form-urlencoded"; req.ContentLength = data.Length; req.Timeout = 7000;
                using (Stream st = req.GetRequestStream()) st.Write(data, 0, data.Length);
                using (WebResponse resp = req.GetResponse()) using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd().Trim();
            }
            catch { return ""; }
        }
    }
}
