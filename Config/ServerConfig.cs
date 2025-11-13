using System.Net;
using System.Text.Json;

namespace FtpServer.Config
{
    public class ServerConfig
    {
        public string Root { get; set; } = Path.GetFullPath("./ftp_root");
        public string Bind { get; set; } = "0.0.0.0";
        public int ControlPort { get; set; } = 21;
        public int PasvMin { get; set; } = 50000;
        public int PasvMax { get; set; } = 50100;
        public int PreLoginIdleSeconds { get; set; } = 120;
        public int PostLoginIdleSeconds { get; set; } = 300;
        public bool AllowAnonymous { get; set; } = true;

        public IPAddress BindAddress => IPAddress.Parse(Bind);

        public static ServerConfig Load(string path)
        {
            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<ServerConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new ServerConfig();

            Directory.CreateDirectory(cfg.Root);
            return cfg;
        }
    }
}
