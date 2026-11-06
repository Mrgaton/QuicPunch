using System.Security.Cryptography;
public static class ECDsaHelper
{
    private static readonly HashAlgorithmName alg = HashAlgorithmName.SHA384;
    private static readonly ECCurve curve = ECCurve.NamedCurves.nistP384;
    private static readonly int keySize = 384 / 8;

    public static (byte[] publicKey, byte[] privateKey) GenerateKeyPair()
    {
        using (var ecdsa = ECDsa.Create(curve))
        {
            return (ecdsa.ExportSubjectPublicKeyInfo(), ecdsa.ExportECPrivateKey());
        }
    }

    public static byte[] SignData(byte[] data, byte[] privateKey)
    {
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportECPrivateKey(privateKey, out _);

            return ecdsa.SignData(data, alg);
        }
    }

    public static bool VerifySignature(byte[] data, byte[] signature, byte[] publicKey)
    {
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);

            return ecdsa.VerifyData(data, signature, alg);
        }
    }
}
