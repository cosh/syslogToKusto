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

namespace syslogToKusto
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Settings settings = GetSettings();

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
            var messages = new ConcurrentQueue<String>();

            tasks.Add(Task.Run(() => Batch(logger, settings.BatchSettings, messages, ingestionJobs)));

            tasks.Add(Task.Run(() => Listen(logger, settings.ListenIP, settings.ListenPort, messages)));
            tasks.Add(Task.Run(() => SendToKusto(logger, ingestionJobs, settings.Kusto)));

            logger.LogInformation($"Created {tasks.Count} tasks to work on ingestion of syslog data into kusto");

            Task.WaitAll(tasks.ToArray());
        }

        private static async Task Listen(ILogger logger, string listenIP, int listenPort, ConcurrentQueue<String> messages)
        {
            IPEndPoint anyIP = new IPEndPoint(IPAddress.Parse(listenIP), 0);
            UdpClient udpListener = new UdpClient(listenPort);
            byte[] bytesReceived; 
            string syslogMessage;

            /* Main Loop */
            /* Listen for incoming data on udp port 514 (default for SysLog events) */
            while (true)
            {
                try
                {
                    bytesReceived = udpListener.Receive(ref anyIP);
                    syslogMessage = Encoding.UTF8.GetString(bytesReceived);
                    messages.Enqueue(syslogMessage);
                }
                catch (Exception ex) 
                {
                    logger.LogError(ex, "Error receiving and transforming syslog nessage");
                }
            }
        }

        private static void Batch(ILogger logger,
            SettingsBatching batchSettings, ConcurrentQueue<String> messages, ConcurrentQueue<IngestionJob> ingestionJobs)
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
                        kustoIngestionProperties.Format = Kusto.Data.Common.DataSourceFormat.txt;

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
            return settings;
        }
    }
}