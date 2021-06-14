using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Mining;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NLog;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Stratum
{
    public class StratumClient
    {
        private static readonly Encoding UTF8Encoding = new UTF8Encoding(false);

        public StratumClient(ILogger logger, IMasterClock clock, string connectionId)
        {
            this.logger = logger;

            receivePipe = new Pipe(PipeOptions.Default);

            sendQueue = new BufferBlock<object>(new DataflowBlockOptions
            {
                BoundedCapacity = SendQueueCapacity,
                EnsureOrdered = true,
            });

            this.clock = clock;
            ConnectionId = connectionId;
            IsAlive = true;
        }

        public StratumClient()
        {
            // For unit testing only
        }

        private readonly ILogger logger;
        private readonly IMasterClock clock;

        private const int MaxInboundRequestLength = 0x8000;
        private const int MaxOutboundRequestLength = 0x8000;

        private Stream networkStream;
        private readonly Pipe receivePipe;
        private readonly BufferBlock<object> sendQueue;
        private WorkerContextBase context;
        private readonly Subject<Unit> terminated = new();
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private bool expectingProxyHeader;

        private static readonly JsonSerializer serializer = new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        private const int SendQueueCapacity = 32;
        private static readonly TimeSpan sendTimeout = TimeSpan.FromMilliseconds(10000);

        #region API-Surface

        public void Run(Socket socket,
            (IPEndPoint IPEndPoint, PoolEndpoint PoolEndpoint) endpoint,
            X509Certificate2 cert,
            Func<StratumClient, JsonRpcRequest, CancellationToken, Task> onRequestAsync,
            Action<StratumClient> onCompleted,
            Action<StratumClient, Exception> onError)
        {
            PoolEndpoint = endpoint.IPEndPoint;
            RemoteEndpoint = (IPEndPoint) socket.RemoteEndPoint;

            expectingProxyHeader = endpoint.PoolEndpoint.TcpProxyProtocol?.Enable == true;

            Task.Run(async () =>
            {
                try
                {
                    // prepare socket
                    socket.NoDelay = true;
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                    // create stream
                    networkStream = new NetworkStream(socket, true);
                    logger.Info(() => $"[{ConnectionId}] Accepting connection from {RemoteEndpoint.Address}:{RemoteEndpoint.Port} ...");

                    using(var disposables = new CompositeDisposable(networkStream, cts))
                    {
                        if(endpoint.PoolEndpoint.Tls)
                        {
                            var sslStream = new SslStream(networkStream, false);
                            disposables.Add(sslStream);

                            // TLS handshake
                            await sslStream.AuthenticateAsServerAsync(cert, false, SslProtocols.Tls11 | SslProtocols.Tls12, false);
                            networkStream = sslStream;

                            logger.Info(() => $"[{ConnectionId}] {sslStream.SslProtocol.ToString().ToUpper()}-{sslStream.CipherAlgorithm.ToString().ToUpper()} Connection from {RemoteEndpoint.Address}:{RemoteEndpoint.Port} accepted on port {endpoint.IPEndPoint.Port}");
                        }
                        else
                        {
                            logger.Info(() => $"[{ConnectionId}] Connection from {RemoteEndpoint.Address}:{RemoteEndpoint.Port} accepted on port {endpoint.IPEndPoint.Port}");
                        }

                        // Async I/O loop(s)
                        var tasks = new[]
                        {
                            FillReceivePipeAsync(),
                            ProcessReceivePipeAsync(endpoint.PoolEndpoint.TcpProxyProtocol, onRequestAsync),
                            ProcessSendQueueAsync()
                        };

                        await Task.WhenAny(tasks);

                        // We are done with this client, make sure all tasks complete
                        await receivePipe.Reader.CompleteAsync();
                        await receivePipe.Writer.CompleteAsync();
                        sendQueue.Complete();

                        // additional safety net to ensure remaining tasks don't linger
                        cts.Cancel();

                        // Signal completion or error
                        var error = tasks.FirstOrDefault(t => t.IsFaulted)?.Exception;

                        if(error == null)
                            onCompleted(this);
                        else
                            onError(this, error);
                    }
                }

                catch(Exception ex)
                {
                    onError(this, ex);
                }

                finally
                {
                    // Release external observables
                    IsAlive = false;
                    terminated.OnNext(Unit.Default);

                    logger.Info(() => $"[{ConnectionId}] Stratum Client Connection Closed");
                }
            });
        }

        public string ConnectionId { get; }
        public IPEndPoint PoolEndpoint { get; private set; }
        public IPEndPoint RemoteEndpoint { get; private set; }
        public DateTime? LastReceive { get; set; }
        public bool IsAlive { get; set; }
        public IObservable<Unit> Terminated => terminated.AsObservable();

        public void SetContext<T>(T value) where T : WorkerContextBase
        {
            context = value;
        }

        public T ContextAs<T>() where T : WorkerContextBase
        {
            return (T) context;
        }

        public ValueTask RespondAsync<T>(T payload, object id)
        {
            return RespondAsync(new JsonRpcResponse<T>(payload, id));
        }

        public ValueTask RespondErrorAsync(StratumError code, string message, object id, object result = null, object data = null)
        {
            return RespondAsync(new JsonRpcResponse(new JsonRpcException((int) code, message, null), id, result));
        }

        public ValueTask RespondAsync<T>(JsonRpcResponse<T> response)
        {
            return SendAsync(response);
        }

        public ValueTask NotifyAsync<T>(string method, T payload)
        {
            return NotifyAsync(new JsonRpcRequest<T>(method, payload, null));
        }

        public ValueTask NotifyAsync<T>(JsonRpcRequest<T> request)
        {
            return SendAsync(request);
        }

        public void Disconnect()
        {
            networkStream.Close();
        }

        #endregion // API-Surface

        private async ValueTask SendAsync<T>(T payload)
        {
            Contract.RequiresNonNull(payload, nameof(payload));

            using(var ctsTimeout = new CancellationTokenSource())
            {
                using(var ctsComposite = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, ctsTimeout.Token))
                {
                    ctsTimeout.CancelAfter(sendTimeout);

                    if(!await sendQueue.SendAsync(payload, ctsComposite.Token))
                    {
                        // this will force a disconnect down the line 
                        throw new IOException($"Send queue stalled at {sendQueue.Count} of {SendQueueCapacity} items");
                    }
                }
            }
        }

        private async Task FillReceivePipeAsync()
        {
            while(true)
            {
                logger.Debug(() => $"[{ConnectionId}] [1] [FILL RECEIVE PIPE] Waiting for data...");

                var memory = receivePipe.Writer.GetMemory(MaxInboundRequestLength + 1);

                // read from network directly into pipe memory
                var cb = await networkStream.ReadAsync(memory, cts.Token);

                logger.Trace(() => $"[{ConnectionId}] [1] [FILL RECEIVE PIPE] Received data: {UTF8Encoding.GetString(memory.ToArray(), 0, cb)}");

                if(cb == 0)
                    break; // EOF

                LastReceive = clock.UtcNow;

                // hand off to pipe
                receivePipe.Writer.Advance(cb);

                var result = await receivePipe.Writer.FlushAsync(cts.Token);
                if(result.IsCompleted)
                    break;
            }
        }

        private async Task ProcessReceivePipeAsync(TcpProxyProtocolConfig proxyProtocol, Func<StratumClient, JsonRpcRequest, CancellationToken, Task> onRequestAsync)
        {
            while(true)
            {
                logger.Debug(() => $"[{ConnectionId}] [2] [PROCESS RECEIVE PIPE] Waiting for data...");

                var result = await receivePipe.Reader.ReadAsync(cts.Token);

                var buffer = result.Buffer;
                SequencePosition? position;

                logger.Trace(() => $"[{ConnectionId}] [2] [PROCESS RECEIVE PIPE] Received data: {result.Buffer.AsString(UTF8Encoding)}");

                if(buffer.Length > MaxInboundRequestLength)
                    throw new InvalidDataException($"Incoming data exceeds maximum of {MaxInboundRequestLength}");


                do
                {
                    // Scan buffer for line terminator
                    position = buffer.PositionOf((byte) '\n');

                    if(position != null)
                    {
                        var slice = buffer.Slice(0, position.Value);

                        if(!expectingProxyHeader || !ProcessProxyHeader(slice, proxyProtocol))
                            await ProcessRequestAsync(onRequestAsync, slice);

                        // Skip consumed section
                        buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
                    }
                } while(position != null);

                receivePipe.Reader.AdvanceTo(buffer.Start, buffer.End);

                if(result.IsCompleted)
                    break;
            }
        }

        private async Task ProcessSendQueueAsync()
        {
            while(true)
            {
                var msg = await sendQueue.ReceiveAsync(cts.Token);

                await SendMessage(msg);
            }
        }

        private async Task SendMessage(object msg)
        {
            logger.Trace(() => $"[{ConnectionId}] [3] [SendMessage] {JsonConvert.SerializeObject(msg)}");

            var buffer = ArrayPool<byte>.Shared.Rent(MaxOutboundRequestLength);

            try
            {
                await using(var stream = new MemoryStream(buffer, true))
                {
                    // serialize
                    await using(var writer = new StreamWriter(stream, UTF8Encoding, MaxOutboundRequestLength, true))
                    {
                        serializer.Serialize(writer, msg);
                    }

                    stream.WriteByte((byte) '\n'); // terminator

                    // send
                    using(var ctsTimeout = new CancellationTokenSource())
                    {
                        using(var ctsComposite = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, ctsTimeout.Token))
                        {
                            ctsTimeout.CancelAfter(sendTimeout);

                            await networkStream.WriteAsync(buffer, 0, (int) stream.Position, ctsComposite.Token);
                            await networkStream.FlushAsync(ctsComposite.Token);
                        }
                    }
                }
            }

            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task ProcessRequestAsync(Func<StratumClient, JsonRpcRequest, CancellationToken, Task> onRequestAsync, ReadOnlySequence<byte> lineBuffer)
        {
            using(var reader = new JsonTextReader(new StreamReader(new MemoryStream(lineBuffer.ToArray()), UTF8Encoding)))
            {
                var request = serializer.Deserialize<JsonRpcRequest>(reader);

                if(request == null)
                    throw new JsonException("Unable to deserialize request");

                // Dispatch
                await onRequestAsync(this, request, cts.Token);
            }
        }

        /// <summary>
        /// Returns true if the line was consumed
        /// </summary>
        private bool ProcessProxyHeader(ReadOnlySequence<byte> seq, TcpProxyProtocolConfig proxyProtocol)
        {
            expectingProxyHeader = false;

            var line = seq.AsString(UTF8Encoding);
            var peerAddress = RemoteEndpoint.Address;

            if(line.StartsWith("PROXY "))
            {
                var proxyAddresses = proxyProtocol.ProxyAddresses?.Select(IPAddress.Parse).ToArray();
                if(proxyAddresses == null || !proxyAddresses.Any())
                    proxyAddresses = new[] { IPAddress.Loopback, IPUtils.IPv4LoopBackOnIPv6, IPAddress.IPv6Loopback };

                if(proxyAddresses.Any(x => x.Equals(peerAddress)))
                {
                    logger.Debug(() => $"[{ConnectionId}] Received Proxy-Protocol header: {line}");

                    // split header parts
                    var parts = line.Split(" ");
                    var remoteAddress = parts[2];
                    var remotePort = parts[4];

                    // Update client
                    RemoteEndpoint = new IPEndPoint(IPAddress.Parse(remoteAddress), int.Parse(remotePort));
                    logger.Info(() => $"[{ConnectionId}] Real-IP via Proxy-Protocol: {RemoteEndpoint.Address}");
                }

                else
                {
                    throw new InvalidDataException($"[{ConnectionId}] Received spoofed Proxy-Protocol header from {peerAddress}");
                }

                return true;
            }

            else if(proxyProtocol.Mandatory)
            {
                throw new InvalidDataException($"[{ConnectionId}] Missing mandatory Proxy-Protocol header from {peerAddress}. Closing connection.");
            }

            return false;
        }
    }
}
