﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Threading;
using KafkaNet.Common;
using KafkaNet.Model;
using KafkaNet.Protocol;
using KafkaNet.Statistics;

namespace KafkaNet
{
    /// <summary>
    /// The TcpSocket provides an abstraction from the main driver from having to handle connection to and reconnections with a server.
    /// The interface is intentionally limited to only read/write.  All connection and reconnect details are handled internally.
    /// </summary>
    public class KafkaTcpSocket : IKafkaTcpSocket
    {
        public event Action OnServerDisconnected;
        public event Action<int> OnReconnectionAttempt;
        public event Action<int> OnReadFromSocketAttempt;
        public event Action<int> OnBytesReceived;
        public event Action<KafkaDataPayload> OnWriteToSocketAttempt;

        private const int DefaultReconnectionTimeout = 100;
        private const int DefaultReconnectionTimeoutMultiplier = 2;
        private const int MaxReconnectionTimeoutMinutes = 5;

        private readonly CancellationTokenSource _disposeToken = new CancellationTokenSource();
        private readonly CancellationTokenRegistration _disposeRegistration;
        private readonly IKafkaLog _log;
        private readonly KafkaEndpoint _endpoint;
        private readonly TimeSpan _maximumReconnectionTimeout;

        private readonly AsyncCollection<SocketPayloadSendTask> _sendTaskQueue;
        private readonly AsyncCollection<SocketPayloadReadTask> _readTaskQueue;

        private readonly Task _socketTask;
        private readonly AsyncLock _clientLock = new AsyncLock();
        private TcpClient _client;
        private int _disposeCount;
        private readonly Action _processNetworkstreamTasksAction;
        private X509Certificate2 _clientCert;
        private bool _selfSignedTrainMode;
        private bool? _allowSelfSignedServerCert;
        private SslStream _sslStream;
        private NetworkStream _netStream;

        /// <summary>
        /// Construct socket and open connection to a specified server.
        /// </summary>
        /// <param name="log">Logging facility for verbose messaging of actions.</param>
        /// <param name="endpoint">The IP endpoint to connect to.</param>
        /// <param name="maximumReconnectionTimeout">The maximum time to wait when backing off on reconnection attempts.</param>
        public KafkaTcpSocket(IKafkaLog log, KafkaEndpoint endpoint, TimeSpan? maximumReconnectionTimeout = null) : this(log, endpoint, maximumReconnectionTimeout ?? TimeSpan.FromMinutes(MaxReconnectionTimeoutMinutes), null)
        {
        }

        private KafkaTcpSocket(IKafkaLog log, KafkaEndpoint endpoint, TimeSpan? maximumReconnectionTimeout, KafkaOptions kafkaOptions)
        {

            _log = log;
            _endpoint = endpoint;
            _maximumReconnectionTimeout = maximumReconnectionTimeout ?? TimeSpan.FromMinutes(MaxReconnectionTimeoutMinutes);
            _processNetworkstreamTasksAction = ProcessNetworkstreamTasks;
            _allowSelfSignedServerCert = kafkaOptions?.TslAllowSelfSignedServerCert;

            if (!string.IsNullOrWhiteSpace(kafkaOptions?.TslClientCertPfxPathOrCertStoreSubject))
            {         
                _selfSignedTrainMode = kafkaOptions.TslSelfSignedTrainMode;
                _clientCert = GetClientCert(kafkaOptions.TslClientCertPfxPathOrCertStoreSubject, kafkaOptions.TslClientCertStoreFriendlyName, kafkaOptions?.TslClientCertPassword);
                _processNetworkstreamTasksAction = ProcessNetworkstreamTasksTsl;
            }

            _sendTaskQueue = new AsyncCollection<SocketPayloadSendTask>();
            _readTaskQueue = new AsyncCollection<SocketPayloadReadTask>();

            //dedicate a long running task to the read/write operations
            _socketTask = Task.Factory.StartNew(DedicatedSocketTask, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);

            _disposeRegistration = _disposeToken.Token.Register(() =>
            {
                _sendTaskQueue.CompleteAdding();
                _readTaskQueue.CompleteAdding();
            });
        }

