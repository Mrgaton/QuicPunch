using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace QuicPunch.Helpers
{
    public sealed class CertManager : IDisposable
    {
        private static readonly byte[] BundleMagic = "QPID"u8.ToArray();
        private const byte BundleVersion = 0x02;

        public const string EcdhExtensionOid = "1.3.6.1.4.1.99999.1";
        public const int SignatureLength = 64; // 32 bytes r + 32 bytes s for NIST P-256 curve

        private readonly object _lock = new();
        private readonly string _configPath;
        private readonly string _prefix;

        private X509Certificate2? _peerCertificate;
        private byte[]? _peerCertPublicHash;
        private ECDiffieHellman? _ecdhKey;
        private byte[]? _ecdhPublicKeyRaw;
        private ECDsa? _curve;
        private byte[]? _curveHash;
        private byte[]? _nostrPrivateKey;
        private byte[]? _nostrPublicKey;
        private string? _nostrPublicKeyHex;
        private byte[] _sessionNonce = RandomNumberGenerator.GetBytes(32);
        private ECDiffieHellman? _ephemeralEcdh;
        private byte[]? _ephemeralEcdhPublicKeyRaw;
        private bool _isLoaded;
        private bool _isDisposed;

        public string IdentityPath { get; }
        public string CertPath { get; }
        public string EcdhKeyPath { get; }

        public CertManager(string configPath, string prefix)
        {
            _configPath = configPath;
            _prefix = prefix;
            IdentityPath = Path.Combine(configPath, $"{prefix}_identity.bin");
            CertPath = Path.Combine(configPath, $"{prefix}_peerCert.pfx");
            EcdhKeyPath = Path.Combine(configPath, $"{prefix}_ecdhKey.key");

            if (!Directory.Exists(configPath))
            {
                Directory.CreateDirectory(configPath);
            }
        }

        private void EnsureLoadedLocked()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(CertManager));

            if (_isLoaded)
                return;

            if (File.Exists(IdentityPath))
            {
                if (TryLoadBundle(IdentityPath, out var loadedCert, out var loadedEcdh))
                {
                    _peerCertificate = loadedCert;
                    _ecdhKey = loadedEcdh;
                    _isLoaded = true;
                    return;
                }
            }

            if (File.Exists(CertPath) && File.Exists(EcdhKeyPath))
            {
                if (TryLoadLegacy(CertPath, EcdhKeyPath, out var legacyCert, out var legacyEcdh))
                {
                    _peerCertificate = legacyCert;
                    _ecdhKey = legacyEcdh;
                    _isLoaded = true;
                    SaveBundleAtomic(IdentityPath, _peerCertificate!, _ecdhKey!);
                    return;
                }
            }

            GenerateAndSaveAtomicIdentity();
            _isLoaded = true;
        }

        private static X509KeyStorageFlags DefaultKeyStorageFlags => OperatingSystem.IsWindows()
            ? (X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet)
            : (X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);

        private static bool TryLoadBundle(string path, out X509Certificate2? cert, out ECDiffieHellman? ecdh)
        {
            cert = null;
            ecdh = null;
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (data.Length < 5 + 4 + 4 || !data.AsSpan(0, 4).SequenceEqual(BundleMagic) || data[4] != BundleVersion)
                    return false;

                int offset = 5;
                int pfxLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
                offset += 4;
                if (pfxLen <= 0 || offset + pfxLen > data.Length)
                    return false;

                byte[] pfxBytes = data.AsSpan(offset, pfxLen).ToArray();
                offset += pfxLen;

                int ecdhLen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
                offset += 4;
                if (ecdhLen <= 0 || offset + ecdhLen > data.Length)
                    return false;

                byte[] ecdhBytes = data.AsSpan(offset, ecdhLen).ToArray();

                var loadedCert = X509CertificateLoader.LoadPkcs12(
                    pfxBytes,
                    (string?)null,
                    DefaultKeyStorageFlags);

                var loadedEcdh = ECDiffieHellman.Create();
                loadedEcdh.ImportPkcs8PrivateKey(ecdhBytes, out _);

                var ecdhExt = loadedCert.Extensions[EcdhExtensionOid];
                if (ecdhExt == null || !CryptographicOperations.FixedTimeEquals(ecdhExt.RawData, loadedEcdh.ExportSubjectPublicKeyInfo()))
                {
                    loadedCert.Dispose();
                    loadedEcdh.Dispose();
                    return false;
                }

                using (var ecdsa = loadedCert.GetECDsaPublicKey())
                {
                    if (ecdsa == null || ecdsa.KeySize != 256 || loadedEcdh.KeySize != 256)
                    {
                        loadedCert.Dispose();
                        loadedEcdh.Dispose();
                        return false;
                    }
                }

                cert = loadedCert;
                ecdh = loadedEcdh;
                return true;
            }
            catch
            {
                cert?.Dispose();
                ecdh?.Dispose();
                return false;
            }
        }

        private static bool TryLoadLegacy(string certPath, string keyPath, out X509Certificate2? cert, out ECDiffieHellman? ecdh)
        {
            cert = null;
            ecdh = null;
            try
            {
                var loadedCert = X509CertificateLoader.LoadPkcs12FromFile(
                    certPath,
                    (string?)null,
                    DefaultKeyStorageFlags);

                var loadedEcdh = ECDiffieHellman.Create();
                loadedEcdh.ImportPkcs8PrivateKey(File.ReadAllBytes(keyPath), out _);

                var ecdhExt = loadedCert.Extensions[EcdhExtensionOid];
                if (ecdhExt == null || !CryptographicOperations.FixedTimeEquals(ecdhExt.RawData, loadedEcdh.ExportSubjectPublicKeyInfo()))
                {
                    loadedCert.Dispose();
                    loadedEcdh.Dispose();
                    return false;
                }

                using (var ecdsa = loadedCert.GetECDsaPublicKey())
                {
                    if (ecdsa == null || ecdsa.KeySize != 256 || loadedEcdh.KeySize != 256)
                    {
                        loadedCert.Dispose();
                        loadedEcdh.Dispose();
                        return false;
                    }
                }

                cert = loadedCert;
                ecdh = loadedEcdh;
                return true;
            }
            catch
            {
                cert?.Dispose();
                ecdh?.Dispose();
                return false;
            }
        }

        private void GenerateAndSaveAtomicIdentity()
        {
            _ecdhKey?.Dispose();
            _peerCertificate?.Dispose();

            var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var (username, hostname, _) = Utilities.GenerateRandomIdentityNames();
            var cert = GenerateIdentityCertificate(hostname, username, ecdh);

            SaveBundleAtomic(IdentityPath, cert, ecdh);

            _peerCertificate = cert;
            _ecdhKey = ecdh;
        }

        private static void SaveBundleAtomic(string targetPath, X509Certificate2 cert, ECDiffieHellman ecdh)
        {
            byte[] pfxBytes = cert.Export(X509ContentType.Pfx);
            byte[] ecdhBytes = ecdh.ExportPkcs8PrivateKey();

            int totalLength = BundleMagic.Length + 1 + 4 + pfxBytes.Length + 4 + ecdhBytes.Length;
            byte[] bundle = new byte[totalLength];

            int offset = 0;
            BundleMagic.CopyTo(bundle.AsSpan(offset));
            offset += BundleMagic.Length;

            bundle[offset++] = BundleVersion;

            BinaryPrimitives.WriteInt32LittleEndian(bundle.AsSpan(offset, 4), pfxBytes.Length);
            offset += 4;
            pfxBytes.CopyTo(bundle.AsSpan(offset));
            offset += pfxBytes.Length;

            BinaryPrimitives.WriteInt32LittleEndian(bundle.AsSpan(offset, 4), ecdhBytes.Length);
            offset += 4;
            ecdhBytes.CopyTo(bundle.AsSpan(offset));

            string tempFile = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tempFile, bundle);

            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(tempFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch { }
            }

            File.Move(tempFile, targetPath, overwrite: true);
        }

        public X509Certificate2 PeerCertificate
        {
            get
            {
                lock (_lock)
                {
                    EnsureLoadedLocked();
                    return _peerCertificate!;
                }
            }
        }

        public string PeerName
        {
            get
            {
                lock (_lock)
                {
                    EnsureLoadedLocked();
                    string? upn = _peerCertificate?.GetNameInfo(X509NameType.UpnName, false);
                    if (!string.IsNullOrWhiteSpace(upn))
                        return upn;

                    string? cn = _peerCertificate?.GetNameInfo(X509NameType.SimpleName, false);
                    if (!string.IsNullOrWhiteSpace(cn))
                        return $"user@{cn}";

                    return "quic-punch-peer";
                }
            }
        }

        public byte[] CertPublicHash
        {
            get
            {
                lock (_lock)
                {
                    if (_peerCertPublicHash != null)
                        return _peerCertPublicHash;

                    EnsureLoadedLocked();
                    return _peerCertPublicHash = SHA3_256.HashData(_peerCertificate!.GetPublicKey());
                }
            }
        }

        public ECDiffieHellman EcdhKey
        {
            get
            {
                lock (_lock)
                {
                    EnsureLoadedLocked();
                    return _ecdhKey!;
                }
            }
        }

        public byte[] DeriveStaticSecret(ECDiffieHellmanPublicKey remotePublicKey)
        {
            if (remotePublicKey == null)
                throw new ArgumentNullException(nameof(remotePublicKey));

            lock (_lock)
            {
                if (_isDisposed)
                    throw new ObjectDisposedException(nameof(CertManager));

                EnsureLoadedLocked();
                return _ecdhKey!.DeriveRawSecretAgreement(remotePublicKey);
            }
        }

        public byte[] EcdhPublicKeyRaw
        {
            get
            {
                lock (_lock)
                {
                    if (_ecdhPublicKeyRaw != null)
                        return _ecdhPublicKeyRaw;

                    EnsureLoadedLocked();
                    var ext = _peerCertificate!.Extensions[EcdhExtensionOid];
                    if (ext != null)
                        return _ecdhPublicKeyRaw = ext.RawData;

                    return _ecdhPublicKeyRaw = _ecdhKey!.ExportSubjectPublicKeyInfo();
                }
            }
        }

        public ECDsa Curve
        {
            get
            {
                lock (_lock)
                {
                    if (_curve != null)
                        return _curve;

                    EnsureLoadedLocked();
                    return _curve = _peerCertificate!.GetECDsaPrivateKey() ?? throw new InvalidOperationException("Certificate does not contain an ECDSA private key.");
                }
            }
        }

        public byte[] CurveHash
        {
            get
            {
                lock (_lock)
                {
                    if (_curveHash != null)
                        return _curveHash;

                    return _curveHash = SHA3_256.HashData(Curve.ExportSubjectPublicKeyInfo());
                }
            }
        }

        private static readonly byte[] NostrIdentityInfo = "QuicPunch:NostrIdentity"u8.ToArray();

        public byte[] NostrPrivateKey
        {
            get
            {
                lock (_lock)
                {
                    if (_isDisposed)
                        throw new ObjectDisposedException(nameof(CertManager));

                    if (_nostrPrivateKey != null)
                        return _nostrPrivateKey;

                    EnsureLoadedLocked();
                    byte[] ecdhBytes = _ecdhKey!.ExportPkcs8PrivateKey();
                    try
                    {
                        _nostrPrivateKey = NostrSchnorr.DerivePrivateKey(ecdhBytes, NostrIdentityInfo);
                        return _nostrPrivateKey;
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(ecdhBytes);
                    }
                }
            }
        }

        public byte[] NostrPublicKey
        {
            get
            {
                lock (_lock)
                {
                    if (_nostrPublicKey != null)
                        return _nostrPublicKey;

                    _nostrPublicKey = NostrSchnorr.GetPublicKeyX(NostrPrivateKey);
                    return _nostrPublicKey;
                }
            }
        }

        public string NostrPublicKeyHex
        {
            get
            {
                lock (_lock)
                {
                    if (_nostrPublicKeyHex != null)
                        return _nostrPublicKeyHex;

                    _nostrPublicKeyHex = Convert.ToHexString(NostrPublicKey).ToLowerInvariant();
                    return _nostrPublicKeyHex;
                }
            }
        }

        public static X509Certificate2 GenerateIdentityCertificate(string hostname, string username, ECDiffieHellman ecdh)
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var request = new CertificateRequest(
                $"CN={hostname}",
                ecdsa,
                HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(
                    certificateAuthority: false,
                    hasPathLengthConstraint: false,
                    pathLengthConstraint: 0,
                    critical: true));

            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature,
                    critical: true));

            var eku = new OidCollection
            {
                new("1.3.6.1.5.5.7.3.1"),
                new("1.3.6.1.5.5.7.3.2")
            };

            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    eku,
                    critical: true));

            var san = new SubjectAlternativeNameBuilder();
            string upn = $"{username}@{hostname}";
            san.AddUserPrincipalName(upn);
            san.AddDnsName(hostname);
            san.AddDnsName("quic-punch");
            san.AddDnsName("cloudflare-quic.com");
            san.AddDnsName("cdn.cloudflare.net");
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            san.AddIpAddress(IPAddress.IPv6Loopback);
            san.AddIpAddress(IPAddress.Any);
            request.CertificateExtensions.Add(san.Build());

            request.CertificateExtensions.Add(
                new X509SubjectKeyIdentifierExtension(
                    request.PublicKey,
                    false));

            byte[] ecdhPub = ecdh.ExportSubjectPublicKeyInfo();
            request.CertificateExtensions.Add(
                new X509Extension(
                    new Oid(EcdhExtensionOid, "ECDH Key Extension"),
                    ecdhPub,
                    critical: false));

            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
            var notAfter = notBefore.AddYears(20);

            var cert = request.CreateSelfSigned(
                notBefore,
                notAfter);

            return X509CertificateLoader.LoadPkcs12(
                cert.Export(X509ContentType.Pfx),
                (string?)null,
                DefaultKeyStorageFlags);
        }

        public static X509Certificate2 GenerateIdentityCertificate(string peerId, ECDiffieHellman ecdh)
        {
            if (peerId.Contains('@'))
            {
                var parts = peerId.Split('@', 2);
                return GenerateIdentityCertificate(parts[1], parts[0], ecdh);
            }
            return GenerateIdentityCertificate(peerId, "user", ecdh);
        }

        public byte[] SessionNonce
        {
            get
            {
                lock (_lock)
                {
                    return _sessionNonce;
                }
            }
        }

        public ECDiffieHellman EphemeralEcdhKey
        {
            get
            {
                lock (_lock)
                {
                    if (_ephemeralEcdh != null)
                        return _ephemeralEcdh;
                    _ephemeralEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                    _ephemeralEcdhPublicKeyRaw = _ephemeralEcdh.ExportSubjectPublicKeyInfo();
                    return _ephemeralEcdh;
                }
            }
        }

        public byte[] EphemeralEcdhPublicKeyRaw
        {
            get
            {
                lock (_lock)
                {
                    if (_ephemeralEcdhPublicKeyRaw != null)
                        return _ephemeralEcdhPublicKeyRaw;
                    _ = EphemeralEcdhKey;
                    return _ephemeralEcdhPublicKeyRaw!;
                }
            }
        }

        public void CopySessionEntropyTo(global::QuicPunch.PeerInfo peer)
        {
            if (peer == null)
                throw new ArgumentNullException(nameof(peer));

            lock (_lock)
            {
                if (_isDisposed)
                    throw new ObjectDisposedException(nameof(CertManager));

                if (_ephemeralEcdh == null)
                {
                    _ephemeralEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                    _ephemeralEcdhPublicKeyRaw = _ephemeralEcdh.ExportSubjectPublicKeyInfo();
                }

                peer.SetLocalEntropy(_sessionNonce, _ephemeralEcdh);
            }
        }

        public void RenewSessionEntropy()
        {
            lock (_lock)
            {
                _sessionNonce = RandomNumberGenerator.GetBytes(32);
                try { _ephemeralEcdh?.Dispose(); } catch { }
                _ephemeralEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                _ephemeralEcdhPublicKeyRaw = _ephemeralEcdh.ExportSubjectPublicKeyInfo();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_isDisposed)
                    return;

                _isDisposed = true;
                if (_nostrPrivateKey != null)
                {
                    CryptographicOperations.ZeroMemory(_nostrPrivateKey);
                    _nostrPrivateKey = null;
                }

                _peerCertificate?.Dispose();
                _peerCertificate = null;
                _ecdhKey?.Dispose();
                _ecdhKey = null;
                _curve?.Dispose();
                _curve = null;
                try { _ephemeralEcdh?.Dispose(); } catch { }
                _ephemeralEcdh = null;
            }
        }
    }
}