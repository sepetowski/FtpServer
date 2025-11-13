using FtpServer.Models;
using System.Text.Json;

namespace FtpServer.Config
{
    public class UsersConfig
    {
        public List<User> Users { get; set; } = new();

        public static UsersConfig Load(string path)
        {
            var json = File.ReadAllText(path);

            return JsonSerializer.Deserialize<UsersConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new UsersConfig();
        }
    }
}
