// ReSharper disable MethodSupportsCancellation

using System.Collections.Concurrent;
using Confluent.Kafka;
using Krimson.Consumers.Interceptors;
using Krimson.Interceptors;
using Krimson.Logging;
using Krimson.Processors.Configuration;
using Krimson.Processors.Interceptors;
using Krimson.Producers;
using Serilog;

namespace Krimson.Processors;

public enum KrimsonProcessorStatus {
    Activated,
    Terminating,
    Terminated
}

[PublicAPI]
public sealed class KrimsonProcessor : IKrimsonProcessor {
    public static KrimsonProcessorBuilder Builder => new();

    public KrimsonProcessor(KrimsonProcessorOptions options) {
        ClientId         = options.ConsumerConfiguration.ClientId;
        GroupId          = options.ConsumerConfiguration.GroupId;
        Topics           = options.InputTopics;
        BootstrapServers = options.ConsumerConfiguration.BootstrapServers;
        
        Router = options.Router;
        Logger = Log.ForContext(Serilog.Core.Constants.SourceContextPropertyName, ClientId);

        Intercept = options.Interceptors
            .Prepend(new KrimsonProcessorLogger().WithName($"KrimsonProcessor({ClientId})"))
            .Prepend(new ConfluentConsumerLogger())
            .Intercept;

        var serializer   = options.SerializerFactory();
        var deserializer = options.DeserializerFactory();
        
        Producer = new KrimsonProducer(
            options.ProducerConfiguration,
            Intercept,
            serializer,
            options.OutputTopic?.Name
        );
        
        // All handlers (except the log handler) are executed as a
        // side-effect of, and on the same thread as the Consume or
        // Close methods. Any exception thrown in a handler (with
        // the exception of the log and error handlers) will
        // be propagated to the application via the initiating
        // call. i.e. in this example, any exceptions thrown in this
        // handler will be exposed via the Consume method in the main
        // consume loop and handled by the try/catch block there.
        
        Consumer = new ConsumerBuilder<byte[], object?>(options.ConsumerConfiguration)
            .SetValueDeserializer(deserializer)
            .SetLogHandler((csr, log) => Intercept(new ConfluentConsumerLog(ClientId, csr.GetInstanceName(), log)))
            .SetErrorHandler((csr, err) => Intercept(new ConfluentConsumerError(ClientId, csr.GetInstanceName(), err)))
            .SetPartitionsAssignedHandler((_, partitions) => Intercept(new PartitionsAssigned(this, partitions)))
            .SetOffsetsCommittedHandler((_, committed) => Intercept(new PositionsCommitted(this, committed.Offsets, committed.Error)))
            .SetPartitionsRevokedHandler((_, positions) => {
                Intercept(new PartitionsRevoked(this, positions));
                Flush(positions);
            })
            .SetPartitionsLostHandler((_, positions) => {
                Intercept(new PartitionsLost(this, positions));
                Flush(positions);
            })
            .Build();

        Status = KrimsonProcessorStatus.Terminated;
    }

    ILogger                    Logger    { get; }
    IConsumer<byte[], object?> Consumer  { get; }
    KrimsonProducer            Producer  { get; }
    Intercept                  Intercept { get; }
    KrimsonProcessorRouter     Router    { get; }

    CancellationTokenSource Cancellator  { get; set; } = null!;
    OnProcessorTerminated   OnTerminated { get; set; } = null!;

    public string                 ClientId         { get; }
    public string                 GroupId          { get; }
    public string[]               Topics           { get; }
    public string                 BootstrapServers { get; }
    public KrimsonProcessorStatus Status           { get; private set; }

    public async Task Activate(CancellationToken terminationToken, OnProcessorTerminated? onTerminated = null) {
        if (Status == KrimsonProcessorStatus.Activated)
            return;

        Consumer.Subscribe(Topics);

        Intercept(new ProcessorActivated(this));

        Status       = KrimsonProcessorStatus.Activated;
        OnTerminated = onTerminated ?? ((_, _, _) => Task.CompletedTask);
        Cancellator  = CancellationTokenSource.CreateLinkedTokenSource(terminationToken);
        
        await Task.Yield();

        try {
            await foreach (var record in Consumer.Records(position => Intercept(new PartitionEndReached(this, position)), Cancellator.Token))
                await ProcessRecord(record, Cancellator.Token);
        }
        catch (Exception ex) {
            await Terminate(ex);
        }
        finally {
            if (Status == KrimsonProcessorStatus.Activated)
                await Terminate();
        }
    }

