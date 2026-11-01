using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Mystic
{
    /// <summary>
    /// Minimal OSC 1.0 codec for the VMC protocol: messages with int (i), float (f) and string (s) arguments, and
    /// bundles (which VMC apps commonly send). OSC is big-endian; strings are null-terminated and padded to 4 bytes.
    /// </summary>
    public static class Osc
    {
        public readonly struct Message
        {
            public readonly string Address;
            public readonly object[] Args; // int, float or string

            public Message(string address, object[] args)
            {
                Address = address;
                Args = args;
            }

            public string S(int i) => i < Args.Length ? Args[i] as string : null;
            public float F(int i) => i < Args.Length && Args[i] is float f ? f : i < Args.Length && Args[i] is int n ? n : 0f;
        }

        // ------------------------------------------------------------------ decoding

        /// <summary>Decodes a packet (a message or a bundle, possibly nested) into messages.</summary>
        public static void Decode(byte[] data, int offset, int length, List<Message> output)
        {
            if (length >= 16 && Matches(data, offset, "#bundle"))
            {
                int pos = offset + 16; // "#bundle\0" + 8-byte time tag
                int end = offset + length;
                while (pos + 4 <= end)
                {
                    int size = ReadInt(data, pos);
                    pos += 4;
                    if (size <= 0 || pos + size > end) break;
                    Decode(data, pos, size, output);
                    pos += size;
                }
                return;
            }

            int p = offset;
            int stop = offset + length;
            string address = ReadString(data, ref p, stop);
            if (address == null || address.Length == 0 || address[0] != '/') return;
            string tags = p < stop ? ReadString(data, ref p, stop) : ",";
            if (tags == null || tags.Length == 0 || tags[0] != ',') return;

            var args = new List<object>(tags.Length - 1);
            for (int i = 1; i < tags.Length; i++)
            {
                switch (tags[i])
                {
                    case 'i':
                        if (p + 4 > stop) return;
                        args.Add(ReadInt(data, p));
                        p += 4;
                        break;
                    case 'f':
                        if (p + 4 > stop) return;
                        args.Add(ReadFloat(data, p));
                        p += 4;
                        break;
                    case 's':
                        var s = ReadString(data, ref p, stop);
                        if (s == null) return;
                        args.Add(s);
                        break;
                    case 'b':
                        if (p + 4 > stop) return;
                        int n = ReadInt(data, p);
                        p += 4 + ((n + 3) & ~3);
                        args.Add(null);
                        break;
                    case 'T': args.Add(1); break;
                    case 'F': args.Add(0); break;
                    default: args.Add(null); break; // N, I and others carry no data
                }
            }
            output.Add(new Message(address, args.ToArray()));
        }

        static bool Matches(byte[] d, int o, string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (d[o + i] != s[i]) return false;
            return true;
        }

        static string ReadString(byte[] d, ref int p, int stop)
        {
            int start = p;
            while (p < stop && d[p] != 0) p++;
            if (p >= stop) return null;
            var s = Encoding.UTF8.GetString(d, start, p - start);
            p = start + ((p - start + 4) & ~3); // skip the terminator and padding
            return s;
        }

        static int ReadInt(byte[] d, int p) => (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];

        static float ReadFloat(byte[] d, int p)
        {
            var b = BitConverter.IsLittleEndian
                ? new[] { d[p + 3], d[p + 2], d[p + 1], d[p] }
                : new[] { d[p], d[p + 1], d[p + 2], d[p + 3] };
            return BitConverter.ToSingle(b, 0);
        }

        // ------------------------------------------------------------------ encoding

        /// <summary>Builds one OSC bundle from messages; reuse it per frame.</summary>
        public sealed class BundleWriter
        {
            readonly MemoryStream _bundle = new MemoryStream(8192);
            readonly MemoryStream _msg = new MemoryStream(256);
            readonly StringBuilder _tags = new StringBuilder(16);
            readonly List<object> _args = new List<object>(8);

            public void Begin()
            {
                _bundle.SetLength(0);
                WriteString(_bundle, "#bundle");
                for (int i = 0; i < 7; i++) _bundle.WriteByte(0);
                _bundle.WriteByte(1); // time tag 1 = "immediately"
            }

            public void Add(string address, params object[] args)
            {
                _msg.SetLength(0);
                _tags.Clear().Append(',');
                _args.Clear();
                foreach (var a in args)
                {
                    _tags.Append(a is int ? 'i' : a is float ? 'f' : 's');
                    _args.Add(a);
                }
                WriteString(_msg, address);
                WriteString(_msg, _tags.ToString());
                foreach (var a in _args)
                {
                    if (a is int i) WriteInt(_msg, i);
                    else if (a is float f) WriteFloat(_msg, f);
                    else WriteString(_msg, a as string ?? "");
                }
                WriteInt(_bundle, (int)_msg.Length);
                _msg.WriteTo(_bundle);
            }

            public int Length => (int)_bundle.Length;
            public byte[] Buffer => _bundle.GetBuffer();
        }

        static void WriteString(Stream s, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            s.Write(bytes, 0, bytes.Length);
            int pad = 4 - (bytes.Length % 4); // at least one null terminator
            for (int i = 0; i < pad; i++) s.WriteByte(0);
        }

        static void WriteInt(Stream s, int v)
        {
            s.WriteByte((byte)(v >> 24));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        static void WriteFloat(Stream s, float v)
        {
            var b = BitConverter.GetBytes(v);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            s.Write(b, 0, 4);
        }
    }
}
