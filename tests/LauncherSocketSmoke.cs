using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using v232.Launcher.WPF.Models;
using v232.Launcher.WPF.Services;

internal static class LauncherSocketSmoke
{
    private static int Main()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string metadataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client.release.json");
        File.WriteAllText(metadataPath,
            "{\"format\":1,\"profile\":\"Online\",\"server\":{\"host\":\"127.0.0.1\",\"port\":" + port + "}}");
        Exception serverError = null;

        var serverThread = new Thread(() =>
        {
            try
            {
                using (TcpClient accepted = listener.AcceptTcpClient())
                using (NetworkStream stream = accepted.GetStream())
                {
                    for (int index = 0; index < 2; index++)
                    {
                        ReadFrame(stream);
                        SendFrame(stream, 1000 + index + 1);
                    }
                }
            }
            catch (Exception ex)
            {
                serverError = ex;
            }
        });
        serverThread.IsBackground = true;
        serverThread.Start();

        try
        {
            var client = new Client();
            if (!client.Connect())
                throw new InvalidOperationException("Client could not connect to loopback server.");

            client.Send(new OutPacket(1));
            InPacket firstPacket = client.Receive();
            firstPacket.readInt();
            int first = firstPacket.readInt();
            client.Send(new OutPacket(2));
            InPacket secondPacket = client.Receive();
            secondPacket.readInt();
            int second = secondPacket.readInt();

            serverThread.Join(5000);
            if (serverError != null)
                throw new InvalidOperationException("Loopback server failed.", serverError);
            if (first != 1001 || second != 1002)
                throw new InvalidOperationException("Sequential receives did not return both responses: " + first + ", " + second + ".");

            Console.WriteLine("PASS: sequential launcher socket receives return both responses.");
            return 0;
        }
        finally
        {
            listener.Stop();
            if (File.Exists(metadataPath))
                File.Delete(metadataPath);
        }
    }

    private static void ReadFrame(NetworkStream stream)
    {
        byte[] header = ReadExactly(stream, 4);
        int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        ReadExactly(stream, length);
    }

    private static void SendFrame(NetworkStream stream, int value)
    {
        byte[] frame =
        {
            0, 0, 0, 4,
            (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)
        };
        stream.Write(frame, 0, frame.Length);
        stream.Flush();
    }

    private static byte[] ReadExactly(NetworkStream stream, int length)
    {
        byte[] bytes = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = stream.Read(bytes, offset, length - offset);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        }
        return bytes;
    }
}