        private X509Certificate2 GetClientCert(string clientCertSubject, string clientCertFriendlyName, string tslClientCertPassword)
        {
            return clientCertSubject.EndsWith(".pfx") ?  new X509Certificate2(clientCertSubject, tslClientCertPassword) : GetClientCertFromCertStore(clientCertSubject, clientCertFriendlyName);
        }

        private static X509Certificate2 GetClientCertFromCertStore(string clientCertSubject, string clientCertFriendlyName)
        {
            var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            X509Certificate2 cert = null;
            var certs =
                store.Certificates.Find(X509FindType.FindBySubjectName, clientCertSubject, false)
                    .OfType<X509Certificate2>().ToList();
            store.Close();
            if (certs.Count > 0)
            {
                cert = certs.FirstOrDefault(x => x.FriendlyName == clientCertFriendlyName) ?? certs.First();
            }

            if (cert == null)
            {
                throw new Exception(
                    $"Unable to find client certificate with subject {clientCertSubject} or friendly name {clientCertFriendlyName}");
            }
            return cert;
        }

        public KafkaTcpSocket(KafkaEndpoint endpoint, KafkaOptions kafkaOptions) : this(kafkaOptions.Log, endpoint, kafkaOptions.MaximumReconnectionTimeout, kafkaOptions)
        {

        }

        #region Interface Implementation...
        /// <summary>
        /// The IP Endpoint to the server.
        /// </summary>
        public KafkaEndpoint Endpoint { get { return _endpoint; } }

        /// <summary>
        /// Read a certain byte array size return only when all bytes received.
        /// </summary>
        /// <param name="readSize">The size in bytes to receive from server.</param>
        /// <returns>Returns a byte[] array with the size of readSize.</returns>
        public Task<byte[]> ReadAsync(int readSize)
        {
            return EnqueueReadTask(readSize, CancellationToken.None);
        }

        /// <summary>
        /// Read a certain byte array size return only when all bytes received.
        /// </summary>
        /// <param name="readSize">The size in bytes to receive from server.</param>
        /// <param name="cancellationToken">A cancellation token which will cancel the request.</param>
        /// <returns>Returns a byte[] array with the size of readSize.</returns>
        public Task<byte[]> ReadAsync(int readSize, CancellationToken cancellationToken)
        {
            return EnqueueReadTask(readSize, cancellationToken);
        }

        /// <summary>
        /// Convenience function to write full buffer data to the server.
        /// </summary>
        /// <param name="payload">The buffer data to send.</param>
        /// <returns>Returns Task handle to the write operation with size of written bytes..</returns>
        public Task<KafkaDataPayload> WriteAsync(KafkaDataPayload payload)
        {
            return WriteAsync(payload, CancellationToken.None);
        }

        /// <summary>
        /// Write the buffer data to the server.
        /// </summary>
        /// <param name="payload">The buffer data to send.</param>
        /// <param name="cancellationToken">A cancellation token which will cancel the request.</param>
        /// <returns>Returns Task handle to the write operation with size of written bytes..</returns>
        public Task<KafkaDataPayload> WriteAsync(KafkaDataPayload payload, CancellationToken cancellationToken)
        {
            return EnqueueWriteTask(payload, cancellationToken);
        }
        #endregion

        private Task<KafkaDataPayload> EnqueueWriteTask(KafkaDataPayload payload, CancellationToken cancellationToken)
        {
            var sendTask = new SocketPayloadSendTask(payload, cancellationToken, _log);
            _sendTaskQueue.Add(sendTask);
            //StatisticsTracker.QueueNetworkWrite(_endpoint, payload);
            return sendTask.Tcp.Task;
        }

        private Task<byte[]> EnqueueReadTask(int readSize, CancellationToken cancellationToken)
        {
            var readTask = new SocketPayloadReadTask(readSize, cancellationToken, _log);
            _readTaskQueue.Add(readTask);
            return readTask.Tcp.Task;
        }

        private void DedicatedSocketTask()
        {
            while (_disposeToken.IsCancellationRequested == false)
            {
                try
                {
                    _processNetworkstreamTasksAction();
                }
                catch (Exception ex)
                {
                    if (_disposeToken.IsCancellationRequested)
                    {
                        _log.WarnFormat("KafkaTcpSocket thread shutting down because of a dispose call.");
                        var disposeException = new ObjectDisposedException("Object is disposing.");
                        _sendTaskQueue.DrainAndApply(t => t.Tcp.TrySetException(disposeException));
                        _readTaskQueue.DrainAndApply(t => t.Tcp.TrySetException(disposeException));
                        return;
                    }

                    if (ex is ServerDisconnectedException)
                    {
                        if (OnServerDisconnected != null) OnServerDisconnected();
                        _log.ErrorFormat(ex.Message);
                        continue;
                    }

                    _log.ErrorFormat("Exception occured in Socket handler task.  Exception: {0}", ex);
                }
            }
        }

