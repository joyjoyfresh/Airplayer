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

        public Header(string hex)
        {
            Values = new List<string>();

            var bytes = HexToBytes(hex);
            var raw = Encoding.ASCII.GetString(bytes);

            var split = raw.Split(':', 2);
            if (split.Length == 2)
            {
                Name = split[0].Trim();
                var vals = split[1].Split(',');
                foreach (var v in vals)
                {
                    Values.Add(v.Trim());
                }
            }
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
