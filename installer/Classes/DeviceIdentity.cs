using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Droute.Installer.Classes
{
    /// <summary>
    /// Per-installation key pair. This is what replaces the credential that used
    /// to be compiled into the binary: the private key is generated on this
    /// machine, never leaves it, and is different for every install, so there is
    /// no shared secret for anyone to recover from the executable.
    /// </summary>
    internal sealed class DeviceIdentity : IDisposable
    {
        // The 26 byte prefix of a P-256 SubjectPublicKeyInfo. .NET Framework 4.8
        // has no ExportSubjectPublicKeyInfo, so the structure is assembled by
        // hand around the public point. It is a fixed encoding, not a guess:
        // SEQUENCE(89) { SEQUENCE(19) { id-ecPublicKey, prime256v1 }, BIT STRING(66) }
        private static readonly byte[] P256SpkiPrefix =
        {
            0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01,
            0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00
        };

        private const string SignatureDomain = "droute-session-v1\0";

        private readonly CngKey _key;
        private readonly ECDsaCng _ecdsa;

        public byte[] PublicKeyDer { get; private set; }

        /// <summary>Derived from the key itself, so re-registering converges on the same device.</summary>
        public string DeviceId { get; private set; }

        private DeviceIdentity(CngKey key)
        {
            _key = key;
            _ecdsa = new ECDsaCng(key) { HashAlgorithm = CngAlgorithm.Sha256 };

            ECParameters parameters = _ecdsa.ExportParameters(false);
            PublicKeyDer = BuildSpki(parameters.Q.X, parameters.Q.Y);

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(PublicKeyDer);
                var builder = new StringBuilder(32);
                for (int i = 0; i < 16; i++)
                    builder.Append(hash[i].ToString("x2"));
                DeviceId = builder.ToString();
            }
        }

        public static string StorePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "liberar.live");
                return Path.Combine(dir, "device.key");
            }
        }

        /// <summary>
        /// Loads the existing identity or creates one. The key material is sealed
        /// with DPAPI under the current user, so another account on the same
        /// machine cannot read it.
        /// </summary>
        public static DeviceIdentity LoadOrCreate()
        {
            string path = StorePath;

            if (File.Exists(path))
            {
                try
                {
                    byte[] blob = ProtectedData.Unprotect(
                        File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                    return new DeviceIdentity(CngKey.Import(blob, CngKeyBlobFormat.EccPrivateBlob));
                }
                catch (Exception)
                {
                    // A corrupted or foreign-user blob is not recoverable. Starting
                    // over costs one proof of work, which is preferable to failing
                    // activation forever.
                    TryDelete(path);
                }
            }

            var creationParameters = new CngKeyCreationParameters
            {
                ExportPolicy = CngExportPolicies.AllowPlaintextExport,
                KeyUsage = CngKeyUsages.Signing
            };

            CngKey key = CngKey.Create(CngAlgorithm.ECDsaP256, null, creationParameters);

            byte[] exported = key.Export(CngKeyBlobFormat.EccPrivateBlob);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path,
                    ProtectedData.Protect(exported, null, DataProtectionScope.CurrentUser));
            }
            finally
            {
                Array.Clear(exported, 0, exported.Length);
            }

            return new DeviceIdentity(key);
        }

        /// <summary>
        /// Signs the broker's challenge. The domain prefix and device id are part
        /// of the signed message so a signature made here cannot be replayed into
        /// another context; the broker rebuilds the identical bytes.
        /// </summary>
        public byte[] SignChallenge(string nonceHex)
        {
            byte[] domain = Encoding.ASCII.GetBytes(SignatureDomain);
            byte[] device = Encoding.ASCII.GetBytes(DeviceId);
            byte[] nonce = HexToBytes(nonceHex);

            var message = new byte[domain.Length + device.Length + nonce.Length];
            Buffer.BlockCopy(domain, 0, message, 0, domain.Length);
            Buffer.BlockCopy(device, 0, message, domain.Length, device.Length);
            Buffer.BlockCopy(nonce, 0, message, domain.Length + device.Length, nonce.Length);

            // SignData produces the IEEE P1363 layout, r and s concatenated at 32
            // bytes each. The broker reads exactly those 64 bytes.
            return _ecdsa.SignData(message);
        }

        /// <summary>Removes the identity. Used by the uninstall path so nothing is left behind.</summary>
        public static void Remove()
        {
            TryDelete(StorePath);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static byte[] BuildSpki(byte[] x, byte[] y)
        {
            if (x == null || y == null || x.Length != 32 || y.Length != 32)
                throw new CryptographicException("unexpected P-256 public point size");

            var der = new byte[P256SpkiPrefix.Length + 1 + 64];
            Buffer.BlockCopy(P256SpkiPrefix, 0, der, 0, P256SpkiPrefix.Length);
            der[P256SpkiPrefix.Length] = 0x04; // uncompressed point
            Buffer.BlockCopy(x, 0, der, P256SpkiPrefix.Length + 1, 32);
            Buffer.BlockCopy(y, 0, der, P256SpkiPrefix.Length + 33, 32);
            return der;
        }

        private static byte[] HexToBytes(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0)
                throw new FormatException("challenge nonce is not hexadecimal");

            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        public void Dispose()
        {
            _ecdsa?.Dispose();
            _key?.Dispose();
        }
    }
}
