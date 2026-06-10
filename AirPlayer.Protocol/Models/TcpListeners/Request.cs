using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AirPlayer.Protocol.Models.Enums;

namespace AirPlayer.Protocol.Models
{
    /// <summary>
    /// RTSP/HTTP 请求解析器（从十六进制字符串解析 AirPlay 协议请求）
    /// </summary>
    public class Request
    {
        public const string AIRTUNES_SERVER_VERSION = "AirTunes/220.68";

        private readonly byte[] _rawRequest;
        private readonly byte[] _headerBytes;
        private bool _valid = true;
        private RequestType _type;
        private string _path;
        private ProtocolType _protocol;
        private byte[] _rawBody;
        private HeadersCollection _headers;

        public bool IsValid => _valid;
        public RequestType Type => _type;
        public string Path => _path;
        public ProtocolType Protocol => _protocol;
        public byte[] Body => _rawBody;
        public HeadersCollection Headers => _headers;

        public Request(string hex) : this(HexToBytes(hex ?? throw new ArgumentNullException(nameof(hex))))
        {
        }

        public Request(byte[] rawRequest)
        {
            _rawRequest = rawRequest ?? throw new ArgumentNullException(nameof(rawRequest));
            _headers = new HeadersCollection();
            _rawBody = Array.Empty<byte>();

            var headerLength = FindHeaderLength(_rawRequest);
            if (headerLength < 0)
            {
                _valid = false;
                _headerBytes = _rawRequest;
            }
            else
            {
                _headerBytes = _rawRequest.Take(headerLength).ToArray();
            }

            Initialize();
        }

        public Task<byte[]> GetFullRawAsync()
        {
            return Task.FromResult(_rawRequest);
        }

        private void Initialize()
        {
            var headerText = Encoding.ASCII.GetString(_headerBytes);
            var rows = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None).ToArray();

            if (rows?.Any() == false)
            {
                _valid = false;
                return;
            }

            var type = ResolveRequestType(rows[0]);
            if (!type.HasValue)
            {
                _valid = false;
                return;
            }

            var path = ResolvePath(rows[0]);
            if (string.IsNullOrWhiteSpace(path))
            {
                _valid = false;
                return;
            }

            var protocol = ResolveProtocol(rows[0]);
            if (!protocol.HasValue)
            {
                _valid = false;
                return;
            }

            _type = type.Value;
            _path = path;
            _protocol = protocol.Value;

            foreach (var header in rows.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(header))
                {
                    break;
                }
                var h = ResolveHeader(header);
                _headers.Add(h.Name, h);
            }

            if (_headers.ContainsKey("Content-Length"))
            {
                var contentLength = _headers.GetValue<int>("Content-Length");
                if (contentLength > 0)
                {
                    var bodyOffset = _headerBytes.Length + 4;
                    var bodyBytes = _rawRequest.Skip(bodyOffset).Take(contentLength).ToArray();
                    if (contentLength == bodyBytes.Length)
                    {
                        _rawBody = bodyBytes;
                    }
                    else
                    {
                        _valid = false;
                    }
                }
            }
        }

        private RequestType? ResolveRequestType(string line)
        {
            var method = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (method == null) return null;
            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase)) return RequestType.GET;
            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase)) return RequestType.POST;
            if (method.Equals("SETUP", StringComparison.OrdinalIgnoreCase)) return RequestType.SETUP;
            if (method.Equals("RECORD", StringComparison.OrdinalIgnoreCase)) return RequestType.RECORD;
            if (method.Equals("GET_PARAMETER", StringComparison.OrdinalIgnoreCase)) return RequestType.GET_PARAMETER;
            if (method.Equals("SET_PARAMETER", StringComparison.OrdinalIgnoreCase)) return RequestType.SET_PARAMETER;
            if (method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase)) return RequestType.OPTIONS;
            if (method.Equals("ANNOUNCE", StringComparison.OrdinalIgnoreCase)) return RequestType.ANNOUNCE;
            if (method.Equals("FLUSH", StringComparison.OrdinalIgnoreCase)) return RequestType.FLUSH;
            if (method.Equals("TEARDOWN", StringComparison.OrdinalIgnoreCase)) return RequestType.TEARDOWN;
            if (method.Equals("PAUSE", StringComparison.OrdinalIgnoreCase)) return RequestType.PAUSE;
            if (method.Equals("SETPEERS", StringComparison.OrdinalIgnoreCase)) return RequestType.SETPEERS;
            return null;
        }

        private string ResolvePath(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? parts[1] : null;
        }

        private ProtocolType? ResolveProtocol(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var protocol = parts.Length >= 3 ? parts[2] : null;
            if (protocol == null) return null;
            if (protocol.Equals("HTTP/1.0", StringComparison.OrdinalIgnoreCase)) return ProtocolType.HTTP10;
            if (protocol.Equals("HTTP/1.1", StringComparison.OrdinalIgnoreCase)) return ProtocolType.HTTP11;
            if (protocol.Equals("RTSP/1.0", StringComparison.OrdinalIgnoreCase)) return ProtocolType.RTSP10;
            return null;
        }

        public Response GetBaseResponse()
        {
            var response = new Response(_protocol, StatusCode.OK);
            response.Headers.Add("Server", AIRTUNES_SERVER_VERSION);
            if (_headers.ContainsKey("CSeq"))
            {
                response.Headers.Add("CSeq", _headers["CSeq"]);
            }
            return response;
        }

        private Header ResolveHeader(string line)
        {
            return Header.FromPlain(line);
        }

        private static int FindHeaderLength(byte[] data)
        {
            for (var i = 0; i <= data.Length - 4; i++)
            {
                if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                {
                    return i;
                }
            }

            return -1;
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
