#region License
/*
 * WebSocket.cs
 *
 * This code is derived from WebSocket.java
 * (http://github.com/adamac/Java-WebSocket-client).
 *
 * The MIT License
 *
 * Copyright (c) 2009 Adam MacBeth
 * Copyright (c) 2010-2016 sta.blockhead
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
 * - Frank Razenberg <frank@zzattack.org>
 * - David Wood <dpwood@gmail.com>
 * - Liryna <liryna.stark@gmail.com>
 */
#endregion

using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebSocketSharp.Memory;
using WebSocketSharp.Net;
using WebSocketSharp.Net.WebSockets;

namespace WebSocketSharp
{
    /// <summary>
    /// Implements the WebSocket interface.
    /// </summary>
    /// <remarks>
    ///   <para>
    ///   This class provides a set of methods and properties for two-way
    ///   communication using the WebSocket protocol.
    ///   </para>
    ///   <para>
    ///   The WebSocket protocol is defined in
    ///   <see href="http://tools.ietf.org/html/rfc6455">RFC 6455</see>.
    ///   </para>
    /// </remarks>
    public class WebSocket : IDisposable
    {
        #region Private Fields

        private AuthenticationChallenge _authChallenge;
        private string _base64Key;
        private bool _isClient;
        private Action _closeContext;
        private CompressionMethod _compression;
        private WebSocketContext _context;
        private CookieCollection _cookies;
        private NetworkCredential _credentials;
        private bool _emitOnPing;
        private bool _enableRedirection;
        private string _extensions;
        private bool _extensionsRequested;
        private object _messageEventQueueMutex;
        private object _forPing = new object();
        private object _forSend = new object();
        private object _forState = new object();
        private RecyclableMemoryStream _fragmentsBuffer;
        private bool _fragmentsCompressed;
        private OpCode _fragmentsOpcode;
        private const string _guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private Func<WebSocketContext, string> _handshakeRequestChecker;
        private bool _ignoreExtensions;
        private bool _inContinuation;
        private volatile bool _inMessage;
        private volatile Logger _logger;
        private static readonly int _maxRetryCountForConnect;
        private Action<MessageEvent> _message;
        private Queue<MessageEvent> _messageEventQueue;
        private uint _nonceCount;
        private string _origin;
        private ManualResetEvent _pongReceived;
        private bool _preAuth;
        private string _protocol;
        private string[] _protocols;
        private bool _protocolsRequested;
        private NetworkCredential _proxyCredentials;
        private Uri _proxyUri;
        private volatile WebSocketState _readyState;
        private ManualResetEvent _receivingExited;
        private int _retryCountForConnect;
        private bool _isSecure;
        private ClientSslConfiguration _sslConfig;
        private Stream _stream;
        private TcpClient _tcpClient;
        private Uri _uri;
        private TimeSpan _waitTime;
        private const string _version = "13";

        #endregion

        #region Internal Fields

        /// <summary>
        /// Represents the length used to determine whether the data should be fragmented in sending.
        /// </summary>
        /// <remarks>
        ///   <para>
        ///   The data will be fragmented if that length is greater than the value of this field.
        ///   </para>
        ///   <para>
        ///   If you would like to change the value, you must set it to a value between <c>125</c> and
        ///   <c>Int32.MaxValue - 14</c> inclusive.
        ///   </para>
        /// </remarks>
        internal static readonly int FragmentLength;

        /// <summary>
        /// Represents the random number generator used internally.
        /// </summary>
        internal static readonly RandomNumberGenerator RandomNumber;

        #endregion

        #region Constructors

        #region Static Constructor

        static WebSocket()
        {
            _maxRetryCountForConnect = 10;
            FragmentLength = 1016;
            RandomNumber = new RNGCryptoServiceProvider();
        }

        #endregion

        #region Private Constructor

        private WebSocket()
        {
            _compression = CompressionMethod.None;
            _readyState = WebSocketState.Connecting;
            _cookies = new CookieCollection();
            _messageEventQueue = new Queue<MessageEvent>();
            _messageEventQueueMutex = ((ICollection)_messageEventQueue).SyncRoot;
        }

        #endregion

        #region Internal Constructors

        // As server
        internal WebSocket(HttpListenerWebSocketContext context, string protocol) : this()
        {
            _context = context;
            _protocol = protocol;

            _closeContext = context.Close;
            _logger = context.Log;
            _message = MessageServer;
            _isSecure = context.IsSecureConnection;
            _stream = context.Stream;
            _waitTime = TimeSpan.FromSeconds(1);
        }

        // As server
        internal WebSocket(TcpListenerWebSocketContext context, string protocol) : this()
        {
            _context = context;
            _protocol = protocol;

            _closeContext = context.Close;
            _logger = context.Log;
            _message = MessageServer;
            _isSecure = context.IsSecureConnection;
            _stream = context.Stream;
            _waitTime = TimeSpan.FromSeconds(1);
        }

        #endregion

        #region Public Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="WebSocket"/> class with
        /// <paramref name="url"/> and optionally <paramref name="protocols"/>.
        /// </summary>
        /// <param name="url">
        ///   <para>
        ///   A <see cref="string"/> that specifies the URL to which to connect.
        ///   </para>
        ///   <para>
        ///   The scheme of the URL must be ws or wss.
        ///   </para>
        ///   <para>
        ///   The new instance uses a secure connection if the scheme is wss.
        ///   </para>
        /// </param>
        /// <param name="protocols">
        ///   <para>
        ///   An array of <see cref="string"/> that specifies the names of
        ///   the subprotocols if necessary.
        ///   </para>
        ///   <para>
        ///   Each value of the array must be a token defined in
        ///   <see href="http://tools.ietf.org/html/rfc2616#section-2.2">
        ///   RFC 2616</see>.
        ///   </para>
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="url"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="url"/> is an empty string.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="url"/> is an invalid WebSocket URL string.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="protocols"/> contains a value that is not a token.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="protocols"/> contains a value twice.
        ///   </para>
        /// </exception>
        public WebSocket(string url, params string[] protocols) : this()
        {
            if (url == null)
                throw new ArgumentNullException(nameof(url));

            if (url.Length == 0)
                throw new ArgumentException("An empty string.", nameof(url));

            if (!url.TryCreateWebSocketUri(out _uri, out string msg))
                throw new ArgumentException(msg, nameof(url));

            if (protocols != null && protocols.Length > 0)
            {
                if (!CheckProtocols(protocols, out msg))
                    throw new ArgumentException(msg, nameof(protocols));
                _protocols = protocols;
            }

            _base64Key = CreateBase64Key();
            _isClient = true;
            _logger = new Logger();
            _message = MessageClient;
            _isSecure = _uri.Scheme == "wss";
            _waitTime = TimeSpan.FromSeconds(5);
        }

        #endregion

        #endregion Constructors

        #region Internal Properties

        internal CookieCollection CookieCollection => _cookies;

        internal bool IsConnected => _readyState == WebSocketState.Open || _readyState == WebSocketState.Closing;

        // As server
        internal Func<WebSocketContext, string> CustomHandshakeRequestChecker
        {
            get => _handshakeRequestChecker;
            set => _handshakeRequestChecker = value;
        }

        // As server
        internal bool IgnoreExtensions
        {
            get => _ignoreExtensions;
            set => _ignoreExtensions = value;
        }

        internal bool HasMessage
        {
            get
            {
                lock (_messageEventQueueMutex)
                    return _messageEventQueue.Count > 0;
            }
        }

        #endregion

        #region Public Properties

        public object StateSyncRoot => _forState;

        /// <summary>
        /// Gets or sets the compression method used to compress a message.
        /// </summary>
        /// <remarks>
        /// The set operation does nothing if the connection has already been
        /// established or it is closing.
        /// </remarks>
        /// <value>
        ///   <para>
        ///   One of the <see cref="CompressionMethod"/> enum values.
        ///   </para>
        ///   <para>
        ///   It specifies the compression method used to compress a message.
        ///   </para>
        ///   <para>
        ///   The default value is <see cref="CompressionMethod.None"/>.
        ///   </para>
        /// </value>
        /// <exception cref="InvalidOperationException">
        /// The set operation is not available if this instance is not a client.
        /// </exception>
        public CompressionMethod Compression
        {
            get => _compression;
            set
            {
                if (!_isClient)
                    throw new InvalidOperationException("This instance is not a client.");

                if (!CanSet(out string msg))
                {
                    _logger.Warn(msg);
                    return;
                }

                lock (_forState)
                {
                    if (!CanSet(out msg))
                    {
                        _logger.Warn(msg);
                        return;
                    }
                    _compression = value;
                }
            }
        }

        /// <summary>
        /// Gets the HTTP cookies included in the handshake request/response.
        /// </summary>
        /// <value>
        ///   <para>An thread-safe array of <see cref="Cookie"/>.</para>
        /// </value>
        public Cookie[] GetCookies() => _cookies.ToArray();

        /// <summary>
        /// Gets the credentials for the HTTP authentication (Basic/Digest).
        /// </summary>
        /// <value>
        ///   <para>
        ///   A <see cref="NetworkCredential"/> that represents the credentials
        ///   used to authenticate the client.
        ///   </para>
        ///   <para>
        ///   The default value is <see langword="null"/>.
        ///   </para>
        /// </value>
        public NetworkCredential Credentials => _credentials;

        /// <summary>
        /// Gets or sets a value indicating whether a <see cref="OnMessage"/> event
        /// is emitted when a ping is received.
        /// </summary>
        /// <value>
        ///   <para>
        ///   <c>true</c> if this instance emits a <see cref="OnMessage"/> event
        ///   when receives a ping; otherwise, <c>false</c>.
        ///   </para>
        ///   <para>
        ///   The default value is <c>false</c>.
        ///   </para>
        /// </value>
        public bool EmitOnPing
        {
            get => _emitOnPing;
            set => _emitOnPing = value;
        }

        /// <summary>
        /// Gets or sets a value indicating whether the URL redirection for
        /// the handshake request is allowed.
        /// </summary>
        /// <remarks>
        /// The set operation does nothing if the connection has already been
        /// established or it is closing.
        /// </remarks>
        /// <value>
        ///   <para>
        ///   <c>true</c> if this instance allows the URL redirection for
        ///   the handshake request; otherwise, <c>false</c>.
        ///   </para>
        ///   <para>
        ///   The default value is <c>false</c>.
        ///   </para>
        /// </value>
        /// <exception cref="InvalidOperationException">
        /// The set operation is not available if this instance is not a client.
        /// </exception>
        public bool EnableRedirection
        {
            get => _enableRedirection;
            set
            {
                if (!_isClient)
                    throw new InvalidOperationException("This instance is not a client.");

                if (!CanSet(out string msg))
                {
                    _logger.Warn(msg);
                    return;
                }

                lock (_forState)
                {
                    if (!CanSet(out msg))
                    {
                        _logger.Warn(msg);
                        return;
                    }
                    _enableRedirection = value;
                }
            }
        }

        /// <summary>
        /// Gets the extensions selected by server.
        /// </summary>
        /// <value>
        /// A <see cref="string"/> that will be a list of the extensions
        /// negotiated between client and server, or an empty string if
        /// not specified or selected.
        /// </value>
        public string Extensions => _extensions ?? string.Empty;

        /// <summary>
        /// Gets a value indicating whether the connection is alive.
        /// </summary>
        /// <remarks>
        /// The get operation returns the value by using a ping/pong if the connection is open.
        /// </remarks>
        /// <value>
        /// <c>true</c> if the connection is alive; otherwise, <c>false</c>.
        /// </value>
        public bool IsAlive => Ping();

        /// <summary>
        /// Gets a value indicating whether a secure connection is used.
        /// </summary>
        /// <value>
        /// <c>true</c> if this instance uses a secure connection; otherwise,
        /// <c>false</c>.
        /// </value>
        public bool IsSecure => _isSecure;

