using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Testcontainers.PostgreSql;

namespace MyDotNetEventStore.Tests;

[SetUpFixture]
public class PostgresEventStoreSetup
{
    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder()
        .Build();

    private TracerProvider _tracerProvider;
    private MeterProvider _metricProvider;

    public static NpgsqlConnection Connection;

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        _tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("MyDotNetEventStore"))
            .AddSource("MyDotNetEventStore", "EventStoreTest")
            .AddOtlpExporter(exporter =>
            {
                exporter.Endpoint = new Uri("http://localhost:5341/ingest/otlp/v1/traces");
                exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
            })
            .AddOtlpExporter(exporter =>
            {
                exporter.Endpoint = new Uri("http://localhost:4317");
                exporter.Protocol = OtlpExportProtocol.Grpc;
            })
            .Build();

        _metricProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter("MyDotNetEventStore")
            .ConfigureResource(resource => { resource.AddService("MyDotNetEventStore"); })
            .AddOtlpExporter(exporter =>
            {
                exporter.Endpoint = new Uri("http://localhost:4317");
                exporter.Protocol = OtlpExportProtocol.Grpc;
            })
            .Build();

        await _postgresContainer.StartAsync();

        // var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Trace));
        // NpgsqlLoggingConfiguration.InitializeLogging(loggerFactory);
        // NpgsqlLoggingConfiguration.InitializeLogging(loggerFactory, parameterLoggingEnabled: true);

        Connection = new NpgsqlConnection(_postgresContainer.GetConnectionString());

        await Connection.OpenAsync();

        await EventStoreSchema.BuildSchema(Connection);
    }


    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        _tracerProvider.Dispose();
        _metricProvider.Dispose();
        await Connection.DisposeAsync();
        await _postgresContainer.DisposeAsync();
    }
}