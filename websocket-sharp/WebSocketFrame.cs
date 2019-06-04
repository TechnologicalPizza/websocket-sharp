#region License
/*
 * WebSocketFrame.cs
 *
 * The MIT License
 *
 * Copyright (c) 2012-2015 sta.blockhead
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 */
#endregion

#region Contributors
/*
 * Contributors:
 * - Chris Swiedler
 */
#endregion

using System;
using System.IO;
using System.Text;
using WebSocketSharp.Memory;

namespace WebSocketSharp
{
    internal struct WebSocketFrame
    {
        #region Private Fields

        private byte[] _extPayloadLength;
        private Fin _fin;
        private Mask _mask;
        private byte[] _maskingKey;
        private OpCode _opcode;
        private PayloadData _payloadData;
        private byte _payloadLength;
        private Rsv _rsv1;
        private Rsv _rsv2;
        private Rsv _rsv3;

        #endregion

        #region Internal Fields

        /// <summary>
        /// Represents the ping frame without the payload data as an array of <see cref="byte"/>.
        /// </summary>
        /// <remarks>
        /// The value of this field is created from a non masked frame, so it can only be used to
        /// send a ping from a server.
        /// </remarks>
        internal static readonly byte[] EmptyPingBytes;

        #endregion

        #region Static Constructor

        static WebSocketFrame()
        {
            using (var tmp = CreatePingFrame(false).ToMemory())
                EmptyPingBytes = tmp.ToArray();
        }

        #endregion

        #region Internal Constructors

        internal WebSocketFrame(OpCode opcode, PayloadData payloadData, bool mask)
           : this(Fin.Final, opcode, payloadData, false, mask)
        {
        }

        internal WebSocketFrame(Fin fin, OpCode opcode, byte[] data, bool compressed, bool mask)
            : this(fin, opcode, new PayloadData(data), compressed, mask)
        {
        }

        internal WebSocketFrame(
            Fin fin, OpCode opcode, PayloadData payloadData, bool compressed, bool mask)
        {
            _fin = fin;
            _rsv1 = opcode.IsData() && compressed ? Rsv.On : Rsv.Off;
            _rsv2 = Rsv.Off;
            _rsv3 = Rsv.Off;
            _opcode = opcode;

            var len = payloadData.Length;
            if (len < 126)
            {
                _payloadLength = (byte)len;
                _extPayloadLength = Array.Empty<byte>();
            }
            else if (len < 0x010000)
            {
                _payloadLength = 126;
                _extPayloadLength = ((ushort)len).ToByteArray(ByteOrder.Big);
            }
            else
            {
                _payloadLength = 127;
                _extPayloadLength = len.ToByteArray(ByteOrder.Big);
            }

            if (mask)
            {
                _mask = Mask.On;
                _maskingKey = CreateMaskingKey();
                payloadData.Mask(_maskingKey);
            }
            else
            {
                _mask = Mask.Off;
                _maskingKey = Array.Empty<byte>();
            }

            _payloadData = payloadData;
        }

        #endregion

        #region Internal Properties

        internal int ExtendedPayloadLengthCount => _payloadLength < 126 ? 0 : (_payloadLength == 126 ? 2 : 8);

        internal ulong FullPayloadLength
        {
            get => _payloadLength < 126
                    ? _payloadLength
                    : _payloadLength == 126
                        ? _extPayloadLength.ToUInt16(ByteOrder.Big)
                        : _extPayloadLength.ToUInt64(ByteOrder.Big);
        }

        #endregion

        #region Public Properties

        public bool IsBinary => _opcode == OpCode.Binary;
        public bool IsClose => _opcode == OpCode.Close;
        public bool IsCompressed => _rsv1 == Rsv.On;
        public bool IsContinuation => _opcode == OpCode.Cont;
        public bool IsControl => _opcode >= OpCode.Close;
        public bool IsData => _opcode == OpCode.Text || _opcode == OpCode.Binary;
        public bool IsPing => _opcode == OpCode.Ping;
        public bool IsPong => _opcode == OpCode.Pong;
        public bool IsText => _opcode == OpCode.Text;

