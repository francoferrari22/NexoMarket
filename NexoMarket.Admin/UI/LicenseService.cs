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
        private const int TrialDays = 30;
        public LicenseService(string dataRoot) { _dataRoot = dataRoot; EnsureTrialStart(); }
        public string MachineId { get { return LicenseCore.MachineId(); } }
        public string LicensePath { get { return Path.Combine(_dataRoot, "nexomarket.license"); } }
        private string TrialStartPath { get { return Path.Combine(_dataRoot, "trial_started_utc.txt"); } }

        private void EnsureTrialStart()
        {
            try
            {
                Directory.CreateDirectory(_dataRoot);
                if (!File.Exists(TrialStartPath)) File.WriteAllText(TrialStartPath, DateTime.UtcNow.ToString("o"), Encoding.UTF8);
            }
            catch { }
        }

        private bool GetTrial(out DateTime startedUtc, out int daysRemaining)
        {
            startedUtc = DateTime.MinValue; daysRemaining = 0;
            try
            {
                if (!File.Exists(TrialStartPath)) return false;
                if (!DateTime.TryParse(File.ReadAllText(TrialStartPath, Encoding.UTF8).Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind, out startedUtc)) return false;
                if (startedUtc.Kind != DateTimeKind.Utc) startedUtc = startedUtc.ToUniversalTime();
                DateTime expires = startedUtc.AddDays(TrialDays);
                if (expires <= DateTime.UtcNow) return false;
                daysRemaining = Math.Max(0, (int)(expires.Date - DateTime.UtcNow.Date).TotalDays);
                return true;
            }
            catch { return false; }
        }

        public bool IsTrialActive(out int daysRemaining)
        {
            DateTime started; return GetTrial(out started, out daysRemaining);
        }

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
            string remote = GetRemoteStatus();
            if (!string.IsNullOrWhiteSpace(remote) && !string.Equals(remote, "Active", StringComparison.OrdinalIgnoreCase))
            {
                status = remote; daysRemaining = 0; return false;
            }

            if (r != null)
            {
                status = LicenseCore.Status(r, DateTime.UtcNow);
                daysRemaining = LicenseCore.DaysRemaining(r, DateTime.UtcNow);
                if (r.StoreId == StoreId() && r.MachineId == MachineId && LicenseCore.Verify(r, GetPublicKey()) && status == "Activa") return true;
            }

            int trialDays;
            DateTime trialStarted;
            if (GetTrial(out trialStarted, out trialDays))
            {
                status = "Activa · Prueba 30 días";
                daysRemaining = trialDays;
                return true;
            }

            status = r == null ? "Sin licencia" : LicenseCore.Status(r, DateTime.UtcNow);
            daysRemaining = r == null ? 0 : LicenseCore.DaysRemaining(r, DateTime.UtcNow);
            return false;
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

        public void InstallActivationCode(string code, string serverBaseUrl)
        {
            LicenseRecord r;
            if (!LicenseCore.TryParse(code, out r)) throw new InvalidDataException("Código de activación inválido.");
            if (!string.Equals(r.StoreId, StoreId(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("El Store ID del código no coincide con esta tienda.");
            if (!string.Equals(r.MachineId, MachineId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("El Machine ID del código no coincide con esta PC.");
            string pub = GetPublicKey();
            if (string.IsNullOrWhiteSpace(pub) && !string.IsNullOrWhiteSpace(serverBaseUrl))
            {
                string url = serverBaseUrl.Trim().TrimEnd('/') + "/api/licenses/public-key";
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url); req.Method = "GET"; req.Timeout = 8000;
                using (WebResponse resp = req.GetResponse()) using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) pub = sr.ReadToEnd().Trim();
            }
            if (!LicenseCore.Verify(r, pub)) throw new InvalidDataException("Firma digital inválida o clave pública no disponible.");
            Directory.CreateDirectory(_dataRoot);
            File.WriteAllText(LicensePath, code.Trim(), Encoding.UTF8);
            File.WriteAllText(Path.Combine(_dataRoot, "license_public_key.xml"), string.IsNullOrWhiteSpace(pub) ? (r.PublicKeyXml ?? "") : pub, Encoding.UTF8);
            try { if (File.Exists(RemoteStatusPath)) File.Delete(RemoteStatusPath); } catch { }
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
            try { if (File.Exists(RemoteStatusPath)) File.Delete(RemoteStatusPath); } catch { }
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
                        File.WriteAllText(RemoteStatusPath, token.Substring(8).Trim(), Encoding.UTF8); return false;
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
