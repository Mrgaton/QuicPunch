using QuicPunch;
using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace QuicPunch
{
    internal class CertManager
    {
        internal CertManager(string configPath) 
        {
            CertPath = Path.Combine(configPath, "peerCert.pfx");

            if (!Directory.Exists(configPath))
            {
                Directory.CreateDirectory(configPath);
            }
        }
        public string CertPath {  get; private set; }
        public X509Certificate2? PeerCertificate { 
            get
            {
                if (_peerCertificate != null)
                    return _peerCertificate;

                if (File.Exists(CertPath))
                {
                    return _peerCertificate = new X509Certificate2(
                        CertPath,
                        (string?)null,
                        X509KeyStorageFlags.Exportable |
                        X509KeyStorageFlags.PersistKeySet);
                }
                else
                {
                    var cert = GenerateIdentityCertificate(Environment.MachineName);

                    File.WriteAllBytes(CertPath, cert.Export(X509ContentType.Pfx));

                    return _peerCertificate = cert;
                }
            } 
        }

        private X509Certificate2 _peerCertificate;
        public byte[] CertPublicHash
        {
            get
            {
                if (_peerCertPublicHash != null)
                    return _peerCertPublicHash;

                return _peerCertPublicHash = SHA3_384.HashData(PeerCertificate.GetPublicKey());
            }
        }
        public byte[] _peerCertPublicHash;

        public ECDsa Curve { 
            get
            {
                if (_curve != null)
                    return _curve;

                return _curve = PeerCertificate.GetECDsaPrivateKey();
            } 
        }
        public ECDsa _curve;
        public byte[] CurveHash
        {
            get
            {
                if (_curveHash != null)
                    return _curveHash;

                return _curveHash = SHA3_256.HashData(Curve.ExportSubjectPublicKeyInfo());
            }
        }
        public byte[] _curveHash;
        public X509Certificate2 GenerateIdentityCertificate(string peerId)
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var request = new CertificateRequest(
                $"CN={peerId}",
                ecdsa,
                HashAlgorithmName.SHA384);


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


            san.AddUserPrincipalName(peerId);

            request.CertificateExtensions.Add(san.Build());

            request.CertificateExtensions.Add(
                new X509SubjectKeyIdentifierExtension(
                    request.PublicKey,
                    false));

            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
            var notAfter = notBefore.AddYears(20);

            var cert = request.CreateSelfSigned(
                notBefore,
                notAfter);

            return new X509Certificate2(
                cert.Export(X509ContentType.Pfx),
                (string?)null,
                X509KeyStorageFlags.Exportable |
                X509KeyStorageFlags.PersistKeySet);
        }
    }
}
