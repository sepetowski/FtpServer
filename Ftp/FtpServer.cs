using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using FtpServer.Config;
using FtpServer.Exceptions;
using Microsoft.Extensions.Logging;

namespace FtpServer.Ftp
{
    public class FtpServer
    {
        private readonly ServerConfig _server;
        private readonly UsersConfig _users;
        private readonly ILogger _logger;
        private readonly PasvPortPool _portPool;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;

        public FtpServer(ServerConfig server, UsersConfig users, ILogger logger)
        {
            _server = server;
            _users = users;
            _logger = logger;
            _portPool = new PasvPortPool(server.PasvMin, server.PasvMax);
        }

        public async Task StartAsync()
        {
            Directory.CreateDirectory(_server.Root);

            _listener = new TcpListener(_server.BindAddress, _server.ControlPort);
            _listener.Start();

            _logger.LogInformation(
                "FTP server started: address={addr}, port={port}, root={root}, pasv_range={min}-{max}, anonymous={anon}, users=[{users}]",
                _server.BindAddress, _server.ControlPort, _server.Root, _server.PasvMin, _server.PasvMax,
                _server.AllowAnonymous ? "ENABLED" : "DISABLED",
                _users.Users.Count == 0 ? "(none)" : string.Join(",", _users.Users.Select(u => u.Username)));

            _cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; _cts.Cancel(); };

            var tasks = new List<Task>();
            while (!_cts.IsCancellationRequested)
            {
                if (!_listener.Pending())
                {
                    await Task.Delay(50, _cts.Token).ContinueWith(_ => { });
                    continue;
                }
               
                var client = await _listener.AcceptTcpClientAsync();
                client.NoDelay = true;

                // Handle client in a separate task
                tasks.Add(Task.Run(() => HandleClientAsync(client)));
            }

            _listener.Stop();
            await Task.WhenAll(tasks);
        }

