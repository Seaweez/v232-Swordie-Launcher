using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using v232.Launcher.WPF.Models;
using v232.Launcher.WPF.Services;

internal static class LauncherFreshTokenSmoke
{
    private const string FreshToken = "fresh-token-from-api";

    private static async Task<int> Main()
    {
        try
        {
            Configs.LocalIP = "MTI3LjAuMC4x";
            MethodInfo refresh = typeof(LoginService).GetMethod("RefreshAuthenticationForLaunchAsync");
            Require(refresh != null, "LoginService must refresh its authentication token before launch.");

            using (var api = new FakeApiServer())
            {
                api.Start();
                var service = new LoginService("smoke-user", "smoke-password")
                {
                    Auth = true,
                    Token = "stale-token"
                };

                var result = (Task<bool>)refresh.Invoke(service, null);
                Require(await result, "The fresh API authentication must succeed.");
                Require(service.Auth, "A successful refresh must leave the launcher authenticated.");
                Require(service.Token == FreshToken, "Launch must use the newly issued token, not the cached token.");
                Require(api.RequestCount == 1, "Refreshing for launch must make exactly one new API authentication request.");
            }

            Console.WriteLine("PASS: launch authentication refreshes the cached token.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error.Message);
            return 1;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class FakeApiServer : IDisposable
    {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 8483);
        private Task worker;

        public int RequestCount { get; private set; }

        public void Start()
        {
            listener.Start();
            worker = Task.Run(ServeOnce);
        }

        private void ServeOnce()
        {
            using (TcpClient client = listener.AcceptTcpClient())
            using (NetworkStream stream = client.GetStream())
            {
                byte[] lengthBytes = ReadExactly(stream, 4);
                int requestLength = (lengthBytes[0] << 24) | (lengthBytes[1] << 16) | (lengthBytes[2] << 8) | lengthBytes[3];
                byte[] request = ReadExactly(stream, requestLength);
                Require(requestLength >= 2, "Authentication request must include an opcode.");
                Require(request[0] == 100 && request[1] == 0, "Authentication request must use the launcher auth opcode.");
                RequestCount++;

                byte[] tokenBytes = Encoding.UTF8.GetBytes(FreshToken);
                using (var payload = new MemoryStream())
                using (var writer = new BinaryWriter(payload, Encoding.UTF8, true))
                {
                    writer.Write((short)100);
                    writer.Write((byte)0);
                    writer.Write((short)tokenBytes.Length);
                    writer.Write(tokenBytes);
                    writer.Write((byte)0);
                    writer.Flush();
                    byte[] response = payload.ToArray();
                    stream.WriteByte((byte)(response.Length >> 24));
                    stream.WriteByte((byte)(response.Length >> 16));
                    stream.WriteByte((byte)(response.Length >> 8));
                    stream.WriteByte((byte)response.Length);
                    stream.Write(response, 0, response.Length);
                }
            }
        }

        public void Dispose()
        {
            listener.Stop();
            if (worker != null)
                worker.GetAwaiter().GetResult();
        }

        private static byte[] ReadExactly(Stream stream, int length)
        {
            byte[] buffer = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = stream.Read(buffer, offset, length - offset);
                if (read == 0)
                    throw new EndOfStreamException("The launcher API socket closed early.");
                offset += read;
            }
            return buffer;
        }
    }
}
