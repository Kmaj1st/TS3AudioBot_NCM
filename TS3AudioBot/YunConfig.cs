using NLog;
using System.Collections.Generic;
using System.IO;
using TS3AudioBot.Audio;
using YamlDotNet.Serialization;

namespace TS3AudioBot
{
	public class YunConfig
    {
		public int Version { get; set; }
		public Mode PlayMode { get; set; }
		public string NcmApi { get; set; }
		public bool IsQrlogin { get; set; }
		public int CookieUpdateIntervalMin { get; set; }
		public bool AutoPause { get; set; }
		public Dictionary<string, string> Header { get; set; }
		public string DefaultImage { get; set; }

		[YamlIgnore]
		private static string? ConfigDir { get; set; }
		[YamlIgnore]
		private static string? ConfigFileName { get; set; }
		[YamlIgnore]
        public int CurrentVersion = 1;

		private static string ConfigPath(string? configDir = null, string? configFilename = null)
		{
			ConfigDir = configDir ?? ConfigDir;
			ConfigDir ??= Path.Combine(Directory.GetCurrentDirectory(), "config");
			ConfigFileName = configFilename ?? ConfigFileName;
			ConfigFileName ??= "ncmconfig.yaml";
			return Path.Combine(ConfigDir, ConfigFileName);
		}
		public static YunConfig GetConfig(string? configDir = null, string? configFilename=null)
        {
			string path = ConfigPath(configDir, configFilename);
			try
            {
                var config = YamlSerialize.Deserializer<YunConfig>(path);
                if (config.Version < config.CurrentVersion)
                {
                    config.Version = config.CurrentVersion;
                    config.Save();
                }
                return config;
            }
            catch (FileNotFoundException e)
            {
                var config = new YunConfig
                {
                    Version = 1,
                    PlayMode = Mode.SeqPlay,
                    NcmApi = "http://127.0.0.1:3000",
                    IsQrlogin = false,
                    AutoPause = true,
                    CookieUpdateIntervalMin = 30,
                    Header = new Dictionary<string, string>
                    {
                        { "Cookie", "" },
                        { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36 Edg/122.0.0.0" }
                    },
					DefaultImage = "https://cataas.com/cat/says/GoodMusic?fontSize=32&fontColor=gold&width=256&height=256"
				};
                config.Save();
				NLog.LogManager.GetCurrentClassLogger().Warn(e);
				NLog.LogManager.GetCurrentClassLogger().Warn("new ncmconfig file has been generated");
				return config;
            }
        }

        public void Save()
        {
			string path = ConfigPath();
			if (ConfigDir != null && !Directory.Exists(ConfigDir))
			{
				Directory.CreateDirectory(ConfigDir);
			}
			YamlSerialize.Serializer(this, path is null ? "" : path);
        }
    }
}
