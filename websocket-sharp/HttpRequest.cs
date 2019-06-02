#region License
/*
 * HttpRequest.cs
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
 * - David Burhans
 */
#endregion

using System;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using WebSocketSharp.Memory;
using WebSocketSharp.Net;

namespace WebSocketSharp
{
    internal class HttpRequest : HttpBase
    {
        #region Private Fields

        private CookieCollection _cookies;
        private string _method;
        private string _uri;

        #endregion

        #region Private Constructors

        private HttpRequest(string method, string uri, Version version, NameValueCollection headers)
            : base(version, headers)
        {
            _method = method;
            _uri = uri;
        }

        #endregion

        #region Internal Constructors

        internal HttpRequest(string method, string uri)
          : this(method, uri, HttpVersion.Version11, new NameValueCollection())
        {
            Headers["User-Agent"] = "websocket-sharp/1.0";
        }

        #endregion

        #region Public Properties

        public AuthenticationResponse AuthenticationResponse
        {
            get
            {
                var res = Headers["Authorization"];
                return res != null && res.Length > 0
                       ? AuthenticationResponse.Parse(res)
                       : null;
            }
        }

        public CookieCollection Cookies
        {
            get
            {
                if (_cookies == null)
                    _cookies = Headers.GetCookies(false);

                return _cookies;
            }
        }

        public string HttpMethod => _method;

        public bool IsWebSocketRequest
        {
            get => _method == "GET"
                     && ProtocolVersion > HttpVersion.Version10
                     && Headers.Upgrades("websocket");
        }

        public string RequestUri => _uri;

        #endregion

        #region Internal Methods

        internal static HttpRequest CreateConnectRequest(Uri uri)
        {
            var host = uri.DnsSafeHost;
            var port = uri.Port;
            var authority = string.Format("{0}:{1}", host, port);
            var req = new HttpRequest("CONNECT", authority);
            req.Headers["Host"] = port == 80 ? host : authority;

            return req;
        }

        internal static HttpRequest CreateWebSocketRequest(Uri uri)
        {
            var req = new HttpRequest("GET", uri.PathAndQuery);
            var headers = req.Headers;

            // Only includes a port number in the Host header value if it's non-default.
            // See: https://tools.ietf.org/html/rfc6455#page-17
            var port = uri.Port;
            var schm = uri.Scheme;
            headers["Host"] = (port == 80 && schm == "ws") || (port == 443 && schm == "wss")
                              ? uri.DnsSafeHost
                              : uri.Authority;

            headers["Upgrade"] = "websocket";
            headers["Connection"] = "Upgrade";

            return req;
        }

        internal HttpResponse GetResponse(Stream stream, int millisecondsTimeout)
        {
            using (var tmp = ToMemory())
                tmp.CopyBytesTo(stream);

            return Read(stream, HttpResponse.Parse, millisecondsTimeout);
        }

        internal static HttpRequest Parse(string[] headerParts)
        {
            var requestLine = headerParts[0].Split(new[] { ' ' }, 3);
            if (requestLine.Length != 3)
                throw new ArgumentException("Invalid request line: " + headerParts[0]);

            var headers = new WebHeaderCollection();
            for (int i = 1; i < headerParts.Length; i++)
                headers.InternalSet(headerParts[i], false);

            return new HttpRequest(
              requestLine[0], requestLine[1], new Version(requestLine[2].Substring(5)), headers);
        }

        internal static HttpRequest Read(Stream stream, int millisecondsTimeout)
        {
            return Read(stream, Parse, millisecondsTimeout);
        }

        #endregion

        #region Public Methods

        public void SetCookies(CookieCollection cookies)
        {
            if (cookies == null || cookies.Count == 0)
                return;

            // TODO: reduce allocs
            var buff = new StringBuilder(64);
            var sortedCookies = cookies.GetSorted();
            foreach (var cookie in sortedCookies)
                if (!cookie.Expired)
                    buff.AppendFormat("{0}; ", cookie.ToString());

            var len = buff.Length;
            if (len > 2)
            {
                buff.Length = len - 2;
                Headers["Cookie"] = buff.ToString();
            }
        }

        public override RecyclableMemoryStream ToMemory()
        {
            var result = RecyclableMemoryManager.Shared.GetStream();
            try
            {
                var writer = new StreamWriter(result, Encoding.UTF8);
                writer.Write("{0} {1} HTTP/{2}{3}", _method, _uri, ProtocolVersion, CrLf);

                var headers = Headers;
                for (int i = 0; i < headers.Count; i++)
                {
                    string key = headers.GetKey(i);
                    writer.Write("{0}: {1}{2}", key, headers[key], CrLf);
                }
                writer.Write(CrLf);

                writer.Flush();
                WriteEntityBody(writer, result);

                result.Position = 0;
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }



        public override string ToString()
        {
            var output = new StringBuilder(64);
            output.AppendFormat("{0} {1} HTTP/{2}{3}", _method, _uri, ProtocolVersion, CrLf);
            var headers = Headers;
            for (int i = 0; i < headers.Count; i++)
            {
                string key = headers.GetKey(i);
                output.AppendFormat("{0}: {1}{2}", key, headers[key], CrLf);
            }

            output.Append(CrLf);
            output.Append(EntityBody);
            return output.ToString();
        }

        #endregion
    }
}