using System;
using System.Text;

namespace v232.Launcher.WPF.Models
{
    public class OutPacket
    {
        public byte[] buf = new byte[256];
        public int len;

        public OutPacket(short op)
        {
            this.WriteShort(op);
        }

        public void WriteShort(short s)
        {
            this.Write(new byte[2]
            {
                (byte) s,
                (byte) ((uint) s >> 8)
            });
        }

        public void WriteByte(byte b)
        {
            this.Write(new byte[1] { b });
        }

        public void WriteInt(int i)
        {
            this.Write(new byte[4]
            {
                (byte) i,
                (byte) (i >> 8),
                (byte) (i >> 16),
                (byte) (i >> 24)
            });
        }

        public void WriteString(string str)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(str ?? string.Empty);
            if (bytes.Length > short.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(str), "UTF-8 strings cannot exceed 32,767 bytes.");

            this.WriteShort((short)bytes.Length);
            this.Write(bytes);
        }

        public void Write(byte[] data)
        {
            int len = this.len;
            foreach (byte num in data)
            {
                this.buf[len] = num;
                ++len;
            }
            this.len = len;
        }
    }
}
