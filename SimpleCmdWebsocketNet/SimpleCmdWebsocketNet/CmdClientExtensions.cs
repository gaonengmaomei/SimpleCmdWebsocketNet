using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleCmdWebsocketNet
{
    public static class CmdClientExtensions
    {
        public static async Task<T> SendAndWaitAsync<T>(this CmdClient client, string cmdName, byte[] data, CancellationToken externalToken = default, int timeoutMs = 10 * 1000)
        {
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.PendingTcss[cmdName] = new PendingTcsContext
            {
                Tcs = tcs,
                CreateTime = DateTime.Now
            };

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken))
            {
                cts.CancelAfter(timeoutMs);

                using (cts.Token.Register(() => tcs.TrySetCanceled()))
                {
                    try
                    {
                        await client.SendAsync(data, externalToken);

                        var result = await tcs.Task;
                        return (T)result;
                    }
                    catch (OperationCanceledException)
                    {
                        externalToken.ThrowIfCancellationRequested();

                        throw new TimeoutException($"{cmdName}响应超时({timeoutMs}ms)");
                    }
                    finally
                    {
                        client.PendingTcss.TryRemove(cmdName, out _);
                    }
                }
            }
        }

        public static void Handle<T>(this CmdClient client, string cmdName, Action<T> handler)
        {
            if (handler == null) return;

            client.CommandMap[cmdName] = (data) =>
            {
                if (data is T typedData)
                {
                    handler(typedData);
                }
            };
        }

        public static void Unhandle(this CmdClient client, string cmdName)
        {
            client.CommandMap.TryRemove(cmdName, out _);
        }


        public static Task<T> WaitHandle<T>(this CmdClient client, string cmdName, DateTime? dateTime = null)
        {
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

            var time = dateTime ?? DateTime.Now;
            client.PendingTcss[cmdName] = new PendingTcsContext
            {
                Tcs = tcs,
                CreateTime = time,
            };

            return CastTask<T>(tcs.Task);
        }

        public static void UnWaitHandle(this CmdClient client, string cmdName)
        {
            if (client.PendingTcss.TryRemove(cmdName, out var item))
            {
                item.Tcs.TrySetCanceled();
            }
        }

        private static async Task<T> CastTask<T>(Task<object> task)
        {
            var result = await task;
            return (T)result;
        }
    }
}
