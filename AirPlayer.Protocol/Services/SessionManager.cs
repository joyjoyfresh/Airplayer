using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using AirPlayer.Protocol.Models;

namespace AirPlayer.Protocol.Services
{
    /// <summary>
    /// AirPlay 会话管理器（单例，维护所有客户端会话状态）
    /// </summary>
    public class SessionManager
    {
        private static SessionManager? _current;
        private readonly ConcurrentDictionary<string, Session> _sessions;

        public static SessionManager Current => _current ??= new SessionManager();

        private SessionManager()
        {
            _sessions = new ConcurrentDictionary<string, Session>();
        }

        public Task<Session> GetSessionAsync(string key)
        {
            key = NormalizeKey(key);
            _sessions.TryGetValue(key, out Session? session);
            return Task.FromResult(session ?? new Session(key));
        }

        public Task CreateOrUpdateSessionAsync(string key, Session session)
        {
            key = NormalizeKey(key);
            _sessions.AddOrUpdate(key, session, (k, old) => session);
            return Task.CompletedTask;
        }

        private static string NormalizeKey(string key)
        {
            return string.IsNullOrWhiteSpace(key) ? "default" : key;
        }
    }
}
