namespace syslogToKusto
{
    public class SettingsBatching
    {
        public string KustoTable { get; set; } = "syslogRaw";

        public string MappingName { get; set; } = "map";

        public int BatchLimitInMinutes { get; set; } = 5;

        public int BatchLimitNumberOfEvents { get; set; } = 1000;
    }
}