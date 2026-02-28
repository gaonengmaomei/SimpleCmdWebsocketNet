using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SimpleCmdWebsocketNet
{
    public class CmdClient : IDisposable
    {
        private readonly ClientWebSocket _ws;

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);

        private int _isReceiving = 0;
        private readonly Channel<byte[]> _messageChannel = Channel.CreateUnbounded<byte[]>();
        private Func<byte[], MessageParseResult> _messageParser;

        private bool _isDisposed = false;

        public ConcurrentDictionary<string, PendingTcsContext> PendingTcss { get; } = new ConcurrentDictionary<string, PendingTcsContext>();

        public ConcurrentDictionary<string, Action<object>> CommandMap { get; } = new ConcurrentDictionary<string, Action<object>>();

        private int _started = 0;

        public event Action<Exception> OnError;
        public event Action<Exception> OnFatalError;

        public WebSocketState State => _ws?.State ?? WebSocketState.None;

        public CmdClient(ClientWebSocket ws)
        {
            _ws = ws ?? throw new ArgumentNullException(nameof(ws));
        }

        public static CmdClient Create(ClientWebSocket ws)
        {
            return new CmdClient(ws);
        }

        public async Task ConnectAsync(CmdClientOptions options, CancellationToken cancellationToken = default)
        {
            await _connectLock.WaitAsync(cancellationToken);
            try
            {
                if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
                    return;

                if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.Connecting)
                    return;

                if (_ws.State != WebSocketState.None)
                {
                    throw new Exception("WebSocket状态异常");
                }

                if (options.MessageParser is null)
                    throw new Exception("缺少MessageParser");
                _messageParser = options.MessageParser;

                var uri = new Uri(options.Url);
                if (options.AddOrigin)
                {
                    _ws.Options.SetRequestHeader("Origin", options.Origin);
                }

                if (options.ValidateCertificate)
                {
                    try
                    {
                        if (!ServicePointManager.SecurityProtocol.HasFlag(SecurityProtocolType.Tls12))
                        {
                            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                        }
                    }
                    catch { }
                }

                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token))
                {
                    if (options.EnableConnectTimeout)
                        connectCts.CancelAfter(TimeSpan.FromSeconds(options.ConnectTimeoutSeconds));

                    try
                    {
                        await _ws.ConnectAsync(new Uri(options.Url), connectCts.Token);
                    }
                    catch (Exception ex)
                    {
                        OnFatalError?.Invoke(ex);
                        throw;
                    }

                }

                _ = Task.Run(ReceiveLoopAsync, _cts.Token);
                _ = Task.Run(ConsumeLoopAsync, _cts.Token);
            }
            finally
            {
                _connectLock.Release();
            }
        }

        public async Task SendAsync(byte[] data, CancellationToken externalToken = default)
        {
            if (_ws.State != WebSocketState.Open)
                throw new Exception("连接已关闭");

            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, externalToken))
            {
                await _sendLock.WaitAsync(linkedCts.Token);
                try
                {
                    await _ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    OnFatalError?.Invoke(ex);
                    throw;
                }
                finally 
                { 
                    _sendLock.Release();
                }
            }
        }

        private async Task ReceiveLoopAsync()
        {
            if (Interlocked.CompareExchange(ref _isReceiving, 1, 0) != 0)
                return;
            var buffer = new byte[1024 * 8];
            var ms = new MemoryStream();

            try
            {
                while (!_cts.Token.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        OnFatalError?.Invoke(ex);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Close) 
                        break;

                    try
                    {
                        if (result.EndOfMessage && ms.Length == 0)
                        {
                            byte[] data = new byte[result.Count];
                            Buffer.BlockCopy(buffer, 0, data, 0, result.Count);
                            _messageChannel.Writer.TryWrite(data);
                        }
                        else
                        {
                            ms.Write(buffer, 0, result.Count);

                            if (result.EndOfMessage)
                            {
                                _messageChannel.Writer.TryWrite(ms.ToArray());
                                if (ms.Capacity <= 1024 * 64)
                                {
                                    ms.SetLength(0);
                                    ms.Position = 0;
                                }
                                else
                                    ms = new MemoryStream();
                            }
                        }
                    }
                    catch(Exception ex)
                    {
                        OnError?.Invoke(ex);
                        ms.SetLength(0);
                        ms.Position = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!(ex is OperationCanceledException))
                {
                    OnFatalError?.Invoke(ex);
                }
            }
            finally
            {
                ms.Dispose();
                Interlocked.Exchange(ref _isReceiving, 0);
            }
        }

        private async Task ConsumeLoopAsync()
        {
            var reader = _messageChannel.Reader;
            try
            {
                while (await reader.WaitToReadAsync(_cts.Token))
                {
                    while (reader.TryRead(out var rawBytes))
                    {
                        try
                        {
                            var msg = _messageParser(rawBytes);
                            if (string.IsNullOrEmpty(msg.Name))
                                continue;

                            if (PendingTcss.TryRemove(msg.Name, out var item))
                            {
                                item.Tcs.TrySetResult(msg.Data);
                            }
                            else if (CommandMap.TryGetValue(msg.Name, out var cmdAction))
                            {
                                _ = Task.Run(() =>
                                {
                                    try 
                                    { 
                                        cmdAction(msg.Data);
                                    }
                                    catch (Exception ex)
                                    {
                                        OnError?.Invoke(new Exception($"{msg.Name}执行异常:", ex));
                                    }
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            OnError?.Invoke(new Exception("消息解析或处理失败", ex));
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OnFatalError?.Invoke(new Exception("消费循环意外终止", ex));
            }
        }

        public void Dispose()
        {
            if (_isDisposed) 
                return;

            _cts.Cancel();
            foreach (var item in PendingTcss)
            {
                item.Value.Tcs.TrySetException(new ObjectDisposedException(nameof(CmdClient), "连接已关闭"));
            }
            PendingTcss.Clear();
            _messageChannel.Writer.TryComplete();
            _ws?.Abort();
            _ws?.Dispose();
            _cts.Dispose();
            _sendLock.Dispose();
            _connectLock.Dispose();
            _isDisposed = true;
        }
    }
}
