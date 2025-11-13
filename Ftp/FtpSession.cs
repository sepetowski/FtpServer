using System.Net;
using System.Net.Sockets;
using System.Globalization;
using FtpServer.Config;

namespace FtpServer.Ftp
{
    public class FtpSession
    {
        private readonly PasvPortPool portPool;
        private readonly TcpClient controlClient;
        private readonly ServerConfig cfg;
        public bool LoggedIn { get; set; }
        public string PendingUser { get; set; } = "";
        public string UserName { get; set; } = "";
        public bool IsAnonymous => string.Equals(UserName, "anonymous", StringComparison.OrdinalIgnoreCase);

        public string RootPath { get; private set; }
        private string CurrentRel = "/";

        public TcpListener? PasvListener { get; private set; }
        private int PasvPort = 0;

        public FtpSession(ServerConfig cfg, PasvPortPool pool, TcpClient control)
        {
            this.cfg = cfg;
            portPool = pool;
            controlClient = control;
            RootPath = Path.GetFullPath(cfg.Root);
        }

        public void SetUserRoot(string newRoot)
        {
            RootPath = Path.GetFullPath(newRoot);
            Directory.CreateDirectory(RootPath);
            CurrentRel = "/";
        }

        public string GetFtpCwd() => CurrentRel;

        public bool TryChangeDir(string arg)
        {
            var path = MapDir(arg);
            if (path == null) return false;
            if (Directory.Exists(path))
            {
                CurrentRel = ToRelFtp(path);
                return true;
            }
            return false;
        }

        // List directory contents in Unix format
        public IEnumerable<string> BuildUnixList(string? arg)
        {
            var targetPath = MapDir(string.IsNullOrWhiteSpace(arg) ? "." : arg!);
            if (targetPath == null || !Directory.Exists(targetPath))
                yield break;

            var dir = new DirectoryInfo(targetPath);
            foreach (var d in dir.GetDirectories())
                yield return FormatUnixListLine(d);
            foreach (var f in dir.GetFiles())
                yield return FormatUnixListLine(f);
        }

        // Format a single line in Unix 'ls -l' style
        private static string FormatUnixListLine(FileSystemInfo fsi)
        {
            bool isDir = (fsi.Attributes & FileAttributes.Directory) != 0;
            string perms = isDir ? "drwxr-xr-x" : "-rw-r--r--";

            int links = 1;
            string owner = "owner";
            string group = "group";

            long size = isDir ? 0 : fsi is FileInfo fi ? fi.Length : 0;
            var dt = fsi.LastWriteTime;
            string date = dt.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);

            return $"{perms} {links,3} {owner,8} {group,8} {size,10} {date} {fsi.Name}";
        }

        // Map an FTP path to a physical path, ensuring it's within the root
        public string? MapPath(string ftpPath)
        {
            string rel = ftpPath.StartsWith("/") ? ftpPath : CombineFtp(CurrentRel, ftpPath);

            // physical path
            var full = Path.GetFullPath(Path.Combine(RootPath,rel.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));

            // ensure within root
            if (!full.StartsWith(RootPath, StringComparison.OrdinalIgnoreCase))
                return null;

            return full;
        }

        public string? MapDir(string ftpPath) => MapPath(ftpPath);

        // Combine two FTP paths, handling ., .., and /
        private static string CombineFtp(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a)) a = "/";
            if (string.IsNullOrWhiteSpace(b)) b = "";

            var p = a.EndsWith("/") ? a + b : a + "/" + b;
            var parts = new Stack<string>();

            foreach (var part in p.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".") continue;
                if (part == "..") { if (parts.Count > 0) parts.Pop(); continue; }
                parts.Push(part);
            }

            var arr = parts.ToArray();
            Array.Reverse(arr);

            return "/" + string.Join("/", arr);
        }

        // Convert a full physical path to a relative FTP path
        private string ToRelFtp(string full)
        {
            var rel = full.Substring(RootPath.Length).TrimStart(Path.DirectorySeparatorChar);
            return "/" + rel.Replace(Path.DirectorySeparatorChar, '/');
        }

        // Get the IP address to be used in the PASV reply
        public IPAddress GetPassiveReplyAddress()
        {
            var ip = ((IPEndPoint)controlClient.Client.LocalEndPoint!).Address;

            if (cfg.BindAddress != IPAddress.Any && cfg.BindAddress != IPAddress.IPv6Any)
                ip = cfg.BindAddress;

            if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
                ip = IPAddress.Loopback;

            return ip.MapToIPv4();
        }

        // Try to open a PASV data connection listener on an available port
        public bool TryOpenPasv(out TcpListener listener, out int port)
        {
            listener = null!;
            port = 0;

            while (portPool.TryAcquire(out var p))
            {
                try
                {
                    var bindIp = cfg.BindAddress.Equals(IPAddress.Any) || cfg.BindAddress.Equals(IPAddress.IPv6Any)
                        ? IPAddress.Any
                        : cfg.BindAddress;

                    var ep = new IPEndPoint(bindIp, p);
                    var l = new TcpListener(ep);

                    l.Start();            
                    PasvListener = l;
                    PasvPort = p;

                    listener = l;
                    port = p;
                    return true;           
                }
                catch (Exception) { }
                finally
                {
                    if (listener == null && p != 0)
                        portPool.Release(p); 
                }
            }

            return false;
        }

        // Accept an incoming PASV data connection
        public async Task<TcpClient?> AcceptDataAsync()
        {
            if (PasvListener == null) return null;
            try
            {
                PasvListener.Server.ReceiveTimeout = 15000;
                PasvListener.Server.SendTimeout = 15000;

                var acceptTask = PasvListener.AcceptTcpClientAsync();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using (cts.Token.Register(() => { try { PasvListener.Stop(); } catch { } }))
                {
                    var cli = await acceptTask;
                    return cli;
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                ClosePasv();
            }
        }

        // Close the PASV listener and release the port
        public void ClosePasv()
        {
            if (PasvListener != null)
            {
                try { PasvListener.Stop(); } catch { }
                if (PasvPort != 0) portPool.Release(PasvPort);
                PasvListener = null;
                PasvPort = 0;
            }
        }
    }
}
