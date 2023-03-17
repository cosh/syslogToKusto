
namespace syslogToKusto
{
    public class Settings
    {
        public string APPINSIGHTS_CONNECTIONSTRING { get; set; } = null!;

        public String SyslogServerName { get; set; }
        
        public int ListenPort { get; set; }

        public SettingsKusto Kusto { get; set; } = null!;

        public SettingsBatching BatchSettings { get; set; } = null!;
    }
}