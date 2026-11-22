// Bounded decoding for the CurseTorrent build of MonoTorrent (MIT).
using System;

namespace MonoTorrent.BEncoding
{
    internal sealed class DecodeBudget
    {
        internal const int MaximumStringBytes = 16 * 1024 * 1024;
        readonly int maximumDepth;
        readonly int maximumObjects;
        readonly long maximumBytes;
        int objects;
        long bytes;
        internal DecodeBudget (int maximumDepth = 64, int maximumObjects = 262144, long maximumBytes = 32 * 1024 * 1024)
        {
            this.maximumDepth = maximumDepth;
            this.maximumObjects = maximumObjects;
            this.maximumBytes = maximumBytes;
        }
        internal void Item (int depth)
        {
            if (depth > maximumDepth || ++objects > maximumObjects)
                throw new BEncodingException ("Bencoded data exceeds the supported nesting or object limit.");
        }
        internal void String (int length)
        {
            if (length < 0 || length > MaximumStringBytes || (bytes += length) > maximumBytes)
                throw new BEncodingException ("Bencoded data exceeds the supported byte budget.");
        }
        internal static int AppendLength (int length, int digit)
        {
            if (length > (MaximumStringBytes - digit) / 10)
                throw new BEncodingException ("Bencoded string exceeds the supported length.");
            return length * 10 + digit;
        }
    }
}
