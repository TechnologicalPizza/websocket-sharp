#region License
/*
 * HttpBase.cs
 *
 * The MIT License
 *
 * Copyright (c) 2012-2014 sta.blockhead
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
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Threading;
using WebSocketSharp.Net;

namespace WebSocketSharp
{
    internal abstract class HttpBase
    {
        #region Private Fields

        private NameValueCollection _headers;
        private const int _headersMaxLength = 8192;
        private Version _version;

        #endregion

        #region Internal Fields

        internal MemoryStream EntityBodyData;

        #endregion

        #region Protected Fields

        protected const string CrLf = "\r\n";

        #endregion

        #region Protected Constructors

        protected HttpBase(Version version, NameValueCollection headers)
        {
            _version = version;
            _headers = headers;
        }

        #endregion

        #region Public Properties

        public NameValueCollection Headers => _headers;
        public Version ProtocolVersion => _version;

        public string EntityBody
        {
            get
            {
                if (EntityBodyData == null || EntityBodyData.Length == 0)
                    return string.Empty;

                Encoding enc = null;

                var contentType = _headers["Content-Type"];
                if (contentType != null && contentType.Length > 0)
                    enc = HttpUtility.GetEncoding(contentType);

                string body;
                using (var reader = new StreamReader(EntityBodyData, enc ?? Encoding.UTF8))
                    body = reader.ReadToEnd();

                EntityBodyData.Position = 0;
                return body;
            }
        }

        #endregion

        #region Private Methods

        private static MemoryStream ReadEntityBody(Stream stream, string length)
        {
            if (!long.TryParse(length, out long len))
                throw new ArgumentException("Cannot be parsed.", "length");

            if (len < 0)
                throw new ArgumentOutOfRangeException("length", "Less than zero.");

            return stream.ReadBytes(len);
        }

        private static string[] ReadHeaders(Stream stream, int maxLength)
        {
            using (var tmp = new MemoryStream())
            {
                var cnt = 0;
                void Add(int i)
                {
                    if (i == -1)
                        throw new EndOfStreamException("The header cannot be read from the data source.");

                    tmp.WriteByte((byte)i);
                    cnt++;
                }

                var read = false;
                while (cnt < maxLength)
                {
                    if (stream.ReadByte().EqualsWith('\r', Add) &&
                        stream.ReadByte().EqualsWith('\n', Add) &&
                        stream.ReadByte().EqualsWith('\r', Add) &&
                        stream.ReadByte().EqualsWith('\n', Add))
                    {
                        read = true;
                        break;
                    }
                }

                if (!read)
                    throw new WebSocketException("The length of header part is greater than the max length.");

                return Encoding.UTF8.GetString(tmp.ToArray())
                       .Replace(CrLf + " ", " ")
                       .Replace(CrLf + "\t", " ")
                       .Split(new[] { CrLf }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        #endregion

        #region Protected Methods

        protected static T Read<T>(Stream stream, Func<string[], T> parser, int millisecondsTimeout)
          where T : HttpBase
        {
            var timeout = false;
            var timer = new Timer(
              state =>
              {
                  timeout = true;
                  stream.Close();
              },
              null,
              millisecondsTimeout,
              -1);

            T http = null;
            Exception exception = null;
            try
            {
                http = parser(ReadHeaders(stream, _headersMaxLength));
                var contentLen = http.Headers["Content-Length"];
                if (contentLen != null && contentLen.Length > 0)
                    http.EntityBodyData = ReadEntityBody(stream, contentLen);
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                timer.Change(-1, -1);
                timer.Dispose();
            }

            var msg = timeout
                      ? "A timeout has occurred while reading an HTTP request/response."
                      : exception != null
                        ? "An exception has occurred while reading an HTTP request/response."
                        : null;

            if (msg != null)
                throw new WebSocketException(msg, exception);

            return http;
        }

        #endregion

        #region Public Methods

        public byte[] ToByteArray()
        {
            return Encoding.UTF8.GetBytes(ToString());
        }

        #endregion
    }
}
