using System;
using System.IO;
using System.Reflection;

namespace Droute.Installer.Classes
{
    /// <summary>
    /// Build-time settings for reaching the credential broker.
    ///
    /// Nothing here is a secret, by design. Following the same approach as the
    /// Mullvad client, the binary carries only an address and a trust anchor:
    /// every value in this file can be published without weakening anything,
    /// because authority comes from the per-device key, not from the build.
    /// </summary>
    internal static class BrokerConfig
    {
        /// <summary>
        /// Harmless public-build default. It only reaches a broker deliberately
        /// started on the same computer. Operators should override it with the
        /// environment variable or broker.url instead of committing an endpoint.
        /// </summary>
        private const string DefaultBaseUrl = "http://127.0.0.1:8080";

        /// <summary>
        /// Base URL of the broker, resolved once at startup. Resolution order:
        /// the <c>LIBERAR_BROKER_URL</c> environment variable, then a
        /// <c>broker.url</c> file next to the executable, then the compiled
        /// default. Only an address is involved; overriding it grants no
        /// authority, since every request is still signed by the device key.
        /// </summary>
        public static readonly string BaseUrl = ResolveBaseUrl();

        private static string ResolveBaseUrl()
        {
            string configured = Environment.GetEnvironmentVariable("LIBERAR_BROKER_URL");

            if (string.IsNullOrWhiteSpace(configured))
            {
                try
                {
                    string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        string path = Path.Combine(dir, "broker.url");
                        if (File.Exists(path))
                            configured = File.ReadAllText(path);
                    }
                }
                catch
                {
                    // A missing or unreadable override just falls back to the
                    // compiled default; it must never keep the client from starting.
                }
            }

            return string.IsNullOrWhiteSpace(configured) ? DefaultBaseUrl : configured.Trim();
        }

        /// <summary>
        /// SHA-1 thumbprints of the certificate authorities the broker chain must
        /// terminate at. Empty means "trust whatever Windows trusts".
        ///
        /// Pin the ROOT, never the leaf: a leaf pin would break every ninety days
        /// when the certificate renews, and this client cannot update itself. Fill
        /// this before public release with the root of whichever CA issues the
        /// broker certificate, read from the live endpoint:
        ///
        ///   openssl s_client -connect host:443 -showcerts &lt; /dev/null
        ///
        /// then take the thumbprint of the last certificate in the chain.
        /// </summary>
        public static readonly string[] PinnedRootThumbprints = { };

        /// <summary>Ceiling on proof-of-work effort, so a hostile difficulty cannot hang the UI.</summary>
        public const int MaxPoWDifficulty = 28;

        public const int RequestTimeoutMs = 20000;
    }
}
