#region License
/*
 * PayloadData.cs
 *
 * The MIT License
 *
 * Copyright (c) 2012-2016 sta.blockhead
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

using System;

namespace WebSocketSharp
{
    internal struct PayloadData
    {
        #region Private Fields

        private readonly byte[] _data;
        private ushort _code;
        private bool _codeSet;
        private string _reason;
        private bool _reasonSet;

        #endregion

        #region Public Fields

        /// <summary>
        /// Represents the empty payload data.
        /// </summary>
        public static PayloadData Empty { get; } = new PayloadData(1005, string.Empty);

        /// <summary>
        /// Represents the allowable max length.
        /// </summary>
        /// <remarks>
        ///   <para>
        ///   A <see cref="WebSocketException"/> will occur if the payload data length is
        ///   greater than the value of this field.
        ///   </para>
        ///   <para>
        ///   If you would like to change the value, you must set it to a value between
        ///   <c>WebSocket.FragmentLength</c> and <c>Int64.MaxValue</c> inclusive.
        ///   </para>
        /// </remarks>
        public static readonly ulong MaxLength = int.MaxValue;

        #endregion

        #region Internal Constructors

        internal PayloadData(byte[] data)
            : this(data, data.LongLength)
        {
        }

        internal PayloadData(byte[] data, long length)
        {
            _data = data;
            Length = length;
            ExtensionDataLength = 0;

            _code = 0;
            _codeSet = false;

            _reason = string.Empty;
            _reasonSet = false;
        }

        internal PayloadData(ushort code, string reason)
        {
            _code = code;
            _codeSet = true;

            _reason = reason ?? string.Empty;
            _reasonSet = true;

            _data = code.Append(reason);
            Length = _data.LongLength;
            ExtensionDataLength = 0;
        }

        #endregion

        #region Internal Properties

        internal ushort Code
        {
            get
            {
                if (!_codeSet)
                {
                    _code = Length > 1 ? _data.ToUInt16(ByteOrder.Big) : (ushort)1005;
                    _codeSet = true;
                }
                return _code;
            }
        }

        internal long ExtensionDataLength { get; }

        internal bool HasReservedCode => Length > 1 && Code.IsReserved();

        internal string Reason
        {
            get
            {
                if (!_reasonSet)
                {
                    _reason = Length > 2 ? _data.UTF8Decode(2, (int)(Length - 2)) : string.Empty;
                    _reasonSet = true;
                }
                return _reason;
            }
        }

        #endregion

        #region Public Properties

        public ReadOnlyMemory<byte> ApplicationData =>
            new ReadOnlyMemory<byte>(_data, (int)ExtensionDataLength, (int)(Length - ExtensionDataLength));

        public ReadOnlyMemory<byte> ExtensionData =>
            new ReadOnlyMemory<byte>(_data, 0, (int)ExtensionDataLength);

        public byte[] Data => _data;

        public long Length { get; }

        #endregion

        #region Internal Methods

        internal void Mask(byte[] key)
        {
            for (long i = 0; i < Length; i++)
                _data[i] = (byte)(_data[i] ^ key[i % 4]);
        }

        #endregion

        #region Public Methods

        public override string ToString()
        {
            return BitConverter.ToString(_data);
        }

        #endregion
    }
}