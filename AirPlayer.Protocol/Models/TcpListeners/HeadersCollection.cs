using System;
using System.Collections.Generic;
using System.Linq;

namespace AirPlayer.Protocol.Models
{
    /// <summary>
    /// HTTP/RTSP 请求头集合
    /// </summary>
    public class HeadersCollection
    {
        private readonly Dictionary<string, Header> _headers = new Dictionary<string, Header>(StringComparer.OrdinalIgnoreCase);

        public void Add(string name, Header header)
        {
            _headers[name] = header;
        }

        public void Add(string name, string value)
        {
            _headers[name] = new Header($"{name}: {value}")
            {
                Name = name,
                Values = new List<string> { value }
            };
        }

        public bool ContainsKey(string name)
        {
            return _headers.ContainsKey(name);
        }

        public string this[string name]
        {
            get
            {
                if (_headers.TryGetValue(name, out var header))
                {
                    return string.Join(",", header.Values);
                }
                return null;
            }
        }

        public T GetValue<T>(string name)
        {
            var val = this[name];
            if (val != null)
            {
                return (T)Convert.ChangeType(val, typeof(T));
            }
            return default;
        }

        public IEnumerable<Header> GetHeaders()
        {
            return _headers.Values;
        }
    }
}
