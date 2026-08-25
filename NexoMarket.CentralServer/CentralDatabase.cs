using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace NexoMarket.CentralServer
{
    /// <summary>
    /// Persistencia central. PostgreSQL es la fuente de verdad para identidad,
    /// tiendas y dispositivos. Los documentos XML siguen existiendo solamente
    /// para compatibilidad/migración de catálogo legado.
    /// </summary>
    public sealed class CentralDatabase : IDisposable
    {
        private readonly string _connectionString;
        private bool _initialized;
        private readonly object _gate = new object();
        public bool Enabled { get { return !string.IsNullOrWhiteSpace(_connectionString); } }

        public CentralDatabase()
        {
            _connectionString = BuildConnectionString(Environment.GetEnvironmentVariable("NEXOMARKET_DATABASE_URL") ?? Environment.GetEnvironmentVariable("DATABASE_URL"));
        }

        private static string BuildConnectionString(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            raw = raw.Trim();
            try
            {
                if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
                {
                    Uri uri = new Uri(raw);
                    NpgsqlConnectionStringBuilder b = new NpgsqlConnectionStringBuilder();
                    b.Host = uri.Host; b.Port = uri.IsDefaultPort ? 5432 : uri.Port;
                    string[] auth = uri.UserInfo.Split(new[] { ':' }, 2);
                    b.Username = Uri.UnescapeDataString(auth[0]);
                    if (auth.Length > 1) b.Password = Uri.UnescapeDataString(auth[1]);
                    string db = uri.AbsolutePath.Trim('/'); b.Database = string.IsNullOrWhiteSpace(db) ? "postgres" : Uri.UnescapeDataString(db);
                    b.SslMode = SslMode.Require; b.TrustServerCertificate = true;
                    return b.ConnectionString;
                }
                return raw;
            }
            catch { return raw; }
        }
        private NpgsqlConnection Open() { NpgsqlConnection c = new NpgsqlConnection(_connectionString); c.Open(); return c; }
        private void EnsureInitialized(NpgsqlConnection c)
        {
            lock (_gate)
            {
                if (_initialized) return;
                using (NpgsqlCommand cmd = c.CreateCommand())
                {
                    cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS nexomarket_documents(dataset TEXT PRIMARY KEY, content TEXT NOT NULL, updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW());
CREATE INDEX IF NOT EXISTS idx_nexomarket_documents_updated_at ON nexomarket_documents(updated_at);
CREATE TABLE IF NOT EXISTS nexomarket_accounts(
    account_id TEXT PRIMARY KEY,
    email TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL DEFAULT '',
    phone TEXT NOT NULL DEFAULT '',
    role TEXT NOT NULL DEFAULT 'seller',
    store_id TEXT NOT NULL DEFAULT '',
    salt TEXT NOT NULL DEFAULT '',
    password_hash TEXT NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_nexomarket_accounts_store ON nexomarket_accounts(store_id);
CREATE TABLE IF NOT EXISTS nexomarket_devices(
    device_id TEXT PRIMARY KEY,
    store_id TEXT NOT NULL,
    account_email TEXT NOT NULL,
    device_name TEXT NOT NULL DEFAULT 'Windows',
    device_token_hash TEXT NOT NULL UNIQUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    active BOOLEAN NOT NULL DEFAULT TRUE
);
CREATE INDEX IF NOT EXISTS idx_nexomarket_devices_store ON nexomarket_devices(store_id);
CREATE TABLE IF NOT EXISTS nexomarket_pairings(
    pairing_id TEXT PRIMARY KEY,
    store_id TEXT NOT NULL,
    account_email TEXT NOT NULL,
    token_hash TEXT NOT NULL UNIQUE,
    expires_at TIMESTAMPTZ NOT NULL,
    used BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_nexomarket_pairings_expiry ON nexomarket_pairings(expires_at);
";
                    cmd.ExecuteNonQuery();
                }
                _initialized = true;
            }
        }
        private static string HashToken(string value)
        {
            using (SHA256 sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? "")));
        }
        public string GetDocument(string dataset)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(dataset)) return null;
            try { using (NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){cmd.CommandText="SELECT content FROM nexomarket_documents WHERE dataset=@dataset";cmd.Parameters.AddWithValue("dataset",dataset);object v=cmd.ExecuteScalar();return v==null||v==DBNull.Value?null:Convert.ToString(v);}}} catch{return null;}
        }
        public bool SaveDocument(string dataset,string content)
        {
            if(!Enabled||string.IsNullOrWhiteSpace(dataset)||content==null)return false;
            try{using(NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){cmd.CommandText="INSERT INTO nexomarket_documents(dataset,content,updated_at) VALUES(@dataset,@content,NOW()) ON CONFLICT(dataset) DO UPDATE SET content=EXCLUDED.content,updated_at=NOW();";cmd.Parameters.AddWithValue("dataset",dataset);cmd.Parameters.AddWithValue("content",content);cmd.ExecuteNonQuery();}}return true;}catch{return false;}
        }
        public bool EnsureDocument(string dataset,string content){if(!Enabled)return false;return GetDocument(dataset)!=null||SaveDocument(dataset,content);}
        public string Status(){if(!Enabled)return "disabled";try{using(NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){cmd.CommandText="SELECT COUNT(*) FROM nexomarket_documents";long n=Convert.ToInt64(cmd.ExecuteScalar());return "connected|documents="+n.ToString(System.Globalization.CultureInfo.InvariantCulture);}}}catch(Exception ex){return "error|"+ex.GetType().Name;}}

        public bool UpsertAccount(string id,string name,string email,string phone,string role,string storeId,string salt,string passwordHash,string createdAt)
        {
            if(!Enabled||string.IsNullOrWhiteSpace(email))return false;
            try{using(NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){
                cmd.CommandText=@"INSERT INTO nexomarket_accounts(account_id,email,name,phone,role,store_id,salt,password_hash,created_at,updated_at)
VALUES(@id,@email,@name,@phone,@role,@store,@salt,@hash,COALESCE(NULLIF(@created,'')::timestamptz,NOW()),NOW())
ON CONFLICT(email) DO UPDATE SET account_id=EXCLUDED.account_id,name=EXCLUDED.name,phone=EXCLUDED.phone,role=EXCLUDED.role,store_id=EXCLUDED.store_id,salt=EXCLUDED.salt,password_hash=EXCLUDED.password_hash,updated_at=NOW();";
                cmd.Parameters.AddWithValue("id",string.IsNullOrWhiteSpace(id)?Guid.NewGuid().ToString("N"):id);cmd.Parameters.AddWithValue("email",email.Trim().ToLowerInvariant());cmd.Parameters.AddWithValue("name",name??"");cmd.Parameters.AddWithValue("phone",phone??"");cmd.Parameters.AddWithValue("role",role??"seller");cmd.Parameters.AddWithValue("store",storeId??"");cmd.Parameters.AddWithValue("salt",salt??"");cmd.Parameters.AddWithValue("hash",passwordHash??"");cmd.Parameters.AddWithValue("created",createdAt??"");cmd.ExecuteNonQuery();}}
                return true;}catch{return false;}
        }
        public Dictionary<string,string> GetAccount(string email)
        {
            if(!Enabled||string.IsNullOrWhiteSpace(email))return null;
            try{using(NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){cmd.CommandText="SELECT account_id,name,email,phone,role,store_id,salt,password_hash,created_at FROM nexomarket_accounts WHERE lower(email)=lower(@email) LIMIT 1";cmd.Parameters.AddWithValue("email",email.Trim());using(NpgsqlDataReader r=cmd.ExecuteReader()){if(!r.Read())return null;return new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"id",r.GetString(0)},{"name",r.GetString(1)},{"email",r.GetString(2)},{"phone",r.GetString(3)},{"role",r.GetString(4)},{"storeId",r.GetString(5)},{"salt",r.GetString(6)},{"passwordHash",r.GetString(7)},{"createdAt",r.GetDateTime(8).ToUniversalTime().ToString("o")}};}}}}catch{return null;}
        }
        public Dictionary<string,string> GetSellerByStore(string storeId)
        {
            if(!Enabled||string.IsNullOrWhiteSpace(storeId))return null;
            try{using(NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){cmd.CommandText="SELECT account_id,name,email,phone,role,store_id,salt,password_hash,created_at FROM nexomarket_accounts WHERE lower(store_id)=lower(@store) AND role='seller' ORDER BY updated_at DESC LIMIT 1";cmd.Parameters.AddWithValue("store",storeId.Trim());using(NpgsqlDataReader r=cmd.ExecuteReader()){if(!r.Read())return null;return new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"id",r.GetString(0)},{"name",r.GetString(1)},{"email",r.GetString(2)},{"phone",r.GetString(3)},{"role",r.GetString(4)},{"storeId",r.GetString(5)},{"salt",r.GetString(6)},{"passwordHash",r.GetString(7)},{"createdAt",r.GetDateTime(8).ToUniversalTime().ToString("o")}};}}}}catch{return null;}
        }
        public string CreatePairing(string storeId,string email,int minutes)
        {
            if(!Enabled||string.IsNullOrWhiteSpace(storeId)||string.IsNullOrWhiteSpace(email))return null;
            try{string raw=Convert.ToBase64String(RandomBytes(32)).Replace("+","-").Replace("/","_").TrimEnd('=');string id=Guid.NewGuid().ToString("N");DateTime exp=DateTime.UtcNow.AddMinutes(minutes<1?5:minutes);using(NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){cmd.CommandText="UPDATE nexomarket_pairings SET used=TRUE WHERE store_id=@store AND account_email=@email AND used=FALSE; INSERT INTO nexomarket_pairings(pairing_id,store_id,account_email,token_hash,expires_at,used) VALUES(@id,@store,@email,@hash,@exp,FALSE);";cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("store",storeId.Trim());cmd.Parameters.AddWithValue("email",email.Trim().ToLowerInvariant());cmd.Parameters.AddWithValue("hash",HashToken(raw));cmd.Parameters.AddWithValue("exp",exp);cmd.ExecuteNonQuery();}}return raw;}catch{return null;}
        }
        public Dictionary<string,string> CompletePairing(string token,string deviceId,string deviceName)
        {
            if(!Enabled||string.IsNullOrWhiteSpace(token)||string.IsNullOrWhiteSpace(deviceId))return null;
            try{using(NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand tx=c.CreateCommand()){tx.CommandText="BEGIN; SELECT pairing_id,store_id,account_email FROM nexomarket_pairings WHERE token_hash=@hash AND used=FALSE AND expires_at>NOW() LIMIT 1 FOR UPDATE;";tx.Parameters.AddWithValue("hash",HashToken(token));using(NpgsqlDataReader r=tx.ExecuteReader()){if(!r.Read()){r.Close();using(NpgsqlCommand rb=c.CreateCommand()){rb.CommandText="ROLLBACK";rb.ExecuteNonQuery();}return null;}string pairingId=r.GetString(0),storeId=r.GetString(1),email=r.GetString(2);r.Close();string rawDeviceToken=Convert.ToBase64String(RandomBytes(32)).Replace("+","-").Replace("/","_").TrimEnd('=');using(NpgsqlCommand up=c.CreateCommand()){up.CommandText="INSERT INTO nexomarket_devices(device_id,store_id,account_email,device_name,device_token_hash,created_at,last_seen_at,active) VALUES(@id,@store,@email,@name,@hash,NOW(),NOW(),TRUE) ON CONFLICT(device_id) DO UPDATE SET store_id=EXCLUDED.store_id,account_email=EXCLUDED.account_email,device_name=EXCLUDED.device_name,device_token_hash=EXCLUDED.device_token_hash,last_seen_at=NOW(),active=TRUE; UPDATE nexomarket_pairings SET used=TRUE WHERE pairing_id=@pair;";up.Parameters.AddWithValue("id",deviceId);up.Parameters.AddWithValue("store",storeId);up.Parameters.AddWithValue("email",email);up.Parameters.AddWithValue("name",deviceName??"Windows");up.Parameters.AddWithValue("hash",HashToken(rawDeviceToken));up.Parameters.AddWithValue("pair",pairingId);up.ExecuteNonQuery();}using(NpgsqlCommand commit=c.CreateCommand()){commit.CommandText="COMMIT";commit.ExecuteNonQuery();}return new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"deviceId",deviceId},{"deviceToken",rawDeviceToken},{"storeId",storeId},{"email",email}};}}}}catch{return null;}
        }
        public bool ValidateDevice(string deviceId,string deviceToken,string storeId)
        {
            if(!Enabled||string.IsNullOrWhiteSpace(deviceId)||string.IsNullOrWhiteSpace(deviceToken)||string.IsNullOrWhiteSpace(storeId))return false;
            try{using(NpgsqlConnection c=Open()){EnsureInitialized(c);using(NpgsqlCommand cmd=c.CreateCommand()){cmd.CommandText="UPDATE nexomarket_devices SET last_seen_at=NOW() WHERE device_id=@id AND device_token_hash=@hash AND store_id=@store AND active=TRUE";cmd.Parameters.AddWithValue("id",deviceId);cmd.Parameters.AddWithValue("hash",HashToken(deviceToken));cmd.Parameters.AddWithValue("store",storeId);return cmd.ExecuteNonQuery()==1;}}}catch{return false;}
        }
        private static byte[] RandomBytes(int count){byte[] b=new byte[count];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(b);return b;}
        public void Dispose() { }
    }
}
