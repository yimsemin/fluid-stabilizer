using System;
using System.IO;

namespace FluidStabilizer.Rl
{
    /// <summary>
    /// Shared little-endian, length-prefixed frame IO for the TCP bridges
    /// (<see cref="TrainingServer"/> and <see cref="RemotePolicyController"/>).
    /// Each frame is <c>[u32 length][payload]</c>; scalars little-endian.
    /// </summary>
    internal static class BridgeIo
    {
        public const int MaxFrame = 64 * 1024 * 1024;

        public static byte[] ReadFrame(Stream s)
        {
            var lenBuf = new byte[4];
            if (!ReadExact(s, lenBuf, 4)) return null;
            int len = (int)GetU32(lenBuf, 0);
            if (len < 0 || len > MaxFrame) throw new InvalidDataException($"frame length {len}");
            var buf = new byte[len];
            if (len > 0 && !ReadExact(s, buf, len)) return null;
            return buf;
        }

        public static void WriteFrame(Stream s, byte[] payload)
        {
            var len = new byte[4];
            PutU32(len, 0, (uint)payload.Length);
            s.Write(len, 0, 4);
            s.Write(payload, 0, payload.Length);
            s.Flush();
        }

        public static bool ReadExact(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        public static byte[] PackFloats(float[] v, int prefixByte = -1)
        {
            int head = prefixByte >= 0 ? 1 : 0;
            var b = new byte[head + v.Length * 4];
            if (head == 1) b[0] = (byte)prefixByte;
            for (int i = 0; i < v.Length; i++) PutF32(b, head + i * 4, v[i]);
            return b;
        }

        public static float[] UnpackFloats(byte[] b, int offset, int count)
        {
            var v = new float[count];
            for (int i = 0; i < count; i++) v[i] = GetF32(b, offset + i * 4);
            return v;
        }

        public static void PutU32(byte[] b, int o, uint v) => Write(b, o, BitConverter.GetBytes(v));
        public static void PutI32(byte[] b, int o, int v) => Write(b, o, BitConverter.GetBytes(v));
        public static void PutF32(byte[] b, int o, float v) => Write(b, o, BitConverter.GetBytes(v));

        public static uint GetU32(byte[] b, int o) => BitConverter.ToUInt32(Slice(b, o, 4), 0);
        public static int GetI32(byte[] b, int o) => BitConverter.ToInt32(Slice(b, o, 4), 0);
        public static float GetF32(byte[] b, int o) => BitConverter.ToSingle(Slice(b, o, 4), 0);

        private static void Write(byte[] b, int o, byte[] src)
        {
            if (!BitConverter.IsLittleEndian) Array.Reverse(src);
            Array.Copy(src, 0, b, o, src.Length);
        }

        private static byte[] Slice(byte[] b, int o, int n)
        {
            var t = new byte[n];
            Array.Copy(b, o, t, 0, n);
            if (!BitConverter.IsLittleEndian) Array.Reverse(t);
            return t;
        }
    }
}
