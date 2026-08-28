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
        private X509Certificate2 _certificate;

        public X509Certificate2 Certificate
        {
            get => _certificate;

            private set
            {
                _certificate = value;
                _certHash = null;
            }
        }

        private byte[]? _certHash;
        public byte[] CertHash => _certHash ??= SHA3_256.HashData(Certificate.GetPublicKey());

        private byte[]? _idRaw;
        public byte[] IdRaw => _idRaw ??= CertHash == null ? throw new NullReferenceException("Certificate hash is null.") : XxHash128.Hash(CertHash);
        private Guid? _id;
        public Guid Id => _id ??= new Guid(IdRaw);

        public AesCng? aes = null;

        private ECDsa? _curve;
        public ECDsa Curve => _curve ??= Certificate.GetECDsaPublicKey() ?? throw new InvalidOperationException("Certificate does not contain an ECDSA public key.");

        public string? Name;

        public QuicPunch.NetworkType NetworkType;

        public string? OnionAddress;

        public IPEndPoint? ActiveEndPoint;

        public IPAddress[] Addresses = Array.Empty<IPAddress>();

        public int MinPort;
        public int MaxPort;

        public DateTime LastSeen;

        public TransportType ActiveTransport { get; set; } = TransportType.Wan;
        public TorQuicConnectionManager? TorChannel { get; set; }

        public long? UpTicks { get; set; }
        public long? DownTicks { get; set; }
        public TimeSpan? Ping { get; set; }

        public byte[]? EcdhPublicKey;
        public byte[]? SessionNonce;
        public byte[]? EphemeralEcdhPublicKey;

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
            LocalSessionNonce = RandomNumberGenerator.GetBytes(32);
            try { LocalEphemeralEcdh?.Dispose(); } catch { }
            LocalEphemeralEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _localEphemeralPublicKeyRaw = null;
        }

        public PeerInfo() { }

        public PeerInfo(X509Certificate2? certificate, byte[]? ecdhPublicKey)
        {
            this.Certificate = certificate;
            this.EcdhPublicKey = ecdhPublicKey;
        }

        public PeerInfo SetCertificate(X509Certificate2 certificate)
        {
            this.Certificate = certificate;
            return this;
        }
        public PeerInfo SetECDHPublicKey(byte[] ecdhPublicKey)
        {
            this.EcdhPublicKey = ecdhPublicKey;
            return this;
        }
        public PeerInfo SetCertificateHash(byte[] hash)
        {
            this._certHash = hash;
            return this;
        }

        public bool IsTrusted(QuicPunch qp) => qp.IsTrustedPeer(this);

        public PeerInfo InitSession(QuicPunch qp, TransportType transport = TransportType.Wan)
        {
            var certMgr = qp.GetCertManager(transport);
            var localStaticEcdh = certMgr.EcdhKey;

            if (localStaticEcdh == null)
                throw new ArgumentNullException(nameof(localStaticEcdh));
            if (EcdhPublicKey == null)
                throw new ArgumentNullException("Remote public key is null.");

            if (LocalEphemeralEcdh == null)
            {
                LocalEphemeralEcdh = certMgr.EphemeralEcdhKey;
                _localEphemeralPublicKeyRaw = certMgr.EphemeralEcdhPublicKeyRaw;
            }
            if (LocalSessionNonce == null || LocalSessionNonce.Length == 0)
            {
                LocalSessionNonce = (byte[])certMgr.SessionNonce.Clone();
            }

            using var remoteStaticEcdh = ECDiffieHellman.Create();
            remoteStaticEcdh.ImportSubjectPublicKeyInfo(EcdhPublicKey, out _);

            byte[] staticSecret = localStaticEcdh.DeriveRawSecretAgreement(remoteStaticEcdh.PublicKey);
            byte[]? ephemeralSecret = null;

            if (EphemeralEcdhPublicKey != null && EphemeralEcdhPublicKey.Length > 0)
            {
                try
                {
                    using var remoteEphemeralEcdh = ECDiffieHellman.Create();
                    remoteEphemeralEcdh.ImportSubjectPublicKeyInfo(EphemeralEcdhPublicKey, out _);
                    ephemeralSecret = LocalEphemeralEcdh.DeriveRawSecretAgreement(remoteEphemeralEcdh.PublicKey);
                }
                catch (Exception)
                {
                    ephemeralSecret = null;
                }
            }

            byte[] ikm = ephemeralSecret != null
                ? SHA256.HashData(Utilities.Combine(staticSecret, ephemeralSecret))
                : staticSecret;

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
                if (ephemeralSecret != null) CryptographicOperations.ZeroMemory(ephemeralSecret);
                if (ikm != staticSecret) CryptographicOperations.ZeroMemory(ikm);
                CryptographicOperations.ZeroMemory(keyMaterial);
            }
        }

        public void Dispose()
        {
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