        // Handle an individual FTP client session
        private async Task HandleClientAsync(TcpClient client)
        {
            var remote = client.Client.RemoteEndPoint?.ToString() ?? string.Empty;
            var sessionId = Guid.NewGuid().ToString("N")[..8];
    
            using (_logger.BeginScope(new Dictionary<string, object>
            {
                ["Remote"] = remote,
                ["Session"] = sessionId
            }))
            {
                _logger.LogInformation("Client connected to FTP server");

                using (client)
                {
                    
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

                    await writer.WriteLineAsync("220 Server ready");

                    var session = new FtpSession(_server, _portPool, client);

                    try
                    {
                        while (true)
                        {
                            string? line;
                            try
                            {
                                stream.ReadTimeout = (int)TimeSpan.FromSeconds(session.LoggedIn ? _server.PostLoginIdleSeconds : _server.PreLoginIdleSeconds).TotalMilliseconds;
                                line = await reader.ReadLineAsync();
                            }
                            catch (IOException)
                            {
                                _logger.LogWarning("Closing control connection due to inactivity (timeout)");
                                await writer.WriteLineAsync("421 Timeout - closing control connection");
                                break;
                            }

                            if (line == null)
                            {
                                _logger.LogInformation("Client closed the control connection");
                                break;
                            }

                            var (cmd, arg) = ParseCommand(line);
                            _logger.LogDebug("Received command {cmd} with argument \"{arg}\"", cmd, arg);

                            await HandleCommandAsync(session, cmd, arg, writer);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unexpected error during client session");
                        try { await writer.WriteLineAsync("421 Server error, closing connection"); } catch { }
                    }
                    finally
                    {
                        session.ClosePasv();
                        _logger.LogInformation("Client disconnected from FTP server");
                    }
                }
            }
        }

        private async Task HandleCommandAsync(FtpSession session, string cmd, string arg, StreamWriter writer)
        {
            try
            {
                switch (cmd)
                {
                    // no operation - keepalive
                    case "NOOP":
                        await HandleNoop(writer);
                        break;

                    // options - ignore
                    case "OPTS":
                        await HandleOpts(arg, writer);
                        break;

                    // system type - always UNIX L8
                    case "SYST":
                        await HandleSyst(writer);
                        break;

                    // file type - only binary supported
                    case "TYPE":
                        await HandleType(arg, writer);
                        break;

                    // features
                    case "FEAT":
                        await HandleFeat(writer);
                        break;

                    // username
                    case "USER":
                        await HandleUser(session, arg, writer);
                        break;

                    // user password
                    case "PASS":
                        await HandlePass(session, arg, writer);
                        break;

                    // current directory
                    case "PWD":
                        await HandlePwd(session, writer);
                        break;

                    // change directory
                    case "CWD":
                        await HandleCwd(session, arg, writer);
                        break;

                    // back to parent directory
                    case "CDUP":
                        await HandleCdup(session, writer);
                        break;

                    // passive mode
                    case "PASV":
                        await HandlePasv(session, writer);
                        break;

                    // list directory
                    case "LIST":
                        await HandleList(session, arg, writer);
                        break;

                    // download file
                    case "RETR":
                        await HandleRetr(session, arg, writer);
                        break;

                    // save file
                    case "STOR":
                        await HandleStor(session, arg, writer);
                        break;

                    // delete file
                    case "DELE":
                        await HandleDele(session, arg, writer);
                        break;

                    // create directory
                    case "MKD":
                        await HandleMkd(session, arg, writer);
                        break;

                    // delete directory
                    case "RMD":
                        await HandleRmd(session, arg, writer);
                        break;

                    // quit session
                    case "QUIT":
                        await HandleQuit(writer);
                        break;

                    default:
                        await HandleUnknown(cmd, arg, writer);
                        break;
                }
            }
            catch (FtpReplyException ex)
            {
                _logger.LogWarning("Sending FTP reply to client: {reply}", ex.Message);
                await writer.WriteLineAsync(ex.Message);
            }
        }

        private async Task HandleUser(FtpSession session, string arg, StreamWriter writer)
        {
            if (string.Equals(arg, "anonymous", StringComparison.OrdinalIgnoreCase))
            {
                if (!_server.AllowAnonymous)
                {
                    _logger.LogInformation("Anonymous login attempt rejected (anonymous access disabled)");
                    await writer.WriteLineAsync("530 Anonymous access denied");
                }
                else
                {
                    session.PendingUser = "anonymous";
                    _logger.LogInformation("Anonymous user provided, asking for password (any value accepted)");
                    await writer.WriteLineAsync("331 Anonymous login ok, send any password");
                }
            }
            else
            {
                session.PendingUser = arg ?? "";
                _logger.LogInformation("Username received: {user} (awaiting password)", session.PendingUser);
                await writer.WriteLineAsync("331 Password required");
            }
        }

        private async Task HandlePass(FtpSession session, string arg, StreamWriter writer)
        {
            if (session.PendingUser == "anonymous")
            {
                if (!_server.AllowAnonymous)
                {
                    _logger.LogInformation("Anonymous login attempt blocked at password step (anonymous disabled)");
                    await writer.WriteLineAsync("530 Anonymous access denied");
                }
                else
                {
                    session.LoggedIn = true;
                    session.UserName = "anonymous";
                    var anonHome = Path.Combine(_server.Root, "anonymous");
                    session.SetUserRoot(anonHome);
                    _logger.LogInformation("User 'anonymous' logged in successfully, home directory={home}", anonHome);
                    await writer.WriteLineAsync("230 Logged in.");
                }
            }
            else
            {
                var rec = _users.Users.FirstOrDefault(u =>
                    string.Equals(u.Username, session.PendingUser, StringComparison.Ordinal));

                if (rec != null && string.Equals(rec.Password ?? "", arg ?? "", StringComparison.Ordinal))
                {
                    session.LoggedIn = true;
                    session.UserName = rec.Username;
                    var home = Path.Combine(_server.Root, "users", rec.Username);
                    session.SetUserRoot(home);
                    _logger.LogInformation("User '{user}' logged in successfully, home directory={home}", rec.Username, home);
                    await writer.WriteLineAsync("230 Logged in.");
                }
                else
                {
                    _logger.LogWarning("User login failed: username={user}", session.PendingUser);
                    await writer.WriteLineAsync("530 Login incorrect");
                }
            }
        }

        private async Task HandlePasv(FtpSession session, StreamWriter writer)
        {
            RequireLogin(session);
            if (session.PasvListener != null)
            {
                _logger.LogDebug("PASV: closing previous passive data listener");
                session.ClosePasv();
            }

            var ipForReply = session.GetPassiveReplyAddress();
            if (!session.TryOpenPasv(out var _, out var port))
            {
                _logger.LogError("PASV: failed to open passive data listener");
                await writer.WriteLineAsync("421 Can't open passive connection");
                return;
            }

            // (h1,h2,h3,h4,p1,p2) - A port = p1 * 256 + p2 
            var h = ipForReply.GetAddressBytes();
            var p1 = port / 256;
            var p2 = port % 256;

            //(192,168,0,15,195,251) -> port 50123
            _logger.LogInformation(
                "Passive mode enabled: ip={ip}, port={port}, reply tuple=({a},{b},{c},{d},{p1},{p2})",
                ipForReply, port, h[0], h[1], h[2], h[3], p1, p2);

            await writer.WriteLineAsync($"227 Entering Passive Mode ({h[0]},{h[1]},{h[2]},{h[3]},{p1},{p2})");
        }

        private async Task HandleList(FtpSession session, string arg, StreamWriter writer)
        {
            RequireLogin(session);

            var data = await session.AcceptDataAsync();
            if (data == null)
            {
                _logger.LogWarning("LIST command aborted: data connection could not be established");
                await writer.WriteLineAsync("425 Can't open data connection");
                return;
            }

            var target = string.IsNullOrWhiteSpace(arg) ? "." : arg;
            var sw = Stopwatch.StartNew();
            int lines = 0;

            await writer.WriteLineAsync("150 Opening data connection for LIST");
            try
            {
                using (data)
                using (var ds = data.GetStream())
                using (var dw = new StreamWriter(ds, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true })
                {
                    foreach (var lineOut in session.BuildUnixList(target))
                    {
                        lines++;
                        await dw.WriteLineAsync(lineOut);
                    }
                }
                sw.Stop();
                _logger.LogInformation(
                    "LIST completed: target=\"{arg}\", items={count}, duration={ms} ms, cwd={cwd}",
                    arg, lines, sw.ElapsedMilliseconds, session.GetFtpCwd());
                await writer.WriteLineAsync("226 Transfer complete");
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogWarning(ex, "LIST failed after {ms} ms", sw.ElapsedMilliseconds);
                await writer.WriteLineAsync("451 Local error in processing");
            }
        }

        private async Task HandleRetr(FtpSession session, string arg, StreamWriter writer)
        {
            RequireLogin(session);
            if (string.IsNullOrWhiteSpace(arg))
            {
                _logger.LogWarning("RETR command rejected: no filename provided");
                await writer.WriteLineAsync("501 Filename required");
                return;
            }

            var path = session.MapPath(arg);
            if (path == null || !File.Exists(path))
            {
                _logger.LogWarning("RETR command failed: file not found \"{arg}\" (mapped path={mapped})", arg, path ?? "(null)");
                await writer.WriteLineAsync("550 File not found");
                return;
            }

            long size = new FileInfo(path).Length;
            var data = await session.AcceptDataAsync();
            if (data == null)
            {
                _logger.LogWarning("RETR command aborted: data connection could not be established");
                await writer.WriteLineAsync("425 Can't open data connection");
                return;
            }

            await writer.WriteLineAsync($"150 Opening data connection for {Path.GetFileName(path)}");

            var sw = Stopwatch.StartNew();
            try
            {
                using (data)
                using (var ds = data.GetStream())
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await fs.CopyToAsync(ds);
                }
                sw.Stop();
                _logger.LogInformation(
                    "File download completed (RETR): \"{name}\", size={size}B, duration={ms} ms, path={path}",
                    Path.GetFileName(path), size, sw.ElapsedMilliseconds, path);
                await writer.WriteLineAsync("226 Transfer complete");
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogWarning(
                    ex,
                    "File download failed (RETR): \"{name}\" after {ms} ms",
                    Path.GetFileName(path), sw.ElapsedMilliseconds);
                await writer.WriteLineAsync("451 Local error in processing");
            }
        }

