﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Rebus.Bus;
using Rebus.Config.Outbox;
using Rebus.Logging;
using Rebus.Threading;
using Rebus.Transport;

namespace Rebus.SqlServer.Outbox;

class OutboxForwarder : IDisposable, IInitializable
{
    static readonly Retrier SendRetrier = new(new[]
    {
        TimeSpan.FromSeconds(0.1),
        TimeSpan.FromSeconds(0.1),
        TimeSpan.FromSeconds(0.1),
        TimeSpan.FromSeconds(0.1),
        TimeSpan.FromSeconds(0.1),
        TimeSpan.FromSeconds(0.5),
        TimeSpan.FromSeconds(0.5),
        TimeSpan.FromSeconds(0.5),
        TimeSpan.FromSeconds(0.5),
        TimeSpan.FromSeconds(0.5),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1),
    });

    readonly CancellationTokenSource _cancellationTokenSource = new();
    readonly IOutboxStorage _outboxStorage;
    readonly ITransport _transport;
    readonly IAsyncTask _forwarder;
    readonly IAsyncTask _cleaner;
    readonly ILog _logger;
    readonly OutboxOptions _outboxOptions;

    public OutboxForwarder(IAsyncTaskFactory asyncTaskFactory, IRebusLoggerFactory rebusLoggerFactory, IOutboxStorage outboxStorage, ITransport transport, OutboxOptions outboxOptions) // Interface for OutboxOptions? Is this even the right way to get options here?
    {
        if (asyncTaskFactory == null) throw new ArgumentNullException(nameof(asyncTaskFactory));
        _outboxStorage = outboxStorage;
        _transport = transport;
        _forwarder = asyncTaskFactory.Create("OutboxForwarder", RunForwarder, intervalSeconds: 1);
        _cleaner = asyncTaskFactory.Create("OutboxCleaner", RunCleaner, intervalSeconds: outboxOptions.CleanerIntervalInSeconds);
        _logger = rebusLoggerFactory.GetLogger<OutboxForwarder>();
        _outboxOptions = outboxOptions;
    }

    public void Initialize()
    {
        _forwarder.Start();
        _cleaner.Start();
    }

    async Task RunForwarder()
    {
        _logger.Debug("Checking outbox storage for pending messages");

        var cancellationToken = _cancellationTokenSource.Token;

        while (!cancellationToken.IsCancellationRequested)
        {
            using var batch = await _outboxStorage.GetNextMessageBatch();

            if (!batch.Any())
            {
                _logger.Debug("No pending messages found");
                return;
            }

            await ProcessMessageBatch(batch, cancellationToken);

            await batch.Complete();
        }
    }

    async Task ProcessMessageBatch(IReadOnlyCollection<OutboxMessage> batch, CancellationToken cancellationToken)
    {
        _logger.Debug("Sending {count} pending messages", batch.Count);

        using var scope = new RebusTransactionScope();

        foreach (var message in batch)
        {
            var destinationAddress = message.DestinationAddress;
            var transportMessage = message.ToTransportMessage();
            var transactionContext = scope.TransactionContext;

            Task SendMessage() => _transport.Send(destinationAddress, transportMessage, transactionContext);

            await SendRetrier.ExecuteAsync(SendMessage, cancellationToken);
        }

        await scope.CompleteAsync();

        _logger.Debug("Successfully sent {count} messages", batch.Count);
    }

    async Task RunCleaner()
    {
        if (_outboxOptions.EnableOutboxCleaner)
        {
            _logger.Debug("Checking outbox storage for messages to be deleted");
            await _outboxStorage.Clean();
        }
    }

    public void TryEagerSend(IEnumerable<OutgoingTransportMessage> outgoingMessages, string correlationId)
    {
        var list = outgoingMessages.ToList();

#pragma warning disable CS4014
        if (!list.Any()) return;

        Task.Run(async () =>
        {
            try
            {
                using var batch = await _outboxStorage.GetNextMessageBatch(correlationId);

                await ProcessMessageBatch(batch, _cancellationTokenSource.Token);

                await batch.Complete();
            }
            catch (Exception)
            {
                // just leave sending to the background sender
            }
        });
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _forwarder?.Dispose();
        _cleaner?.Dispose();
        _cancellationTokenSource?.Dispose();
    }

#pragma warning restore CS4014
}