        private void ProcessNetworkstreamTasks()
        {
            CreateNetStream();
            ProcessNetworkstreamTasks(_netStream);
        }

        private void ProcessNetworkstreamTasksTsl()
        {
            CreateNetStream();
            CreateSslStream();     
            ProcessNetworkstreamTasks(_sslStream);
        }


        private void CreateSslStream()
        {
            AssignNewSslStream();            
            _sslStream.AuthenticateAsClient(_endpoint.ServeUri.Host, new X509Certificate2Collection(_clientCert), System.Security.Authentication.SslProtocols.Tls12, false);  
        }

        private void CreateNetStream()
        {          
            if (_netStream != null)
            {
                _log.WarnFormat("Non-null net stream found. Disposing before reassignment.");
                _netStream.DisposeSafely(_log);
            }

            _netStream = GetStreamAsync().Result;
        }


        private void AssignNewSslStream()
        {
            if (_sslStream != null)
            {
                _log.WarnFormat("Non-null ssl stream found. Disposing before reassignment");
                _sslStream.DisposeSafely(_log);
            }
            _sslStream = new SslStream(_netStream, true, VerifyServerCertificate, null);
        }

        private bool VerifyServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslpolicyerrors)
        {
            if (sslpolicyerrors == SslPolicyErrors.None)
            {
                return true;
            }

            if (_allowSelfSignedServerCert.HasValue && _allowSelfSignedServerCert.Value)
            {
                var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);

                store.Open(OpenFlags.ReadOnly);

                var serverCert =
                    store.Certificates.Find(X509FindType.FindByThumbprint, ((X509Certificate2)certificate).Thumbprint, false)
                        .OfType<X509Certificate2>()
                        .FirstOrDefault();
                store.Close();
                var match = certificate.Equals(serverCert);
                if (!match && _selfSignedTrainMode)
                {
                    store.Open(OpenFlags.ReadWrite);
                    store.Add((X509Certificate2)certificate);
                    store.Close();
                    match = true;
                }

                return match;
            }

