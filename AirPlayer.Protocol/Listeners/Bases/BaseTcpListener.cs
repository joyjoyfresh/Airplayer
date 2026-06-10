using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AirPlayer.Protocol.Models;
using AirPlayer.Protocol.Models.Enums;

namespace AirPlayer.Protocol.Listeners
{
    /// <summary>
    /// TCP 监听器基类（处理 RTSP 请求或原始二进制数据）
    /// </summary>
    public class BaseTcpListener : BaseListener
    {
        private readonly ushort _port;
        private TcpListener _listener;
        private readonly ConcurrentDictionary<string, Task> _connections;
        private readonly bool _rawData;
        private readonly CancellationTokenSource _cancellationTokenSource;

        public int LocalPort => _listener.LocalEndpoint != null ? ((IPEndPoint)_listener.LocalEndpoint).Port : _port;

        public BaseTcpListener(ushort port, bool rawData = false)
        {
            _port = port;
            _rawData = rawData;
            _listener = TcpListener.Create(_port);
            _connections = new ConcurrentDictionary<string, Task>();
            _cancellationTokenSource = new CancellationTokenSource();
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                _listener.Start();
                Console.WriteLine($"[DEBUG-TCP] Listening on port {LocalPort}");
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"[DEBUG-TCP] Port {_port} unavailable ({ex.SocketErrorCode}), fallback to dynamic port");
                _listener = TcpListener.Create(0);
                _listener.Start();
                Console.WriteLine($"[DEBUG-TCP] Listening on fallback port {LocalPort}");
            }

            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationTokenSource.Token);
            Task.Run(() => AcceptClientsAsync(source.Token), source.Token);
            return Task.CompletedTask;
        }

        public override Task StopAsync()
        {
            _cancellationTokenSource.Cancel();
            try { _listener.Stop(); } catch { }
            return Task.CompletedTask;
        }

        public virtual Task OnDataReceivedAsync(Request request, Response response, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public virtual Task OnRawReceivedAsync(TcpClient client, NetworkStream stream, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private async Task AcceptClientsAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    var task = HandleClientAsync(client, cancellationToken);
                    var remoteEndpoint = client.Client.RemoteEndPoint.ToString();
                    if (!_connections.TryAdd(remoteEndpoint, task))
                    {
                        client.Close();
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            catch (SocketException) { }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            try
            {
                var remoteEndpoint = client.Client.RemoteEndPoint.ToString();
                var stream = client.GetStream();

                if (_rawData)
                {
                    await OnRawReceivedAsync(client, stream, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ReadFormattedAsync(client, stream, cancellationToken).ConfigureAwait(false);
                }

                if (client.Connected)
                {
                    client.Close();
                }
                _connections.Remove(remoteEndpoint, out _);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BaseTcpListener] Client error: {ex.Message}");
            }
        }

        private async Task ReadFormattedAsync(TcpClient client, NetworkStream stream, CancellationToken cancellationToken)
        {
            while (client.Connected && stream.CanRead && !cancellationToken.IsCancellationRequested)
            {
                Request request;
                try
                {
                    request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }

                if (request == null)
                {
                    break;
                }

                if (!request.IsValid)
                {
                    Console.WriteLine("[DEBUG-RTSP] Invalid request received");
                    break;
                }

                var response = request.GetBaseResponse();
                var cseq = request.Headers.ContainsKey("CSeq") ? request.Headers["CSeq"] : "?";
                Console.WriteLine($"[DEBUG-RTSP] >>> {request.Type} {request.Path} CSeq={cseq} Body={request.Body.Length}");

                await OnDataReceivedAsync(request, response, cancellationToken).ConfigureAwait(false);
                await SendResponseAsync(stream, response).ConfigureAwait(false);

                Console.WriteLine($"[DEBUG-RTSP] <<< {response.StatusCode}");
            }
        }

        private async Task<Request> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var headerBytes = await ReadUntilHeaderEndAsync(stream, cancellationToken).ConfigureAwait(false);
            if (headerBytes == null || headerBytes.Length == 0)
            {
                return null;
            }

            var headerText = Encoding.ASCII.GetString(headerBytes);
            var contentLength = GetContentLength(headerText);
            var bodyBytes = contentLength > 0
                ? await ReadExactAsync(stream, contentLength, cancellationToken).ConfigureAwait(false)
                : Array.Empty<byte>();
            var requestBytes = headerBytes.Concat(Encoding.ASCII.GetBytes("\r\n\r\n")).Concat(bodyBytes).ToArray();
            return new Request(requestBytes);
        }

        private static async Task<byte[]> ReadUntilHeaderEndAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var buffer = new List<byte>();
            var one = new byte[1];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(one, 0, 1, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    if (buffer.Count == 0)
                    {
                        return null;
                    }
                    throw new EndOfStreamException();
                }

                buffer.Add(one[0]);
                var count = buffer.Count;
                if (count >= 4 && buffer[count - 4] == '\r' && buffer[count - 3] == '\n' && buffer[count - 2] == '\r' && buffer[count - 1] == '\n')
                {
                    return buffer.Take(count - 4).ToArray();
                }
            }

            return null;
        }

        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length, CancellationToken cancellationToken)
        {
            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(buffer, offset, length - offset, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }
                offset += read;
            }
            return buffer;
        }

        private static int GetContentLength(string headerText)
        {
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                var parts = line.Split(':', 2);
                if (parts.Length == 2 && parts[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(parts[1].Trim(), out var value))
                    {
                        return value;
                    }
                }
            }
            return 0;
        }

        private async Task SendResponseAsync(NetworkStream stream, Response response)
        {
            var format = $"{response.GetProtocol()} {(int)response.StatusCode} {response.StatusCode.ToString().ToUpperInvariant()}\r\n";
            foreach (var header in response.Headers.GetHeaders())
            {
                format += $"{header.Name}: {string.Join(",", header.Values)}\r\n";
            }
            format += "\r\n";

            var formatBuffer = Encoding.ASCII.GetBytes(format);
            byte[] payload;
            var bodyBuffer = await response.ReadAsync();
            if (bodyBuffer?.Any() == true)
            {
                payload = formatBuffer.Concat(bodyBuffer).ToArray();
            }
            else
            {
                payload = formatBuffer;
            }

            try
            {
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
            }
            catch (IOException) { }
        }
    }
}
