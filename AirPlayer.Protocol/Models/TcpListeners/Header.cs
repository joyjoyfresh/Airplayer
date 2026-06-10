using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AirPlayer.Protocol.Models
{
    /// <summary>
    /// HTTP/RTSP 请求头解析器
    /// </summary>
    public class Header
    {
        public string Name { get; set; }
        public List<string> Values { get; set; }

        public Header(string name, string value)
        {
            Name = name?.Trim();
            Values = new List<string>();
            if (value != null)
            {
                Values.Add(value.Trim());
            }
        }

        public static Header FromHex(string hex)
        {
            var bytes = HexToBytes(hex);
            var raw = Encoding.ASCII.GetString(bytes);
            return FromPlain(raw);
        }

        public static Header FromPlain(string raw)
        {
            var split = raw.Split(':', 2);
            if (split.Length != 2)
            {
                return new Header(raw.Trim(), string.Empty);
            }

            return new Header(split[0], split[1]);
        }

        private static byte[] HexToBytes(string hex)
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
