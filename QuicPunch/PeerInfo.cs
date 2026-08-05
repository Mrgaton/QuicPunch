using System.Globalization;
using System.IO.Hashing;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace QuicPunch
{
    public class PeerInfo
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
        public byte[] CertHash => _certHash ??= SHA3_384.HashData(Certificate.GetPublicKey());

        private byte[]? _idRaw;
        public byte[] IdRaw => _idRaw ??= CertHash == null ? throw new NullReferenceException("Certificate hash is null.") : XxHash128.Hash(CertHash);
        private Guid? _id;
        public Guid Id => _id ??= new Guid(IdRaw);

        public AesCng? aes = null;

        private ECDsa? _curve;
        public ECDsa Curve => _curve ??= Certificate.GetECDsaPublicKey() ?? throw new InvalidOperationException("Certificate does not contain an ECDSA public key.");

        public string Name;

        public QuicPunch.NetworkType NetworkType;

        public IPEndPoint ActiveEndPoint;

        public IPAddress[] Addresses = Array.Empty<IPAddress>();

        public int MinPort;
        public int MaxPort;

        public DateTime LastSeen;

        public long? UpTicks { get; set; }
        public long? DownTicks { get; set; }
        public TimeSpan? Ping { get; set; }

        public byte[] EcdhPublicKey;
        public byte[]? SessionKey { get; set; }
        public AesGcm? PeerCipher { get; set; }

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

        public PeerInfo InitSession(QuicPunch qp)
        {
            var localEcdh = qp.CertManager.EcdhKey;

            if (localEcdh == null)
                throw new ArgumentNullException(nameof(localEcdh));
            if (EcdhPublicKey == null)
                throw new ArgumentNullException("Remote public key is null.");

            using var remoteEcdh = ECDiffieHellman.Create();
            remoteEcdh.ImportSubjectPublicKeyInfo(EcdhPublicKey, out _);

            byte[] sharedSecret = localEcdh.DeriveRawSecretAgreement(remoteEcdh.PublicKey);

            var sessionKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ikm: sharedSecret,
                outputLength: 16,
                salt: qp.PoolId
            );

            PeerCipher = new AesGcm(sessionKey, tagSizeInBytes: 16);

            return this;
        }
    }
}
