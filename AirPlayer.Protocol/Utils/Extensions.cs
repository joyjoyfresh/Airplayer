using System;
using System.Linq;

namespace AirPlayer.Protocol.Utils
{
    /// <summary>
    /// 扩展方法（十六进制转换等）
    /// </summary>
    public static class Extensions
    {
        public static byte[] HexToBytes(this string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }
    }
}
