using System;
using System.IO;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace NexoMarket.CentralServer
{
    /// <summary>
    /// Almacenamiento persistente externo para NexoMarket.
    /// Render puede reiniciar o recrear el contenedor; R2 conserva los objetos.
    /// </summary>
    internal sealed class R2ObjectStore
    {
        private readonly AmazonS3Client _client;
        private readonly string _bucket;
        private readonly string _publicBaseUrl;

        public bool Enabled { get; private set; }
        public string PublicBaseUrl { get { return _publicBaseUrl; } }

        public R2ObjectStore()
        {
            string accountId = Environment.GetEnvironmentVariable("R2_ACCOUNT_ID") ?? "";
            string accessKey = Environment.GetEnvironmentVariable("R2_ACCESS_KEY_ID") ?? "";
            string secret = Environment.GetEnvironmentVariable("R2_SECRET_ACCESS_KEY") ?? "";
            _bucket = Environment.GetEnvironmentVariable("R2_BUCKET") ?? "nexomarket";
            _publicBaseUrl = (Environment.GetEnvironmentVariable("R2_PUBLIC_BASE_URL") ?? "").TrimEnd('/');
            if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secret)) return;
            try
            {
                var credentials = new BasicAWSCredentials(accessKey, secret);
                var config = new AmazonS3Config
                {
                    ServiceURL = "https://" + accountId + ".r2.cloudflarestorage.com",
                    ForcePathStyle = true,
                    UseHttp = false,
                    AuthenticationRegion = "auto",
                    Timeout = TimeSpan.FromSeconds(25),
                    ReadWriteTimeout = TimeSpan.FromSeconds(25)
                };
                _client = new AmazonS3Client(credentials, config);
                Enabled = true;
            }
            catch { Enabled = false; }
        }

        public bool PutBytes(string key, byte[] bytes, string contentType, out string error)
        {
            error = "not_configured";
            if (!Enabled || bytes == null) return false;
            try
            {
                using (var ms = new MemoryStream(bytes, false))
                {
                    var req = new PutObjectRequest
                    {
                        BucketName = _bucket,
                        Key = key.TrimStart('/'),
                        InputStream = ms,
                        ContentLength = bytes.LongLength,
                        ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                        // Cloudflare R2 no admite el payload streaming SigV4 que
                        // AWSSDK.S3 utiliza por defecto para algunos PutObject.
                        // R2 requiere UNSIGNED-PAYLOAD y sin checksum automático.
                        DisablePayloadSigning = true,
                        DisableDefaultChecksumValidation = true
                    };
                    _client.PutObjectAsync(req).GetAwaiter().GetResult();
                    error = "";
                    return true;
                }
            }
            catch (Exception ex) { error = ex.GetType().Name + ":" + (ex.Message ?? ""); return false; }
        }

        public bool PutText(string key, string text)
        {
            string error; return PutBytes(key, Encoding.UTF8.GetBytes(text ?? ""), "application/xml; charset=utf-8", out error);
        }

        public byte[] GetBytes(string key)
        {
            if (!Enabled) return null;
            try
            {
                var response = _client.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = key.TrimStart('/') }).GetAwaiter().GetResult();
                using (response.ResponseStream)
                using (var ms = new MemoryStream()) { response.ResponseStream.CopyTo(ms); return ms.ToArray(); }
            }
            catch { return null; }
        }

        public string GetText(string key)
        {
            byte[] data = GetBytes(key);
            return data == null ? null : Encoding.UTF8.GetString(data);
        }

        public string PublicUrl(string key)
        {
            return string.IsNullOrWhiteSpace(_publicBaseUrl) ? "" : _publicBaseUrl + "/" + key.TrimStart('/');
        }
    }
}
