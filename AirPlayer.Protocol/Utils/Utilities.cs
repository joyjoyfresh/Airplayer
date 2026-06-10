using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace AirPlayer.Protocol.Utils
{
    public static class Utilities
    {
        public const string PAIR_VERIFY_AES_KEY = "Pair-Verify-AES-Key";
        public const string PAIR_VERIFY_AES_IV = "Pair-Verify-AES-IV";

        public static byte[] CopyOfRange(byte[] src, int start, int end)
        {
            int len = end - start;
            byte[] dest = new byte[len];
            Array.Copy(src, start, dest, 0, len);
            return dest;
        }

        public static byte[] Hash(byte[] first, byte[] last)
        {
            byte[] combined = first.Concat(last).ToArray();
            byte[] hashed = SHA512.HashData(combined);
            return hashed;
        }

        public static ushort SeqNumCmp(int s1, int s2)
        {
            return (ushort)(s1 - s2);
        }

        public static void Swap(byte[] arr, int idxA, int idxB)
        {
            using (var mem = new MemoryStream(arr))
            using (var reader = new BinaryReader(mem))
            using (var writer = new BinaryWriter(mem))
            {
                mem.Position = idxA;
                var a = reader.ReadInt32();
                mem.Position = idxB;
                var b = reader.ReadInt32();
                mem.Position = idxB;
                writer.Write(a);
                mem.Position = idxA;
                writer.Write(b);
            }
        }

        public static IBufferedCipher InitializeChiper(byte[] ecdhShared)
        {
            var pairverifyaeskey = Encoding.UTF8.GetBytes(PAIR_VERIFY_AES_KEY);
            var pairverifyaesiv = Encoding.UTF8.GetBytes(PAIR_VERIFY_AES_IV);

            byte[] digestAesKey = Hash(pairverifyaeskey, ecdhShared);
            byte[] sharedSecretSha512AesKey = CopyOfRange(digestAesKey, 0, 16);

            byte[] digestAesIv = Hash(pairverifyaesiv, ecdhShared);
            byte[] sharedSecretSha512AesIv = CopyOfRange(digestAesIv, 0, 16);

            var aesCipher = CipherUtilities.GetCipher("AES/CTR/NoPadding");
            KeyParameter keyParameter = ParameterUtilities.CreateKeyParameter("AES", sharedSecretSha512AesKey);
            var cipherParameters = new ParametersWithIV(keyParameter, sharedSecretSha512AesIv, 0, sharedSecretSha512AesIv.Length);
            aesCipher.Init(true, cipherParameters);
            return aesCipher;
        }
    }
}
