# Syslog to Kusto

![the logo](media/logo.png)

**Syslog** is a standard protocol used to forward system messages, network device logs, and application logs from various sources to a centralized logging system for analysis, troubleshooting, and monitoring. **Kusto** ([Azure Data Explorer](https://azure.microsoft.com/services/data-explorer/), [Azure Synapse Data Explorer](https://docs.microsoft.com/azure/synapse-analytics/data-explorer/data-explorer-overview), [MyFreeCluster](https://aka.ms/kustofree)) is a cloud-based data analytics platform that enables users to store, query, and analyze large-scale log data and other types of structured and unstructured data in real-time.

The **"Syslog to Kusto"** tool is a solution created to help users ingest data from Syslog sources into Kusto. This tool allows organizations to analyze and gain insights from Syslog data in Kusto alongside other types of data such as Azure Monitor logs, Azure Storage logs, and more.

The tool works by capturing Syslog messages and forwarding them to Kusto using the Kusto ingestion API.

The tool provides several features such as:

1. **Easy configuration**: The tool can be configured using JSON

1. **Integration with Kusto**: The tool integrates seamlessly with Kusto, allowing users to query and analyze Syslog data alongside other types of data.

In summary, **Syslog to Kusto** is a tool designed to help organizations ingest and analyze Syslog data in Kusto, enabling them to gain insights from their log data and improve their IT operations, security, and compliance.

## Schema in Kusto

```kusto
// Create table command
////////////////////////////////////////////////////////////
.create table syslogRaw  (['Payload']:string, ['RemoteEndPoint']:dynamic, ['SyslogServerName']:string, ['ReceivedBytes']:int)

// Create mapping command
////////////////////////////////////////////////////////////
.create table syslogRaw ingestion json mapping 'map' '[{"column":"Payload", "Properties":{"Path":"$[\'Payload\']"}},{"column":"RemoteEndPoint", "Properties":{"Path":"$[\'RemoteEndPoint\']"}},{"column":"SyslogServerName", "Properties":{"Path":"$[\'SyslogServerName\']"}},{"column":"ReceivedBytes", "Properties":{"Path":"$[\'ReceivedBytes\']"}}]'

// Create the update function
.create-or-alter function with (folder = "Update") Update_Syslog() {
syslogRaw#
| extend timestamp = ingestion_time()
| parse Payload with "<" Priority:int ">" message:string
| extend message = iff(isempty( message), Payload, message), Priority = iff(isempty( Priority), int(-1), Priority)
| project-away Payload
| extend Address = tostring(RemoteEndPoint.Address), AddressFamily = tostring(RemoteEndPoint.AddressFamily), Port = toint(RemoteEndPoint.Port)
| project-away RemoteEndPoint
}

// Create the syslog table and transform data
.set-or-append syslog <| Update_Syslog

// Enable the update policy
.alter table syslog policy update
```
[
    {
        "IsEnabled": true,
        "Source": "syslogRaw",
        "Query": "Update_Syslog",
        "IsTransactional": true,
        "PropagateIngestionProperties": false
    }
]
```

// Create statistics by priority
.create materialized-view with (backfill = true) priorityStats on table syslog
{
    syslog
    | summarize ReceivedBytes = avg(ReceivedBytes) by Priority, timestamp = bin(timestamp, 1h)
}

// Create statistics by address
.create materialized-view with (backfill = true) addressStats on table syslog
{
    syslog
    | summarize ReceivedBytes = avg(ReceivedBytes) by Address, timestamp = bin(timestamp, 1h)
}
```

## Configuration

```json
{
  "Settings": {
    "APPINSIGHTS_CONNECTIONSTRING": "",
    "ListenPort": "Listening port",
    "SyslogServerName": "the name / id of this syslogserver",
    "Kusto": {
      "ClientId": "service principal client id",
      "ClientSecret": "service principal client secret",
      "TenantId": "azure ad tenant",
      "ClusterName": "<clustername>.<azureRegion>",
      "DbName": "kusto database name",
      "MaxRetries": 10,
      "MsBetweenRetries": 60000
    },
    "BatchSettings": {
      "KustoTable": "raw",
      "MappingName": "map",
      "BatchLimitInMinutes": 5,
      "BatchLimitNumberOfEvents": "1000"
    }
  }
}
```

Have fun sending data Kusto and create beautiful visualizations using the [dashboards](https://dataexplorer.azure.com/dashboards).
