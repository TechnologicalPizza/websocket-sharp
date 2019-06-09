#region License
/*
 * MessageEventArgs.cs
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
    /// <summary>
    /// Represents the event data for the <see cref="WebSocket.OnMessage"/> event.
    /// </summary>
    /// <remarks>
    ///   <para>
    ///   That event occurs when the <see cref="WebSocket"/> receives
    ///   a message or a ping if the <see cref="WebSocket.EmitOnPing"/>
    ///   property is set to <c>true</c>.
    ///   </para>
    ///   <para>
    ///   If you would like to get the message data, you should access
    ///   the <see cref="Data"/> or <see cref="RawData"/> property.
    ///   </para>
    /// </remarks>
    public readonly struct MessageEvent
    {
        #region Internal Constructors

        internal MessageEvent(WebSocketFrame frame)
        {
            Opcode = frame.Opcode;
            RawData = frame.PayloadData.ApplicationData;
        }

        internal MessageEvent(OpCode opcode, ReadOnlyMemory<byte> rawData)
        {
            if ((ulong)rawData.Length > PayloadData.MaxLength)
                throw new WebSocketException(CloseStatusCode.TooBig);

            Opcode = opcode;
            RawData = rawData;
        }

        #endregion

        #region Internal Properties

        /// <summary>
        /// Gets the opcode for the message.
        /// </summary>
        /// <value>
        /// <see cref="OpCode.Text"/>, <see cref="OpCode.Binary"/>,
        /// or <see cref="OpCode.Ping"/>.
        /// </value>
        internal OpCode Opcode { get; }

        #endregion

        #region Public Properties

        /// <summary>
        /// Gets the message data as <see cref="ReadOnlyMemory{byte}"/>.
        /// </summary>
        /// <value>
        /// An array of <see cref="byte"/> that represents the message data.
        /// </value>
        public ReadOnlyMemory<byte> RawData { get; }

        /// <summary>
        /// Gets a value indicating whether the message type is binary.
        /// </summary>
        /// <value>
        /// <c>true</c> if the message type is binary; otherwise, <c>false</c>.
        /// </value>
        public bool IsBinary => Opcode == OpCode.Binary;

        /// <summary>
        /// Gets a value indicating whether the message type is ping.
        /// </summary>
        /// <value>
        /// <c>true</c> if the message type is ping; otherwise, <c>false</c>.
        /// </value>
        public bool IsPing => Opcode == OpCode.Ping;

        /// <summary>
        /// Gets a value indicating whether the message type is text.
        /// </summary>
        /// <value>
        /// <c>true</c> if the message type is text; otherwise, <c>false</c>.
        /// </value>
        public bool IsText => Opcode == OpCode.Text;

        #endregion
    }
}