        private async Task HandleStor(FtpSession session, string arg, StreamWriter writer)
        {
            RequireLogin(session);
            if (string.IsNullOrWhiteSpace(arg))
            {
                _logger.LogWarning("STOR command rejected: no filename provided");
                await writer.WriteLineAsync("501 Filename required");
                return;
            }


            var path = session.MapPath(arg);
            if (path == null)
            {
                _logger.LogWarning("STOR command failed: invalid target path for \"{arg}\"", arg);
                await writer.WriteLineAsync("550 Invalid path");
                return;
            }

            var data = await session.AcceptDataAsync();
            if (data == null)
            {
                _logger.LogWarning("STOR command aborted: data connection could not be established");
                await writer.WriteLineAsync("425 Can't open data connection");
                return;
            }

            await writer.WriteLineAsync("150 Opening data connection for upload");

            var sw = Stopwatch.StartNew();
            try
            {
                using (data)
                using (var ds = data.GetStream())
                using (var fs = new FileStream(path, File.Exists(path) ? FileMode.Truncate : FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await ds.CopyToAsync(fs);
                }
                sw.Stop();
                var newSize = new FileInfo(path).Length;
                _logger.LogInformation(
                    "File upload completed (STOR): \"{name}\", size={size}B, duration={ms} ms, path={path}",
                    Path.GetFileName(path), newSize, sw.ElapsedMilliseconds, path);
                await writer.WriteLineAsync("226 Transfer complete");
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogWarning(
                    ex,
                    "File upload failed (STOR): \"{name}\" after {ms} ms (path={path})",
                    Path.GetFileName(path), sw.ElapsedMilliseconds, path);
                await writer.WriteLineAsync("451 Local error in processing");
            }
        }

        private async Task HandleDele(FtpSession session, string arg, StreamWriter writer)
        {
            RequireLogin(session);
            if (string.IsNullOrWhiteSpace(arg))
            {
                _logger.LogWarning("DELE command rejected: no filename provided");
                await writer.WriteLineAsync("501 Filename required");
                return;
            }

            var path = session.MapPath(arg);
            if (path == null || !File.Exists(path))
            {
                _logger.LogWarning("DELE command failed: file not found \"{arg}\" (mapped path={mapped})", arg, path ?? "(null)");
                await writer.WriteLineAsync("550 File not found");
                return;
            }

            try
            {
                var size = new FileInfo(path).Length;
                File.Delete(path);
                _logger.LogInformation(
                    "File deleted (DELE): \"{name}\", size={size}B, path={path}",
                    Path.GetFileName(path), size, path);
                await writer.WriteLineAsync("250 File deleted");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "File delete failed (DELE): path={path}", path);
                await writer.WriteLineAsync("450 Delete failed");
            }
        }

