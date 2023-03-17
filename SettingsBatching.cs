namespace syslogToKusto
{
    public class SettingsBatching
    {
        public string KustoTable { get; set; }

        public int BatchLimitInMinutes { get; set; }

        public int BatchLimitNumberOfEvents { get; set; }
    }
}