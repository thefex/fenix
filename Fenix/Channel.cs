using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Fenix.ClientOperations;
using Fenix.Common;
using Fenix.Internal;
using Fenix.Logging;
using Fenix.Responses;
using Fenix.Utils.Threading;
using Newtonsoft.Json.Linq;

namespace Fenix
{
    /// <summary>
    /// Represents a subscription to single phoenix channel.
    /// </summary>
    public class Channel : IDisposable, IChannel
    {
        private readonly Settings _settings;
        private readonly SocketLogicHandler _handler;
        private readonly object _payload;

        private readonly ConcurrentQueue<(PushOperation, int?, TimeSpan?)> _pushBuffer =
                new ConcurrentQueue<(PushOperation, int?, TimeSpan?)>();

        private readonly ConcurrentDictionary<string, Action<IChannel, JObject>> _bindings =
                new ConcurrentDictionary<string, Action<IChannel, JObject>>();


        private ChannelState _state;

        private int _joinedOnce;

        private int _sending;

        public long JoinRef { get; }

        public long PushRef { get; }

        public bool Joined
        {
            get => _joinedOnce == 1;
            set
            {
                Interlocked.Exchange(ref _joinedOnce, 1);
                _state = ChannelState.Joined;
            }
        }

        /// <summary>
        /// Phoenix channel name to which this subscription is joined.
        /// </summary>
        public string Topic { get; private set; }


        internal Channel(
            Settings settings,
            SocketLogicHandler handler,
            string topic,
            object payload
        )
        {
            Ensure.NotNull(settings, nameof(settings));
            Ensure.NotNull(handler, nameof(handler));
            Ensure.NotNull(topic, nameof(topic));

            _settings = settings;
            _handler = handler;
            _payload = payload;
            JoinRef = _handler.MakeRef();
            Topic = topic;
            _state = ChannelState.Closed;
        }


        public void Dispose()
        {
            Leave();
        }

        public IChannel Subscribe(string eventName, Action<IChannel, JObject> callback)
        {
            _bindings.AddOrUpdate(eventName, callback, (_, action) => callback);
            return this;
        }

        public IChannel Unsubscribe(string eventName)
        {
            _bindings.TryRemove(eventName, out _);
            return this;
        }

        public async Task<JoinResult> JoinAsync()
        {
            var source = TaskCompletionSourceFactory.Create<JoinResult>();
            var operation = new JoinChannelOperation(
                _settings.Logger,
                source,
                this,
                JoinRef,
                _payload
            );
            await EnqueueOperation(operation).ConfigureAwait(false);
            return await source.Task.ConfigureAwait(false);
        }

        public void Leave()
        {
            throw new NotImplementedException();
        }

        public async Task<SendResult> SendAsync(string eventType, object payload, int? maxRetries = null,
            TimeSpan? timeout = null)
        {
            var source = TaskCompletionSourceFactory.Create<SendResult>();
            _pushBuffer.Enqueue(
                (
                    new PushOperation(
                        _settings.Logger,
                        source,
                        JoinRef,
                        Topic,
                        eventType, payload
                    ),
                    maxRetries.GetValueOrDefault(_settings.MaxRetries),
                    timeout.GetValueOrDefault(_settings.OperationTimeout)
                )
            );
            await TryProcessBuffer();
            return await source.Task.ConfigureAwait(false);
        }

        public void Receive(Push push)
        {
            var channel = this;
            if (_bindings.TryGetValue(push.ChannelEvent, out var callback))
            {
                ThreadPool.QueueUserWorkItem(_ => { callback.Invoke(channel, push.Payload); });
            }
        }


        private async Task TryProcessBuffer()
        {
            if (Interlocked.CompareExchange(ref _sending, 1, 0) == 0)
            {
                do
                {
                    while (_pushBuffer.TryDequeue(out var valueTuple) && _state == ChannelState.Joined)
                    {
                        var (operation, retries, timeout) = valueTuple;
                        operation.SetJoinRef(JoinRef);
                        await EnqueueOperation(operation, retries, timeout);
                    }

                    Interlocked.Exchange(ref _sending, 0);
                } while (_pushBuffer.Count > 0 && Interlocked.CompareExchange(ref _sending, 1, 0) == 0 &&
                         _state == ChannelState.Joined);
            }
        }
        
        private async Task EnqueueOperation(IClientOperation operation, int? retries = null, TimeSpan? timeout = null)
        {
            while (_handler.TotalOperationCount >= _settings.MaxQueueSize)
            {
                await Task.Delay(1).ConfigureAwait(false);
            }

            _handler.EnqueueMessage(
                new StartOperationMessage(
                    operation,
                    retries.GetValueOrDefault(_settings.MaxRetries),
                    timeout.GetValueOrDefault(_settings.OperationTimeout)
                )
            );
        }
    }
}