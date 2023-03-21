using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.ApplicationInsights.Extensibility;
using System.Diagnostics;
using System.Text;
using Kusto.Data;
using Kusto.Ingest;
using System.Net.Sockets;
using System.Net;
using System.Text.Json;

namespace syslogToKusto
{
    internal class Program
    {
        private static readonly IPEndPoint _blankEndpoint = new IPEndPoint(IPAddress.Any, 0);
        private static readonly JsonSerializerOptions serializerOptions = new JsonSerializerOptions();

        static void Main(string[] args)
        {
            Settings settings = GetSettings();

            serializerOptions.WriteIndented = false;

            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder
                    .AddFilter("Microsoft", LogLevel.Warning)
                    .AddFilter("System", LogLevel.Warning)
                    .AddFilter("syslogToKusto", LogLevel.Debug)
                    .AddConsole();

                if (!String.IsNullOrWhiteSpace(settings.APPINSIGHTS_CONNECTIONSTRING))
                {
                    builder.AddApplicationInsights(
                        telemetryConf => { telemetryConf.ConnectionString = settings.APPINSIGHTS_CONNECTIONSTRING; },
                        loggerOptions => {  });
                }
            });

            ILogger logger = loggerFactory.CreateLogger<Program>();

            var tasks = new List<Task>();
            var ingestionJobs = new ConcurrentQueue<IngestionJob>();
            var messages = new ConcurrentQueue<string>();

            tasks.Add(Task.Run(() => Batch(logger, settings.BatchSettings, messages, ingestionJobs)));

            tasks.Add(Task.Run(() => Listen(logger, settings.ListenPort, messages, settings.SyslogServerName)));
            tasks.Add(Task.Run(() => SendToKusto(logger, ingestionJobs, settings.Kusto)));

            logger.LogInformation($"Created {tasks.Count} tasks to work on ingestion of syslog data into kusto");

