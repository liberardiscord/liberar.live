using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace Droute.Installer.Classes
{
    internal sealed class BrokerException : Exception
    {
        public BrokerException(string message) : base(message) { }
    }

    /// <summary>
    /// Talks to the credential broker. Every activation obtains a fresh SOCKS5
    /// credential that the server generates, binds to this machine's address and
    /// expires on its own, so a credential recovered from memory or from the
    /// registry is worthless within minutes and useless from anywhere else.
    /// </summary>
    internal sealed class BrokerClient
    {
        [DataContract]
        private class RegisterChallengeResponse
        {
            [DataMember(Name = "nonce")] public string Nonce { get; set; }
            [DataMember(Name = "difficulty")] public int Difficulty { get; set; }
        }

        [DataContract]
        private class RegisterRequest
        {
            [DataMember(Name = "nonce")] public string Nonce { get; set; }
            [DataMember(Name = "counter")] public ulong Counter { get; set; }
            [DataMember(Name = "public_key")] public string PublicKey { get; set; }
        }

        [DataContract]
        private class RegisterResponse
        {
            [DataMember(Name = "device_id")] public string DeviceId { get; set; }
        }

        [DataContract]
        private class ChallengeRequest
        {
            [DataMember(Name = "device_id")] public string DeviceId { get; set; }
        }

        [DataContract]
        private class ChallengeResponse
        {
            [DataMember(Name = "nonce")] public string Nonce { get; set; }
        }

        [DataContract]
        private class SessionRequest
        {
            [DataMember(Name = "device_id")] public string DeviceId { get; set; }
            [DataMember(Name = "nonce")] public string Nonce { get; set; }
            [DataMember(Name = "signature")] public string Signature { get; set; }
        }

        [DataContract]
        private class SessionResponse
        {
            [DataMember(Name = "host")] public string Host { get; set; }
            [DataMember(Name = "port")] public int Port { get; set; }
            [DataMember(Name = "username")] public string Username { get; set; }
            [DataMember(Name = "password")] public string Password { get; set; }
            [DataMember(Name = "expires_in")] public int ExpiresIn { get; set; }
        }

        [DataContract]
        private class ErrorResponse
        {
            [DataMember(Name = "error")] public string Error { get; set; }
        }

        private static int _validationInstalled;

        private readonly string _baseUrl;

        public BrokerClient() : this(BrokerConfig.BaseUrl) { }

        public BrokerClient(string baseUrl)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            InstallCertificateValidation();
        }

        /// <summary>
        /// Asks the broker whether it is answering, for the readiness checklist.
        ///
        /// It is a plain reachability probe: no device key, no registration, no
        /// state on either side. The answer only tells the user that activating
        /// would fail right now, which is worth knowing before they try.
        ///
        /// It also says whose side is down, because "try again later" and "check
        /// your connection" send the user to completely different places.
        /// </summary>
        public AlcanceServidor IsReachable()
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(_baseUrl + "/healthz");
                request.Method = "GET";
                request.Timeout = 6000;
                request.ReadWriteTimeout = 6000;
                request.UserAgent = "liberar.live";
                request.KeepAlive = false;

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if ((int)response.StatusCode < 400)
                        return AlcanceServidor.Ok;
                }
                return AlcanceServidor.ServidorFora;
            }
            catch
            {
                // Only blame the user's connection when Windows itself says it is
                // down. Getting this backwards sends people to reboot a router
                // that was never the problem, and it is our side that fails first.
                return TemInternet() ? AlcanceServidor.ServidorFora : AlcanceServidor.SemInternet;
            }
        }

        /// <summary>
        /// What Windows itself thinks of this machine's connectivity, through the
        /// Network List Manager.
        ///
        /// Deliberately not a request to some third party: asking an unrelated
        /// server whether the internet works would tell that server this machine
        /// runs this program, and the answer is already available locally.
        ///
        /// Anything unexpected answers true, so an unavailable service can never
        /// make the program accuse the user's connection without evidence.
        /// </summary>
        private static bool TemInternet()
        {
            object gerente = null;
            try
            {
                Type tipo = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
                if (tipo == null)
                    return true;

                gerente = Activator.CreateInstance(tipo);
                object resposta = tipo.InvokeMember(
                    "IsConnectedToInternet", BindingFlags.GetProperty, null, gerente, null);
                return !(resposta is bool) || (bool)resposta;
            }
            catch
            {
                return true;
            }
            finally
            {
                if (gerente != null)
                {
                    try { Marshal.ReleaseComObject(gerente); }
                    catch { }
                }
            }
        }

        /// <summary>
        /// Obtains a credential for one activation, registering the device first
        /// if the broker does not know it yet.
        /// </summary>
        public SessionCredential RequestSession(DeviceIdentity identity, CancellationToken cancellation)
        {
            string nonce;
            try
            {
                nonce = RequestChallenge(identity.DeviceId);
            }
            catch (BrokerException)
            {
                // An unknown device is the expected state on first run, and also
                // after the operator revokes and the user reinstalls.
                Register(identity, cancellation);
                nonce = RequestChallenge(identity.DeviceId);
            }

            byte[] signature = identity.SignChallenge(nonce);

            var response = Post<SessionRequest, SessionResponse>("/v1/session", new SessionRequest
            {
                DeviceId = identity.DeviceId,
                Nonce = nonce,
                Signature = Convert.ToBase64String(signature)
            });

            if (string.IsNullOrEmpty(response.Username) || string.IsNullOrEmpty(response.Password))
                throw new BrokerException("O servidor devolveu uma credencial vazia.");
            if (string.IsNullOrWhiteSpace(response.Host) || response.Host.Length > 253 ||
                response.Port < 1 || response.Port > 65535)
                throw new BrokerException("O servidor devolveu um endereço de conexão inválido.");

            return new SessionCredential
            {
                Host = response.Host,
                Port = response.Port,
                Username = response.Username,
                Password = response.Password,
                ExpiresIn = TimeSpan.FromSeconds(response.ExpiresIn)
            };
        }

        private void Register(DeviceIdentity identity, CancellationToken cancellation)
        {
            var challenge = Post<object, RegisterChallengeResponse>("/v1/register/challenge", null);

            if (challenge.Difficulty > BrokerConfig.MaxPoWDifficulty)
                throw new BrokerException("O servidor pediu um custo de registro fora do previsto.");

            ulong counter = SolvePoW(challenge.Nonce, challenge.Difficulty, cancellation);

            var registered = Post<RegisterRequest, RegisterResponse>("/v1/register", new RegisterRequest
            {
                Nonce = challenge.Nonce,
                Counter = counter,
                PublicKey = Convert.ToBase64String(identity.PublicKeyDer)
            });

            if (!string.Equals(registered.DeviceId, identity.DeviceId, StringComparison.Ordinal))
                throw new BrokerException("O servidor registrou um identificador diferente do esperado.");
        }

        private string RequestChallenge(string deviceId)
        {
            var response = Post<ChallengeRequest, ChallengeResponse>("/v1/challenge",
                new ChallengeRequest { DeviceId = deviceId });

            if (string.IsNullOrEmpty(response.Nonce))
                throw new BrokerException("O servidor devolveu um desafio vazio.");

            return response.Nonce;
        }

        /// <summary>
        /// Finds a counter whose SHA-256 digest starts with the requested number
        /// of zero bits. The cost is the point: it makes registering devices in
        /// bulk expensive without asking the user for anything.
        /// </summary>
        internal static ulong SolvePoW(string nonceHex, int difficulty, CancellationToken cancellation)
        {
            byte[] nonce = HexToBytes(nonceHex);
            var buffer = new byte[nonce.Length + 8];
            Buffer.BlockCopy(nonce, 0, buffer, 0, nonce.Length);

            using (SHA256 sha = SHA256.Create())
            {
                for (ulong counter = 0; counter < ulong.MaxValue; counter++)
                {
                    if ((counter & 0xFFFF) == 0)
                        cancellation.ThrowIfCancellationRequested();

                    for (int i = 0; i < 8; i++)
                        buffer[nonce.Length + i] = (byte)(counter >> (56 - 8 * i));

                    if (LeadingZeroBits(sha.ComputeHash(buffer)) >= difficulty)
                        return counter;
                }
            }

            throw new BrokerException("Não foi possível resolver o desafio de registro.");
        }

        private static int LeadingZeroBits(byte[] digest)
        {
            int total = 0;
            foreach (byte b in digest)
            {
                if (b != 0)
                {
                    int value = b;
                    while ((value & 0x80) == 0)
                    {
                        total++;
                        value <<= 1;
                    }
                    return total;
                }
                total += 8;
            }
            return total;
        }

        private static byte[] HexToBytes(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0)
                throw new BrokerException("O servidor devolveu um desafio malformado.");

            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        // ------------------------------------------------------------- transport

        private TResponse Post<TRequest, TResponse>(string path, TRequest body)
            where TResponse : class
        {
            var request = (HttpWebRequest)WebRequest.Create(_baseUrl + path);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = BrokerConfig.RequestTimeoutMs;
            request.ReadWriteTimeout = BrokerConfig.RequestTimeoutMs;
            request.UserAgent = "liberar.live";
            request.KeepAlive = false;

            byte[] payload = body == null
                ? Encoding.UTF8.GetBytes("{}")
                : Serialize(body);

            request.ContentLength = payload.Length;
            try
            {
                using (Stream stream = request.GetRequestStream())
                    stream.Write(payload, 0, payload.Length);

                using (var response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                    return Deserialize<TResponse>(stream);
            }
            catch (WebException ex)
            {
                throw new BrokerException(DescribeWebException(ex));
            }
        }

        private static string DescribeWebException(WebException ex)
        {
            var response = ex.Response as HttpWebResponse;
            if (response == null)
            {
                // Nothing came back at all, so the failure is on the way there.
                // Saying whose side it is on costs one local lookup and saves the
                // user from troubleshooting a connection that was never at fault.
                return TemInternet()
                    ? "Não foi possível preparar a liberação agora, a sua internet está funcionando e quem não respondeu foi o nosso servidor, tente novamente daqui a pouco."
                    : "Não foi possível preparar a liberação agora, este computador está sem internet, verifique sua conexão e tente novamente.";
            }

            string code = null;
            try
            {
                using (Stream stream = response.GetResponseStream())
                {
                    var error = Deserialize<ErrorResponse>(stream);
                    code = error?.Error;
                }
            }
            catch (Exception)
            {
                // The body is optional; the status alone is enough to explain.
            }

            switch (code)
            {
                case "too_soon":
                    return "Aguarde alguns minutos antes de liberar novamente.";
                case "daily_quota_exceeded":
                    return "O limite diário de ativações deste computador foi atingido.";
                case "unknown_device":
                    return "Este computador precisa se registrar novamente.";
                case "bad_proof_of_work":
                case "unknown_or_used_challenge":
                    return "O registro expirou antes de terminar. Tente de novo.";
                default:
                    // The server answered, so the connection is fine by definition
                    // and pointing the user at it would be a lie.
                    return "Não foi possível preparar a liberação agora, o nosso servidor respondeu com um erro, tente novamente em instantes.";
            }
        }

        private static byte[] Serialize<T>(T value)
        {
            using (var buffer = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(buffer, value);
                return buffer.ToArray();
            }
        }

        private static T Deserialize<T>(Stream stream) where T : class
        {
            if (stream == null)
                throw new BrokerException("O servidor respondeu sem conteúdo.");

            try
            {
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
            }
            catch (SerializationException)
            {
                throw new BrokerException("O servidor respondeu num formato inesperado.");
            }
        }

        /// <summary>
        /// Requires the broker chain to end at one of the pinned roots. Pinning the
        /// root rather than the leaf survives certificate renewal, which matters
        /// because this client has no update channel of its own.
        /// </summary>
        private static void InstallCertificateValidation()
        {
            if (Interlocked.Exchange(ref _validationInstalled, 1) != 0)
                return;

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            if (BrokerConfig.PinnedRootThumbprints.Length == 0)
                return;

            ServicePointManager.ServerCertificateValidationCallback += ValidateBrokerCertificate;
        }

        private static bool ValidateBrokerCertificate(object sender, X509Certificate certificate,
                                                      X509Chain chain, SslPolicyErrors errors)
        {
            var request = sender as HttpWebRequest;
            Uri broker;
            if (request == null || !Uri.TryCreate(BrokerConfig.BaseUrl, UriKind.Absolute, out broker))
                return errors == SslPolicyErrors.None;

            // Only the broker is pinned. Anything else keeps the default rules.
            if (!string.Equals(request.RequestUri.Host, broker.Host, StringComparison.OrdinalIgnoreCase))
                return errors == SslPolicyErrors.None;

            if (errors != SslPolicyErrors.None || chain == null || chain.ChainElements.Count == 0)
                return false;

            string rootThumbprint = chain.ChainElements[chain.ChainElements.Count - 1]
                .Certificate.Thumbprint;

            foreach (string pinned in BrokerConfig.PinnedRootThumbprints)
            {
                if (string.Equals(pinned, rootThumbprint, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    internal sealed class SessionCredential
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public TimeSpan ExpiresIn { get; set; }
    }
}