    public Task<IReadOnlyCollection<SubscriptionTopicGap>> GetSubscriptionGap() => Consumer.GetSubscriptionGap();

    public Task Terminate() => Terminate(null);
    
    public async ValueTask DisposeAsync() {
        if (Status == KrimsonProcessorStatus.Activated)
            await Terminate().ConfigureAwait(false);
    }

    void Flush(List<TopicPartitionOffset> positions) {
        Producer.Flush();
        Consumer.CommitAll();
    }

    async Task ProcessRecord(KrimsonRecord record, CancellationToken cancellationToken) {
        if (!Router.CanRoute(record)) {
            Consumer.TrackPosition(record);
            Intercept(new InputSkipped(this, record));
            return;
        }

        Intercept(new InputReady(this, record));

        var context = new KrimsonProcessorContext(record, Logger.WithRecordInfo(record), cancellationToken);

        try {
            await Router
                .Process(context)
                .ConfigureAwait(false);
            
            Intercept(new InputConsumed(this, record, context.GeneratedOutput()));
        }
        catch (OperationCanceledException) {
            return;
        }
        catch (Exception ex) {
            Intercept(new InputError(this, record, ex));
            throw;
        }

        ProcessOutput(context.GeneratedOutput());

        void ProcessOutput(IReadOnlyCollection<ProducerRequest> requests) {
            if (requests.Count == 0) {
                Consumer.TrackPosition(record);
                Intercept(new InputProcessed(this, record, requests));
                return;
            }

            var results = new ConcurrentQueue<ProducerResult>();

            foreach (var request in requests)
                Producer.Produce(request, result => OnResult(request, result));

            void OnResult(ProducerRequest message, ProducerResult result) { 
                Intercept(new OutputProcessed(this, Producer.ClientId, result, record, message));

                if (result.Success) {
                    results.Enqueue(result);

                    if (results.Count < requests.Count)
                        return;

                    Consumer.TrackPosition(record);

                    Intercept(new InputProcessed(this, record, requests));
                }
                else {
                    Intercept(new InputError(this, record, result.Exception!));
                    // dont wait here, just let it flow...
                    // this will trigger the cancellation of the token
                    // and gracefully stop processing any other messages
                    #pragma warning disable CS4014
                    Terminate(result.Exception);
                    #pragma warning restore CS4014
                }
            }
        }
    }

    async Task Terminate(Exception? exception) {
        if (!Cancellator.IsCancellationRequested)
            Cancellator.Cancel(); // this will start asking everyone to just stop...
        
        if (exception is OperationCanceledException)
            exception = null;

        if (Status != KrimsonProcessorStatus.Activated) {
            Intercept(
                new ProcessorTerminated(
                    this, Array.Empty<SubscriptionTopicGap>(), new InvalidOperationException(
                        $"{ClientId} already {Status.ToString().ToLower()}. This should not happen! Investigate!", exception
                    )
                )
            );

            return;
        }

        Status = KrimsonProcessorStatus.Terminating;

        Intercept(new ProcessorTerminating(this));
        
        IReadOnlyCollection<SubscriptionTopicGap> gaps = Array.Empty<SubscriptionTopicGap>();

        try {
            // stop the consumer first since this will
            // trigger flush by partitions revoked/lost
            gaps = await Consumer
                .Stop()
                .ConfigureAwait(false);
            
            // at this point disposing of the producer
            // should not have to flush any message
            // requests but maybe some internal ones
            await Producer
                .DisposeAsync()
                .ConfigureAwait(false);
            
            // finally dispose 
            Consumer.Dispose();
        }
        catch (Exception vex) {
            vex = new Exception($"{ClientId} terminated violently! {vex.Message}", vex);
            exception = exception is not null
                ? new AggregateException(exception, vex).Flatten()
                : vex;
        }

        Status = KrimsonProcessorStatus.Terminated;

        Intercept(new ProcessorTerminated(this, gaps, exception));

        try {
            await OnTerminated(this, gaps, exception).ConfigureAwait(false);
        }
        catch (Exception ex) {
            Intercept(new ProcessorTerminatedUserHandlingError(this, ex)); // maybe remove it
        }
    }
}