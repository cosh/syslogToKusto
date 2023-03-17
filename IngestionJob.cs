namespace syslogToKusto
{
    internal class IngestionJob
    {
        public string ToBeIngested { get; internal set; }
        public SettingsBatching BatchInfo { get; internal set; }
    }
}