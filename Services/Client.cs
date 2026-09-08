using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using v232.Launcher.WPF.Models;

namespace v232.Launcher.WPF.Services
{
    public class Client
    {
        private static ManualResetEvent connectDone = new ManualResetEvent(false);
        private static ManualResetEvent sendDone = new ManualResetEvent(false);
        private static ManualResetEvent receiveDone = new ManualResetEvent(false);

        private int PORT = Configs.APIServerPort;
        private string HOST = Configs.GetServerIP();
        public Socket socket;
        private bool connected;

        public bool Connect()
        {
            try
            {
                this.PORT = Configs.APIServerPort;
                this.HOST = Configs.GetServerIP();
                Client.connectDone.Reset();

                IPAddress[] addresses = Dns.GetHostAddresses(this.HOST);
                if (addresses.Length == 0)
                {
                    throw new Exception("No IP addresses found for the specified host.");
                }

                IPAddress address = addresses[0];
                IPEndPoint ipEndPoint = new IPEndPoint(address, this.PORT);

                this.socket?.Close();
                this.socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                this.socket.NoDelay = true;
                this.socket.SendTimeout = 10000;
                this.socket.ReceiveTimeout = 15000;
                this.socket.BeginConnect((EndPoint)ipEndPoint, new AsyncCallback(this.ConnectCallback), (object)this.socket);

                if (!Client.connectDone.WaitOne(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Connect timeout.");

                if (this.socket == null || !this.socket.Connected)
                    throw new SocketException((int)SocketError.NotConnected);

                connected = true;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                connected = false;
                try { this.socket?.Close(); } catch { }
                return false;
            }
        }

        public bool IsConnected()
        {
            return connected;
        }

        public void Disconnect()
        {
            try
            {
                connected = false;
                this.socket?.Close();
            }
            catch { }
        }

        private void ConnectCallback(IAsyncResult ar)
        {
            try
            {
                Socket asyncState = (Socket)ar.AsyncState;
                asyncState.EndConnect(ar);
                Console.WriteLine("Socket connected to {0}", (object)asyncState.RemoteEndPoint.ToString());
                Client.connectDone.Set();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        public void Send(OutPacket outPacket)
        {
            if (this.socket == null || !this.socket.Connected)
                throw new InvalidOperationException("The launcher socket is not connected.");

            int len = outPacket.len;
            byte[] numArray = new byte[4]
            {
                (byte) 0,
                (byte) 0,
                (byte) 0,
                (byte) len
            };
            numArray[2] = (byte)(len >> 8);
            numArray[1] = (byte)(len >> 16);
            numArray[0] = (byte)(len >> 24);
            byte[] buffer = new byte[4 + len];
            for (int index = 0; index < numArray.Length; ++index)
                buffer[index] = numArray[index];
            for (int length = numArray.Length; length < len + 4; ++length)
                buffer[length] = outPacket.buf[length - 4];

            int sent = 0;
            while (sent < buffer.Length)
            {
                int written = this.socket.Send(buffer, sent, buffer.Length - sent, SocketFlags.None);
                if (written <= 0)
                    throw new IOException("The launcher socket closed while sending a packet.");
                sent += written;
            }
        }

        private void SendCallback(IAsyncResult ar)
        {
            try
            {
                Console.WriteLine("Sent {0} bytes to server.", (object)((Socket)ar.AsyncState).EndSend(ar));
                Client.sendDone.Set();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        public InPacket Receive()
        {
            if (this.socket == null || !this.socket.Connected)
                throw new InvalidOperationException("The launcher socket is not connected.");

            byte[] header = ReceiveExactly(4);
            int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            if (length < 0 || length > 252)
                throw new InvalidDataException("Invalid launcher packet length: " + length);

            byte[] payload = ReceiveExactly(length);
            InPacket inPacket = new InPacket();
            Buffer.BlockCopy(header, 0, inPacket.bufData, 0, header.Length);
            Buffer.BlockCopy(payload, 0, inPacket.bufData, header.Length, payload.Length);
            inPacket.len = length;
            inPacket.curLen = header.Length + payload.Length;
            return inPacket;
        }

        private byte[] ReceiveExactly(int length)
        {
            byte[] bytes = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = this.socket.Receive(bytes, offset, length - offset, SocketFlags.None);
                if (read == 0)
                    throw new IOException("The launcher socket closed while receiving a packet.");
                offset += read;
            }
            return bytes;
        }

        private void ReceiveCallback(IAsyncResult ar)
        {
            try
            {
                InPacket asyncState = (InPacket)ar.AsyncState;
                int num = this.socket.EndReceive(ar);
                if (asyncState.len == -1)
                {
                    if (num + asyncState.lenBufPtr >= 4)
                    {
                        for (int index = 0; index < 4 - asyncState.lenBufPtr; ++index)
                            asyncState.lenBuf[index - asyncState.lenBufPtr] = asyncState.buf[index];
                        asyncState.len = (int)asyncState.buf[3] + ((int)asyncState.buf[2] << 8) + ((int)asyncState.buf[1] << 16) + ((int)asyncState.buf[0] << 24);
                    }
                    else
                    {
                        for (int index = 0; index < num; ++index)
                            asyncState.lenBuf[index + asyncState.lenBufPtr] = asyncState.buf[index];
                        asyncState.lenBufPtr += num;
                    }
                }
                if (num > 0)
                {
                    for (int index = 0; index < num; ++index)
                        asyncState.bufData[index + asyncState.curLen] = asyncState.buf[index];
                    asyncState.curLen += num;
                    if (asyncState.curLen < asyncState.len)
                        this.socket.BeginReceive(asyncState.buf, 0, 256, SocketFlags.None, new AsyncCallback(this.ReceiveCallback), (object)asyncState);
                    else
                        Client.receiveDone.Set();
                }
                else
                    Client.receiveDone.Set();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }
    }
}