        /// <summary>
        /// Gets the logging function.
        /// </summary>
        /// <remarks>
        /// The default logging level is <see cref="LogLevel.Error"/>.
        /// </remarks>
        /// <value>
        /// A <see cref="Logger"/> that provides the logging function.
        /// </value>
        public Logger Log
        {
            get => _logger;
            internal set => _logger = value;
        }

        /// <summary>
        /// Gets or sets the value of the HTTP Origin header to send with
        /// the handshake request.
        /// </summary>
        /// <remarks>
        ///   <para>
        ///   The HTTP Origin header is defined in
        ///   <see href="http://tools.ietf.org/html/rfc6454#section-7">
        ///   Section 7 of RFC 6454</see>.
        ///   </para>
        ///   <para>
        ///   This instance sends the Origin header if this property has any.
        ///   </para>
        ///   <para>
        ///   The set operation does nothing if the connection has already been
        ///   established or it is closing.
        ///   </para>
        /// </remarks>
        /// <value>
        ///   <para>
        ///   A <see cref="string"/> that represents the value of the Origin
        ///   header to send.
        ///   </para>
        ///   <para>
        ///   The syntax is &lt;scheme&gt;://&lt;host&gt;[:&lt;port&gt;].
        ///   </para>
        ///   <para>
        ///   The default value is <see langword="null"/>.
        ///   </para>
        /// </value>
        /// <exception cref="InvalidOperationException">
        /// The set operation is not available if this instance is not a client.
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   The value specified for a set operation is not an absolute URI string.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The value specified for a set operation includes the path segments.
        ///   </para>
        /// </exception>
        public string Origin
        {
            get => _origin;
            set
            {
                if (!_isClient)
                    throw new InvalidOperationException("This instance is not a client.");

                if (!value.IsNullOrEmpty())
                {
                    if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri))
                        throw new ArgumentException("Not an absolute URI string.", "value");
                    
                    if (uri.Segments.Length > 1)
                        throw new ArgumentException("It includes the path segments.", "value");
                }

                if (!CanSet(out string msg))
                {
                    _logger.Warn(msg);
                    return;
                }

