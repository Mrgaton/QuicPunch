using System.Globalization;
using System.IO.Hashing;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using QuicPunch.Helpers;
using TransportType = QuicPunch.QuicPunch.TransportType;

namespace QuicPunch
{
    public class PeerInfo : IDisposable
    {
        private readonly object _sessionLock = new();
        private bool _isDisposed;
        private X509Certificate2? _certificate;

        public X509Certificate2? Certificate
        {
            get => _certificate;

            private set
            {
                _certificate = value;
                _certHash = null;
                _idRaw = null;
                _id = null;
            }
        }

        private byte[]? _certHash;
        public bool HasCertificate => _certificate != null;
        public bool HasIdentity => _certHash != null || _certificate != null;

        public bool TryGetCertificateHash(out byte[] certHash)
        {
            if (_certHash != null)
            {
                certHash = _certHash;
                return true;
            }

            if (_certificate != null)
            {
                certHash = _certHash = SHA3_256.HashData(_certificate.GetPublicKey());
                return true;
            }

            certHash = Array.Empty<byte>();
            return false;
        }

        public byte[] CertHash => TryGetCertificateHash(out var certHash)
            ? certHash
            : Array.Empty<byte>();

        private byte[]? _idRaw;
        public bool TryGetId(out Guid id)
        {
            if (_id.HasValue)
            {
                id = _id.Value;
                return true;
            }

            if (!TryGetCertificateHash(out var certHash))
            {
                id = Guid.Empty;
                return false;
            }

            _idRaw ??= XxHash128.Hash(certHash);
            _id = new Guid(_idRaw);
            id = _id.Value;
            return true;
        }

        public byte[] IdRaw => TryGetId(out _) ? (_idRaw ?? Array.Empty<byte>()) : Array.Empty<byte>();

        private Guid? _id;
        public Guid Id => TryGetId(out var id) ? id : Guid.Empty;

        public uint ShortId => TryGetId(out _) ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(IdRaw.AsSpan(0, 4)) : 0;

        private ECDsa? _curve;
        public ECDsa Curve => _curve ??= Certificate?.GetECDsaPublicKey() ?? throw new InvalidOperationException("Certificate does not contain an ECDSA public key.");

        public string? Name;

        public QuicPunch.NetworkType NetworkType;

        public string? OnionAddress;

        public IPEndPoint? ActiveEndPoint;

        public Helpers.QuicConnectionTelemetry? LastTelemetry;

        public IPAddress[] Addresses = Array.Empty<IPAddress>();

        public int MinPort;
        public int MaxPort;

        public DateTime LastSeen;
        public long LastActivityTimestampMonotonic { get; private set; } = System.Diagnostics.Stopwatch.GetTimestamp();

