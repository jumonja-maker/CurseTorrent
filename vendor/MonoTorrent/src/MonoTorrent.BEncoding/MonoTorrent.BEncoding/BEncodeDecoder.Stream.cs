//
// BEncodeDecoder.Stream.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2006 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.IO;
using System.Security.Cryptography;

namespace MonoTorrent.BEncoding
{
    static partial class BEncodeDecoder
    {
        static readonly BEncodedString InfoKey = new BEncodedString ("info");

        public static BEncodedValue Decode (Stream reader, bool strictDecoding)
            => Decode (reader, strictDecoding, reader.ReadByte (), new DecodeBudget (), 0);

        public static (BEncodedDictionary torrent, RawInfoHashes infohashes) DecodeTorrent (Stream reader)
        {
            var budget = new DecodeBudget ();
            budget.Item (0);
            var torrent = new BEncodedDictionary ();
            if (reader.ReadByte () != 'd')
                throw new BEncodingException ("Invalid data found. Aborting"); // Remove the leading 'd'

            int read;
            byte[]? infohashSHA1 = null;
            byte[]? infohashSHA256 = null;
            while ((read = reader.ReadByte ()) != -1) {
                if (read == 'e')
                    return (torrent, new RawInfoHashes (infohashSHA1, infohashSHA256));

                if (read < '0' || read > '9')
                    throw new BEncodingException ("Invalid key length");

                BEncodedValue value;
                var key = (BEncodedString) Decode (reader, false, read, budget, 1);         // keys have to be BEncoded strings

                if ((read = reader.ReadByte ()) == 'd') {
                    if (InfoKey.Equals (key)) {
                        using var sha1Reader = new HashingReader (reader, (byte) 'd', SHA1.Create ());
                        using var sha256Reader = new HashingReader (sha1Reader, (byte) 'd', SHA256.Create ());
                        budget.Item (1);
                        value = DecodeDictionary (sha256Reader, false, budget, 1);
                        infohashSHA1 = sha1Reader.TransformFinalBlock ();
                        infohashSHA256 = sha256Reader.TransformFinalBlock ();
                    } else {
                        budget.Item (1);
                        value = DecodeDictionary (reader, false, budget, 1);
                    }
                } else {
                    value = Decode (reader, false, read, budget, 1);
                }
                torrent.Add (key, value);
            }

            throw new BEncodingException ("Invalid data found. Aborting");
        }

        internal static BEncodedValue Decode (Stream reader, bool strictDecoding, int read, DecodeBudget budget, int depth)
        {
            budget.Item (depth);
            switch (read) {
                case 'i':
                    return DecodeNumber (reader);

                case 'd':
                    return DecodeDictionary (reader, strictDecoding, budget, depth);

                case 'l':
                    return DecodeList (reader, strictDecoding, budget, depth);

                case '1':
                case '2':
                case '3':
                case '4':
                case '5':
                case '6':
                case '7':
                case '8':
                case '9':
                case '0':
                    return DecodeString (reader, read - '0', budget);

                default:
                    throw new BEncodingException ("Could not find what value to decode");
            }
        }

        static BEncodedDictionary DecodeDictionary (Stream reader, bool strictDecoding, DecodeBudget budget, int depth)
        {
            int read;
            BEncodedString? oldkey = null;
            var dictionary = new BEncodedDictionary ();
            while ((read = reader.ReadByte ()) != -1) {
                if (read == 'e')
                    return dictionary;

                if (read < '0' || read > '9')
                    throw new BEncodingException ("Invalid key length");

                budget.Item (depth + 1);
                var key = DecodeString (reader, read - '0', budget);         // keys have to be BEncoded strings

                if (oldkey != null && oldkey.CompareTo (key) > 0)
                    if (strictDecoding)
                        throw new BEncodingException (
                            $"Illegal BEncodedDictionary. The attributes are not ordered correctly. Old key: {oldkey}, New key: {key}");

                oldkey = key;
                var value = Decode (reader, strictDecoding, reader.ReadByte (), budget, depth + 1);                     // the value is a BEncoded value
                dictionary.Add (key, value);
            }

            throw new BEncodingException ("Invalid data found. Aborting");
        }

        static BEncodedList DecodeList (Stream reader, bool strictDecoding, DecodeBudget budget, int depth)
        {
            var list = new BEncodedList ();
            int read;
            while ((read = reader.ReadByte ()) != -1) {
                if (read == 'e')
                    return list;
                list.Add (Decode (reader, strictDecoding, read, budget, depth + 1));
            }

            throw new BEncodingException ("Invalid data found. Aborting");
        }

        static BEncodedNumber DecodeNumber (Stream reader)
        {
            int val = reader.ReadByte ();
            bool negative = val == '-';
            if (negative) val = reader.ReadByte ();
            ulong result = 0;
            ulong limit = negative ? 1UL << 63 : long.MaxValue;
            bool any = false;
            while (val != -1) {
                if (val == 'e') {
                    if (!any) throw new BEncodingException ("An integer must contain digits.");
                    return negative ? (result == (1UL << 63) ? long.MinValue : -(long) result) : (long) result;
                }
                if (val < '0' || val > '9' || result > (limit - (ulong) (val - '0')) / 10)
                    throw new BEncodingException ("Bencoded integer is invalid or outside Int64 range.");
                result = result * 10 + (ulong) (val - '0');
                any = true;
                val = reader.ReadByte ();
            }
            throw new BEncodingException ("Incomplete integer.");
        }

        static BEncodedString DecodeString (Stream reader, int length, DecodeBudget budget)
        {
            int read;
            while ((read = reader.ReadByte ()) != -1) {
                if (read == ':') {
                    budget.String (length);
                    var bytes = new byte[length];
                    int total = 0;
                    while (total < length) {
                        int count = reader.Read (bytes, total, length - total);
                        if (count == 0) throw new BEncodingException ("Couldn't decode string");
                        total += count;
                    }
                    return BEncodedString.FromMemory (bytes);
                }

                if (read < '0' || read > '9')
                    throw new BEncodingException ($"Invalid BEncodedString. Length was '{length}' instead of a number");
                length = DecodeBudget.AppendLength (length, read - '0');
            }

            throw new BEncodingException ("Invalid data found. Aborting");
        }
    }
}
