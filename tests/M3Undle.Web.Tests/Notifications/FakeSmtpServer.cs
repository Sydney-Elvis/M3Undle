using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using IOStream = System.IO.Stream;

namespace M3Undle.Web.Tests.Notifications;

public enum SmtpTlsMode { None, StartTls, Implicit }

public sealed record ReceivedSmtpMessage(string From, IReadOnlyList<string> Recipients, string Data);

/// <summary>
/// A small scripted SMTP server so transport tests observe real protocol behaviour: required TLS, authentication, per-stage
/// reply codes, and the faults a catcher cannot produce (a dropped final acknowledgement, a disconnect after acceptance).
/// </summary>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public SmtpTlsMode Tls { get; init; } = SmtpTlsMode.StartTls;
    public bool AdvertiseStartTls { get; init; } = true;
    public X509Certificate2 Certificate { get; } = CreateCertificate();
    public string? RequiredUser { get; init; } = "mailer";
    public string? RequiredPassword { get; init; } = "p@ss word ";
    public bool RequireAuth { get; init; } = true;
    public string RcptReply { get; set; } = "250 OK";
    public string AuthReply { get; set; } = "235 Authentication successful";
    public string FinalReply { get; set; } = "250 2.0.0 Ok: queued as TEST123";
    public bool DropBeforeFinalReply { get; set; }
    public bool StallBeforeFinalReply { get; set; }
    public bool CloseAfterFinalReply { get; set; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public List<ReceivedSmtpMessage> Messages { get; } = [];
    public List<string> AuthAttempts { get; } = [];
    public bool CredentialsSeenOverPlaintext { get; private set; }
    public TaskCompletionSource DataReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeSmtpServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => HandleAsync(client));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            IOStream stream = client.GetStream();
            var secure = false;
            if (Tls == SmtpTlsMode.Implicit)
            {
                var ssl = new SslStream(stream);
                await ssl.AuthenticateAsServerAsync(Certificate);
                stream = ssl;
                secure = true;
            }

            await WriteAsync(stream, "220 fake.smtp ESMTP ready");
            string? from = null;
            var recipients = new List<string>();

            while (await ReadLineAsync(stream) is { } line)
            {
                var upper = line.ToUpperInvariant();
                if (upper.StartsWith("EHLO") || upper.StartsWith("HELO"))
                {
                    var capabilities = new List<string> { "250-fake.smtp", "250-8BITMIME", "250-SIZE 10485760" };
                    if (!secure && Tls == SmtpTlsMode.StartTls && AdvertiseStartTls)
                        capabilities.Add("250-STARTTLS");
                    if (secure || Tls == SmtpTlsMode.None)
                        capabilities.Add("250-AUTH PLAIN LOGIN");
                    capabilities.Add("250 PIPELINING");
                    foreach (var capability in capabilities)
                        await WriteAsync(stream, capability);
                }
                else if (upper == "STARTTLS")
                {
                    await WriteAsync(stream, "220 Ready to start TLS");
                    var ssl = new SslStream(stream);
                    await ssl.AuthenticateAsServerAsync(Certificate);
                    stream = ssl;
                    secure = true;
                }
                else if (upper.StartsWith("AUTH"))
                {
                    await HandleAuthAsync(stream, line, secure);
                }
                else if (upper.StartsWith("MAIL FROM"))
                {
                    from = Extract(line);
                    recipients.Clear();
                    await WriteAsync(stream, "250 OK");
                }
                else if (upper.StartsWith("RCPT TO"))
                {
                    if (RcptReply.StartsWith('2'))
                        recipients.Add(Extract(line));
                    await WriteAsync(stream, RcptReply);
                }
                else if (upper == "DATA")
                {
                    await WriteAsync(stream, "354 End data with <CR><LF>.<CR><LF>");
                    var data = await ReadDataAsync(stream);
                    DataReceived.TrySetResult();

                    if (DropBeforeFinalReply)
                        return;
                    if (StallBeforeFinalReply)
                    {
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                        return;
                    }

                    if (FinalReply.StartsWith('2'))
                    {
                        lock (Messages)
                            Messages.Add(new ReceivedSmtpMessage(from ?? string.Empty, [.. recipients], data));
                    }

                    await WriteAsync(stream, FinalReply);
                    if (CloseAfterFinalReply)
                        return;
                }
                else if (upper == "QUIT")
                {
                    await WriteAsync(stream, "221 Bye");
                    return;
                }
                else if (upper == "RSET" || upper == "NOOP")
                {
                    await WriteAsync(stream, "250 OK");
                }
                else
                {
                    await WriteAsync(stream, "502 Command not implemented");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task HandleAuthAsync(IOStream stream, string line, bool secure)
    {
        if (!secure)
            CredentialsSeenOverPlaintext = true;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var mechanism = parts.Length > 1 ? parts[1].ToUpperInvariant() : string.Empty;
        string user, password;
        if (mechanism == "PLAIN")
        {
            var encoded = parts.Length > 2 ? parts[2] : null;
            if (encoded is null)
            {
                await WriteAsync(stream, "334 ");
                encoded = await ReadLineAsync(stream) ?? string.Empty;
            }

            var fields = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split('\0');
            user = fields.Length > 1 ? fields[1] : string.Empty;
            password = fields.Length > 2 ? fields[2] : string.Empty;
        }
        else if (mechanism == "LOGIN")
        {
            await WriteAsync(stream, "334 VXNlcm5hbWU6");
            user = Encoding.UTF8.GetString(Convert.FromBase64String(await ReadLineAsync(stream) ?? string.Empty));
            await WriteAsync(stream, "334 UGFzc3dvcmQ6");
            password = Encoding.UTF8.GetString(Convert.FromBase64String(await ReadLineAsync(stream) ?? string.Empty));
        }
        else
        {
            await WriteAsync(stream, "504 Unrecognized authentication type");
            return;
        }

        lock (AuthAttempts)
            AuthAttempts.Add($"{user}\n{password}");
        var ok = AuthReply.StartsWith('2') && user == RequiredUser && password == RequiredPassword;
        await WriteAsync(stream, ok ? AuthReply : (AuthReply.StartsWith('2') ? "535 5.7.8 Authentication credentials invalid" : AuthReply));
    }

    private static string Extract(string line)
    {
        var start = line.IndexOf('<');
        var end = line.IndexOf('>');
        return start >= 0 && end > start ? line[(start + 1)..end] : line;
    }

    private static async Task WriteAsync(IOStream stream, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static async Task<string?> ReadLineAsync(IOStream stream)
    {
        var builder = new StringBuilder();
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer) == 1)
        {
            if (buffer[0] == '\n')
                return builder.ToString().TrimEnd('\r');
            builder.Append((char)buffer[0]);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static async Task<string> ReadDataAsync(IOStream stream)
    {
        var builder = new StringBuilder();
        while (await ReadLineAsync(stream) is { } line)
        {
            if (line == ".")
                break;
            builder.Append(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line).Append("\r\n");
        }

        return builder.ToString();
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        Certificate.Dispose();
    }
}