            Task.WaitAll(tasks.ToArray());
        }

        private static async Task Listen(ILogger logger, int listenPort, ConcurrentQueue<string> messages, string syslogServerName)
        {
            using var udpSocket = new Socket(SocketType.Dgram, ProtocolType.Udp);

            udpSocket.Bind(new IPEndPoint(IPAddress.Any, listenPort));

            logger.LogInformation($"Listening {listenPort}");

            await DoReceiveAsync(logger, udpSocket, messages, syslogServerName);
        }

        private static async Task DoReceiveAsync(ILogger logger, Socket udpSocket, ConcurrentQueue<string> messages, string syslogServerName)
        {
            byte[] buffer = GC.AllocateArray<byte>(length: 1000, pinned: true);
            Memory<byte> bufferMem = buffer.AsMemory();

            string syslogMessage;

            while (true)
            {
                try
                {
                    var result = await udpSocket.ReceiveFromAsync(bufferMem, SocketFlags.None, _blankEndpoint);

                    syslogMessage = CreateMessageForKusto(Encoding.UTF8.GetString(bufferMem.ToArray(), 0, result.ReceivedBytes), result, syslogServerName);

                    messages.Enqueue(syslogMessage);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error receiving message from socket");
                }
            }
        }

        private static string CreateMessageForKusto(string payload, SocketReceiveFromResult result, string syslogServerName)
        {
            MessageForKusto helper = new MessageForKusto();

            helper.Payload = payload.Trim();
            helper.ReceivedBytes = result.ReceivedBytes;
            helper.RemoteEndPoint = result.RemoteEndPoint;
            helper.SyslogServerName = syslogServerName;

            return JsonSerializer.Serialize(helper, serializerOptions);
        }

        private static void Batch(ILogger logger,
            SettingsBatching batchSettings, ConcurrentQueue<string> messages, ConcurrentQueue<IngestionJob> ingestionJobs)
        {
            var timeLimit = batchSettings.BatchLimitInMinutes * 60 * 1000;

            var sw = Stopwatch.StartNew();

            StringBuilder sb = new StringBuilder();
            int eventCount = 0;

            while (true)
            {
                String syslogEvent;
                while (messages.TryDequeue(out syslogEvent))
                {
                    //still something to add
                    sb.AppendLine(syslogEvent);
                    eventCount++;
                }

                //nothing to add any longer, enough for an ingestion command?
                if (eventCount > batchSettings.BatchLimitNumberOfEvents || sw.ElapsedMilliseconds > timeLimit)
                {
                    if (eventCount > 0)
                    {
                        string tempFile = Path.GetRandomFileName();
                        File.WriteAllText(tempFile, sb.ToString());

                        ingestionJobs.Enqueue(new IngestionJob() { ToBeIngested = tempFile, BatchInfo = batchSettings });

                        logger.LogInformation($"Created a file {tempFile} with {eventCount} events");
                    }

                    //prepare for next batch
                    sw.Restart();
                    eventCount = 0;
                    sb.Clear();
                }

                Thread.Sleep(1000);
            }
        }

        private static void SendToKusto(ILogger logger, ConcurrentQueue<IngestionJob> ingestionJobs, SettingsKusto kusto)
        {
            var kustoConnectionStringBuilderEngine =
                new KustoConnectionStringBuilder($"https://{kusto.ClusterName}.kusto.windows.net").WithAadApplicationKeyAuthentication(
                    applicationClientId: kusto.ClientId,
                    applicationKey: kusto.ClientSecret,
                    authority: kusto.TenantId);

            using (IKustoIngestClient client = KustoIngestFactory.CreateDirectIngestClient(kustoConnectionStringBuilderEngine))
            {
                while (true)
                {
                    IngestionJob job;
                    while (ingestionJobs.TryDequeue(out job))
                    {
                        //Ingest from blobs according to the required properties
                        var kustoIngestionProperties = new KustoIngestionProperties(databaseName: kusto.DbName, tableName: job.BatchInfo.KustoTable);
                        kustoIngestionProperties.SetAppropriateMappingReference(job.BatchInfo.MappingName, Kusto.Data.Common.DataSourceFormat.multijson);
                        kustoIngestionProperties.Format = Kusto.Data.Common.DataSourceFormat.multijson;

                        logger.LogDebug($"About start ingestion into table {job.BatchInfo.KustoTable} using file {job.ToBeIngested}");

                        //ingest
                        Ingest(logger, client, job, kustoIngestionProperties, kusto);

                        logger.LogInformation($"Finished ingestion into table {job.BatchInfo.KustoTable} using file {job.ToBeIngested}");

                        Thread.Sleep(100);

                        File.Delete(job.ToBeIngested);
                        logger.LogDebug($"Deleted file {job.ToBeIngested} because of successful ingestion");

                        Thread.Sleep(10000);
                    }
                }
            }
        }

        static void Ingest(ILogger logger, IKustoIngestClient client, IngestionJob job, KustoIngestionProperties kustoIngestionProperties, SettingsKusto kusto)
        {
            int retries = 0;

            while (retries < kusto.MaxRetries)
            {
                try
                {
                    client.IngestFromStorage(job.ToBeIngested, kustoIngestionProperties);
                    return;
                }
                catch (Exception e)
                {
                    logger.LogError(e, $"Could not ingest {job.ToBeIngested} into table {job.BatchInfo.KustoTable}.");
                    retries++;
                    Thread.Sleep(kusto.MsBetweenRetries);
                }
            }

        }

        private static Settings GetSettings()
        {
            string developmentConfiguration = "appsettingsDevelopment.json";
            string configFile = "appsettings.json";
            string fileUsedForConfiguration = null;

            if (File.Exists(developmentConfiguration))
            {
                fileUsedForConfiguration = developmentConfiguration;
            }
            else
            {
                fileUsedForConfiguration = configFile;
            }

            IConfiguration config = new ConfigurationBuilder()
                .AddJsonFile(fileUsedForConfiguration)
                .AddEnvironmentVariables()
                .Build();

            Settings settings = config.GetRequiredSection("Settings").Get<Settings>();

            // Override any settings from environment variables, useful for Docker Container configurations
            settings.ListenPort = GetEnvironmentVariable<int>(nameof(Settings.ListenPort), settings.ListenPort);
            settings.SyslogServerName = GetEnvironmentVariable<string>(nameof(Settings.SyslogServerName), settings.SyslogServerName);
            
            settings.Kusto.ClientId = GetEnvironmentVariable<string>(nameof(SettingsKusto.ClientId), settings.Kusto.ClientId);
            settings.Kusto.ClientSecret = GetEnvironmentVariable<string>(nameof(SettingsKusto.ClientSecret), settings.Kusto.ClientSecret);
            settings.Kusto.ClusterName = GetEnvironmentVariable<string>(nameof(SettingsKusto.ClusterName), settings.Kusto.ClusterName);
            settings.Kusto.TenantId = GetEnvironmentVariable<string>(nameof(SettingsKusto.TenantId), settings.Kusto.TenantId);
            settings.Kusto.DbName = GetEnvironmentVariable<string>(nameof(SettingsKusto.DbName), settings.Kusto.DbName);
            settings.Kusto.MaxRetries = GetEnvironmentVariable<int>(nameof(SettingsKusto.MaxRetries), settings.Kusto.MaxRetries);
            settings.Kusto.MsBetweenRetries = GetEnvironmentVariable<int>(nameof(SettingsKusto.MsBetweenRetries), settings.Kusto.MsBetweenRetries);

            settings.BatchSettings.KustoTable = GetEnvironmentVariable<string>(nameof(SettingsBatching.KustoTable), settings.BatchSettings.KustoTable);
            settings.BatchSettings.MappingName = GetEnvironmentVariable<string>(nameof(SettingsBatching.MappingName), settings.BatchSettings.MappingName);
            settings.BatchSettings.BatchLimitInMinutes = GetEnvironmentVariable<int>(nameof(SettingsBatching.BatchLimitInMinutes), settings.BatchSettings.BatchLimitInMinutes);
            settings.BatchSettings.BatchLimitNumberOfEvents = GetEnvironmentVariable<int>(nameof(SettingsBatching.BatchLimitNumberOfEvents), settings.BatchSettings.BatchLimitNumberOfEvents);

            return settings;
        }

        private static T GetEnvironmentVariable<T>(string name, T defaultValue)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }

            return defaultValue;
        }
    }
}