                lock (_forState)
                {
                    if (!CanSet(out msg))
                    {
                        _logger.Warn(msg);
                        return;
                    }
                    _origin = !value.IsNullOrEmpty() ? value.TrimEnd('/') : value;
                }
            }
        }

        /// <summary>
        /// Gets the name of subprotocol selected by the server.
        /// </summary>
        /// <value>
        ///   <para>
        ///   A <see cref="string"/> that will be one of the names of
        ///   subprotocols specified by client.
        ///   </para>
        ///   <para>
        ///   An empty string if not specified or selected.
        ///   </para>
        /// </value>
        public string Protocol
        {
            get => _protocol ?? string.Empty;
            internal set => _protocol = value;
        }

        /// <summary>
        /// Gets the current state of the connection.
        /// </summary>
        /// <value>
        ///   <para>
        ///   One of the <see cref="WebSocketState"/> enum values.
        ///   </para>
        ///   <para>
        ///   It indicates the current state of the connection.
        ///   </para>
        ///   <para>
        ///   The default value is <see cref="WebSocketState.Connecting"/>.
        ///   </para>
        /// </value>
        public WebSocketState ReadyState => _readyState;

        /// <summary>
        /// Gets the configuration for secure connection.
        /// </summary>
        /// <remarks>
        /// This configuration will be referenced when attempts to connect,
        /// so it must be configured before any connect method is called.
        /// </remarks>
        /// <value>
        /// A <see cref="ClientSslConfiguration"/> that represents
        /// the configuration used to establish a secure connection.
        /// </value>
        /// <exception cref="InvalidOperationException">
        ///   <para>
        ///   This instance is not a client.
        ///   </para>
        ///   <para>
        ///   This instance does not use a secure connection.
        ///   </para>
        /// </exception>
        public ClientSslConfiguration SslConfiguration
        {
            get
            {
                AssertIsClient();
                
                if (!_isSecure)
                    throw new InvalidOperationException("This instance does not use a secure connection.");
                
                return GetSslConfiguration();
            }
        }

        /// <summary>
        /// Gets the URL to which to connect.
        /// </summary>
        /// <value>
        /// A <see cref="Uri"/> that represents the URL to which to connect.
        /// </value>
        public Uri Url => _isClient ? _uri : _context.RequestUri;

        /// <summary>
        /// Gets or sets the time to wait for the response to the ping or close.
        /// </summary>
        /// <remarks>
        /// The set operation does nothing if the connection has already been
        /// established or it is closing.
        /// </remarks>
        /// <value>
        ///   <para>
        ///   A <see cref="TimeSpan"/> to wait for the response.
        ///   </para>
        ///   <para>
        ///   The default value is the same as 5 seconds if this instance is
        ///   a client.
        ///   </para>
        /// </value>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The value specified for a set operation is zero or less.
        /// </exception>
        public TimeSpan WaitTime
        {
            get => _waitTime;

            set
            {
                if (value <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException("value", "Zero or less.");

                if (!CanSet(out string msg))
                {
                    _logger.Warn(msg);
                    return;
                }

                lock (_forState)
                {
                    if (!CanSet(out msg))
                    {
                        _logger.Warn(msg);
                        return;
                    }
                    _waitTime = value;
                }
            }
        }

        #endregion

        #region Public Events

        /// <summary>
        /// Occurs when the WebSocket connection has been closed.
        /// </summary>
        public event EventHandler<CloseEvent> OnClose;

        /// <summary>
        /// Occurs when the <see cref="WebSocket"/> gets an error.
        /// </summary>
        public event EventHandler<ErrorEvent> OnError;

        /// <summary>
        /// Occurs when the <see cref="WebSocket"/> receives a message.
        /// </summary>
        public event EventHandler<MessageEvent> OnMessage;

        /// <summary>
        /// Occurs when the WebSocket connection has been established.
        /// </summary>
        public event EventHandler OnOpen;

        #endregion

        #region Private Methods

        // As server
        private bool InternalAccept()
        {
            if (_readyState == WebSocketState.Open)
            {
                _logger.Warn("The handshake request has already been accepted.");
                return false;
            }

            lock (_forState)
            {
                if (_readyState == WebSocketState.Open)
                {
                    _logger.Warn("The handshake request has already been accepted.");
                    return false;
                }

                if (_readyState == WebSocketState.Closing)
                {
                    _logger.Error("The close process has set in.");
                    Error("An interruption has occurred while attempting to accept.", null);
                    return false;
                }

                if (_readyState == WebSocketState.Closed)
                {
                    _logger.Error("The connection has been closed.");
                    Error("An interruption has occurred while attempting to accept.", null);
                    return false;
                }

                try
                {
                    if (!AcceptHandshake())
                        return false;
                }
                catch (Exception ex)
                {
                    _logger.Fatal(ex.Message);
                    _logger.Debug(ex.ToString());

                    Fatal("An exception has occurred while attempting to accept.", ex);
                    return false;
                }

                _readyState = WebSocketState.Open;
                return true;
            }
        }

        // As server
        private bool AcceptHandshake()
        {
            _logger.Debug($"A handshake request from {_context.UserEndPoint}:\n{_context}");

            if (!CheckHandshakeRequest(_context, out string msg))
            {
                _logger.Error(msg);

                InternalRefuseHandshake(
                    CloseStatusCode.ProtocolError,
                    "A handshake error has occurred while attempting to accept.");

                return false;
            }

            if (!CustomCheckHandshakeRequest(_context, out msg))
            {
                _logger.Error(msg);

                InternalRefuseHandshake(
                    CloseStatusCode.PolicyViolation,
                    "A handshake error has occurred while attempting to accept.");

                return false;
            }

            _base64Key = _context.Headers["Sec-WebSocket-Key"];

            if (_protocol != null)
            {
                var vals = _context.SecWebSocketProtocols;
                ProcessSecWebSocketProtocolClientHeader(vals);
            }

            if (!_ignoreExtensions)
            {
                var val = _context.Headers["Sec-WebSocket-Extensions"];
                ProcessSecWebSocketExtensionsClientHeader(val);
            }

            return InternalSendHttpResponse(CreateHandshakeResponse());
        }

        private bool CanSet(out string message)
        {
            if (_readyState == WebSocketState.Open)
            {
                message = "The connection has already been established.";
                return false;
            }

            if (_readyState == WebSocketState.Closing)
            {
                message = "The connection is closing.";
                return false;
            }

            message = null;
            return true;
        }

        // As server
        private bool CheckHandshakeRequest(
            WebSocketContext context, out string message)
        {
            if (!context.IsWebSocketRequest)
            {
                message = "Not a handshake request.";
                return false;
            }

            if (context.RequestUri == null)
            {
                message = "It specifies an invalid Request-URI.";
                return false;
            }

            var headers = context.Headers;
            var key = headers["Sec-WebSocket-Key"];
            if (key == null)
            {
                message = "It includes no Sec-WebSocket-Key header.";
                return false;
            }

            if (key.Length == 0)
            {
                message = "It includes an invalid Sec-WebSocket-Key header.";
                return false;
            }

            var version = headers["Sec-WebSocket-Version"];
            if (version == null)
            {
                message = "It includes no Sec-WebSocket-Version header.";
                return false;
            }

            if (version != _version)
            {
                message = "It includes an invalid Sec-WebSocket-Version header.";
                return false;
            }

            var protocol = headers["Sec-WebSocket-Protocol"];
            if (protocol != null && protocol.Length == 0)
            {
                message = "It includes an invalid Sec-WebSocket-Protocol header.";
                return false;
            }

            if (!_ignoreExtensions)
            {
                var extensions = headers["Sec-WebSocket-Extensions"];
                if (extensions != null && extensions.Length == 0)
                {
                    message = "It includes an invalid Sec-WebSocket-Extensions header.";
                    return false;
                }
            }

            message = null;
            return true;
        }

        // As client
        private bool CheckHandshakeResponse(HttpResponse response, out string message)
        {
            if (response.IsRedirect)
            {
                message = "Indicates the redirection.";
                return false;
            }

            if (response.IsUnauthorized)
            {
                message = "Requires the authentication.";
                return false;
            }

            if (!response.IsWebSocketResponse)
            {
                message = "Not a WebSocket handshake response.";
                return false;
            }

            var headers = response.Headers;
            if (!ValidateSecWebSocketAcceptHeader(headers["Sec-WebSocket-Accept"]))
            {
                message = "Includes no Sec-WebSocket-Accept header, or it has an invalid value.";
                return false;
            }

            if (!ValidateSecWebSocketProtocolServerHeader(headers["Sec-WebSocket-Protocol"]))
            {
                message = "Includes no Sec-WebSocket-Protocol header, or it has an invalid value.";
                return false;
            }

            if (!ValidateSecWebSocketExtensionsServerHeader(headers["Sec-WebSocket-Extensions"]))
            {
                message = "Includes an invalid Sec-WebSocket-Extensions header.";
                return false;
            }

            if (!ValidateSecWebSocketVersionServerHeader(headers["Sec-WebSocket-Version"]))
            {
                message = "Includes an invalid Sec-WebSocket-Version header.";
                return false;
            }

            message = null;
            return true;
        }

        private static bool CheckProtocols(string[] protocols, out string message)
        {
            if (protocols.Contains((protocol) => protocol.IsNullOrEmpty() || !protocol.AsSpan().IsToken()))
            {
                message = "It contains a value that is not a token.";
                return false;
            }

            if (protocols.ContainsTwice())
            {
                message = "It contains a value twice.";
                return false;
            }

            message = null;
            return true;
        }

        private bool CheckReceivedFrame(WebSocketFrame frame, out string message)
        {
            var masked = frame.IsMasked;
            if (_isClient && masked)
            {
                message = "A frame from the server is masked.";
                return false;
            }

            if (!_isClient && !masked)
            {
                message = "A frame from a client is not masked.";
                return false;
            }

            if (_inContinuation && frame.IsData)
            {
                message = "A data frame has been received while receiving continuation frames.";
                return false;
            }

            if (frame.IsCompressed && _compression == CompressionMethod.None)
            {
                message = "A compressed frame has been received without any agreement for it.";
                return false;
            }

            if (frame.Rsv2 == Rsv.On)
            {
                message = "The RSV2 of a frame is non-zero without any negotiation for it.";
                return false;
            }

            if (frame.Rsv3 == Rsv.On)
            {
                message = "The RSV3 of a frame is non-zero without any negotiation for it.";
                return false;
            }

            message = null;
            return true;
        }

        private void InternalClose(ushort code, string reason)
        {
            if (_readyState == WebSocketState.Closing)
            {
                _logger.Info("The closing is already in progress.");
                return;
            }

            if (_readyState == WebSocketState.Closed)
            {
                _logger.Info("The connection has already been closed.");
                return;
            }

            if (code == 1005) // == no status
            {
                InternalClose(PayloadData.Empty, true, true, false);
                return;
            }

            var send = !code.IsReserved();
            InternalClose(new PayloadData(code, reason), send, send, false);
        }

        private void InternalClose(
            PayloadData payloadData, bool send, bool receive, bool received)
        {
            lock (_forState)
            {
                if (_readyState == WebSocketState.Closing)
                {
                    _logger.Info("The closing is already in progress.");
                    return;
                }

                if (_readyState == WebSocketState.Closed)
                {
                    _logger.Info("The connection has already been closed.");
                    return;
                }

                send = send && _readyState == WebSocketState.Open;
                receive = send && receive;

                _readyState = WebSocketState.Closing;
            }

            _logger.Trace("Begin closing the connection.");

            var res = CloseHandshake(payloadData, send, receive, received);
            ReleaseResources();

            _logger.Trace("End closing the connection.");
            _readyState = WebSocketState.Closed;

            var e = new CloseEvent(payloadData, wasClean: res);
            
            try
            {
                OnClose.Emit(this, e);
            }
            catch (Exception ex)
            {
                _logger.Error(ex.ToString());
                Error("An error has occurred during the OnClose event.", ex);
            }
        }

        private void InternalCloseAsync(ushort code, string reason)
        {
            if (_readyState == WebSocketState.Closing)
            {
                _logger.Info("The closing is already in progress.");
                return;
            }

            if (_readyState == WebSocketState.Closed)
            {
                _logger.Info("The connection has already been closed.");
                return;
            }

            if (code == 1005) // == no status
            {
                CloseAsync(PayloadData.Empty, true, true, false);
                return;
            }

            var send = !code.IsReserved();
            CloseAsync(new PayloadData(code, reason), send, send, false);
        }

        private void CloseAsync(
            PayloadData payloadData, bool send, bool receive, bool received)
        {
            Action<PayloadData, bool, bool, bool> closer = InternalClose;
            closer.BeginInvoke(
                payloadData, send, receive, received, closer.EndInvoke, null);
        }

        private bool CloseHandshake(
            PayloadData payloadData, bool send, bool receive, bool received)
        {
            bool sent = false;
            if (send)
            {
                var frame = WebSocketFrame.CreateCloseFrame(payloadData, _isClient);
                sent = SendBytes(frame);
            }

            if (!received && sent && receive && _receivingExited != null)
                received = _receivingExited.WaitOne(_waitTime);

            bool ret = sent && received;
            _logger.Debug($"Was clean?: {ret}\n  sent: {sent}\n  received: {received}");
            return ret;
        }

        // As client
        private bool InternalConnect()
        {
            if (_readyState == WebSocketState.Open)
            {
                _logger.Warn("The connection has already been established.");
                return false;
            }

            lock (_forState)
            {
                if (_readyState == WebSocketState.Open)
                {
                    _logger.Warn("The connection has already been established.");
                    return false;
                }

                if (_readyState == WebSocketState.Closing)
                {
                    _logger.Error("The close process has set in.");
                    Error("An interruption has occurred while attempting to connect.", null);
                    return false;
                }

                if (_retryCountForConnect > _maxRetryCountForConnect)
                {
                    _logger.Error("An opportunity for reconnecting has been lost.");
                    Error("An interruption has occurred while attempting to connect.", null);
                    return false;
                }

                _readyState = WebSocketState.Connecting;

                try
                {
                    DoHandshake();
                }
                catch (Exception ex)
                {
                    _retryCountForConnect++;

                    _logger.Fatal(ex.Message);
                    _logger.Debug(ex.ToString());

                    Fatal("An exception has occurred while attempting to connect.", ex);
                    return false;
                }

                _retryCountForConnect = 1;
                _readyState = WebSocketState.Open;
                return true;
            }
        }

        // As client
        private string CreateExtensions()
        {
            var buff = new StringBuilder(80);

            if (_compression != CompressionMethod.None)
            {
                var str = _compression.ToExtensionString(
                  "server_no_context_takeover", "client_no_context_takeover");

                buff.AppendFormat("{0}, ", str);
            }

            var len = buff.Length;
            if (len > 2)
            {
                buff.Length = len - 2;
                return buff.ToString();
            }

            return null;
        }

        // As server
        private HttpResponse CreateHandshakeFailureResponse(HttpStatusCode code)
        {
            var ret = HttpResponse.CreateCloseResponse(code);
            ret.Headers["Sec-WebSocket-Version"] = _version;

            return ret;
        }

        // As client
        private HttpRequest CreateHandshakeRequest()
        {
            var ret = HttpRequest.CreateWebSocketRequest(_uri);

            var headers = ret.Headers;
            if (!_origin.IsNullOrEmpty())
                headers["Origin"] = _origin;

            headers["Sec-WebSocket-Key"] = _base64Key;

            _protocolsRequested = _protocols != null;
            if (_protocolsRequested)
                headers["Sec-WebSocket-Protocol"] = _protocols.ToString(", ");

            _extensionsRequested = _compression != CompressionMethod.None;
            if (_extensionsRequested)
                headers["Sec-WebSocket-Extensions"] = CreateExtensions();

            headers["Sec-WebSocket-Version"] = _version;

            AuthenticationResponse authRes = null;
            if (_authChallenge != null && _credentials != null)
            {
                authRes = new AuthenticationResponse(_authChallenge, _credentials, _nonceCount);
                _nonceCount = authRes.NonceCount;
            }
            else if (_preAuth)
            {
                authRes = new AuthenticationResponse(_credentials);
            }

            if (authRes != null)
                headers["Authorization"] = authRes.ToString();

            if (_cookies.Count > 0)
                ret.SetCookies(_cookies);

            return ret;
        }

        // As server
        private HttpResponse CreateHandshakeResponse()
        {
            var ret = HttpResponse.CreateWebSocketResponse();

            var headers = ret.Headers;
            headers["Sec-WebSocket-Accept"] = CreateResponseKey(_base64Key);

            if (_protocol != null)
                headers["Sec-WebSocket-Protocol"] = _protocol;

            if (_extensions != null)
                headers["Sec-WebSocket-Extensions"] = _extensions;

            if (_cookies.Count > 0)
                ret.SetCookies(_cookies);

            return ret;
        }

        // As server
        private bool CustomCheckHandshakeRequest(
            WebSocketContext context, out string message)
        {
            if (_handshakeRequestChecker == null)
            {
                message = null;
                return true;
            }
            message = _handshakeRequestChecker(context);
            return message == null;
        }

        //private MessageEventArgs DequeueFromMessageEventQueue()
        //{
        //    lock (_messageEventQueueMutex)
        //        return _messageEventQueue.Count > 0 ? _messageEventQueue.Dequeue() : null;
        //}

        // As client
        private void DoHandshake()
        {
            SetClientStream();
            using (var res = SendHandshakeRequest())
            {
                if (!CheckHandshakeResponse(res, out string msg))
                    throw new WebSocketException(CloseStatusCode.ProtocolError, msg);

                if (_protocolsRequested)
                    _protocol = res.Headers["Sec-WebSocket-Protocol"];

                if (_extensionsRequested)
                    ProcessSecWebSocketExtensionsServerHeader(res.Headers["Sec-WebSocket-Extensions"]);

                ProcessCookies(res.Cookies);
            }
        }

        private void EnqueueToMessageEventQueue(MessageEvent e)
        {
            lock (_messageEventQueueMutex)
                _messageEventQueue.Enqueue(e);
        }

        private void Error(string message, Exception exception)
        {
            try
            {
                OnError.Emit(this, new ErrorEvent(message, exception));
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message);
                _logger.Debug(ex.ToString());
            }
        }

        private void Fatal(string message, ushort code)
        {
            var payload = new PayloadData(code, message);
            InternalClose(payload, !code.IsReserved(), false, false);
        }

        private void Fatal(string message, Exception exception)
        {
            var code = exception is WebSocketException
                       ? ((WebSocketException)exception).Code
                       : CloseStatusCode.Abnormal;

            Fatal(message, (ushort)code);
        }

        private void Fatal(string message, CloseStatusCode code)
        {
            Fatal(message, (ushort)code);
        }

        private ClientSslConfiguration GetSslConfiguration()
        {
            if (_sslConfig == null)
                _sslConfig = new ClientSslConfiguration(_uri.DnsSafeHost);

            return _sslConfig;
        }

        private void Message()
        {
            MessageEvent e;
            lock (_messageEventQueueMutex)
            {
                if (_inMessage || _messageEventQueue.Count == 0 || _readyState != WebSocketState.Open)
                    return;

                _inMessage = true;
                e = _messageEventQueue.Dequeue();
            }

            _message(e);
        }

        private void MessageClient(MessageEvent e)
        {
            do
            {
                try
                {
                    OnMessage.Emit(this, e);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex.ToString());
                    Error("An error has occurred during an OnMessage event.", ex);
                }

                lock (_messageEventQueueMutex)
                {
                    if (_messageEventQueue.Count == 0 || _readyState != WebSocketState.Open)
                    {
                        _inMessage = false;
                        break;
                    }
                    e = _messageEventQueue.Dequeue();
                }
            }
            while (true);
        }

        private void MessageServer(MessageEvent e)
        {
            try
            {
                OnMessage.Emit(this, e);
            }
            catch (Exception ex)
            {
                _logger.Error(ex.ToString());
                Error("An error has occurred during an OnMessage event.", ex);
            }

            lock (_messageEventQueueMutex)
            {
                if (_messageEventQueue.Count == 0 || _readyState != WebSocketState.Open)
                {
                    _inMessage = false;
                    return;
                }
                e = _messageEventQueue.Dequeue();
            }

            ThreadPool.QueueUserWorkItem(state => MessageServer(e));
        }

        private void Open()
        {
            _inMessage = true;
            StartReceiving();
            try
            {
                OnOpen.Emit(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.Error(ex.ToString());
                Error("An error has occurred during the OnOpen event.", ex);
            }

            MessageEvent e;
            lock (_messageEventQueueMutex)
            {
                if (_messageEventQueue.Count == 0 || _readyState != WebSocketState.Open)
                {
                    _inMessage = false;
                    return;
                }
                e = _messageEventQueue.Dequeue();
            }
            _message.BeginInvoke(e, _message.EndInvoke, null);
        }

        private bool Ping(byte[] data)
        {
            if (_readyState != WebSocketState.Open)
                return false;

            var pongReceived = _pongReceived;
            if (pongReceived == null)
                return false;

            lock (_forPing)
            {
                try
                {
                    pongReceived.Reset();
                    if (!InternalSend(Fin.Final, OpCode.Ping, data, false))
                        return false;

                    return pongReceived.WaitOne(_waitTime);
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }

        private bool ProcessCloseFrame(WebSocketFrame frame)
        {
            var payload = frame.PayloadData;
            InternalClose(payload, !payload.HasReservedCode, false, true);
            return false;
        }

        // As client
        private void ProcessCookies(CookieCollection cookies)
        {
            if (cookies.Count == 0)
                return;

            _cookies.SetOrRemove(cookies);
        }

        private bool ProcessDataFrame(WebSocketFrame frame)
        {
            MessageEvent args;
            if (frame.IsCompressed)
            {
                using (var tmp = frame.PayloadData.ApplicationData.Decompress(_compression))
                    args = new MessageEvent(frame.Opcode, tmp.CopyToArray());
            }
            else
                args = new MessageEvent(frame);

            EnqueueToMessageEventQueue(args);
            return true;
        }

        private bool ProcessFragmentFrame(WebSocketFrame frame)
        {
            if (!_inContinuation)
            {
                // Must process first fragment.
                if (frame.IsContinuation)
                    return true;

                _fragmentsOpcode = frame.Opcode;
                _fragmentsCompressed = frame.IsCompressed;
                _fragmentsBuffer = RecyclableMemoryManager.Shared.GetStream();
                _inContinuation = true;
            }

            _fragmentsBuffer.Write(frame.PayloadData.ApplicationData.Span);
            if (frame.IsFinal)
            {
                try
                {
                    byte[] messageData;
                    if (_fragmentsCompressed)
                    {
                        using (var tmp = _fragmentsBuffer.Decompress(_compression))
                            messageData = tmp.CopyToArray();
                    }
                    else
                        messageData = _fragmentsBuffer.CopyToArray();

                    EnqueueToMessageEventQueue(new MessageEvent(_fragmentsOpcode, messageData));
                }
                finally
                {
                    _fragmentsBuffer.Dispose();
                    _fragmentsBuffer = null;
                }
                _inContinuation = false;
            }
            return true;
        }

        private bool ProcessPingFrame(WebSocketFrame frame)
        {
            _logger.Trace("A ping was received.");
            var pong = WebSocketFrame.CreatePongFrame(frame.PayloadData, _isClient);

            lock (_forState)
            {
                if (_readyState != WebSocketState.Open)
                {
                    _logger.Error("The connection is closing.");
                    return true;
                }

                if (!SendBytes(pong))
                    return false;
            }

            _logger.Trace("A pong to this ping has been sent.");

            if (_emitOnPing)
            {
                if (_isClient)
                    pong.Unmask();

                EnqueueToMessageEventQueue(new MessageEvent(frame));
            }

            return true;
        }

        private bool ProcessPongFrame()
        {
            _logger.Trace("A pong was received.");

            try
            {
                _pongReceived.Set();
            }
            catch (NullReferenceException ex)
            {
                _logger.Error(ex.Message);
                _logger.Debug(ex.ToString());

                return false;
            }
            catch (ObjectDisposedException ex)
            {
                _logger.Error(ex.Message);
                _logger.Debug(ex.ToString());

                return false;
            }

            _logger.Trace("It has been signaled.");
            return true;
        }

        private bool ProcessReceivedFrame(WebSocketFrame frame)
        {
            if (!CheckReceivedFrame(frame, out string msg))
                throw new WebSocketException(CloseStatusCode.ProtocolError, msg);

            frame.Unmask();
            
            return frame.IsFragment
                   ? ProcessFragmentFrame(frame)
                   : frame.IsData
                     ? ProcessDataFrame(frame)
                     : frame.IsPing
                       ? ProcessPingFrame(frame)
                       : frame.IsPong
                         ? ProcessPongFrame()
                         : frame.IsClose
                           ? ProcessCloseFrame(frame)
                           : ProcessUnsupportedFrame(frame);
        }

        // As server
        private void ProcessSecWebSocketExtensionsClientHeader(string value)
        {
            if (value == null)
                return;

            var buff = new StringBuilder(80);
            var comp = false;

            foreach (var elm in value.SplitHeaderValue(','))
            {
                var extension = elm.Trim();
                if (extension.Length == 0)
                    continue;

                if (!comp)
                {
                    if (extension.IsCompressionExtension(CompressionMethod.Deflate))
                    {
                        _compression = CompressionMethod.Deflate;

                        buff.AppendFormat(
                          "{0}, ",
                          _compression.ToExtensionString(
                            "client_no_context_takeover", "server_no_context_takeover"
                          )
                        );

                        comp = true;
                    }
                }
            }

            var len = buff.Length;
            if (len <= 2)
                return;

            buff.Length = len - 2;
            _extensions = buff.ToString();
        }

        // As client
        private void ProcessSecWebSocketExtensionsServerHeader(string value)
        {
            if (value == null)
            {
                _compression = CompressionMethod.None;
                return;
            }
            _extensions = value;
        }

        // As server
        private void ProcessSecWebSocketProtocolClientHeader(IEnumerable<string> values)
        {
            if (values.Contains(val => val == _protocol))
                return;

            _protocol = null;
        }

        private bool ProcessUnsupportedFrame(WebSocketFrame frame)
        {
            _logger.Fatal("An unsupported frame:" + frame.PrintToString(false));
            Fatal("There is no way to handle it.", CloseStatusCode.PolicyViolation);
            return false;
        }

        // As server
        private void InternalRefuseHandshake(CloseStatusCode code, string reason)
        {
            _readyState = WebSocketState.Closing;

            var res = CreateHandshakeFailureResponse(HttpStatusCode.BadRequest);
            InternalSendHttpResponse(res);

            ReleaseServerResources();

            _readyState = WebSocketState.Closed;

            var e = new CloseEvent(code, reason, wasClean: false);
            try
            {
                OnClose.Emit(this, e);
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message);
                _logger.Debug(ex.ToString());
            }
        }

        // As client
        private void ReleaseClientResources()
        {
            if (_stream != null)
            {
                _stream.Dispose();
                _stream = null;
            }

            if (_tcpClient != null)
            {
                _tcpClient.Close();
                _tcpClient = null;
            }
        }

        private void ReleaseCommonResources()
        {
            if (_fragmentsBuffer != null)
            {
                _fragmentsBuffer.Dispose();
                _fragmentsBuffer = null;
                _inContinuation = false;
            }

            if (_pongReceived != null)
            {
                _pongReceived.Close();
                _pongReceived = null;
            }

            if (_receivingExited != null)
            {
                _receivingExited.Close();
                _receivingExited = null;
            }
        }

        private void ReleaseResources()
        {
            if (_isClient)
                ReleaseClientResources();
            else
                ReleaseServerResources();

            ReleaseCommonResources();
        }

        // As server
        private void ReleaseServerResources()
        {
            if (_closeContext == null)
                return;

            _closeContext();
            _closeContext = null;
            _stream = null;
            _context = null;
        }

        private bool InternalSend(OpCode opcode, Stream stream)
        {
            lock (_forSend)
            {
                var src = stream;
                bool compressed = false;
                bool sent = false;
                try
                {
                    if (_compression != CompressionMethod.None)
                    {
                        stream = stream.Compress(_compression);
                        compressed = true;

                        // to free up memory blocks before calling InternalSend
                        src.Dispose();
                        src = null;
                    }

                    sent = InternalSend(opcode, stream, compressed);
                    if (!sent)
                        Error("A send has been interrupted.", null);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex.ToString());
                    Error("An error has occurred during a send.", ex);
                }
                finally
                {
                    if (compressed)
                        stream.Dispose();
                    src?.Dispose();
                }

                return sent;
            }
        }

        private bool InternalSend(OpCode opcode, Stream stream, bool compressed)
        {
            var len = stream.Length;
            if (len == 0)
                return InternalSend(Fin.Final, opcode, Array.Empty<byte>(), false);

            var quo = len / FragmentLength;
            var rem = (int)(len % FragmentLength);

            byte[] buff;
            if (quo == 0)
            {
                buff = new byte[rem];
                return stream.Read(buff, 0, rem) == rem
                    && InternalSend(Fin.Final, opcode, buff, compressed);
            }

            if (quo == 1 && rem == 0)
            {
                buff = new byte[FragmentLength];
                return stream.Read(buff, 0, FragmentLength) == FragmentLength
                    && InternalSend(Fin.Final, opcode, buff, compressed);
            }

            /* Send fragments */

            // Begin
            buff = new byte[FragmentLength];
            bool sent = stream.Read(buff, 0, FragmentLength) == FragmentLength
                && InternalSend(Fin.More, opcode, buff, compressed);

            if (!sent)
                return false;

            var n = rem == 0 ? quo - 2 : quo - 1;
            for (long i = 0; i < n; i++)
            {
                sent = stream.Read(buff, 0, FragmentLength) == FragmentLength
                    && InternalSend(Fin.More, OpCode.Cont, buff, false);

                if (!sent)
                    return false;
            }

            // End
            if (rem == 0)
                rem = FragmentLength;
            else
                buff = new byte[rem];

            return stream.Read(buff, 0, rem) == rem
                && InternalSend(Fin.Final, OpCode.Cont, buff, false);
        }

        private bool InternalSend(Fin fin, OpCode opcode, byte[] data, bool compressed)
        {
            lock (_forState)
            {
                if (_readyState != WebSocketState.Open)
                {
                    _logger.Error("The connection is closing.");
                    return false;
                }

                var frame = new WebSocketFrame(fin, opcode, data, compressed, _isClient);
                return SendBytes(frame);
            }
        }

        private void InternalSendAsync(OpCode opcode, Stream stream, Action<bool> completed)
        {
            Func<OpCode, Stream, bool> sender = InternalSend;
            sender.BeginInvoke(
                opcode,
                stream,
                ar =>
                {
                    try
                    {
                        bool sent = sender.EndInvoke(ar);
                        completed?.Invoke(sent);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex.ToString());
                        Error("An error has occurred during the callback for an async send.", ex);
                    }
                },
                null
            );
        }

        private bool SendBytes(byte[] bytes)
        {
            try
            {
                _stream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message);
                _logger.Debug(ex.ToString());
                return false;
            }
            return true;
        }

        private bool SendBytes(Stream bytes)
        {
            try
            {
                bytes.CopyBytesTo(_stream);
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message);
                _logger.Debug(ex.ToString());
                return false;
            }
            return true;
        }

        private bool SendBytes(WebSocketFrame frame)
        {
            using (var tmp = frame.ToMemory())
                return SendBytes(tmp);
        }

        // As client
        private HttpResponse SendHandshakeRequest()
        {
            using (var req = CreateHandshakeRequest())
            {
                var res = InternalSendHttpRequest(req, 90000);
                try
                {
                    if (res.IsUnauthorized)
                    {
                        var chal = res.Headers["WWW-Authenticate"];
                        _logger.Warn(string.Format("Received an authentication requirement for '{0}'.", chal));
                        if (chal.IsNullOrEmpty())
                        {
                            _logger.Error("No authentication challenge is specified.");
                            return res;
                        }

                        _authChallenge = AuthenticationChallenge.Parse(chal);
                        if (_authChallenge == null)
                        {
                            _logger.Error("An invalid authentication challenge is specified.");
                            return res;
                        }

                        if (_credentials != null &&
                            (!_preAuth || _authChallenge.Scheme == AuthenticationSchemes.Digest))
                        {
                            if (res.HasConnectionClose)
                            {
                                ReleaseClientResources();
                                SetClientStream();
                            }

                            var authRes = new AuthenticationResponse(_authChallenge, _credentials, _nonceCount);
                            _nonceCount = authRes.NonceCount;
                            req.Headers["Authorization"] = authRes.ToString();

                            res.Dispose();
                            res = InternalSendHttpRequest(req, 15000);
                        }
                    }

                    if (res.IsRedirect)
                    {
                        var url = res.Headers["Location"];
                        _logger.Warn(string.Format("Received a redirection to '{0}'.", url));
                        if (_enableRedirection)
                        {
                            if (url.IsNullOrEmpty())
                            {
                                _logger.Error("No url to redirect is located.");
                                return res;
                            }

                            if (!url.TryCreateWebSocketUri(out Uri uri, out string msg))
                            {
                                _logger.Error("An invalid url to redirect is located: " + msg);
                                return res;
                            }

                            res.Dispose();
                            ReleaseClientResources();

                            _uri = uri;
                            _isSecure = uri.Scheme == "wss";

                            SetClientStream();
                            return SendHandshakeRequest();
                        }
                    }
                    return res;
                }
                catch
                {
                    res.Dispose();
                    throw;
                }
            }
        }

        // As client
        private HttpResponse InternalSendHttpRequest(HttpRequest request, int millisecondsTimeout)
        {
            if (_logger.Level <= LogLevel.Debug)
                _logger.Debug("A request to the server:\n" + request.ToString());

            var res = request.GetResponse(_stream, millisecondsTimeout);

            if (_logger.Level <= LogLevel.Debug)
                _logger.Debug("A response to this request:\n" + res.ToString());

            return res;
        }

        // As server
        private bool InternalSendHttpResponse(HttpResponse response)
        {
            if(_logger.Level <= LogLevel.Debug)
                _logger.Debug($"A response to {_context.UserEndPoint}:\n{response}");

            using (var tmp = response.ToMemory())
                return SendBytes(tmp);
        }

        // As client
        private void InternalSendProxyConnectRequest()
        {
            using (var req = HttpRequest.CreateConnectRequest(_uri))
            {
                var res = InternalSendHttpRequest(req, 90000);
                try
                {
                    if (res.IsProxyAuthenticationRequired)
                    {
                        var chal = res.Headers["Proxy-Authenticate"];
                        _logger.Warn($"Received a proxy authentication requirement for '{chal}'.");

                        if (chal.IsNullOrEmpty())
                            throw new WebSocketException("No proxy authentication challenge is specified.");

                        var authChal = AuthenticationChallenge.Parse(chal);
                        if (authChal == null)
                            throw new WebSocketException("An invalid proxy authentication challenge is specified.");

                        if (_proxyCredentials != null)
                        {
                            if (res.HasConnectionClose)
                            {
                                ReleaseClientResources();
                                _tcpClient = new TcpClient(_proxyUri.DnsSafeHost, _proxyUri.Port);
                                _stream = _tcpClient.GetStream();
                            }

                            var authRes = new AuthenticationResponse(authChal, _proxyCredentials, 0);
                            req.Headers["Proxy-Authorization"] = authRes.ToString();

                            res.Dispose();
                            res = InternalSendHttpRequest(req, 15000);
                        }

                        if (res.IsProxyAuthenticationRequired)
                            throw new WebSocketException("A proxy authentication is required.");
                    }

                    if (res.StatusCode[0] != '2')
                        throw new WebSocketException(
                          "The proxy has failed a connection to the requested host and port.");
                }
                finally
                {
                    res.Dispose();
                }
            }
        }

        // As client
        private void SetClientStream()
        {
            if (_proxyUri != null)
            {
                _tcpClient = new TcpClient(_proxyUri.DnsSafeHost, _proxyUri.Port);
                _stream = _tcpClient.GetStream();
                InternalSendProxyConnectRequest();
            }
            else
            {
                _tcpClient = new TcpClient(_uri.DnsSafeHost, _uri.Port);
                _stream = _tcpClient.GetStream();
            }

            if (_isSecure)
            {
                var conf = GetSslConfiguration();
                var host = conf.TargetHost;
                if (host != _uri.DnsSafeHost)
                    throw new WebSocketException(
                      CloseStatusCode.TlsHandshakeFailure, "An invalid host name is specified.");

                try
                {
                    var sslStream = new SslStream(
                        _stream,
                        false,
                        conf.ServerCertificateValidationCallback,
                        conf.ClientCertificateSelectionCallback);

                    sslStream.AuthenticateAsClient(
                        host,
                        conf.ClientCertificates,
                        conf.EnabledSslProtocols,
                        conf.CheckCertificateRevocation);

                    _stream = sslStream;
                }
                catch (Exception ex)
                {
                    throw new WebSocketException(CloseStatusCode.TlsHandshakeFailure, ex);
                }
            }
        }

        private void StartReceiving()
        {
            if (_messageEventQueue.Count > 0)
                _messageEventQueue.Clear();

            _pongReceived = new ManualResetEvent(false);
            _receivingExited = new ManualResetEvent(false);

            void FrameCallback(object state)
            {
                try
                {
                    var frame = WebSocketFrame.ReadFrame(_stream, unmask: false);
                    if (!ProcessReceivedFrame(frame) || _readyState == WebSocketState.Closed)
                    {
                        ManualResetEvent exited = _receivingExited;
                        if (exited != null)
                            exited.Set();
                        return;
                    }

                    // Receive next asap because the Ping or Close needs a response to it.
                    ThreadPool.QueueUserWorkItem(FrameCallback);

                    if (_inMessage || !HasMessage || _readyState != WebSocketState.Open)
                        return;
                    Message();
                }
                catch (Exception ex)
                {
                    _logger.Fatal(ex.ToString());
                    Fatal("An exception has occurred while receiving.", ex);
                }
            }

            ThreadPool.QueueUserWorkItem(FrameCallback);
        }

        // As client
        private bool ValidateSecWebSocketAcceptHeader(string value)
        {
            return value != null && value == CreateResponseKey(_base64Key);
        }

        // As client
        private bool ValidateSecWebSocketExtensionsServerHeader(string value)
        {
            if (value == null)
                return true;

            if (value.Length == 0)
                return false;

            if (!_extensionsRequested)
                return false;

            bool comp = _compression != CompressionMethod.None;
            foreach (var e in value.SplitHeaderValue(','))
            {  
                string ext = e.Trim();
                if (comp && ext.IsCompressionExtension(_compression))
                {
                    if (!ext.Contains("server_no_context_takeover"))
                    {
                        _logger.Error("The server hasn't sent back 'server_no_context_takeover'.");
                        return false;
                    }

                    if (!ext.Contains("client_no_context_takeover"))
                        _logger.Warn("The server hasn't sent back 'client_no_context_takeover'.");

                    var method = _compression.ToExtensionString();
                    var invalid = ext.SplitHeaderValue(';').Contains(
                        t =>
                        {
                            t = t.Trim();
                            return t != method
                                && t != "server_no_context_takeover"
                                && t != "client_no_context_takeover";
                        });

                    if (invalid)
                        return false;
                }
                else
                {
                    return false;
                }
            }

            return true;
        }

        // As client
        private bool ValidateSecWebSocketProtocolServerHeader(string value)
        {
            if (value == null)
                return !_protocolsRequested;

            if (value.Length == 0)
                return false;

            return _protocolsRequested && _protocols.Contains(p => p == value);
        }

        // As client
        private bool ValidateSecWebSocketVersionServerHeader(string value)
        {
            return value == null || value == _version;
        }

        [DebuggerHidden]
        private void AssertOpen()
        {
            if (_readyState != WebSocketState.Open)
                throw new InvalidOperationException("The the connection is not open.");
        }

        [DebuggerHidden]
        private static void AssertValidFileInfo(FileInfo fileInfo, out FileStream stream)
        {
            if (fileInfo == null)
                throw new ArgumentNullException(nameof(fileInfo));

            if (!fileInfo.Exists)
                throw new ArgumentException("The file does not exist.", nameof(fileInfo));

            if (!fileInfo.TryOpenRead(out stream))
                throw new ArgumentException("The file could not be opened.", nameof(fileInfo));
        }

        [DebuggerHidden]
        private void AssertValidStream(Stream stream, int length, out RecyclableMemoryStream bytes)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            if (!stream.CanRead)
                throw new ArgumentException("It cannot be read.", nameof(stream));

            if (length < 1)
                throw new ArgumentException("Less than 1.", nameof(length));

            bytes = stream.ReadBytes(length);
            try
            {
                if (bytes.Length == 0)
                    throw new ArgumentException("No data could be read from it.", nameof(stream));

                if (bytes.Length < length)
                    _logger.Warn($"Only {bytes.Length} byte(s) of data could be read from the stream.");
            }
            catch
            {
                bytes?.Dispose();
                throw;
            }
        }

        [DebuggerHidden]
        private void AssertIsNotClient()
        {
            if (_isClient)
                throw new InvalidOperationException("This instance is a client.");
        }

        [DebuggerHidden]
        private void AssertIsClient()
        {
            if (!_isClient)
                throw new InvalidOperationException("This instance is not a client.");
        }

        #endregion

        #region Internal Methods

        // As server
        internal void Close(HttpResponse response)
        {
            _readyState = WebSocketState.Closing;

            InternalSendHttpResponse(response);
            ReleaseServerResources();

            _readyState = WebSocketState.Closed;
        }

        // As server
        internal void Close(HttpStatusCode code)
        {
            Close(CreateHandshakeFailureResponse(code));
        }

        // As server
        internal void Close(PayloadData payloadData, RecyclableMemoryStream frameAsBytes)
        {
            lock (_forState)
            {
                if (_readyState == WebSocketState.Closing)
                {
                    _logger.Info("The closing is already in progress.");
                    return;
                }

                if (_readyState == WebSocketState.Closed)
                {
                    _logger.Info("The connection has already been closed.");
                    return;
                }

                _readyState = WebSocketState.Closing;
            }

            _logger.Trace("Begin closing the connection.");

            bool sent = frameAsBytes != null && SendBytes(frameAsBytes);
            bool received = sent && _receivingExited != null
                ? _receivingExited.WaitOne(_waitTime)
                : false;

            bool res = sent && received;
            _logger.Debug($"Was clean?: {res}\n  sent: {sent}\n  received: {received}");

            ReleaseServerResources();
            ReleaseCommonResources();

            _logger.Trace("End closing the connection.");
            _readyState = WebSocketState.Closed;

            var e = new CloseEvent(payloadData, wasClean: res);
            try
            {
                OnClose.Emit(this, e);
            }
            catch (Exception ex)
            {
                _logger.Error(ex.ToString());
            }
        }

        // As client
        internal static string CreateBase64Key()
        {
            byte[] src = new byte[16];
            RandomNumber.GetBytes(src);

            return Convert.ToBase64String(src);
        }

        internal static string CreateResponseKey(string base64Key)
        {
            var buff = new StringBuilder(base64Key, 64);
            buff.Append(_guid);

            SHA1 sha1 = new SHA1CryptoServiceProvider();
            var src = sha1.ComputeHash(buff.ToString().UTF8Encode());

            return Convert.ToBase64String(src);
        }

        // As server
        internal void TryAcceptAndOpen()
        {
            try
            {
                if (!AcceptHandshake())
                    return;
            }
            catch (Exception ex)
            {
                _logger.Fatal(ex.Message);
                _logger.Debug(ex.ToString());

                Fatal("An exception has occurred while attempting to accept.", ex);
                return;
            }

            _readyState = WebSocketState.Open;
            Open();
        }

        // As server
        internal bool Ping(byte[] frameAsBytes, TimeSpan timeout)
        {
            if (_readyState != WebSocketState.Open)
                return false;

            var pongReceived = _pongReceived;
            if (pongReceived == null)
                return false;

            lock (_forPing)
            {
                try
                {
                    pongReceived.Reset();

                    lock (_forState)
                    {
                        if (_readyState != WebSocketState.Open)
                            return false;

                        if (!SendBytes(frameAsBytes))
                            return false;
                    }

                    return pongReceived.WaitOne(timeout);
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }

        // As server
        internal void Send(
            OpCode opcode, byte[] data, Dictionary<CompressionMethod, Stream> cache)
        {
            lock (_forSend)
            {
                lock (_forState)
                {
                    if (_readyState != WebSocketState.Open)
                    {
                        _logger.Error("The connection is closing.");
                        return;
                    }

                    if (!cache.TryGetValue(_compression, out Stream found))
                    {
                        found = new WebSocketFrame(
                                  Fin.Final,
                                  opcode,
                                  data.Compress(_compression),
                                  _compression != CompressionMethod.None,
                                  false)
                                .ToMemory();

                        cache.Add(_compression, found);
                    }

                    SendBytes(found);
                }
            }
        }

        // As server
        internal void Send(
            OpCode opcode, Stream stream, Dictionary<CompressionMethod, Stream> cache)
        {
            lock (_forSend)
            {
                if (!cache.TryGetValue(_compression, out Stream found))
                {
                    found = stream.Compress(_compression);
                    cache.Add(_compression, found);
                }
                else
                {
                    found.Position = 0;
                }
                InternalSend(opcode, found, _compression != CompressionMethod.None);
            }
        }
        
        internal static OpCode GetOpCode(MessageFrameType type)
        {
            return type == MessageFrameType.Text ? OpCode.Text : OpCode.Binary;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Accepts the handshake request.
        /// </summary>
        /// <remarks>
        /// This method does nothing if the handshake request has already been
        /// accepted.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        ///   <para>
        ///   This instance is a client.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The close process is in progress.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The connection has already been closed.
        ///   </para>
        /// </exception>
        public void Accept()
        {
            AssertIsNotClient();
        
            if (_readyState == WebSocketState.Closing)
                throw new InvalidOperationException("The close process is in progress.");

            if (_readyState == WebSocketState.Closed)
                throw new InvalidOperationException("The connection has already been closed.");

            if (InternalAccept())
                Open();
        }

        /// <summary>
        /// Accepts the handshake request asynchronously.
        /// </summary>
        /// <remarks>
        ///   <para>This method does not wait for the accept process to be complete.</para>
        ///   <para>This method does nothing if the handshake request has already been accepted.</para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        ///   <para>This instance is a client.</para>
        ///   -or-
        ///   <para>The close process is in progress.</para>
        ///   -or-
        ///   <para>The connection has already been closed.</para>
        /// </exception>
        public void AcceptAsync()
        {
            AssertIsNotClient();
        
            if (_readyState == WebSocketState.Closing)
                throw new InvalidOperationException("The close process is in progress.");
            
            if (_readyState == WebSocketState.Closed)
                throw new InvalidOperationException("The connection has already been closed.");
        
            Func<bool> acceptor = InternalAccept;
            acceptor.BeginInvoke(
                ar =>
                {
                    if (acceptor.EndInvoke(ar))
                        Open();
                },
                null);
        }

        /// <summary>
        /// Closes the connection.
        /// </summary>
        /// <remarks>
        /// This method does nothing if the current state of the connection is Closing or Closed.
        /// </remarks>
        public void Close()
        {
            InternalClose(1005, string.Empty);
        }

        /// <summary>
        /// Closes the connection with the specified code.
        /// </summary>
        /// <remarks>
        /// This method does nothing if the current state of the connection is Closing or Closed.
        /// </remarks>
        /// <param name="code">
        ///   <para>
        ///   A <see cref="ushort"/> that represents the status code indicating the reason for the close.
        ///   </para>
        ///   <para>
        ///   The status codes are defined in
        ///   <see href="http://tools.ietf.org/html/rfc6455#section-7.4">Section 7.4</see> of RFC 6455.
        ///   </para>
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="code"/> is less than 1000 or greater than 4999.
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para><paramref name="code"/> is 1011 (server error). It cannot be used by clients.</para>
        ///   -or-
        ///   <para>
        ///   <paramref name="code"/> is 1010 (mandatory extension). It cannot be used by servers.
        ///   </para>
        /// </exception>
        public void Close(ushort code)
        {
            if (!code.IsCloseStatusCode())
                throw new ArgumentOutOfRangeException(nameof(code), "Less than 1000 or greater than 4999.");
            
            if (_isClient && code == 1011)
                throw new ArgumentException("1011 cannot be used.", nameof(code));

            if (!_isClient && code == 1010)
                throw new ArgumentException("1010 cannot be used.", nameof(code));

            InternalClose(code, string.Empty);
        }

        /// <summary>
        /// Closes the connection with the specified code.
        /// </summary>
        /// <remarks>
        /// This method does nothing if the current state of the connection is Closing or Closed.
        /// </remarks>
        /// <param name="code">
        ///   <para>
        ///   One of the <see cref="CloseStatusCode"/> enum values.
        ///   </para>
        ///   <para>
        ///   It represents the status code indicating the reason for the close.
        ///   </para>
        /// </param>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="code"/> is
        ///   <see cref="CloseStatusCode.ServerError"/>.
        ///   It cannot be used by clients.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="code"/> is
        ///   <see cref="CloseStatusCode.MandatoryExtension"/>.
        ///   It cannot be used by servers.
        ///   </para>
        /// </exception>
        public void Close(CloseStatusCode code)
        {
            if (_isClient && code == CloseStatusCode.ServerError)
                throw new ArgumentException("ServerError cannot be used.", nameof(code));
            
            if (!_isClient && code == CloseStatusCode.MandatoryExtension)
                throw new ArgumentException("MandatoryExtension cannot be used.", nameof(code));
        
            InternalClose((ushort)code, string.Empty);
        }

        /// <summary>
        /// Closes the connection with the specified code and reason.
        /// </summary>
        /// <remarks>
        /// This method does nothing if the current state of the connection is Closing or Closed.
        /// </remarks>
        /// <param name="code">
        ///   <para>
        ///   A <see cref="ushort"/> that represents the status code indicating
        ///   the reason for the close.
        ///   </para>
        ///   <para>
        ///   The status codes are defined in
        ///   <see href="http://tools.ietf.org/html/rfc6455#section-7.4">
        ///   Section 7.4</see> of RFC 6455.
        ///   </para>
        /// </param>
        /// <param name="reason">
        ///   <para>
        ///   A <see cref="string"/> that represents the reason for the close.
        ///   </para>
        ///   <para>
        ///   The size must be 123 bytes or less in UTF-8.
        ///   </para>
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        ///   <para>
        ///   <paramref name="code"/> is less than 1000 or greater than 4999.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The size of <paramref name="reason"/> is greater than 123 bytes.
        ///   </para>
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="code"/> is 1011 (server error).
        ///   It cannot be used by clients.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="code"/> is 1010 (mandatory extension).
        ///   It cannot be used by servers.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="code"/> is 1005 (no status) and there is reason.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="reason"/> could not be UTF-8 encoded.
        ///   </para>
        /// </exception>
        public void Close(ushort code, string reason)
        {
            if (!code.IsCloseStatusCode())
                throw new ArgumentOutOfRangeException(nameof(code), "Less than 1000 or greater than 4999.");

            if (_isClient && code == 1011)
                throw new ArgumentException("1011 cannot be used.", nameof(code));
        
            if (!_isClient && code == 1010)
                throw new ArgumentException("1010 cannot be used.", nameof(code));
        
            if (reason.IsNullOrEmpty())
            {
                InternalClose(code, string.Empty);
                return;
            }

            if (code == 1005)
                throw new ArgumentException("1005 cannot be used.", nameof(code));

            if (!reason.TryGetUTF8EncodedBytes(out byte[] bytes))
                throw new ArgumentException("It could not be UTF-8 encoded.", nameof(reason));

            if (bytes.Length > 123)
                throw new ArgumentOutOfRangeException(nameof(reason), "Its size is greater than 123 bytes.");

            InternalClose(code, reason);
        }

        /// <summary>
        /// Closes the connection with the specified code and reason.
        /// </summary>
        /// <remarks>
        /// This method does nothing if the current state of the connection is Closing or Closed.
        /// </remarks>
        /// <param name="code">
        ///   <para>
        ///   One of the <see cref="CloseStatusCode"/> enum values.
        ///   </para>
        ///   <para>
        ///   It represents the status code indicating the reason for the close.
        ///   </para>
        /// </param>
        /// <param name="reason">
        ///   <para>
        ///   A <see cref="string"/> that represents the reason for the close.
        ///   </para>
        ///   <para>
        ///   The size must be 123 bytes or less in UTF-8.
        ///   </para>
        /// </param>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="code"/> is
        ///   <see cref="CloseStatusCode.ServerError"/>.
        ///   It cannot be used by clients.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="code"/> is
        ///   <see cref="CloseStatusCode.MandatoryExtension"/>.
        ///   It cannot be used by servers.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="code"/> is
        ///   <see cref="CloseStatusCode.NoStatus"/> and there is reason.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="reason"/> could not be UTF-8 encoded.
        ///   </para>
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The size of <paramref name="reason"/> is greater than 123 bytes.
        /// </exception>
        public void Close(CloseStatusCode code, string reason)
        {
            if (_isClient && code == CloseStatusCode.ServerError)
                throw new ArgumentException("ServerError cannot be used.", nameof(code));

            if (!_isClient && code == CloseStatusCode.MandatoryExtension)
                throw new ArgumentException("MandatoryExtension cannot be used.", nameof(code));

            if (reason.IsNullOrEmpty())
            {
                InternalClose((ushort)code, string.Empty);
                return;
            }

            if (code == CloseStatusCode.NoStatus)
                throw new ArgumentException("NoStatus cannot be used.", nameof(code));

            if (!reason.TryGetUTF8EncodedBytes(out byte[] bytes))
                throw new ArgumentException("It could not be UTF-8 encoded.", nameof(reason));

            if (bytes.Length > 123)
                throw new ArgumentOutOfRangeException(nameof(reason), "Its size is greater than 123 bytes.");

            InternalClose((ushort)code, reason);
        }

        /// <summary>
        /// Closes the connection asynchronously.
        /// </summary>
        /// <remarks>
        ///   <para>This method does not wait for the close to be complete.</para>
        ///   <para>This method does nothing if the current state of the connection is Closing or Closed.</para>
        /// </remarks>
        public void CloseAsync()
        {
            InternalCloseAsync(1005, string.Empty);
        }

        /// <summary>
        /// Closes the connection asynchronously with the specified code.
        /// </summary>
        /// <remarks>
        ///   <para>This method does not wait for the close to be complete.</para>
        ///   <para>This method does nothing if the current state of the connection is Closing or Closed.</para>
        /// </remarks>
        /// <param name="code">
        ///   <para>
        ///   A <see cref="ushort"/> that represents the status code indicating
        ///   the reason for the close.
        ///   </para>
        ///   <para>
        ///   The status codes are defined in
        ///   <see href="http://tools.ietf.org/html/rfc6455#section-7.4">
        ///   Section 7.4</see> of RFC 6455.
        ///   </para>
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="code"/> is less than 1000 or greater than 4999.
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="code"/> is 1011 (server error).
        ///   It cannot be used by clients.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="code"/> is 1010 (mandatory extension).
        ///   It cannot be used by servers.
        ///   </para>
        /// </exception>
        public void CloseAsync(ushort code)
        {
            if (!code.IsCloseStatusCode())
                throw new ArgumentOutOfRangeException(nameof(code), "Less than 1000 or greater than 4999.");

            if (_isClient && code == 1011)
                throw new ArgumentException("1011 cannot be used.", nameof(code));

            if (!_isClient && code == 1010)
                throw new ArgumentException("1010 cannot be used.", nameof(code));
        
            InternalCloseAsync(code, string.Empty);
        }

        /// <summary>
        /// Closes the connection asynchronously with the specified code.
        /// </summary>
        /// <remarks>
        ///   <para>This method does not wait for the close to be complete.</para>
        ///   <para>This method does nothing if the current state of the connection is Closing or Closed.</para>
        /// </remarks>
        /// <param name="code">
        ///   <para>
        ///   One of the <see cref="CloseStatusCode"/> enum values.
        ///   </para>
        ///   <para>
        ///   It represents the status code indicating the reason for the close.
        ///   </para>
        /// </param>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="code"/> is
        ///   <see cref="CloseStatusCode.ServerError"/>.
        ///   It cannot be used by clients.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="code"/> is
        ///   <see cref="CloseStatusCode.MandatoryExtension"/>.
        ///   It cannot be used by servers.
        ///   </para>
        /// </exception>
        public void CloseAsync(CloseStatusCode code)
        {
            if (_isClient && code == CloseStatusCode.ServerError)
                throw new ArgumentException("ServerError cannot be used.", nameof(code));

            if (!_isClient && code == CloseStatusCode.MandatoryExtension)
                throw new ArgumentException("MandatoryExtension cannot be used.", nameof(code));

            InternalCloseAsync((ushort)code, string.Empty);
        }

        /// <summary>
        /// Closes the connection asynchronously with the specified code and reason.
        /// </summary>
        /// <remarks>
        ///   <para>This method does not wait for the close to be complete.</para>
        ///   <para>This method does nothing if the current state of the connection is Closing or Closed.</para>
        /// </remarks>
        /// <param name="code">
        ///   <para>
        ///   A <see cref="ushort"/> that represents the status code indicating
        ///   the reason for the close.
        ///   </para>
        ///   <para>
        ///   The status codes are defined in
        ///   <see href="http://tools.ietf.org/html/rfc6455#section-7.4">
        ///   Section 7.4</see> of RFC 6455.
        ///   </para>
        /// </param>
        /// <param name="reason">
        ///   <para>
        ///   A <see cref="string"/> that represents the reason for the close.
        ///   </para>
        ///   <para>
        ///   The size must be 123 bytes or less in UTF-8.
        ///   </para>
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        ///   <para>
        ///   <paramref name="code"/> is less than 1000 or greater than 4999.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The size of <paramref name="reason"/> is greater than 123 bytes.
        ///   </para>
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="code"/> is 1011 (server error).
        ///   It cannot be used by clients.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="code"/> is 1010 (mandatory extension).
        ///   It cannot be used by servers.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="code"/> is 1005 (no status) and there is reason.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="reason"/> could not be UTF-8 encoded.
        ///   </para>
        /// </exception>
        public void CloseAsync(ushort code, string reason)
        {
            if (!code.IsCloseStatusCode())
                throw new ArgumentOutOfRangeException(nameof(code), "Less than 1000 or greater than 4999.");
        
            if (_isClient && code == 1011)
                throw new ArgumentException("1011 cannot be used.", nameof(code));
        
            if (!_isClient && code == 1010)
                throw new ArgumentException("1010 cannot be used.", nameof(code));
        
            if (reason.IsNullOrEmpty())
            {
                InternalCloseAsync(code, string.Empty);
                return;
            }

            if (code == 1005)
                throw new ArgumentException("1005 cannot be used.", nameof(code));
        
            if (!reason.TryGetUTF8EncodedBytes(out byte[] bytes))
                throw new ArgumentException("It could not be UTF-8 encoded.", nameof(reason));
        
            if (bytes.Length > 123)
                throw new ArgumentOutOfRangeException(nameof(reason), "Its size is greater than 123 bytes.");
        
            InternalCloseAsync(code, reason);
        }

        /// <summary>
        /// Closes the connection asynchronously with the specified code and reason.
        /// </summary>
        /// <remarks>
        ///   <para>This method does not wait for the close to be complete.</para>
        ///   <para>This method does nothing if the current state of the connection is Closing or Closed.</para>
        /// </remarks>
        /// <param name="code">
        ///   <para>One of the <see cref="CloseStatusCode"/> enum values.</para>
        ///   <para>It represents the status code indicating the reason for the close.</para>
        /// </param>
        /// <param name="reason">
        ///   <para>A <see cref="string"/> that represents the reason for the close.</para>
        ///   <para>The size must be 123 bytes or less in UTF-8.</para>
        /// </param>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="code"/> is <see cref="CloseStatusCode.ServerError"/>. It cannot be used by clients.
        ///   </para>
        ///   -or-
        ///   <para>
        ///   <paramref name="code"/> is <see cref="CloseStatusCode.MandatoryExtension"/>. It cannot be used by servers.
        ///   </para>
        ///   -or-
        ///   <para><paramref name="code"/> is <see cref="CloseStatusCode.NoStatus"/> and there is reason.</para>
        ///   -or-
        ///   <para><paramref name="reason"/> could not be UTF-8 encoded.</para>
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The size of <paramref name="reason"/> is greater than 123 bytes.
        /// </exception>
        public void CloseAsync(CloseStatusCode code, string reason)
        {
            if (_isClient && code == CloseStatusCode.ServerError)
                throw new ArgumentException("ServerError cannot be used.", nameof(code));
        
            if (!_isClient && code == CloseStatusCode.MandatoryExtension)
                throw new ArgumentException("MandatoryExtension cannot be used.", nameof(code));
        
            if (reason.IsNullOrEmpty())
            {
                InternalCloseAsync((ushort)code, string.Empty);
                return;
            }

            if (code == CloseStatusCode.NoStatus)
                throw new ArgumentException("NoStatus cannot be used.", nameof(code));
        
            if (!reason.TryGetUTF8EncodedBytes(out byte[] bytes))
                throw new ArgumentException("It could not be UTF-8 encoded.", nameof(reason));

            if (bytes.Length > 123)
                throw new ArgumentOutOfRangeException(nameof(reason), "Its size is greater than 123 bytes.");
            
            InternalCloseAsync((ushort)code, reason);
        }

        /// <summary>
        /// Establishes a connection.
        /// </summary>
        /// <remarks>
        /// This method does nothing if the connection has already been established.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        ///   <para>
        ///   This instance is not a client.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The close process is in progress.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   A series of reconnecting has failed.
        ///   </para>
        /// </exception>
        public void Connect()
        {
            AssertIsClient();
        
            if (_readyState == WebSocketState.Closing)
                throw new InvalidOperationException("The close process is in progress.");
            
            if (_retryCountForConnect > _maxRetryCountForConnect)
                throw new InvalidOperationException("A series of reconnecting has failed.");
            
            if (InternalConnect())
                Open();
        }

        /// <summary>
        /// Establishes a connection asynchronously.
        /// </summary>
        /// <remarks>
        ///   <para>
        ///   This method does not wait for the connect process to be complete.
        ///   </para>
        ///   <para>
        ///   This method does nothing if the connection has already been
        ///   established.
        ///   </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        ///   <para>
        ///   This instance is not a client.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The close process is in progress.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   A series of reconnecting has failed.
        ///   </para>
        /// </exception>
        public void ConnectAsync()
        {
            if (!_isClient)
                throw new InvalidOperationException("This instance is not a client.");
        
            if (_readyState == WebSocketState.Closing)
                throw new InvalidOperationException("The close process is in progress.");

            if (_retryCountForConnect > _maxRetryCountForConnect)
                throw new InvalidOperationException("A series of reconnecting has failed.");
        
            Func<bool> connector = InternalConnect;
            connector.BeginInvoke(
                ar =>
                {
                    if (connector.EndInvoke(ar))
                        Open();
                },
                null);
        }

        /// <summary>
        /// Sends a ping using the WebSocket connection.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the send has done with no error and a pong has been
        /// received within a time; otherwise, <c>false</c>.
        /// </returns>
        public bool Ping()
        {
            return Ping(Array.Empty<byte>());
        }

        /// <summary>
        /// Sends a ping with <paramref name="message"/> using the WebSocket
        /// connection.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the send has done with no error and a pong has been
        /// received within a time; otherwise, <c>false</c>.
        /// </returns>
        /// <param name="message">
        ///   <para>
        ///   A <see cref="string"/> that represents the message to send.
        ///   </para>
        ///   <para>
        ///   The size must be 125 bytes or less in UTF-8.
        ///   </para>
        /// </param>
        /// <exception cref="ArgumentException">
        /// <paramref name="message"/> could not be UTF-8 encoded.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The size of <paramref name="message"/> is greater than 125 bytes.
        /// </exception>
        public bool Ping(string message)
        {
            if (message.IsNullOrEmpty())
                return Ping();

            if (!message.TryGetUTF8EncodedBytes(out byte[] bytes))
                throw new ArgumentException("It could not be UTF-8 encoded.", nameof(message));
        
            if (bytes.Length > 125)
                throw new ArgumentOutOfRangeException(nameof(message), "Its size is greater than 125 bytes.");
        
            return Ping(bytes);
        }

        public void SendAsJson(object value, JsonSerializer serializer)
        {
            AssertOpen();

            using (var tmp = RecyclableMemoryManager.Shared.GetStream())
            using (var writer = new StreamWriter(tmp, Ext.PlainUTF8))
            {
                serializer.Serialize(writer, value);
                writer.Flush();
                tmp.Position = 0;
                Send(MessageFrameType.Text, tmp, (int)tmp.Length);
            }
        }

        /// <summary>
        /// Sends the specified data using the WebSocket connection.
        /// </summary>
        /// <param name="data">
        /// An array of <see cref="byte"/> that represents the binary data to send.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The current state of the connection is not Open.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="data"/> is <see langword="null"/>.
        /// </exception>
        public void Send(MessageFrameType type, byte[] data)
        {
            AssertOpen();

            if (data == null)
                throw new ArgumentNullException(nameof(data));

            InternalSend(GetOpCode(type), RecyclableMemoryManager.Shared.GetStream(data));
        }

        /// <summary>
        /// Sends the specified file using the WebSocket connection.
        /// </summary>
        /// <param name="fileInfo">
        ///   <para>
        ///   A <see cref="FileInfo"/> that specifies the file to send.
        ///   </para>
        ///   <para>
        ///   The file is sent as the binary data.
        ///   </para>
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The current state of the connection is not Open.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="fileInfo"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   The file does not exist.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The file could not be opened.
        ///   </para>
        /// </exception>
        public void Send(MessageFrameType type, FileInfo fileInfo)
        {
            AssertOpen();
            AssertValidFileInfo(fileInfo, out var stream);
            InternalSend(GetOpCode(type), stream);
        }

        /// <summary>
        /// Sends the specified data using the WebSocket connection.
        /// </summary>
        /// <param name="text">
        /// A <see cref="string"/> that represents the text data to send.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The current state of the connection is not Open.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="text"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="text"/> could not be UTF-8 encoded.
        /// </exception>
        public void Send(string text)
        {
            AssertOpen();

            if (text == null)
                throw new ArgumentNullException(nameof(text));

            using (var tmp = RecyclableMemoryManager.Shared.GetStream())
            using (var writer = new StreamWriter(tmp, Ext.PlainUTF8))
            {
                writer.Write(text);
                writer.Flush();
                tmp.Position = 0;
                InternalSend(OpCode.Text, tmp);
            }
        }

        /// <summary>
        /// Sends the data from the specified stream using the WebSocket connection.
        /// </summary>
        /// <param name="stream">
        ///   <para>
        ///   A <see cref="Stream"/> instance from which to read the data to send.
        ///   </para>
        ///   <para>
        ///   The data is sent as the binary data.
        ///   </para>
        /// </param>
        /// <param name="length">
        /// An <see cref="int"/> that specifies the number of bytes to send.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The current state of the connection is not Open.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="stream"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="stream"/> cannot be read.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="length"/> is less than 1.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   No data could be read from <paramref name="stream"/>.
        ///   </para>
        /// </exception>
        public void Send(MessageFrameType type, Stream stream, int length)
        {
            AssertOpen();
            AssertValidStream(stream, length, out var bytes);            
            InternalSend(type == MessageFrameType.Text ? OpCode.Text : OpCode.Binary, bytes);
        }

        /// <summary>
        /// Sends the specified data asynchronously using the WebSocket connection.
        /// </summary>
        /// <remarks>
        /// This method does not wait for the send to be complete.
        /// </remarks>
        /// <param name="data">
        /// An array of <see cref="byte"/> that represents the binary data to send.
        /// </param>
        /// <param name="completed">
        ///   <para>
        ///   An <c>Action&lt;bool&gt;</c> delegate or <see langword="null"/>
        ///   if not needed.
        ///   </para>
        ///   <para>
        ///   The delegate invokes the method called when the send is complete.
        ///   </para>
        ///   <para>
        ///   <c>true</c> is passed to the method if the send has done with
        ///   no error; otherwise, <c>false</c>.
        ///   </para>
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The current state of the connection is not Open.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="data"/> is <see langword="null"/>.
        /// </exception>
        public void SendAsync(MessageFrameType type, byte[] data, Action<bool> completed)
        {
            AssertOpen();

            if (data == null)
                throw new ArgumentNullException(nameof(data));

            InternalSendAsync(GetOpCode(type), RecyclableMemoryManager.Shared.GetStream(data), completed);
        }

        /// <summary>
        /// Sends the specified file asynchronously using the WebSocket connection.
        /// </summary>
        /// <remarks>
        /// This method does not wait for the send to be complete.
        /// </remarks>
        /// <param name="fileInfo">
        ///   <para>
        ///   A <see cref="FileInfo"/> that specifies the file to send.
        ///   </para>
        ///   <para>
        ///   The file is sent as the binary data.
        ///   </para>
        /// </param>
        /// <param name="completed">
        ///   <para>
        ///   An <c>Action&lt;bool&gt;</c> delegate or <see langword="null"/>
        ///   if not needed.
        ///   </para>
        ///   <para>
        ///   The delegate invokes the method called when the send is complete.
        ///   </para>
        ///   <para>
        ///   <c>true</c> is passed to the method if the send has done with
        ///   no error; otherwise, <c>false</c>.
        ///   </para>
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The current state of the connection is not Open.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="fileInfo"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   The file does not exist.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The file could not be opened.
        ///   </para>
        /// </exception>
        public void SendAsync(MessageFrameType type, FileInfo fileInfo, Action<bool> completed)
        {
            AssertOpen();
            AssertValidFileInfo(fileInfo, out var stream);
            InternalSendAsync(GetOpCode(type), stream, completed);
        }

        /// <summary>
        /// Sends the specified data asynchronously using the WebSocket connection.
        /// </summary>
        /// <remarks>
        /// This method does not wait for the send to be complete.
        /// </remarks>
        /// <param name="text">
        /// A <see cref="string"/> that represents the text data to send.
        /// </param>
        /// <param name="completed">
        ///   <para>
        ///   An <c>Action&lt;bool&gt;</c> delegate or <see langword="null"/>
        ///   if not needed.
        ///   </para>
        ///   <para>
        ///   The delegate invokes the method called when the send is complete.
        ///   </para>
        ///   <para>
        ///   <c>true</c> is passed to the method if the send has done with
        ///   no error; otherwise, <c>false</c>.
        ///   </para>
        /// </param>
        /// <exception cref="InvalidOperationException">The connection is not open.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="text"/> could not be UTF-8 encoded.</exception>
        public void SendAsync(string text, Action<bool> completed)
        {
            AssertOpen();

            if (text == null)
                throw new ArgumentNullException(nameof(text));

            using (var tmp = RecyclableMemoryManager.Shared.GetStream())
            using (var writer = new StreamWriter(tmp, Ext.PlainUTF8))
            {
                writer.Write(text);
                writer.Flush();
                tmp.Position = 0;
                InternalSendAsync(OpCode.Text, tmp, completed);
            }
        }

        /// <summary>
        /// Sends the data from the specified stream asynchronously using the WebSocket connection.
        /// </summary>
        /// <remarks>This method does not wait for the send to be complete.</remarks>
        /// <param name="stream">
        ///   <para>A <see cref="Stream"/> instance from which to read the data to send.</para>
        ///   <para>The data is sent as the binary data.</para>
        /// </param>
        /// <param name="length">
        /// An <see cref="int"/> that specifies the number of bytes to send.
        /// </param>
        /// <param name="completed">
        ///   <para>
        ///   An <c>Action&lt;bool&gt;</c> delegate or <see langword="null"/> if not needed.
        ///   </para>
        ///   <para>The delegate invokes the method called when the send is complete.</para>
        ///   <para>
        ///   <c>true</c> is passed to the method if the send has done with
        ///   no error; otherwise, <c>false</c>.
        ///   </para>
        /// </param>
        /// <exception cref="InvalidOperationException">The connection is not open.</exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="stream"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para><paramref name="stream"/> cannot be read.</para>
        ///   -or-
        ///   <para><paramref name="length"/> is less than 1.</para>
        ///   -or-
        ///   <para>No data could be read from <paramref name="stream"/>.</para>
        /// </exception>
        public void SendAsync(MessageFrameType type, Stream stream, int length, Action<bool> completed)
        {
            AssertOpen();
            AssertValidStream(stream, length, out var bytes);
            InternalSendAsync(GetOpCode(type), bytes, completed);
        }

        /// <summary>
        /// Sets an HTTP cookie to send with the handshake request.
        /// </summary>
        /// <remarks>
        /// This method does nothing if the connection has already been
        /// established or it is closing.
        /// </remarks>
        /// <param name="cookie">A <see cref="Cookie"/> that represents the cookie to send.</param>
        /// <exception cref="InvalidOperationException">This instance is not a client.</exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="cookie"/> is <see langword="null"/>.
        /// </exception>
        public void SetCookie(Cookie cookie)
        {
            AssertIsClient();

            if (cookie == null)
                throw new ArgumentNullException(nameof(cookie));

            if (!CanSet(out string msg))
            {
                _logger.Warn(msg);
                return;
            }

            lock (_forState)
            {
                if (!CanSet(out msg))
                {
                    _logger.Warn(msg);
                    return;
                }

                lock (_cookies.SyncRoot)
                    _cookies.SetOrRemove(cookie);
            }
        }

        /// <summary>
        /// Sets the credentials for the HTTP authentication (Basic/Digest).
        /// </summary>
        /// <remarks>
        /// This method does nothing if the connection has already been
        /// established or it is closing.
        /// </remarks>
        /// <param name="username">
        ///   <para>
        ///   A <see cref="string"/> that represents the username associated with the credentials.
        ///   </para>
        ///   <para>
        ///   <see langword="null"/> or an empty string if initializes the credentials.
        ///   </para>
        /// </param>
        /// <param name="password">
        ///   <para>
        ///   A <see cref="string"/> that represents the password for the username
        ///   associated with the credentials.
        ///   </para>
        ///   <para><see langword="null"/> or an empty string if not necessary.</para>
        /// </param>
        /// <param name="preAuth">
        /// <c>true</c> if sends the credentials for the Basic authentication in
        /// advance with the first handshake request; otherwise, <c>false</c>.
        /// </param>
        /// <exception cref="InvalidOperationException">This instance is not a client.</exception>
        /// <exception cref="ArgumentException">
        ///   <para><paramref name="username"/> contains an invalid character.</para>
        ///   -or-
        ///   <para><paramref name="password"/> contains an invalid character.</para>
        /// </exception>
        public void SetCredentials(string username, string password, bool preAuth)
        {
            AssertIsClient();

            if (!username.IsNullOrEmpty())
                if (username.Contains(':') || !username.AsSpan().IsText())
                    throw new ArgumentException("It contains an invalid character.", nameof(username));
             

            if (!password.IsNullOrEmpty())
                if (!password.AsSpan().IsText())
                    throw new ArgumentException("It contains an invalid character.", nameof(password));

            if (!CanSet(out string msg))
            {
                _logger.Warn(msg);
                return;
            }

            lock (_forState)
            {
                if (!CanSet(out msg))
                {
                    _logger.Warn(msg);
                    return;
                }

                if (username.IsNullOrEmpty())
                {
                    _credentials = null;
                    _preAuth = false;
                    return;
                }

                _credentials = new NetworkCredential(username, password, _uri.PathAndQuery);
                _preAuth = preAuth;
            }
        }

        /// <summary>
        /// Sets the URL of the HTTP proxy server through which to connect and
        /// the credentials for the HTTP proxy authentication (Basic/Digest).
        /// </summary>
        /// <remarks>
        /// This method does nothing if the connection has already been established or it is closing.
        /// </remarks>
        /// <param name="url">
        ///   <para>
        ///   A <see cref="string"/> that represents the URL of the proxy server through which to connect.
        ///   </para>
        ///   <para>The syntax is http://&lt;host&gt;[:&lt;port&gt;]. </para>
        ///   <para>
        ///   <see langword="null"/> or an empty string if initializes the URL and the credentials.
        ///   </para>
        /// </param>
        /// <param name="username">
        ///   <para>
        ///   A <see cref="string"/> that represents the username associated with the credentials.
        ///   </para>
        ///   <para>
        ///   <see langword="null"/> or an empty string if the credentials are not necessary.
        ///   </para>
        /// </param>
        /// <param name="password">
        ///   <para>
        ///   A <see cref="string"/> that represents the password for the username
        ///   associated with the credentials.
        ///   </para>
        ///   <para><see langword="null"/> or an empty string if not necessary.</para>
        /// </param>
        /// <exception cref="InvalidOperationException">This instance is not a client. </exception>
        /// <exception cref="ArgumentException">
        ///   <para><paramref name="url"/> is not an absolute URI string.</para>
        ///   -or-
        ///   <para>The scheme of <paramref name="url"/> is not HTTP.</para>
        ///   -or-
        ///   <para><paramref name="url"/> includes the path segments.</para>
        ///   -or-
        ///   <para><paramref name="username"/> contains an invalid character.</para>
        ///   -or-
        ///   <para><paramref name="password"/> contains an invalid character.</para>
        /// </exception>
        public void SetProxy(string url, string username, string password)
        {
            string msg;
            if (!_isClient)
            {
                msg = "This instance is not a client.";
                throw new InvalidOperationException(msg);
            }

            Uri uri = null;
            if (!url.IsNullOrEmpty())
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                    throw new ArgumentException("Not an absolute URI string.", nameof(url));

                if (uri.Scheme != "http")
                    throw new ArgumentException("The scheme part is not HTTP.", nameof(url));

                if (uri.Segments.Length > 1)
                    throw new ArgumentException("It includes the path segments.", nameof(url));
            }

            if (!username.IsNullOrEmpty())
                if (username.Contains(':') || !username.AsSpan().IsText())
                    throw new ArgumentException("It contains an invalid character.", nameof(username));

            if (!password.IsNullOrEmpty())
            {
                if (!password.AsSpan().IsText())
                    throw new ArgumentException("It contains an invalid character.", nameof(password));
            }

            if (!CanSet(out msg))
            {
                _logger.Warn(msg);
                return;
            }

            lock (_forState)
            {
                if (!CanSet(out msg))
                {
                    _logger.Warn(msg);
                    return;
                }

                if (url.IsNullOrEmpty())
                {
                    _proxyUri = null;
                    _proxyCredentials = null;
                    return;
                }

                _proxyUri = uri;
                _proxyCredentials = !username.IsNullOrEmpty()
                                    ? new NetworkCredential(
                                        username, password, $"{_uri.DnsSafeHost}:{_uri.Port}")
                                    : null;
            }
        }

        #endregion

        #region Explicit Interface Implementations

        /// <summary>
        /// Closes the connection and releases all associated resources.
        /// </summary>
        /// <remarks>
        ///   This method closes the connection with close status 1001 (going away).
        ///   And this method does nothing if connection is closing or closed.
        /// </remarks>
        void IDisposable.Dispose()
        {
            InternalClose(1001, string.Empty);
        }

        #endregion
    }
}