            return false;
        }

        private void ProcessNetworkstreamTasks(Stream netStream)
        {
            Task writeTask = Task.FromResult(true);
            Task readTask = Task.FromResult(true);

            //reading/writing from network steam is not thread safe
            //Read and write operations can be performed simultaneously on an instance of the NetworkStream class without the need for synchronization. 
            //As long as there is one unique thread for the write operations and one unique thread for the read operations, there will be no cross-interference 
            //between read and write threads and no synchronization is required. 
            //https://msdn.microsoft.com/en-us/library/z2xae4f4.aspx
            while (_disposeToken.IsCancellationRequested == false && netStream != null)
            {
                Task sendDataReady = Task.WhenAll(writeTask, _sendTaskQueue.OnHasDataAvailable(_disposeToken.Token));
                Task readDataReady = Task.WhenAll(readTask, _readTaskQueue.OnHasDataAvailable(_disposeToken.Token));

                Task.WaitAny(sendDataReady, readDataReady);

                var exception = new[] { writeTask, readTask }
                    .Where(x => x.IsFaulted && x.Exception != null)
                    .SelectMany(x => x.Exception.InnerExceptions)
                    .FirstOrDefault();

                if (exception != null) throw exception;

                if (sendDataReady.IsCompleted) writeTask = ProcessSentTasksAsync(netStream, _sendTaskQueue.Pop());
                if (readDataReady.IsCompleted) readTask = ProcessReadTaskAsync(netStream, _readTaskQueue.Pop());
            }
        }

        private async Task ProcessReadTaskAsync(Stream netStream, SocketPayloadReadTask readTask)
        {
            using (readTask)
            {
                if (readTask != null)
                {
                    try
                    {
                        //StatisticsTracker.IncrementGauge(StatisticGauge.ActiveReadOperation);
                        var readSize = readTask.ReadSize;
                        var result = new List<byte>(readSize);
                        var bytesReceived = 0;

                        while (bytesReceived < readSize)
                        {
                            readSize = readSize - bytesReceived;
                            var buffer = new byte[readSize];

                            if (OnReadFromSocketAttempt != null) OnReadFromSocketAttempt(readSize);

                            bytesReceived = await netStream.ReadAsync(buffer, 0, readSize, readTask.CancellationToken)
                                .WithCancellation(readTask.CancellationToken).ConfigureAwait(false);

                            if (OnBytesReceived != null) OnBytesReceived(bytesReceived);

                            if (bytesReceived <= 0)
                            {
                                _client.DisposeSafely(_log);                                                              
                                _client = null;
                                throw new ServerDisconnectedException(string.Format("Lost connection to server: {0}, Bytes Received {1}", _endpoint, bytesReceived));
                                
                            }

                            result.AddRange(buffer.Take(bytesReceived));
                        }

                        readTask.Tcp.TrySetResult(result.ToArray());
                    }
                    catch (Exception ex)
                    {
                        if (_disposeToken.IsCancellationRequested)
                        {
                            var exception = new ObjectDisposedException("Object is disposing.");
                            readTask?.Tcp.TrySetException(exception);
                            throw exception;
                        }

                        if (ex is ServerDisconnectedException)
                        {
                            readTask.Tcp.TrySetException(ex);
                            throw;
                        }

                        //if an exception made us lose a connection throw disconnected exception
                        if (_client == null || _client.Connected == false)
                        {
                            var exception = new ServerDisconnectedException(string.Format("Lost connection to server: {0}", _endpoint), ex);
                            readTask.Tcp.TrySetException(exception);
                            throw exception;
                        }

                        readTask.Tcp.TrySetException(ex);
                        throw;
                    }
                    finally
                    {
                        //StatisticsTracker.DecrementGauge(StatisticGauge.ActiveReadOperation);
                    } 
                }
            }
        }

        private async Task ProcessSentTasksAsync(Stream netStream, SocketPayloadSendTask sendTask)
        {
            if (sendTask == null) return;

            using (sendTask)
            {
                var failed = false;
                var sw = Stopwatch.StartNew();
                try
                {
                    sw.Restart();
                    //StatisticsTracker.IncrementGauge(StatisticGauge.ActiveWriteOperation);

                    if (OnWriteToSocketAttempt != null) OnWriteToSocketAttempt(sendTask.Payload);
                    await netStream.WriteAsync(sendTask.Payload.Buffer, 0, sendTask.Payload.Buffer.Length).ConfigureAwait(false);

                    sendTask.Tcp.TrySetResult(sendTask.Payload);
                }
                catch (Exception ex)
                {
                    failed = true;
                    if (_disposeToken.IsCancellationRequested)
                    {
                        var exception = new ObjectDisposedException("Object is disposing.");
                        sendTask.Tcp.TrySetException(exception);
                        throw exception;
                    }

                    sendTask.Tcp.TrySetException(ex);
                    throw;
                }
                finally
                {
                    //StatisticsTracker.DecrementGauge(StatisticGauge.ActiveWriteOperation);
                    //StatisticsTracker.CompleteNetworkWrite(sendTask.Payload, sw.ElapsedMilliseconds, failed);
                }
            }
        }

        private async Task<NetworkStream> GetStreamAsync()
        {
            //using a semaphore here to allow async waiting rather than blocking locks
            using (await _clientLock.LockAsync(_disposeToken.Token).ConfigureAwait(false))
            {
                if ((_client == null || _client.Connected == false) && !_disposeToken.IsCancellationRequested)
                {
                    _client = await ReEstablishConnectionAsync().ConfigureAwait(false);
                }

                return _client == null ? null : _client.GetStream();
            }
        }

        /// <summary>
        /// (Re-)establish the Kafka server connection.
        /// Assumes that the caller has already obtained the <c>_clientLock</c>
        /// </summary>
        private async Task<TcpClient> ReEstablishConnectionAsync()
        {
            var attempts = 1;
            var reconnectionDelay = DefaultReconnectionTimeout;
            _log.WarnFormat("No connection to:{0}.  Attempting to connect...", _endpoint);

            DisposeClientIfNotNull();

            while (_disposeToken.IsCancellationRequested == false)
            {
                try
                {
                    if (OnReconnectionAttempt != null) OnReconnectionAttempt(attempts++);
                    AssignNewClient();
                    await _client.ConnectAsync(_endpoint.Endpoint.Address, _endpoint.Endpoint.Port).ConfigureAwait(false);
                    _log.WarnFormat("Connection established to:{0}.", _endpoint);
                    return _client;
                }
                catch
                {
                    reconnectionDelay = reconnectionDelay * DefaultReconnectionTimeoutMultiplier;
                    reconnectionDelay = Math.Min(reconnectionDelay, (int)_maximumReconnectionTimeout.TotalMilliseconds);

                    _log.WarnFormat("Failed connection to:{0}.  Will retry in:{1}", _endpoint, reconnectionDelay);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(reconnectionDelay), _disposeToken.Token).ConfigureAwait(false);
            }

            return _client;
        }

        private void AssignNewClient()
        {
            DisposeClientIfNotNull();

            _client = new TcpClient();
        }

        private void DisposeClientIfNotNull()
        {
            if (_client != null)
            {
                _log.WarnFormat("Non-null TCP client found. Disposing before reassignment");
                _client.DisposeSafely(_log);                              
                _client = null;
                
            }
        }

        public void Dispose()
        {
            if (Interlocked.Increment(ref _disposeCount) != 1) return;
            _disposeToken?.Cancel();
            _socketTask.SafeWait(TimeSpan.FromSeconds(30));

            _socketTask.DisposeSafely(_log);
            _sslStream.DisposeSafely(_log);
            _netStream.DisposeSafely(_log);
            _client.DisposeSafely(_log);
            _disposeRegistration.DisposeSafely(_log);
            _disposeToken.DisposeSafely(_log);
        }
    }

    static class ObjectExtensions
    {
        public static void DisposeSafely(this object objToDispose, IKafkaLog log)
        {
            var disposable = objToDispose as IDisposable;

            if (disposable != null)
            {
                var className = disposable.GetType().Name;
                try
                {
                    disposable.Dispose();
                    log?.DebugFormat("Successfully disposed {0}", className);
                }
                catch (Exception e)
                {
                    log?.WarnFormat("Error disposing {0}, Exception {1}", className, e);
                }
            }
        }
    }

    class SocketPayloadReadTask : IDisposable
    {
        private readonly IKafkaLog _log;
        public CancellationToken CancellationToken { get; private set; }
        public TaskCompletionSource<byte[]> Tcp { get; set; }
        public int ReadSize { get; set; }

        private readonly CancellationTokenRegistration _cancellationTokenRegistration;

        public SocketPayloadReadTask(int readSize, CancellationToken cancellationToken, IKafkaLog log)
        {
            _log = log;
            CancellationToken = cancellationToken;
            Tcp = new TaskCompletionSource<byte[]>();
            ReadSize = readSize;
            _cancellationTokenRegistration = cancellationToken.Register(() => Tcp.TrySetCanceled());
        }

        public void Dispose()
        {
            _cancellationTokenRegistration.DisposeSafely(_log);
        }
    }

    class SocketPayloadSendTask : IDisposable
    {
        private readonly IKafkaLog _log;
        public TaskCompletionSource<KafkaDataPayload> Tcp { get; set; }
        public KafkaDataPayload Payload { get; set; }

        private readonly CancellationTokenRegistration _cancellationTokenRegistration;

        public SocketPayloadSendTask(KafkaDataPayload payload, CancellationToken cancellationToken, IKafkaLog log)
        {
            _log = log;
            Tcp = new TaskCompletionSource<KafkaDataPayload>();
            Payload = payload;
            _cancellationTokenRegistration = cancellationToken.Register(() => Tcp.TrySetCanceled());
        }

        public void Dispose()
        {
            _cancellationTokenRegistration.DisposeSafely(_log);
        }
    }

    public class KafkaDataPayload
    {
        public int CorrelationId { get; set; }
        public ApiKeyRequestType ApiKey { get; set; }
        public int MessageCount { get; set; }
        public bool TrackPayload
        {
            get { return MessageCount > 0; }
        }
        public byte[] Buffer { get; set; }
    }
}
