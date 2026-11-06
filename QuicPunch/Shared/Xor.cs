using System.Security.Cryptography;

public class Xor
{
    public byte[][] Keys { get; set; }
    public int SaltSize { get; set; } = 32;

    public Xor(byte[][] Keys)
    {
        this.Keys = Keys;
    }

    public byte[] XNS(byte[] data) => XNS(data, SaltSize, Keys);

    public static byte[] XNS(byte[] data, int saltSize = 64, params byte[][] keys)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(saltSize);

        byte[][] newKeys = new byte[keys.Length + 1][];
        Array.Copy(keys, newKeys, keys.Length);
        newKeys[keys.Length] = salt;

        byte[] xored = XN(data, newKeys);

        return xored.Concat(salt).ToArray();
    }

    public byte[] XN(byte[] data) => XN(data, Keys);

    public static byte[] XN(byte[] data, params byte[][] keys)
    {
        byte[] result = new byte[data.Length];

        int max = data.Length - 1;
        byte last = (byte)(data.Length % byte.MaxValue);

        for (int i = 0; i < data.Length; i++)
        {
            result[i] = data[max - i];

            for (int k = 0; k < keys.Length; k++)
            {
                byte[] key = keys[k];
                int keyIndex = i % key.Length;

                result[i] ^= key[keyIndex];
                result[i] += last;
            }

            last ^= result[i];

            for (int k = keys.Length - 1; k >= 0; k--)
            {
                byte[] key = keys[k];
                int keyIndex = i % key.Length;

                last += result[i];

                if (i % 2 == 0)
                {
                    last &= key[key.Length - keyIndex - 1];
                }
                else
                {
                    last ^= key[key.Length - keyIndex - 1];
                }
            }
        }

        return result;
    }

    public byte[] UNS(byte[] data) => UNS(data, SaltSize, Keys);

    public static byte[] UNS(byte[] data, int saltSize = 64, params byte[][] keys)
    {
        int dataSize = data.Length - saltSize;

        ReadOnlySpan<byte> xored = data.AsSpan(0, dataSize);
        ReadOnlySpan<byte> salt = data.AsSpan(dataSize, saltSize);

        byte[][] newKeys = new byte[keys.Length + 1][];
        Array.Copy(keys, newKeys, keys.Length);
        newKeys[keys.Length] = salt.ToArray();

        byte[] result = UN(xored.ToArray(), newKeys);

        return result;
    }

    public byte[] UN(byte[] data) => UN(data, Keys);

    public static byte[] UN(byte[] data, params byte[][] keys)
    {
        byte[] result = new byte[data.Length];

        int max = data.Length - 1;
        byte last = (byte)(data.Length % byte.MaxValue);

        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            byte oldB = b;

            for (int k = keys.Length - 1; k >= 0; k--)
            {
                byte[] key = keys[k];
                int keyIndex = i % key.Length;

                b -= last;
                b ^= key[keyIndex];
            }

            result[max - i] = b;

            last ^= oldB;

            for (int k = keys.Length - 1; k >= 0; k--)
            {
                byte[] key = keys[k];
                int keyIndex = i % key.Length;

                last += data[i];

                if (i % 2 == 0)
                {
                    last &= key[key.Length - keyIndex - 1];
                }
                else
                {
                    last ^= key[key.Length - keyIndex - 1];
                }
            }
        }

        return result;
    }

    //Old horrible ones?
    /*public static byte[] X(byte[] data, byte[] key)
    {
        byte[] result = new byte[data.Length];

        int max = data.Length - 1;
        byte last = (byte)(data.Length % byte.MaxValue);

        for (int i = 0; i < data.Length; i++)
        {
            int keyIndex = i % key.Length;

            result[i] = (byte)(data[max - i] ^ key[keyIndex]);
            result[i] += last;

            last += data[max - i];

            if (i % 2 == 0)
            {
                last &= key[(key.Length - keyIndex) - 1];
            }
            else
            {
                last ^= key[(key.Length - keyIndex) - 1];
            }
        }

        return result;
    }

    public static byte[] U(byte[] data, byte[] key)
    {
        byte[] result = new byte[data.Length];

        int max = data.Length - 1;
        byte last = (byte)(data.Length % byte.MaxValue);

        for (int i = 0; i < data.Length; i++)
        {
            int keyIndex = i % key.Length;

            data[i] -= last;

            byte b = (byte)(data[i] ^ key[keyIndex]);

            result[max - i] = b;
            last += b;

            if (i % 2 == 0)
            {
                last &= key[(key.Length - keyIndex) - 1];
            }
            else
            {
                last ^= key[(key.Length - keyIndex) - 1];
            }
        }

        return result;
    }*/
}