        public Rsv Rsv1 => _rsv1;
        public Rsv Rsv2 => _rsv2;
        public Rsv Rsv3 => _rsv3;

        public Fin Fin => _fin;
        public bool IsFinal => _fin == Fin.Final;
        public bool IsFragment => _fin == Fin.More || _opcode == OpCode.Cont;

        public Mask Mask => _mask;
        public bool IsMasked => _mask == Mask.On;
        public byte[] MaskingKey => _maskingKey;

        public OpCode Opcode => _opcode;

        public byte[] ExtendedPayloadLength => _extPayloadLength;

        public PayloadData PayloadData => _payloadData;
        public byte PayloadLength => _payloadLength;

        public long Length => 2 + _extPayloadLength.Length + _maskingKey.Length + _payloadData.Length;

        #endregion

        #region Private Methods

        private static byte[] CreateMaskingKey()
        {
            var key = new byte[4];
            WebSocket.RandomNumber.GetBytes(key);
            return key;
        }

        private static string Dump(WebSocketFrame frame)
        {
            var len = frame.Length;
            var cnt = (long)(len / 4);
            var rem = (int)(len % 4);

            int cntDigit;
            string cntFmt;
            if (cnt < 10000)
            {
                cntDigit = 4;
                cntFmt = "{0,4}";
            }
            else if (cnt < 0x010000)
            {
                cntDigit = 4;
                cntFmt = "{0,4:X}";
            }
            else if (cnt < 0x0100000000)
            {
                cntDigit = 8;
                cntFmt = "{0,8:X}";
            }
            else
            {
                cntDigit = 16;
                cntFmt = "{0,16:X}";
            }

            var spFmt = string.Format("{{0,{0}}}", cntDigit);
            var headerFmt = string.Format(@"
{0} 01234567 89ABCDEF 01234567 89ABCDEF
{0}+--------+--------+--------+--------+\n", spFmt);
            var lineFmt = string.Format("{0}|{{1,8}} {{2,8}} {{3,8}} {{4,8}}|\n", cntFmt);
            var footerFmt = string.Format("{0}+--------+--------+--------+--------+", spFmt);

            var output = new StringBuilder(64);
            long lineCnt = 0;
            void LinePrinter(
                string arg0, string arg1, string arg2, string arg3)
            {
                output.AppendFormat(lineFmt, ++lineCnt, arg0, arg1, arg2, arg3);
            }

            output.AppendFormat(headerFmt, string.Empty);

            using (var frameBytes = frame.ToMemory())
            {
                byte[] buffer = frameBytes.GetBuffer();
                for (long i = 0; i <= cnt; i++)
                {
                    long j = i * 4;
                    if (i < cnt)
                    {
                        LinePrinter(
                            Convert.ToString(buffer[j], 2).PadLeft(8, '0'),
                            Convert.ToString(buffer[j + 1], 2).PadLeft(8, '0'),
                            Convert.ToString(buffer[j + 2], 2).PadLeft(8, '0'),
                            Convert.ToString(buffer[j + 3], 2).PadLeft(8, '0'));
                        continue;
                    }

                    if (rem > 0)
                        LinePrinter(
                            Convert.ToString(buffer[j], 2).PadLeft(8, '0'),
                            rem >= 2 ? Convert.ToString(buffer[j + 1], 2).PadLeft(8, '0') : string.Empty,
                            rem == 3 ? Convert.ToString(buffer[j + 2], 2).PadLeft(8, '0') : string.Empty,
                            string.Empty);
                }

                output.AppendFormat(footerFmt, string.Empty);
                return output.ToString();
            }
        }

        private static string Print(WebSocketFrame frame)
        {
            // Payload Length
            var payloadLen = frame._payloadLength;

            // Extended Payload Length
            var extPayloadLen = payloadLen > 125 ? frame.FullPayloadLength.ToString() : string.Empty;

            // Masking Key
            var maskingKey = BitConverter.ToString(frame._maskingKey);

            // Payload Data
            var payload = payloadLen == 0
                ? string.Empty
                : payloadLen > 125
                  ? "---"
                  : frame.IsText && !(frame.IsFragment || frame.IsMasked || frame.IsCompressed)
                    ? frame._payloadData.ApplicationData.ToArray().UTF8Decode()
                    : frame._payloadData.ToString();

            var fmt = @"
                    FIN: {0}
                   RSV1: {1}
                   RSV2: {2}
                   RSV3: {3}
                 Opcode: {4}
                   MASK: {5}
         Payload Length: {6}
Extended Payload Length: {7}
            Masking Key: {8}
           Payload Data: {9}";

            return string.Format(
                fmt,
                frame._fin,
                frame._rsv1,
                frame._rsv2,
                frame._rsv3,
                frame._opcode,
                frame._mask,
                payloadLen,
                extPayloadLen,
                maskingKey,
                payload);
        }

        private static WebSocketFrame ReadHeader(Stream header)
        {
            if (!header.TryReadByte(out byte first) ||
                !header.TryReadByte(out byte second))
                throw new WebSocketException("The header of a frame cannot be read from the stream.");

            // FIN
            var fin = (first & 0x80) == 0x80 ? Fin.Final : Fin.More;

            // RSV1
            var rsv1 = (first & 0x40) == 0x40 ? Rsv.On : Rsv.Off;

            // RSV2
            var rsv2 = (first & 0x20) == 0x20 ? Rsv.On : Rsv.Off;

            // RSV3
            var rsv3 = (first & 0x10) == 0x10 ? Rsv.On : Rsv.Off;

            // Opcode
            var opcode = (byte)(first & 0x0f);

            // MASK
            var mask = (second & 0x80) == 0x80 ? Mask.On : Mask.Off;

            // Payload Length
            var payloadLen = (byte)(second & 0x7f);

            var err = !opcode.IsSupported()
                      ? "An unsupported opcode."
                      : !opcode.IsData() && rsv1 == Rsv.On
                        ? "A non data frame is compressed."
                        : opcode.IsControl() && fin == Fin.More
                          ? "A control frame is fragmented."
                          : opcode.IsControl() && payloadLen > 125
                            ? "A control frame has a long payload length."
                            : null;

            if (err != null)
                throw new WebSocketException(CloseStatusCode.ProtocolError, err);

            var frame = new WebSocketFrame();
            frame._fin = fin;
            frame._rsv1 = rsv1;
            frame._rsv2 = rsv2;
            frame._rsv3 = rsv3;
            frame._opcode = (OpCode)opcode;
            frame._mask = mask;
            frame._payloadLength = payloadLen;
            return frame;
        }

        private static void ReadExtendedPayloadLength(
            Stream stream, ref WebSocketFrame frame)
        {
            var len = frame.ExtendedPayloadLengthCount;
            if (len == 0)
            {
                frame._extPayloadLength = Array.Empty<byte>();
                return;
            }

            byte[] data = new byte[len];
            stream.ReadBytes(data, 0, data.Length);

            if (data.Length != len)
                throw new WebSocketException(
                  "The extended payload length of a frame cannot be read from the stream.");

            frame._extPayloadLength = data;
        }

        private static void ReadMaskingKey(Stream stream, ref WebSocketFrame frame)
        {
            var len = frame.IsMasked ? 4 : 0;
            if (len == 0)
            {
                frame._maskingKey = Array.Empty<byte>();
                return;
            }

            byte[] data = new byte[len];
            if (!stream.ReadBytes(data, 0, data.Length))
                throw new WebSocketException("The masking key of a frame cannot be read from the stream.");

            frame._maskingKey = data;
        }

        private static void ReadPayloadData(Stream stream, ref WebSocketFrame frame)
        {
            var ulen = frame.FullPayloadLength;
            if (ulen == 0)
            {
                frame._payloadData = PayloadData.Empty;
                return;
            }

            if (ulen > PayloadData.MaxLength)
                throw new WebSocketException(CloseStatusCode.TooBig, "A frame has a long payload length.");

            byte[] data = new byte[(long)ulen];
            if (!stream.ReadBytes(data, 0, data.Length))
                throw new WebSocketException(
                      "The payload data of a frame cannot be read from the stream.");

            frame._payloadData = new PayloadData(data, data.Length);
        }

        #endregion

        #region Internal Methods

        internal static WebSocketFrame CreateCloseFrame(
            PayloadData payloadData, bool mask)
        {
            return new WebSocketFrame(
                Fin.Final, OpCode.Close, payloadData, false, mask);
        }

        internal static WebSocketFrame CreatePingFrame(bool mask)
        {
            return new WebSocketFrame(
                Fin.Final, OpCode.Ping, PayloadData.Empty, false, mask);
        }

        internal static WebSocketFrame CreatePingFrame(byte[] data, bool mask)
        {
            return new WebSocketFrame(
                Fin.Final, OpCode.Ping, new PayloadData(data), false, mask);
        }

        internal static WebSocketFrame CreatePongFrame(
            PayloadData payloadData, bool mask)
        {
            return new WebSocketFrame(
                Fin.Final, OpCode.Pong, payloadData, false, mask);
        }

        internal static WebSocketFrame ReadFrame(Stream stream, bool unmask)
        {
            var frame = ReadHeader(stream);
            ReadExtendedPayloadLength(stream, ref frame);
            ReadMaskingKey(stream, ref frame);
            ReadPayloadData(stream, ref frame);

            if (unmask)
                frame.Unmask();
            return frame;
        }

        internal void Unmask()
        {
            if (_mask == Mask.Off)
                return;

            _mask = Mask.Off;
            _payloadData.Mask(_maskingKey);
            _maskingKey = Array.Empty<byte>();
        }

        #endregion

        #region Public Methods

        public void Print(bool dumped)
        {
            Console.WriteLine(dumped ? Dump(this) : Print(this));
        }

        public string PrintToString(bool dumped)
        {
            return dumped ? Dump(this) : Print(this);
        }

        public RecyclableMemoryStream ToMemory()
        {
            var buff = RecyclableMemoryManager.Shared.GetStream();
            try
            {
                var header = (int)_fin;
                header = (header << 1) + (int)_rsv1;
                header = (header << 1) + (int)_rsv2;
                header = (header << 1) + (int)_rsv3;
                header = (header << 4) + (int)_opcode;
                header = (header << 1) + (int)_mask;
                header = (header << 7) + _payloadLength;

                Span<byte> headerBytes = stackalloc byte[2];
                headerBytes.Write((ushort)header, ByteOrder.Big);
                buff.Write(headerBytes);

                if (_payloadLength > 125)
                    buff.Write(_extPayloadLength, 0, _payloadLength == 126 ? 2 : 8);

                if (_mask == Mask.On)
                    buff.Write(_maskingKey, 0, 4);

                if (_payloadLength > 0)
                    buff.Write(_payloadData.Data.Span);

                buff.Position = 0;
                return buff;
            }
            catch
            {
                buff.Dispose();
                throw;
            }
        }

        public override string ToString()
        {
            using (var tmp = ToMemory())
            {
                byte[] buffer = tmp.GetBuffer();
                var builder = new StringBuilder();
                int charCount = (int)tmp.Length * 3;

                int index = 0;
                for (int i = 0; i < charCount; i += 3)
                {
                    byte b = buffer[index++];
                    builder[i] = GetHexValue(b / 16);
                    builder[i + 1] = GetHexValue(b % 16);
                    builder[i + 2] = '-';
                }

                return builder.ToString(0, builder.Length - 1);
            }
        }

        private static char GetHexValue(int i)
        {
            if (i < 0 || i >= 16)
                throw new ArgumentOutOfRangeException(nameof(i));

            if (i < 10)
                return (char)(i + '0');
            return (char)(i - 10 + 'A');
        }

        #endregion
    }
}