        public void MarkSeen()
        {
            LastSeen = PreciseTime.GetCorrectTime();
            LastActivityTimestampMonotonic = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        public TransportType ActiveTransport { get; set; } = TransportType.Wan;
        public TorQuicConnectionManager? TorChannel { get; set; }

        public TimeSpan? Ping { get; set; }
        public DateTime? LastPingResponseUtc { get; set; }

        public bool IsConnectedAndResponsive(TimeSpan? timeout = null)
        {
            var maxAge = timeout ?? TimeSpan.FromMinutes(1);
            var now = DateTime.UtcNow;

            if (LastPingResponseUtc.HasValue && (now - LastPingResponseUtc.Value) <= maxAge)
                return true;

            if (LastSeen > DateTime.MinValue && (now - LastSeen) <= maxAge)
                return true;

            if (System.Diagnostics.Stopwatch.GetElapsedTime(LastActivityTimestampMonotonic) <= maxAge)
                return true;

            return false;
        }

        public byte[]? EcdhPublicKey;
        public byte[]? SessionNonce;
        public byte[]? EphemeralEcdhPublicKey;
        public byte[]? TokenSignature;
        public string? NostrPubKey;
        public byte[]? NostrPublicKeyBytes;
        public bool IsCertVerified { get; set; }
        public byte[]? CertPublicKey { get; set; }

        /// <summary>
        /// Cached TLS 1.3 0-RTT session resumption ticket for fast connection resumption.
        /// </summary>
        public byte[]? ResumptionTicket { get; set; }

        public byte[]? LocalSessionNonce { get; set; }
        public ECDiffieHellman? LocalEphemeralEcdh { get; set; }
        private byte[]? _localEphemeralPublicKeyRaw;
        public byte[] LocalEphemeralEcdhPublicKeyRaw
        {
            get
            {
                if (_localEphemeralPublicKeyRaw == null && LocalEphemeralEcdh != null)
                {
                    _localEphemeralPublicKeyRaw = LocalEphemeralEcdh.ExportSubjectPublicKeyInfo();
                }
                return _localEphemeralPublicKeyRaw ?? Array.Empty<byte>();
            }
        }
        public byte[]? ActiveLocalSessionNonce { get; private set; }
        public byte[]? ActiveRemoteSessionNonce { get; private set; }

        public byte[]? ActiveSessionKeyId { get; private set; }
        public AesGcm? TxCipher { get; private set; }
        public AesGcm? RxCipher { get; private set; }
        public byte[]? TxSalt { get; private set; }
        public byte[]? RxSalt { get; private set; }

        public bool IsSessionReady => TxCipher != null && RxCipher != null && TxSalt != null && RxSalt != null;
        public AesGcm? PeerCipher => TxCipher;

        private ulong _outboundSequence;
        public AntiReplayWindow InboundReplayFilter { get; } = new();
        public long LastSeenHelloTicks;
        public long LastSeenAckTicks;
        public long LastSeenPingTimestamp;
        public readonly HashSet<Guid> ProcessedHandshakeGuids = new();

        public ulong GetNextOutboundSequence() => Interlocked.Increment(ref _outboundSequence);

        public void RenewLocalEntropy()
        {
            lock (_sessionLock)
            {
                if (_isDisposed) return;
                LocalSessionNonce = RandomNumberGenerator.GetBytes(32);
                try { LocalEphemeralEcdh?.Dispose(); } catch { }
                LocalEphemeralEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                _localEphemeralPublicKeyRaw = null;
            }
        }

        public void EnsureLocalEntropy()
        {
            lock (_sessionLock)
            {
                if (_isDisposed) return;
                if (LocalSessionNonce is { Length: 32 } && LocalEphemeralEcdh != null)
                    return;

                LocalSessionNonce = RandomNumberGenerator.GetBytes(32);
                try { LocalEphemeralEcdh?.Dispose(); } catch { }
                LocalEphemeralEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                _localEphemeralPublicKeyRaw = null;
            }
        }

        internal void SetLocalEntropy(byte[] sessionNonce, ECDiffieHellman sourceEcdh)
        {
            if (sessionNonce == null || sessionNonce.Length != 32)
                throw new ArgumentException("Session nonce must be exactly 32 bytes.", nameof(sessionNonce));
            if (sourceEcdh == null)
                throw new ArgumentNullException(nameof(sourceEcdh));

            byte[] privateKey;
            try
            {
                privateKey = sourceEcdh.ExportPkcs8PrivateKey();
            }
            catch (ObjectDisposedException)
            {
                RenewLocalEntropy();
                return;
            }

            try
            {
                var clonedEcdh = ECDiffieHellman.Create();
                try
                {
                    clonedEcdh.ImportPkcs8PrivateKey(privateKey, out int bytesRead);
                    if (bytesRead != privateKey.Length)
                        throw new CryptographicException("Could not import the complete ephemeral ECDH key.");

                    lock (_sessionLock)
                    {
                        if (_isDisposed)
                        {
                            clonedEcdh.Dispose();
                            return;
                        }

                        try { LocalEphemeralEcdh?.Dispose(); } catch { }
                        LocalEphemeralEcdh = clonedEcdh;
                        clonedEcdh = null!;
                        LocalSessionNonce = (byte[])sessionNonce.Clone();
                        _localEphemeralPublicKeyRaw = LocalEphemeralEcdh.ExportSubjectPublicKeyInfo();
                    }
                }
                finally
                {
                    clonedEcdh?.Dispose();
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
        }

        internal void CopyLocalEntropyTo(PeerInfo target)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            byte[] nonce;
            ECDiffieHellman ecdh;
            lock (_sessionLock)
            {
                if (_isDisposed) return;
                EnsureLocalEntropy();
                nonce = (byte[])LocalSessionNonce!.Clone();
                ecdh = LocalEphemeralEcdh!;
            }
            target.SetLocalEntropy(nonce, ecdh);
        }

        public PeerInfo() { }

        public PeerInfo(X509Certificate2? certificate, byte[]? ecdhPublicKey)
        {
            if (certificate != null)
                Certificate = certificate;
            EcdhPublicKey = ecdhPublicKey;
        }

        public PeerInfo SetCertificate(X509Certificate2 certificate)
        {
            Certificate = certificate;
            return this;
        }
        public PeerInfo SetECDHPublicKey(byte[] ecdhPublicKey)
        {
            EcdhPublicKey = ecdhPublicKey;
            return this;
        }
        public PeerInfo SetCertificateHash(byte[] hash)
        {
            ArgumentNullException.ThrowIfNull(hash);
            if (hash.Length != 32)
                throw new ArgumentException("Certificate hash must be exactly 32 bytes.", nameof(hash));

            _certHash = (byte[])hash.Clone();
            _idRaw = null;
            _id = null;
            return this;
        }

        public bool IsTrusted(QuicPunch qp) => qp.IsTrustedPeer(this);

        public PeerInfo InitSession(QuicPunch qp, TransportType transport = TransportType.Wan)
        {
            lock (_sessionLock)
            {
                if (_isDisposed)
                    throw new ObjectDisposedException(nameof(PeerInfo));

                var certMgr = qp.GetCertManager(transport);

                if (EcdhPublicKey == null)
                    throw new ArgumentNullException("Remote public key is null.");

                if (LocalEphemeralEcdh == null || LocalSessionNonce is not { Length: 32 })
                {
                    // CertManager copies nonce + ECDH under one lock. PeerInfo owns the
                    // resulting clone, so disposing/rekeying one peer cannot affect another.
                    certMgr.CopySessionEntropyTo(this);
                }

                if (SessionNonce is not { Length: 32 })
                    throw new CryptographicException("Remote session nonce must be exactly 32 bytes.");
                if (EphemeralEcdhPublicKey == null || EphemeralEcdhPublicKey.Length == 0)
                    throw new CryptographicException("Remote ephemeral ECDH public key is required.");

                using var remoteStaticEcdh = ECDiffieHellman.Create();
                remoteStaticEcdh.ImportSubjectPublicKeyInfo(EcdhPublicKey, out _);

                byte[] staticSecret = certMgr.DeriveStaticSecret(remoteStaticEcdh.PublicKey);
                byte[] ephemeralSecret;

                using (var remoteEphemeralEcdh = ECDiffieHellman.Create())
                {
                    remoteEphemeralEcdh.ImportSubjectPublicKeyInfo(EphemeralEcdhPublicKey, out int bytesRead);
                    if (bytesRead != EphemeralEcdhPublicKey.Length)
                        throw new CryptographicException("Could not import the complete remote ephemeral ECDH key.");

                    ephemeralSecret = LocalEphemeralEcdh!.DeriveRawSecretAgreement(remoteEphemeralEcdh.PublicKey);
                }

                byte[] ikmSource = Utilities.Combine(staticSecret, ephemeralSecret);
                byte[] ikm = SHA256.HashData(ikmSource);

                byte[] keyMaterial = new byte[72]; // 32B Key_A->B + 4B Salt_A->B + 32B Key_B->A + 4B Salt_B->A

                try
                {
                    var currentPeer = qp.GetCurrentPeer(transport);
                    bool isInitiator = currentPeer != null && currentPeer.Id.CompareTo(this.Id) > 0;

                    byte[] localNonce = LocalSessionNonce;
                    byte[] remoteNonce = SessionNonce ?? Array.Empty<byte>();

                    byte[] nonceA = isInitiator ? localNonce : remoteNonce;
                    byte[] nonceB = isInitiator ? remoteNonce : localNonce;

                    byte[] poolId = qp.PoolId != null && qp.PoolId.Length > 0 ? qp.PoolId : Array.Empty<byte>();
                    byte[] sessionSalt = SHA256.HashData(Utilities.Combine(nonceA, nonceB, poolId));

                    byte[] info = System.Text.Encoding.UTF8.GetBytes("QuicPunch-DataChannel-v2");

                    HKDF.DeriveKey(
                        HashAlgorithmName.SHA256,
                        ikm: ikm,
                        output: keyMaterial,
                        salt: sessionSalt,
                        info: info
                    );

                    ReadOnlySpan<byte> keyAtoB = keyMaterial.AsSpan(0, 32);
                    ReadOnlySpan<byte> saltAtoB = keyMaterial.AsSpan(32, 4);
                    ReadOnlySpan<byte> keyBtoA = keyMaterial.AsSpan(36, 32);
                    ReadOnlySpan<byte> saltBtoA = keyMaterial.AsSpan(68, 4);

                    TxCipher?.Dispose();
                    RxCipher?.Dispose();

                    if (isInitiator)
                    {
                        TxCipher = new AesGcm(keyAtoB, tagSizeInBytes: 16);
                        TxSalt = saltAtoB.ToArray();
                        RxCipher = new AesGcm(keyBtoA, tagSizeInBytes: 16);
                        RxSalt = saltBtoA.ToArray();
                    }
                    else
                    {
                        TxCipher = new AesGcm(keyBtoA, tagSizeInBytes: 16);
                        TxSalt = saltBtoA.ToArray();
                        RxCipher = new AesGcm(keyAtoB, tagSizeInBytes: 16);
                        RxSalt = saltAtoB.ToArray();
                    }

                    ActiveLocalSessionNonce = (byte[])LocalSessionNonce.Clone();
                    ActiveRemoteSessionNonce = (byte[])remoteNonce.Clone();
                    ActiveSessionKeyId = SHA256.HashData(Utilities.Combine(saltAtoB.ToArray(), saltBtoA.ToArray()));
                    InboundReplayFilter.Reset();
                    Interlocked.Exchange(ref _outboundSequence, 0);

                    return this;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(staticSecret);
                    CryptographicOperations.ZeroMemory(ephemeralSecret);
                    CryptographicOperations.ZeroMemory(ikm);
                    CryptographicOperations.ZeroMemory(keyMaterial);
                }
            }
        }

        public void Dispose()
        {
            lock (_sessionLock)
            {
                if (_isDisposed) return;
                _isDisposed = true;

                try { TxCipher?.Dispose(); } catch { }
                try { RxCipher?.Dispose(); } catch { }
                try { _curve?.Dispose(); } catch { }
                try { _certificate?.Dispose(); } catch { }
                try { TorChannel?.Dispose(); } catch { }
                try { LocalEphemeralEcdh?.Dispose(); } catch { }
                LocalEphemeralEcdh = null;
                _localEphemeralPublicKeyRaw = null;
                TxCipher = null;
                RxCipher = null;
                TxSalt = null;
                RxSalt = null;
                ActiveLocalSessionNonce = null;
                ActiveRemoteSessionNonce = null;
                ActiveSessionKeyId = null;
                InboundReplayFilter.Reset();
            }
        }
    }

    public sealed class DiscoveredPeerInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "Discovered Peer";
        public string Token { get; set; } = "";
        public byte[] CertHash { get; set; } = Array.Empty<byte>();
        public string CertHashBase64 { get; set; } = "";
        public string[] Addresses { get; set; } = Array.Empty<string>();
        public string OnionAddress { get; set; } = "";
        public int MinPort { get; set; }
        public int MaxPort { get; set; }
        public QuicPunch.NetworkType NetworkType { get; set; } = QuicPunch.NetworkType.Static;
        public string? NostrPubKey { get; set; }
        public string Source { get; set; } = "Nostr";
        public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
        public bool IsTor => NetworkType == QuicPunch.NetworkType.Tor || !string.IsNullOrEmpty(OnionAddress);
        public bool IsCertVerified { get; set; }
        public byte[]? CertPublicKey { get; set; }
        public string? CertPublicKeyBase64 { get; set; }
    }
}
