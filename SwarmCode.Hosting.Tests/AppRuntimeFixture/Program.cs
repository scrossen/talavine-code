using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AppRuntimeFixture;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args[0] == "exit") return int.Parse(args[1]);
        if (args[0] == "sleep") { await Task.Delay(TimeSpan.FromMinutes(5)); return 0; }
        if (args[0] == "write") { await File.WriteAllTextAsync(args[1], args[2]); return 0; }
        if (args[0] != "http") return 2;
        await Task.Delay(int.Parse(args[2]));
        var listener = new TcpListener(IPAddress.Loopback, int.Parse(args[1]));
        listener.Start();
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var bytes = new byte[8192];
            try
            {
                await stream.ReadAsync(bytes);
                const string body = "<!doctype html><h1>SwarmCode app fixture</h1>\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}"));
            }
            catch (IOException) { }
        }
    }
}
