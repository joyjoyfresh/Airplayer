using System;
using System.Threading;
using System.Threading.Tasks;

namespace AirPlayer.Protocol.Listeners
{
    /// <summary>
    /// 监听器抽象基类
    /// </summary>
    public abstract class BaseListener
    {
        public abstract Task StartAsync(CancellationToken cancellationToken);
        public abstract Task StopAsync();
    }
}