        private async Task HandleMkd(FtpSession session, string arg, StreamWriter writer)
        {
            RequireLogin(session);

            if (string.IsNullOrWhiteSpace(arg))
            {
                _logger.LogWarning("MKD command rejected: no directory name provided");
                await writer.WriteLineAsync("501 Directory name required");
                return;
            }

            var path = session.MapPath(arg);
            if (path == null)
            {
                _logger.LogWarning("MKD command failed: invalid target path \"{arg}\"", arg);
                await writer.WriteLineAsync("550 Invalid path");
                return;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    _logger.LogInformation("MKD skipped: directory already exists \"{path}\"", path);
                    await writer.WriteLineAsync("550 Directory already exists");
                    return;
                }

                Directory.CreateDirectory(path);
                _logger.LogInformation(
                    "Directory created (MKD): name=\"{name}\", path={path}",
                    Path.GetFileName(path), path);

                await writer.WriteLineAsync($"257 \"{arg}\" directory created");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Directory creation failed (MKD): path={path}", path);
                await writer.WriteLineAsync("550 Create directory failed");
            }
        }

        private async Task HandleRmd(FtpSession session, string arg, StreamWriter writer)
        {
            RequireLogin(session);

            if (string.IsNullOrWhiteSpace(arg))
            {
                _logger.LogWarning("RMD command rejected: no directory name provided");
                await writer.WriteLineAsync("501 Directory name required");
                return;
            }

            var path = session.MapPath(arg);
            if (path == null || !Directory.Exists(path))
            {
                _logger.LogWarning("RMD command failed: directory not found \"{arg}\" (mapped path={mapped})", arg, path ?? "(null)");
                await writer.WriteLineAsync("550 Directory not found");
                return;
            }

            try
            {
                if (Directory.EnumerateFileSystemEntries(path).Any())
                {
                    _logger.LogWarning("RMD command rejected: directory is not empty (path={path})", path);
                    await writer.WriteLineAsync("550 Directory not empty");
                    return;
                }

                Directory.Delete(path, recursive: false);

                _logger.LogInformation("Directory removed (RMD): path={path}", path);
                await writer.WriteLineAsync("250 Directory removed");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Directory removal failed (RMD): path={path}", path);
                await writer.WriteLineAsync("550 Remove directory failed");
            }
        }

        private async Task HandleNoop(StreamWriter writer)
        {
            _logger.LogTrace("NOOP command received");
            await writer.WriteLineAsync("200 NOOP ok");
        }

        private async Task HandleOpts(string arg, StreamWriter writer)
        {
            _logger.LogTrace("OPTS command received with options \"{arg}\" - responding 200", arg);
            await writer.WriteLineAsync("200 OPTS ok");
        }

        private async Task HandleSyst(StreamWriter writer)
        {
            _logger.LogTrace("SYST command received - reporting UNIX L8");
            await writer.WriteLineAsync("215 UNIX Type: L8");
        }

        private async Task HandleType(string arg, StreamWriter writer)
        {
            _logger.LogDebug("TYPE command received: requested type={arg}", arg);
            if (string.Equals(arg, "I", StringComparison.OrdinalIgnoreCase))
                await writer.WriteLineAsync("200 Type set to I");
            else
                await writer.WriteLineAsync("504 Only TYPE I supported");
        }

        private async Task HandleFeat(StreamWriter writer)
        {
            _logger.LogTrace("FEAT command received - reporting supported features: PASV, UTF8");
            await writer.WriteLineAsync("211-Features");
            await writer.WriteLineAsync(" PASV");
            await writer.WriteLineAsync(" UTF8");
            await writer.WriteLineAsync("211 End");
        }

        private async Task HandlePwd(FtpSession session, StreamWriter writer)
        {
            RequireLogin(session);
            _logger.LogDebug("PWD: current working directory is \"{cwd}\"", session.GetFtpCwd());
            await writer.WriteLineAsync($"257 \"{session.GetFtpCwd()}\" is current directory");
        }

        private async Task HandleCwd(FtpSession session, string arg, StreamWriter writer)
        {
            RequireLogin(session);

            var before = session.GetFtpCwd();
            var target = string.IsNullOrWhiteSpace(arg) ? "/" : arg;
            var ok = session.TryChangeDir(target);

            _logger.LogInformation(
                "CWD: changing directory to \"{target}\" -> {ok} (from {before} to {after})",
                target, ok ? "OK" : "FAIL", before, session.GetFtpCwd());

            await writer.WriteLineAsync(ok ? "250 Directory successfully changed" : "550 Failed to change directory");
        }

        private async Task HandleCdup(FtpSession session, StreamWriter writer)
        {
            RequireLogin(session);

            var before = session.GetFtpCwd();
            var ok = session.TryChangeDir("..");

            _logger.LogInformation(
                "CDUP: moving to parent directory -> {ok} (from {before} to {after})",
                ok ? "OK" : "FAIL", before, session.GetFtpCwd());

            await writer.WriteLineAsync(ok ? "200 OK" : "550 Failed");
        }

        private async Task HandleQuit(StreamWriter writer)
        {
            _logger.LogInformation("Client requested to terminate the session (QUIT)");
            await writer.WriteLineAsync("221 Bye");
        }

        private async Task HandleUnknown(string cmd, string arg, StreamWriter writer)
        {
            _logger.LogWarning("Unknown or unsupported FTP command: {cmd} (argument=\"{arg}\")", cmd, arg);
            await writer.WriteLineAsync("502 Command not implemented");
        }



        private static (string cmd, string arg) ParseCommand(string line)
        {
            var idx = line.IndexOf(' ');
            if (idx < 0) return (line.Trim().ToUpperInvariant(), "");
            var cmd = line.Substring(0, idx).Trim().ToUpperInvariant();
            var arg = line.Substring(idx + 1).Trim();
            return (cmd, arg);
        }

        private static void RequireLogin(FtpSession s)
        {
            if (!s.LoggedIn)
                throw new FtpReplyException("530 Please login with USER and PASS");
        }
    }
}
