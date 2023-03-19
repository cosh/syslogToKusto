
namespace syslogToKusto
{
    public class Settings
    {
        public string APPINSIGHTS_CONNECTIONSTRING { get; set; } = null!;

        public String SyslogServerName { get; set; } = System.Environment.MachineName;

        public int ListenPort { get; set; } = 514;

        public SettingsKusto Kusto { get; set; } = new SettingsKusto();

        public SettingsBatching BatchSettings { get; set; } = new SettingsBatching();
    }
}