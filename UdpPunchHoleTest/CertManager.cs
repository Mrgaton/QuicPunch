using QuicPunch;
using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace UdpPunchHoleTest
{
    internal class CertManager
    {
        public static string CertPath = Path.Combine(Helpers.AppDataPath, "peerCert.pfx");
        public static string DilithiumPath = Path.Combine(Helpers.AppDataPath, "dilithium.key");
        public static X509Certificate2? PeerCertificate { 
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
                        X509KeyStorageFlags.EphemeralKeySet);
                }
                else
                {
                    var cert = GenerateIdentityCertificate(Environment.MachineName);

                    if (!Directory.Exists(Helpers.AppDataPath))
                    {
                        Directory.CreateDirectory(Helpers.AppDataPath);
                    }

                    File.WriteAllBytes(CertPath, cert.Export(X509ContentType.Pfx));

                    return _peerCertificate = cert;
                }
            } 
        }

        private static X509Certificate2 _peerCertificate;


        public static byte[] PeerCertPublicHash
        {
            get
            {
                if (_peerCertPublicHash != null)
                    return _peerCertPublicHash;

                return _peerCertPublicHash = SHA3_512.HashData(PeerCertificate.GetPublicKey());
            }
        }
        public static byte[] _peerCertPublicHash;

        public static ECDsa Curve { 
            get
            {
                if (_curve != null)
                    return _curve;

                if (File.Exists(DilithiumPath))
                {
                    var ecdsa = ECDsa.Create();
                    ecdsa.ImportECPrivateKey(File.ReadAllBytes(DilithiumPath), out _);
                    return _curve = ecdsa;
                }

                ECDsa curve = ECDsa.Create(ECCurve.NamedCurves.nistP521);
                File.WriteAllBytes(DilithiumPath, curve.ExportECPrivateKey());
                return _curve = curve;
            } 
        }
        public static ECDsa _curve;

        public static X509Certificate2 GenerateIdentityCertificate(
            string peerId,
            IEnumerable<string>? dnsNames = null,
            IEnumerable<IPAddress>? ipAddresses = null)
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384);

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

            if (dnsNames != null)
            {
                foreach (var dns in dnsNames)
                {
                    san.AddDnsName(dns);
                }
            }

            if (ipAddresses != null)
            {
                foreach (var ip in ipAddresses)
                {
                    san.AddIpAddress(ip);
                }
            }

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
                X509KeyStorageFlags.EphemeralKeySet);
        }
    }
}
