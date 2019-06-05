#region License
/*
 * WebSocketSessionManager.cs
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

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Timers;
using WebSocketSharp.Memory;

namespace WebSocketSharp.Server
{
    /// <summary>
    /// Provides the management function for the sessions in a WebSocket service.
    /// </summary>
    /// <remarks>
    /// This class manages the sessions in a WebSocket service provided by
    /// the <see cref="WebSocketServer"/> or <see cref="HttpServer"/>.
    /// </remarks>
    public class WebSocketSessionManager
    {
        #region Private Fields

        private volatile bool _clean;
        private object _forSweep;
        private Logger _log;
        private Dictionary<string, IWebSocketSession> _sessions;
        private volatile ServerState _state;
        private volatile bool _sweeping;
        private System.Timers.Timer _sweepTimer;
        private object _sync;
        private TimeSpan _waitTime;

        #endregion

        #region Internal Constructors

        internal WebSocketSessionManager(Logger log)
        {
            _log = log;

            _clean = true;
            _forSweep = new object();
            _sessions = new Dictionary<string, IWebSocketSession>();
            _state = ServerState.Ready;
            _sync = ((ICollection)_sessions).SyncRoot;
            _waitTime = TimeSpan.FromSeconds(1);

            SetSweepTimer(60000);
        }

        #endregion

        #region Internal Properties

        internal ServerState State => _state;

        #endregion

        #region Public Properties

        /// <summary>
        /// Gets the IDs for the active sessions in the service.
        /// </summary>
        /// <value>
        ///   <para>
        ///   An <c>IEnumerable&lt;string&gt;</c> instance.
        ///   </para>
        ///   <para>
        ///   It provides an enumerator which supports the iteration over
        ///   the collection of the IDs for the active sessions.
        ///   </para>
        /// </value>
        public IEnumerable<string> ActiveIDs
        {
            get
            {
                foreach (var res in Broadping(WebSocketFrame.EmptyPingBytes))
                {
                    if (res.Value)
                        yield return res.Key;
                }
            }
        }

        /// <summary>
        /// Gets the number of the sessions in the service.
        /// </summary>
        /// <value>
        /// An <see cref="int"/> that represents the number of the sessions.
        /// </value>
        public int Count
        {
            get
            {
                lock (_sync)
                    return _sessions.Count;
            }
        }

        /// <summary>
        /// Gets the IDs for the sessions in the service.
        /// </summary>
        /// <value>
        ///   <para>
        ///   An <c>IEnumerable&lt;string&gt;</c> instance.
        ///   </para>
        ///   <para>
        ///   It provides an enumerator which supports the iteration over
        ///   the collection of the IDs for the sessions.
        ///   </para>
        /// </value>
        public List<string> GetIDs()
        {
            if (_state != ServerState.Start)
                return null;

            lock (_sync)
            {
                if (_state != ServerState.Start)
                    return null;

                return _sessions.Keys.ToList();
            }
        }

        /// <summary>
        /// Gets the IDs for the inactive sessions in the service.
        /// </summary>
        /// <value>
        ///   <para>
        ///   An <c>IEnumerable&lt;string&gt;</c> instance.
        ///   </para>
        ///   <para>
        ///   It provides an enumerator which supports the iteration over
        ///   the collection of the IDs for the inactive sessions.
        ///   </para>
        /// </value>
        public IEnumerable<string> InactiveIDs
        {
            get
            {
                foreach (var res in Broadping(WebSocketFrame.EmptyPingBytes))
                {
                    if (!res.Value)
                        yield return res.Key;
                }
            }
        }

        /// <summary>
        /// Gets the session instance with <paramref name="id"/>.
        /// </summary>
        /// <value>
        ///   <para>
        ///   A <see cref="IWebSocketSession"/> instance or <see langword="null"/>
        ///   if not found.
        ///   </para>
        ///   <para>
        ///   The session instance provides the function to access the information
        ///   in the session.
        ///   </para>
        /// </value>
        /// <param name="id">
        /// A <see cref="string"/> that represents the ID of the session to find.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="id"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="id"/> is an empty string.
        /// </exception>
        public IWebSocketSession this[string id]
        {
            get
            {
                if (id == null)
                    throw new ArgumentNullException("id");

                if (id.Length == 0)
                    throw new ArgumentException("An empty string.", "id");

                InternalTryGetSession(id, out IWebSocketSession session);

                return session;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether the inactive sessions in
        /// the WebSocket service are cleaned up periodically.
        /// </summary>
        /// <remarks>
        /// The set operation does nothing if the service has already started or
        /// it is shutting down.
        /// </remarks>
        /// <value>
        /// <c>true</c> if the inactive sessions are cleaned up every 60 seconds;
        /// otherwise, <c>false</c>.
        /// </value>
        public bool KeepClean
        {
            get => _clean;

            set
            {
                if (!CanSet(out string msg))
                {
                    _log.Warn(msg);
                    return;
                }

                lock (_sync)
                {
                    if (!CanSet(out msg))
                    {
                        _log.Warn(msg);
                        return;
                    }

                    _clean = value;
                }
            }
        }

        /// <summary>
        /// Gets the session instances in the service.
        /// </summary>
        /// <value>
        ///   <para>
        ///   An <c>IEnumerable&lt;IWebSocketSession&gt;</c> instance.
        ///   </para>
        ///   <para>
        ///   It provides an enumerator which supports the iteration over
        ///   the collection of the session instances.
        ///   </para>
        /// </value>
        public List<IWebSocketSession> GetSessions()
        {
            if (_state != ServerState.Start)
                return null;

            lock (_sync)
            {
                if (_state != ServerState.Start)
                    return null;

                return _sessions.Values.ToList();
            }
        }

        /// <summary>
        /// Gets or sets the time to wait for the response to the WebSocket Ping or
        /// Close.
        /// </summary>
        /// <remarks>
        /// The set operation does nothing if the service has already started or
        /// it is shutting down.
        /// </remarks>
        /// <value>
        /// A <see cref="TimeSpan"/> to wait for the response.
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
                    _log.Warn(msg);
                    return;
                }

                lock (_sync)
                {
                    if (!CanSet(out msg))
                    {
                        _log.Warn(msg);
                        return;
                    }

                    _waitTime = value;
                }
            }
        }

        #endregion

        #region Private Methods

        // TODO: reduce allocs
        private void Broadcast(OpCode opcode, byte[] data, Action completed)
        {
            var cache = new Dictionary<CompressionMethod, Stream>();
            try
            {
                var sessions = GetSessions();
                foreach (var session in sessions)
                {
                    if (_state != ServerState.Start)
                    {
                        _log.Error("The service is shutting down.");
                        break;
                    }
                    session.Context.WebSocket.Send(opcode, data, cache);
                }

                completed?.Invoke();
            }
            catch (Exception ex)
            {
                _log.Error(ex.Message);
                _log.Debug(ex.ToString());
            }
            finally
            {
                cache.Clear();
            }
        }

        // TODO: reduce allocs
        private void Broadcast(OpCode opcode, Stream stream, Action completed)
        {
            var cache = new Dictionary<CompressionMethod, Stream>();
            try
            {
                var sessions = GetSessions();
                foreach (var session in sessions)
                {
                    if (_state != ServerState.Start)
                    {
                        _log.Error("The service is shutting down.");
                        break;
                    }
                    session.Context.WebSocket.Send(opcode, stream, cache);
                }

                completed?.Invoke();
            }
            catch (Exception ex)
            {
                _log.Error(ex.Message);
                _log.Debug(ex.ToString());
            }
            finally
            {
                foreach (var cached in cache.Values)
                    cached.Dispose();
                cache.Clear();
            }
        }

        private void BroadcastAsync(OpCode opcode, byte[] data, Action completed)
        {
            ThreadPool.QueueUserWorkItem(
              state => Broadcast(opcode, data, completed));
        }

        private void BroadcastAsync(OpCode opcode, Stream stream, Action completed)
        {
            ThreadPool.QueueUserWorkItem(
              state => Broadcast(opcode, stream, completed));
        }

        // TODO: reduce allocs
        private Dictionary<string, bool> Broadping(byte[] frameAsBytes)
        {
            var ret = new Dictionary<string, bool>();
            var sessions = GetSessions();
            foreach (var session in sessions)
            {
                if (_state != ServerState.Start)
                {
                    _log.Error("The service is shutting down.");
                    break;
                }
                var res = session.Context.WebSocket.Ping(frameAsBytes, _waitTime);
                ret.Add(session.ID, res);
            }
            return ret;
        }

        private bool CanSet(out string message)
        {
            if (_state == ServerState.Start)
            {
                message = "The service has already started.";
                return false;
            }

            if (_state == ServerState.ShuttingDown)
            {
                message = "The service is shutting down.";
                return false;
            }

            message = null;
            return true;
        }

        private static string CreateID()
        {
            return Guid.NewGuid().ToString("N");
        }

        private void SetSweepTimer(double interval)
        {
            _sweepTimer = new System.Timers.Timer(interval);
            _sweepTimer.Elapsed += (sender, e) => Sweep();
        }

        private void Stop(PayloadData payloadData, bool send)
        {
            var bytes = send 
                ? WebSocketFrame.CreateCloseFrame(payloadData, false).ToMemory()
                : null;

            try
            {
                lock (_sync)
                {
                    _state = ServerState.ShuttingDown;

                    _sweepTimer.Enabled = false;
                    foreach (var session in _sessions.Values)
                        session.Context.WebSocket.Close(payloadData, bytes);

                    _state = ServerState.Stop;
                }
            }
            finally
            {
                bytes?.Dispose();
            }
        }

        private bool InternalTryGetSession(string id, out IWebSocketSession session)
        {
            if (_state != ServerState.Start)
            {
                session = null;
                return false;
            }
            lock (_sync)
            {
                if (_state != ServerState.Start)
                {
                    session = null;
                    return false;
                }
                return _sessions.TryGetValue(id, out session);
            }
        }

        #endregion

        #region Internal Methods

        internal string Add(IWebSocketSession session)
        {
            lock (_sync)
            {
                if (_state != ServerState.Start)
                    return null;

                var id = CreateID();
                _sessions.Add(id, session);

                return id;
            }
        }

        internal bool Remove(string id)
        {
            lock (_sync)
                return _sessions.Remove(id);
        }

        internal void Start()
        {
            lock (_sync)
            {
                _sweepTimer.Enabled = _clean;
                _state = ServerState.Start;
            }
        }

        internal void Stop(ushort code, string reason)
        {
            if (code == 1005) // == no status
            { 
                Stop(PayloadData.Empty, true);
                return;
            }

            Stop(new PayloadData(code, reason), !code.IsReserved());
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Sends <paramref name="data"/> to every client in the service.
        /// </summary>
        /// <param name="data">
        /// An array of <see cref="byte"/> that represents the binary data to send.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The current state of the manager is not Start.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="data"/> is <see langword="null"/>.
        /// </exception>
        public void Broadcast(byte[] data)
        {
            if (_state != ServerState.Start)
                throw new InvalidOperationException("The the manager is not started.");

            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (data.LongLength <= WebSocket.FragmentLength)
                Broadcast(OpCode.Binary, data, null);
            else
                Broadcast(OpCode.Binary, RecyclableMemoryManager.Shared.GetStream(data), null);
        }

        /// <summary>
        /// Sends <paramref name="data"/> to every client in the service.
        /// </summary>
        /// <param name="data">
        /// A <see cref="string"/> that represents the text data to send.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The current state of the manager is not Start.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="data"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="data"/> could not be UTF-8 encoded.
        /// </exception>
        public void Broadcast(string data)
        {
            if (_state != ServerState.Start)
                throw new InvalidOperationException("The the manager is not started.");

            if (data == null)
                throw new ArgumentNullException("data");

            if (!data.TryGetUTF8EncodedBytes(out byte[] bytes))
                throw new ArgumentException("It could not be UTF-8 encoded.", nameof(data));

            if (bytes.LongLength <= WebSocket.FragmentLength)
                Broadcast(OpCode.Text, bytes, null);
            else
                Broadcast(OpCode.Text, RecyclableMemoryManager.Shared.GetStream(bytes), null);
        }

        /// <summary>
        /// Sends the data from <paramref name="stream"/> to every client in
        /// the WebSocket service.
        /// </summary>
        /// <remarks>
        /// The data is sent as the binary data.
        /// </remarks>
        /// <param name="stream">
        /// A <see cref="Stream"/> instance from which to read the data to send.
        /// </param>
        /// <param name="length">
        /// An <see cref="int"/> that specifies the number of bytes to send.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The current state of the manager is not Start.
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
        public void Broadcast(Stream stream, int length)
        {
            if (_state != ServerState.Start)
                throw new InvalidOperationException("The current state of the manager is not Start.");

            if (stream == null)
                throw new ArgumentNullException("stream");

            if (!stream.CanRead)
                throw new ArgumentException("It cannot be read.", nameof(stream));

            if (length < 1)
                throw new ArgumentException("Less than 1.", nameof(length));

            var bytes = stream.ReadBytes(length);
            var len = bytes.Length;
            if (len == 0)
                throw new ArgumentException("No data could be read from it.", nameof(stream));

            if (len < length)
                _log.Warn($"Only {len} byte(s) of data could be read from the stream.");

            Broadcast(OpCode.Binary, bytes, null);
        }

        /// <summary>
        /// Sends <paramref name="data"/> asynchronously to every client in
        /// the WebSocket service.
        /// </summary>
        /// <remarks>
        /// This method does not wait for the send to be complete.
        /// </remarks>
        /// <param name="data">
        /// An array of <see cref="byte"/> that represents the binary data to send.
        /// </param>
        /// <param name="completed">
        ///   <para>
        ///   An <see cref="Action"/> delegate or <see langword="null"/>
        ///   if not needed.
        ///   </para>
        ///   <para>
        ///   The delegate invokes the method called when the send is complete.
        ///   </para>
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The current state of the manager is not Start.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="data"/> is <see langword="null"/>.
        /// </exception>
        public void BroadcastAsync(byte[] data, Action completed)
        {
            if (_state != ServerState.Start)
            {
                var msg = "The current state of the manager is not Start.";
                throw new InvalidOperationException(msg);
            }

            if (data == null)
                throw new ArgumentNullException("data");

            if (data.LongLength <= WebSocket.FragmentLength)
                BroadcastAsync(OpCode.Binary, data, completed);
            else
                BroadcastAsync(OpCode.Binary, RecyclableMemoryManager.Shared.GetStream(data), completed);
        }

        /// <summary>
        /// Sends <paramref name="data"/> asynchronously to every client in
        /// the WebSocket service.
        /// </summary>
        /// <remarks>
        /// This method does not wait for the send to be complete.
        /// </remarks>
        /// <param name="data">
        /// A <see cref="string"/> that represents the text data to send.
        /// </param>
        /// <param name="completed">
        ///   <para>
        ///   An <see cref="Action"/> delegate or <see langword="null"/>
        ///   if not needed.
        ///   </para>
        ///   <para>
        ///   The delegate invokes the method called when the send is complete.
        ///   </para>
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The current state of the manager is not Start.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="data"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="data"/> could not be UTF-8 encoded.
        /// </exception>
        public void BroadcastAsync(string data, Action completed)
        {
            if (_state != ServerState.Start)
            {
                var msg = "The current state of the manager is not Start.";
                throw new InvalidOperationException(msg);
            }

            if (data == null)
                throw new ArgumentNullException("data");

            if (!data.TryGetUTF8EncodedBytes(out byte[] bytes))
            {
                var msg = "It could not be UTF-8 encoded.";
                throw new ArgumentException(msg, "data");
            }

            if (bytes.LongLength <= WebSocket.FragmentLength)
                BroadcastAsync(OpCode.Text, bytes, completed);
            else
                BroadcastAsync(OpCode.Text, RecyclableMemoryManager.Shared.GetStream(bytes), completed);
        }

        /// <summary>
        /// Sends the data from <paramref name="stream"/> asynchronously to
        /// every client in the service.
        /// </summary>
        /// <remarks>
        ///   <para>
        ///   The data is sent as the binary data.
        ///   </para>
        ///   <para>
        ///   This method does not wait for the send to be complete.
        ///   </para>
        /// </remarks>
        /// <param name="stream">
        /// A <see cref="Stream"/> instance from which to read the data to send.
        /// </param>
        /// <param name="length">
        /// An <see cref="int"/> that specifies the number of bytes to send.
        /// </param>
        /// <param name="completed">
        ///   <para>
        ///   An <see cref="Action"/> delegate or <see langword="null"/>
        ///   if not needed.
        ///   </para>
        ///   <para>
        ///   The delegate invokes the method called when the send is complete.
        ///   </para>
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The current state of the manager is not Start.
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
        public void BroadcastAsync(Stream stream, int length, Action completed)
        {
            if (_state != ServerState.Start)
                throw new InvalidOperationException("The current state of the manager is not Start.");
            
            if (stream == null)
                throw new ArgumentNullException("stream");

            if (!stream.CanRead)
                throw new ArgumentException("It cannot be read.", "stream");
            
            if (length < 1)
                throw new ArgumentException("Less than 1.", "length");
        
            var bytes = stream.ReadBytes(length);
            if (bytes.Length == 0)
                throw new ArgumentException("No data could be read from it.", "stream");
            
            if (bytes.Length < length)
                _log.Warn($"Only {bytes.Length} byte(s) of data could be read from the stream.");

            BroadcastAsync(OpCode.Binary, bytes, completed);
        }

        /// <summary>
        /// Closes the specified session.
        /// </summary>
        /// <param name="id">
        /// A <see cref="string"/> that represents the ID of the session to close.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="id"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="id"/> is an empty string.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The session could not be found.
        /// </exception>
        public void CloseSession(string id)
        {
            if (!TryGetSession(id, out IWebSocketSession session))
            {
                var msg = "The session could not be found.";
                throw new InvalidOperationException(msg);
            }

            session.Context.WebSocket.Close();
        }

        /// <summary>
        /// Closes the specified session with <paramref name="code"/> and
        /// <paramref name="reason"/>.
        /// </summary>
        /// <param name="id">
        /// A <see cref="string"/> that represents the ID of the session to close.
        /// </param>
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
        /// <exception cref="ArgumentNullException">
        /// <paramref name="id"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="id"/> is an empty string.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="code"/> is 1010 (mandatory extension).
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="code"/> is 1005 (no status) and there is
        ///   <paramref name="reason"/>.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="reason"/> could not be UTF-8 encoded.
        ///   </para>
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The session could not be found.
        /// </exception>
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
        public void CloseSession(string id, ushort code, string reason)
        {
            if (!TryGetSession(id, out IWebSocketSession session))
            {
                var msg = "The session could not be found.";
                throw new InvalidOperationException(msg);
            }

            session.Context.WebSocket.Close(code, reason);
        }

        /// <summary>
        /// Closes the specified session with <paramref name="code"/> and
        /// <paramref name="reason"/>.
        /// </summary>
        /// <param name="id">
        /// A <see cref="string"/> that represents the ID of the session to close.
        /// </param>
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
        /// <exception cref="ArgumentNullException">
        /// <paramref name="id"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="id"/> is an empty string.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="code"/> is
        ///   <see cref="CloseStatusCode.MandatoryExtension"/>.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="code"/> is
        ///   <see cref="CloseStatusCode.NoStatus"/> and there is
        ///   <paramref name="reason"/>.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="reason"/> could not be UTF-8 encoded.
        ///   </para>
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The session could not be found.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The size of <paramref name="reason"/> is greater than 123 bytes.
        /// </exception>
        public void CloseSession(string id, CloseStatusCode code, string reason)
        {
            if (!TryGetSession(id, out IWebSocketSession session))
            {
                var msg = "The session could not be found.";
                throw new InvalidOperationException(msg);
            }

            session.Context.WebSocket.Close(code, reason);
        }

        /// <summary>
        /// Sends a ping to the client using the specified session.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the send has done with no error and a pong has been
        /// received from the client within a time; otherwise, <c>false</c>.
        /// </returns>
        /// <param name="id">
        /// A <see cref="string"/> that represents the ID of the session.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="id"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="id"/> is an empty string.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The session could not be found.
        /// </exception>
        public bool PingTo(string id)
        {
            if (!TryGetSession(id, out IWebSocketSession session))
            {
                var msg = "The session could not be found.";
                throw new InvalidOperationException(msg);
            }

            return session.Context.WebSocket.Ping();
        }

        /// <summary>
        /// Sends a ping with <paramref name="message"/> to the client using
        /// the specified session.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the send has done with no error and a pong has been
        /// received from the client within a time; otherwise, <c>false</c>.
        /// </returns>
        /// <param name="message">
        ///   <para>
        ///   A <see cref="string"/> that represents the message to send.
        ///   </para>
        ///   <para>
        ///   The size must be 125 bytes or less in UTF-8.
        ///   </para>
        /// </param>
        /// <param name="id">
        /// A <see cref="string"/> that represents the ID of the session.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="id"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="id"/> is an empty string.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="message"/> could not be UTF-8 encoded.
        ///   </para>
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The session could not be found.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The size of <paramref name="message"/> is greater than 125 bytes.
        /// </exception>
        public bool PingTo(string message, string id)
        {
            if (!TryGetSession(id, out IWebSocketSession session))
            {
                var msg = "The session could not be found.";
                throw new InvalidOperationException(msg);
            }

            return session.Context.WebSocket.Ping(message);
        }

        /// <summary>
        /// Sends <paramref name="data"/> to the client using the specified session.
        /// </summary>
        /// <param name="data">
        /// An array of <see cref="byte"/> that represents the binary data to send.
        /// </param>
        /// <param name="id">
        /// A <see cref="string"/> that represents the ID of the session.
        /// </param>
        /// <exception cref="ArgumentNullException">
        ///   <para>
        ///   <paramref name="id"/> is <see langword="null"/>.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="data"/> is <see langword="null"/>.
        ///   </para>
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="id"/> is an empty string.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        ///   <para>
        ///   The session could not be found.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The current state of the WebSocket connection is not Open.
        ///   </para>
        /// </exception>
        public void SendTo(MessageFrameType type, byte[] data, string id)
        {
            if (!TryGetSession(id, out IWebSocketSession session))
            {
                var msg = "The session could not be found.";
                throw new InvalidOperationException(msg);
            }

            session.Context.WebSocket.Send(type, data);
        }

        /// <summary>
        /// Sends <paramref name="data"/> to the client using the specified session.
        /// </summary>
        /// <param name="data">
        /// A <see cref="string"/> that represents the text data to send.
        /// </param>
        /// <param name="id">
        /// A <see cref="string"/> that represents the ID of the session.
        /// </param>
        /// <exception cref="ArgumentNullException">
        ///   <para>
        ///   <paramref name="id"/> is <see langword="null"/>.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="data"/> is <see langword="null"/>.
        ///   </para>
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="id"/> is an empty string.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="data"/> could not be UTF-8 encoded.
        ///   </para>
        /// </exception>
        /// <exception cref="InvalidOperationException">
        ///   <para>
        ///   The session could not be found.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The current state of the WebSocket connection is not Open.
        ///   </para>
        /// </exception>
        public void SendTo(string data, string id)
        {
            if (!TryGetSession(id, out IWebSocketSession session))
            {
                var msg = "The session could not be found.";
                throw new InvalidOperationException(msg);
            }

            session.Context.WebSocket.Send(data);
        }

        /// <summary>
        /// Sends the data from <paramref name="stream"/> to the client using
        /// the specified session.
        /// </summary>
        /// <remarks>
        /// The data is sent as the binary data.
        /// </remarks>
        /// <param name="stream">
        /// A <see cref="Stream"/> instance from which to read the data to send.
        /// </param>
        /// <param name="length">
        /// An <see cref="int"/> that specifies the number of bytes to send.
        /// </param>
        /// <param name="id">
        /// A <see cref="string"/> that represents the ID of the session.
        /// </param>
        /// <exception cref="ArgumentNullException">
        ///   <para>
        ///   <paramref name="id"/> is <see langword="null"/>.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="stream"/> is <see langword="null"/>.
        ///   </para>
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="id"/> is an empty string.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
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
        /// <exception cref="InvalidOperationException">
        ///   <para>
        ///   The session could not be found.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The current state of the WebSocket connection is not Open.
        ///   </para>
        /// </exception>
        public void SendTo(MessageFrameType type, Stream stream, int length, string id)
        {
            if (!TryGetSession(id, out IWebSocketSession session))
            {
                var msg = "The session could not be found.";
                throw new InvalidOperationException(msg);
            }

            session.Context.WebSocket.Send(type, stream, length);
        }

        /// <summary>
        /// Sends <paramref name="data"/> asynchronously to the client using
        /// the specified session.
        /// </summary>
        /// <remarks>
        /// This method does not wait for the send to be complete.
        /// </remarks>
        /// <param name="data">
        /// An array of <see cref="byte"/> that represents the binary data to send.
        /// </param>
        /// <param name="id">
        /// A <see cref="string"/> that represents the ID of the session.
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
        /// <exception cref="ArgumentNullException">
        ///   <para>
        ///   <paramref name="id"/> is <see langword="null"/>.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="data"/> is <see langword="null"/>.
        ///   </para>
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="id"/> is an empty string.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        ///   <para>
        ///   The session could not be found.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The current state of the WebSocket connection is not Open.
        ///   </para>
        /// </exception>
        public void SendToAsync(MessageFrameType type, byte[] data, string id, Action<bool> completed)
        {
            if (!TryGetSession(id, out IWebSocketSession session))
                throw new InvalidOperationException("The session could not be found.");
        
            session.Context.WebSocket.SendAsync(type, data, completed);
        }

        /// <summary>
        /// Sends <paramref name="data"/> asynchronously to the client using
        /// the specified session.
        /// </summary>
        /// <remarks>
        /// This method does not wait for the send to be complete.
        /// </remarks>
        /// <param name="data">
        /// A <see cref="string"/> that represents the text data to send.
        /// </param>
        /// <param name="id">
        /// A <see cref="string"/> that represents the ID of the session.
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
        /// <exception cref="ArgumentNullException">
        ///   <para>
        ///   <paramref name="id"/> is <see langword="null"/>.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="data"/> is <see langword="null"/>.
        ///   </para>
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="id"/> is an empty string.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="data"/> could not be UTF-8 encoded.
        ///   </para>
        /// </exception>
        /// <exception cref="InvalidOperationException">
        ///   <para>
        ///   The session could not be found.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The current state of the WebSocket connection is not Open.
        ///   </para>
        /// </exception>
        public void SendToAsync(string data, string id, Action<bool> completed)
        {
            if (!TryGetSession(id, out IWebSocketSession session))
            {
                var msg = "The session could not be found.";
                throw new InvalidOperationException(msg);
            }

            session.Context.WebSocket.SendAsync(data, completed);
        }

        /// <summary>
        /// Sends the data from <paramref name="stream"/> asynchronously to
        /// the client using the specified session.
        /// </summary>
        /// <remarks>
        ///   <para>
        ///   The data is sent as the binary data.
        ///   </para>
        ///   <para>
        ///   This method does not wait for the send to be complete.
        ///   </para>
        /// </remarks>
        /// <param name="stream">
        /// A <see cref="Stream"/> instance from which to read the data to send.
        /// </param>
        /// <param name="length">
        /// An <see cref="int"/> that specifies the number of bytes to send.
        /// </param>
        /// <param name="id">
        /// A <see cref="string"/> that represents the ID of the session.
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
        /// <exception cref="ArgumentNullException">
        ///   <para>
        ///   <paramref name="id"/> is <see langword="null"/>.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   <paramref name="stream"/> is <see langword="null"/>.
        ///   </para>
        /// </exception>
        /// <exception cref="ArgumentException">
        ///   <para>
        ///   <paramref name="id"/> is an empty string.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
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
        /// <exception cref="InvalidOperationException">
        ///   <para>
        ///   The session could not be found.
        ///   </para>
        ///   <para>
        ///   -or-
        ///   </para>
        ///   <para>
        ///   The current state of the WebSocket connection is not Open.
        ///   </para>
        /// </exception>
        public void SendToAsync(
            MessageFrameType type, Stream stream, int length, string id, Action<bool> completed)
        {
            if (!TryGetSession(id, out IWebSocketSession session))
                throw new InvalidOperationException("The session could not be found.");
            
            session.Context.WebSocket.SendAsync(type, stream, length, completed);
        }

        /// <summary>
        /// Cleans up the inactive sessions in the service.
        /// </summary>
        public void Sweep()
        {
            if (_sweeping)
            {
                _log.Info("The sweeping is already in progress.");
                return;
            }

            lock (_forSweep)
            {
                if (_sweeping)
                {
                    _log.Info("The sweeping is already in progress.");
                    return;
                }

                _sweeping = true;
            }

            foreach (var id in InactiveIDs)
            {
                if (_state != ServerState.Start)
                    break;

                lock (_sync)
                {
                    if (_state != ServerState.Start)
                        break;

                    if (_sessions.TryGetValue(id, out IWebSocketSession session))
                    {
                        switch (session.ConnectionState)
                        {
                            case WebSocketState.Open:
                                session.Context.WebSocket.Close(CloseStatusCode.Abnormal);
                                break;

                            case WebSocketState.Closing:
                                continue;

                            default:
                                _sessions.Remove(id);
                                break;
                        }
                    }
                }
            }
            _sweeping = false;
        }

        /// <summary>
        /// Tries to get the session instance with <paramref name="id"/>.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the session is successfully found; otherwise,
        /// <c>false</c>.
        /// </returns>
        /// <param name="id">
        /// A <see cref="string"/> that represents the ID of the session to find.
        /// </param>
        /// <param name="session">
        ///   <para>
        ///   When this method returns, a <see cref="IWebSocketSession"/>
        ///   instance or <see langword="null"/> if not found.
        ///   </para>
        ///   <para>
        ///   The session instance provides the function to access
        ///   the information in the session.
        ///   </para>
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="id"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="id"/> is an empty string.
        /// </exception>
        public bool TryGetSession(string id, out IWebSocketSession session)
        {
            if (id == null)
                throw new ArgumentNullException(nameof(id));

            if (id.Length == 0)
                throw new ArgumentException("The string may not be empty.", nameof(id));

            return InternalTryGetSession(id, out session);
        }

        #endregion
    }
}