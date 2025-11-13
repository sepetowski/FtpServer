using FtpServer.Config;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;

namespace FtpServer;

class Program
{
    static async Task Main(string[] args)
    {
        var serverPath = args.Contains("--server") ? args[Array.IndexOf(args, "--server") + 1] : "server.json";
        var usersPath = args.Contains("--users") ? args[Array.IndexOf(args, "--users") + 1] : "users.json";

        var serverCfg = ServerConfig.Load(serverPath);
        var usersCfg = UsersConfig.Load(usersPath);

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information)
            .AddConsole();
        });

        var logger = loggerFactory.CreateLogger("FTP");

        try
        {

            var ftpServer = new Ftp.FtpServer(serverCfg, usersCfg, logger);
            await ftpServer.StartAsync();
        }
        catch (SocketException)
        {
            logger.LogError("Failed to start FTP server: port {port} is already in use or unavailable.",serverCfg.ControlPort);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while starting FTP server.");
        }
    }
}
