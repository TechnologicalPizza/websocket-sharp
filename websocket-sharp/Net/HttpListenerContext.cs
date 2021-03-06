#region License
/*
 * HttpListenerContext.cs
 *
 * This code is derived from HttpListenerContext.cs (System.Net) of Mono
 * (http://www.mono-project.com).
 *
 * The MIT License
 *
 * Copyright (c) 2005 Novell, Inc. (http://www.novell.com)
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

#region Authors
/*
 * Authors:
 * - Gonzalo Paniagua Javier <gonzalo@novell.com>
 */
#endregion

using System;
using System.Security.Principal;
using WebSocketSharp.Net.WebSockets;

namespace WebSocketSharp.Net
{
    /// <summary>
    /// Provides the access to the HTTP request and response objects used by
    /// the <see cref="HttpListener"/>.
    /// </summary>
    /// <remarks>
    /// This class cannot be inherited.
    /// </remarks>
    public sealed class HttpListenerContext
    {
        #region Private Fields

        private HttpListenerWebSocketContext _websocketContext;

        #endregion

        #region Internal Constructors

        internal HttpListenerContext(HttpConnection connection)
        {
            Connection = connection;
            ErrorStatus = 400;
            Request = new HttpListenerRequest(this);
            Response = new HttpListenerResponse(this);
        }

        #endregion

        #region Internal Properties

        internal HttpConnection Connection { get; }

        internal string ErrorMessage { get; set; }

        internal int ErrorStatus { get; set; }

        internal bool HasError => ErrorMessage != null;

        internal HttpListener Listener { get; set; }

        #endregion

        #region Public Properties

        /// <summary>
        /// Gets the HTTP request object that represents a client request.
        /// </summary>
        /// <value>
        /// A <see cref="HttpListenerRequest"/> that represents the client request.
        /// </value>
        public HttpListenerRequest Request { get; }

        /// <summary>
        /// Gets the HTTP response object used to send a response to the client.
        /// </summary>
        /// <value>
        /// A <see cref="HttpListenerResponse"/> that represents a response to the client request.
        /// </value>
        public HttpListenerResponse Response { get; }

        /// <summary>
        /// Gets the client information (identity, authentication, and security roles).
        /// </summary>
        /// <value>
        /// A <see cref="IPrincipal"/> instance that represents the client information.
        /// </value>
        public IPrincipal User { get; private set; }

        #endregion

        #region Internal Methods

        internal bool Authenticate()
        {
            var schm = Listener.SelectAuthenticationScheme(Request);
            if (schm == AuthenticationSchemes.Anonymous)
                return true;

            if (schm == AuthenticationSchemes.None)
            {
                Response.Close(HttpStatusCode.Forbidden);
                return false;
            }

            var realm = Listener.GetRealm();
            var user = HttpUtility.CreateUser(
                  Request.Headers["Authorization"],
                  schm,
                  realm,
                  Request.HttpMethod,
                  Listener.GetUserCredentialsFinder());

            if (user == null || !user.Identity.IsAuthenticated)
            {
                Response.CloseWithAuthChallenge(new AuthenticationChallenge(schm, realm).ToString());
                return false;
            }

            User = user;
            return true;
        }

        internal bool Register()
        {
            return Listener.RegisterContext(this);
        }

        internal void Unregister()
        {
            Listener.UnregisterContext(this);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Accepts a WebSocket handshake request.
        /// </summary>
        /// <returns>
        /// A <see cref="HttpListenerWebSocketContext"/> that represents
        /// the WebSocket handshake request.
        /// </returns>
        /// <param name="protocol">
        /// A <see cref="string"/> that represents the subprotocol supported on
        /// this WebSocket connection.
        /// </param>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="protocol"/> is empty.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="protocol"/> contains an invalid character.
        ///   </para>
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// This method has already been called.
        /// </exception>
        public HttpListenerWebSocketContext AcceptWebSocket(ReadOnlySpan<char> protocol)
        {
            if (_websocketContext != null)
                throw new InvalidOperationException("The accepting is already in progress.");

            if (protocol != null)
            {
                if (protocol.Length == 0)
                    throw new ArgumentException("An empty string.", nameof(protocol));

                if (!protocol.IsToken())
                    throw new ArgumentException("Contains an invalid character.", nameof(protocol));
            }

            _websocketContext = new HttpListenerWebSocketContext(this, protocol.ToString());
            return _websocketContext;
        }

        #endregion
    }
}