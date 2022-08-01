using System.Text.Json.Nodes;
using Google.Protobuf.WellKnownTypes;
using Krimson;
using Krimson.Connectors;
using Krimson.Extensions.DependencyInjection;
using Refit;

var host = Host
    .CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) => {
            services.AddKrimson()
                .AddProtobuf()
                .AddPeriodicSourceConnector<PowerMetersConnector>()
                .AddProducer(pdr => pdr.ClientId("my_app_name").Topic("foo.bar.baz"))
                .AddReader(rdr => rdr.ClientId("my_app_name"));

            services.AddRefitClient<IPowerMetersClient>()
                .ConfigureHttpClient(
                    client => {
                        client.BaseAddress                         = new(ctx.Configuration["PowerMetersClient:Url"]);
                        client.DefaultRequestHeaders.Authorization = new("Token", ctx.Configuration["PowerMetersClient:ApiKey"]);
                    }
                );
        }
    )
    .Build();

await host.RunAsync();

interface IPowerMetersClient {
    [Get("/meters/")]
    public Task<JsonObject?> GetMeters();
}

[BackOffTimeSeconds(30)]
class PowerMetersConnector : KrimsonPeriodicSourceConnector {
    public PowerMetersConnector(IPowerMetersClient client) => Client = client;

    IPowerMetersClient Client { get; }

    public override async IAsyncEnumerable<JsonNode> SourceData(KrimsonPeriodicSourceConnectorContext context) {
        var result = await Client.GetMeters().ConfigureAwait(false);

        foreach (var item in result?.AsArray() ?? new JsonArray())
            yield return item!;
    }

    public override IAsyncEnumerable<SourceRecord> SourceRecords(IAsyncEnumerable<JsonNode> data, CancellationToken cancellationToken) {
        return data.Select(ParseSourceRecord!);

        static SourceRecord ParseSourceRecord(JsonNode node) {
            try {
                var recordId  = node["id"]!.GetValue<string>();
                var timestamp = Timestamp.FromDateTimeOffset(node["last_modified"]!.GetValue<DateTimeOffset>());
                var data      = Struct.Parser.ParseJson(node.ToJsonString());

                return new SourceRecord {
                    Id        = recordId,
                    Data      = data,
                    Timestamp = timestamp,
                    Type      = "power-meters",
                    Operation = SourceOperation.Snapshot
                };
            }
            catch (Exception) {
                return SourceRecord.Empty;
            }
        }
    }
}