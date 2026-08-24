using System;
using System.IO;
using System.Net;
using System.Text;
using NexoMarket.Licensing;

namespace NexoMarket.Admin.UI
{
    public sealed class LicenseService
    {
        private readonly string _dataRoot;
        public LicenseService(string dataRoot) { _dataRoot = dataRoot; }
        public string MachineId { get { return LicenseCore.MachineId(); } }
        public string LicensePath { get { return Path.Combine(_dataRoot, "nexomarket.license"); } }

        public LicenseRecord Load()
        {
            try
            {
                if (!File.Exists(LicensePath)) return null;
                LicenseRecord r;
                return LicenseCore.TryParse(File.ReadAllText(LicensePath, Encoding.UTF8).Trim(), out r) ? r : null;
            }
            catch { return null; }
        }

        public string GetPublicKey()
        {
            string p = Path.Combine(_dataRoot, "license_public_key.xml");
            try { return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : ""; } catch { return ""; }
        }

        public bool IsValid(out string status, out int daysRemaining)
        {
            LicenseRecord r = Load();
            status = LicenseCore.Status(r, DateTime.UtcNow);
            daysRemaining = LicenseCore.DaysRemaining(r, DateTime.UtcNow);
            string remote = GetRemoteStatus();
            if (!string.IsNullOrWhiteSpace(remote) && !string.Equals(remote, "Active", StringComparison.OrdinalIgnoreCase))
                status = remote;
            if (r == null || !string.Equals(r.StoreId, StoreId(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(r.MachineId, MachineId, StringComparison.OrdinalIgnoreCase)) return false;
            string pub = GetPublicKey();
            return LicenseCore.Verify(r, pub) && status == "Activa";
        }

        private string RemoteStatusPath { get { return Path.Combine(_dataRoot, "license_remote_status.txt"); } }
        private string GetRemoteStatus()
        {
            try { return File.Exists(RemoteStatusPath) ? File.ReadAllText(RemoteStatusPath, Encoding.UTF8).Trim() : ""; } catch { return ""; }
        }

        public string StoreId()
        {
            string p = Path.Combine(_dataRoot, "nexomarket_data.xml");
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(p);
                var e = doc.Root.Element("Settings").Elements("Setting");
                foreach (var x in e) if ((string)x.Attribute("Key") == "store_id") return (string)x.Attribute("Value") ?? "";
            } catch { }
            return "";
        }

        public void InstallToken(string token, string publicKeyXml)
        {
            LicenseRecord r;
            if (!LicenseCore.TryParse(token, out r)) throw new InvalidDataException("Licencia inválida.");
            if (!string.Equals(r.StoreId, StoreId(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Store ID no coincide.");
            if (!string.Equals(r.MachineId, MachineId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Machine ID no coincide con esta PC.");
            if (!LicenseCore.Verify(r, publicKeyXml)) throw new InvalidDataException("Firma digital inválida.");
            Directory.CreateDirectory(_dataRoot);
            File.WriteAllText(LicensePath, token.Trim(), Encoding.UTF8);
            File.WriteAllText(Path.Combine(_dataRoot, "license_public_key.xml"), publicKeyXml, Encoding.UTF8);
        }

        public bool RefreshFromServer(string baseUrl)
        {
            try
            {
                baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
                if (baseUrl.Length == 0) return false;
                string url = baseUrl + "/api/licenses/status?storeId=" + Uri.EscapeDataString(StoreId()) + "&machineId=" + Uri.EscapeDataString(MachineId);
                HttpWebRequest req=(HttpWebRequest)WebRequest.Create(url); req.Method="GET"; req.Timeout=7000;
                using(WebResponse resp=req.GetResponse())
                using(StreamReader sr=new StreamReader(resp.GetResponseStream(),Encoding.UTF8))
                {
                    string token=sr.ReadToEnd().Trim();
                    if (token.StartsWith("REVOKED|", StringComparison.OrdinalIgnoreCase))
                    {
                        File.WriteAllText(RemoteStatusPath, token.Substring(8).Trim(), Encoding.UTF8);
                        return false;
                    }
                    LicenseRecord r;
                    if (!LicenseCore.TryParse(token,out r)) return false;
                    string pub=GetPublicKey();
                    if (!LicenseCore.Verify(r,pub)) return false;
                    File.WriteAllText(LicensePath,token,Encoding.UTF8);
                    if(File.Exists(RemoteStatusPath))File.Delete(RemoteStatusPath);
                    return true;
                }
            } catch { return false; }
        }
    }
}
