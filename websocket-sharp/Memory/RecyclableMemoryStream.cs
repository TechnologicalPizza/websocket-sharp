// The MIT License (MIT)
// 
// Copyright (c) 2015-2016 Microsoft
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

namespace WebSocketSharp.Memory
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.IO;
    using System.Threading;

    /// <summary>
    /// MemoryStream implementation that deals with pooling and managing memory streams which use potentially large
    /// buffers.
    /// </summary>
    /// <remarks>
    /// This class works in tandem with the RecyclableMemoryStreamManager to supply MemoryStream
    /// objects to callers, while avoiding these specific problems:
    /// 1. LOH allocations - since all large buffers are pooled, they will never incur a Gen2 GC
    /// 2. Memory waste - A standard memory stream doubles its size when it runs out of room. This
    /// leads to continual memory growth as each stream approaches the maximum allowed size.
    /// 3. Memory copying - Each time a MemoryStream grows, all the bytes are copied into new buffers.
    /// This implementation only copies the bytes when GetBuffer is called.
    /// 4. Memory fragmentation - By using homogeneous buffer sizes, it ensures that blocks of memory
    /// can be easily reused.
    /// 
    /// The stream is implemented on top of a series of uniformly-sized blocks. As the stream's length grows,
    /// additional blocks are retrieved from the memory manager. It is these blocks that are pooled, 
    /// not the stream object itself.
    /// 
    /// The biggest wrinkle in this implementation is when GetBuffer() is called. This requires a single 
    /// contiguous buffer. If only a single block is in use, then that block is returned. If multiple blocks 
    /// are in use, we retrieve a larger buffer from the memory manager. These large buffers are also pooled, 
    /// split by size--they are multiples/exponentials of a chunk size (1 MB by default).
    /// 
    /// Once a large buffer is assigned to the stream the blocks are NEVER again used for this stream. All operations take place on the 
    /// large buffer. The large buffer can be replaced by a larger buffer from the pool as needed. All blocks and large buffers 
    /// are maintained in the stream until the stream is disposed (unless AggressiveBufferReturn is enabled in the stream manager).
    /// 
    /// </remarks>
    public class RecyclableMemoryStream : MemoryStream
    {
        private const long MaxStreamLength = int.MaxValue;

        private int _length;
        private int _position;

        /// <summary>
        /// All of these blocks must be the same size.
        /// </summary>
        private List<byte[]> _blocks;

        /// <summary>
        /// This buffer exists so that WriteByte can forward all of its calls to Write
        /// without creating a new byte[] buffer on every call.
        /// </summary>
        private byte[] _byteBuffer;

        /// <summary>
        /// This list is used to store buffers once they're replaced by something larger.
        /// This is for the cases where you have users of this class that may hold onto the buffers longer
        /// than they should and you want to prevent race conditions which could corrupt the data.
        /// </summary>
        private List<byte[]> _dirtyBuffers;

        protected long _disposedState;

        /// <summary>
        /// This is only set by GetBuffer() if the necessary buffer is larger than a single block size, or on
        /// construction if the caller immediately requests a single large buffer.
        /// </summary>
        /// <remarks>If this field is non-null, it contains the concatenation of the bytes found in the individual
        /// blocks. Once it is created, this (or a larger) largeBuffer will be used for the life of the stream.
        /// </remarks>
        private byte[] _largeBuffer;

        #region Internal Properties
        /// <summary>
        /// Unique identifier for this stream across it's entire lifetime.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        internal Guid ID { get; }

        /// <summary>
        /// Gets the memory manager being used by this stream.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        internal RecyclableMemoryManager MemoryManager { get; }

        /// <summary>
        /// A temporary identifier for the current usage of this stream.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        internal string Tag { get; }

        /// <summary>
        /// Callstack of the constructor. It is only set if MemoryManager.GenerateCallStacks is true,
        /// which should only be in debugging situations.
        /// </summary>
        internal string AllocationStack { get; }

        /// <summary>
        /// Callstack of the Dispose call. It is only set if MemoryManager.GenerateCallStacks is true,
        /// which should only be in debugging situations.
        /// </summary>
        internal string DisposeStack { get; private set; }
        #endregion

        #region Constructors
        /// <summary>
        /// Allocate a new RecyclableMemoryStream object.
        /// </summary>
        /// <param name="memoryManager">The memory manager</param>
        public RecyclableMemoryStream(RecyclableMemoryManager memoryManager)
            : this(memoryManager, null, 0, null)
        {
        }

        /// <summary>
        /// Allocate a new RecyclableMemoryStream object
        /// </summary>
        /// <param name="memoryManager">The memory manager</param>
        /// <param name="tag">A string identifying this stream for logging and debugging purposes</param>
        public RecyclableMemoryStream(RecyclableMemoryManager memoryManager, string tag)
            : this(memoryManager, tag, 0, null)
        {
        }

        /// <summary>
        /// Allocate a new RecyclableMemoryStream object
        /// </summary>
        /// <param name="memoryManager">The memory manager</param>
        /// <param name="tag">A string identifying this stream for logging and debugging purposes</param>
        /// <param name="requestedSize">The initial requested size to prevent future allocations</param>
        public RecyclableMemoryStream(RecyclableMemoryManager memoryManager, string tag, int requestedSize)
            : this(memoryManager, tag, requestedSize, null)
        {
        }

        /// <summary>
        /// Allocate a new RecyclableMemoryStream object
        /// </summary>
        /// <param name="memoryManager">The memory manager</param>
        /// <param name="tag">A string identifying this stream for logging and debugging purposes</param>
        /// <param name="requestedSize">The initial requested size to prevent future allocations</param>
        /// <param name="initialLargeBuffer">
        /// An initial buffer to use. This buffer will be owned by the
        /// stream and returned to the memory manager upon Dispose.
        /// </param>
        internal RecyclableMemoryStream(
            RecyclableMemoryManager memoryManager, string tag, int requestedSize, byte[] initialLargeBuffer)
            : base(Array.Empty<byte>())
        {
            MemoryManager = memoryManager;
            ID = Guid.NewGuid();
            Tag = tag;

            _blocks = MemoryManager.GetArrayList();

            if (requestedSize < memoryManager.BlockSize)
                requestedSize = memoryManager.BlockSize;

            if (initialLargeBuffer == null)
                EnsureCapacity(requestedSize);
            else
                _largeBuffer = initialLargeBuffer;

            if (MemoryManager.GenerateCallStacks)
                AllocationStack = Environment.StackTrace;
            
            RecyclableMemoryManager.Events.Writer.MemoryStreamCreated(ID, Tag, requestedSize);
            MemoryManager.ReportStreamCreated();
        }
        #endregion

        #region Dispose and Finalize
        ~RecyclableMemoryStream()
        {
            Dispose(false);
        }

        /// <summary>
        /// Returns the memory used by this stream back to the pool.
        /// </summary>
        /// <param name="disposing">Whether we're disposing (true), or being called by the finalizer (false)</param>
        [SuppressMessage("Microsoft.Usage", "CA1816:CallGCSuppressFinalizeCorrectly",
            Justification = "We have different disposal semantics, so SuppressFinalize is in a different spot.")]
        protected override void Dispose(bool disposing)
        {
            if (Interlocked.CompareExchange(ref _disposedState, 1, 0) != 0)
            {
                string doubleDisposeStack = null;
                if (MemoryManager.GenerateCallStacks)
                    doubleDisposeStack = Environment.StackTrace;

                RecyclableMemoryManager.Events.Writer.MemoryStreamDoubleDispose(
                    ID, Tag, AllocationStack, DisposeStack, doubleDisposeStack);
                return;
            }

            RecyclableMemoryManager.Events.Writer.MemoryStreamDisposed(ID, Tag);

            if (MemoryManager.GenerateCallStacks)
                DisposeStack = Environment.StackTrace;

            if (disposing)
            {
                MemoryManager.ReportStreamDisposed();
            }
            else
            {
                // We're being finalized.
                RecyclableMemoryManager.Events.Writer.MemoryStreamFinalized(ID, Tag, AllocationStack);

#if !NETSTANDARD1_4
                if (AppDomain.CurrentDomain.IsFinalizingForUnload())
                {
                    // If we're being finalized because of a shutdown, don't go any further.
                    // We have no idea what's already been cleaned up. Triggering events may cause
                    // a crash.
                    base.Dispose(disposing);
                    return;
                }
#endif
                MemoryManager.ReportStreamFinalized();
            }

            MemoryManager.ReportStreamLength(_length);

            if (_largeBuffer != null)
                MemoryManager.ReturnLargeBuffer(_largeBuffer, Tag);

            if (_dirtyBuffers != null)
            {
                foreach (var buffer in _dirtyBuffers)
                    MemoryManager.ReturnLargeBuffer(buffer, Tag);
                MemoryManager.ReturnArrayList(_dirtyBuffers);
            }

            if (_blocks != null)
            {
                MemoryManager.ReturnBlocks(_blocks, Tag);
                MemoryManager.ReturnArrayList(_blocks);
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Equivalent to Dispose()
        /// </summary>
#if NETSTANDARD1_4
        public void Close()
#else
        public override void Close()
#endif
        {
            Dispose(true);
        }
        #endregion

        #region MemoryStream overrides
        /// <summary>
        /// Gets or sets the capacity
        /// </summary>
        /// <remarks>Capacity is always in multiples of the memory manager's block size, unless
        /// the large buffer is in use.  Capacity never decreases during a stream's lifetime. 
        /// Explicitly setting the capacity to a lower value than the current value will have no effect. 
        /// This is because the buffers are all pooled by chunks and there's little reason to 
        /// allow stream truncation.
        /// </remarks>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public override int Capacity
        {
            get
            {
                AssertNotDisposed();
                if (_largeBuffer != null)
                    return _largeBuffer.Length;

                long size = (long)_blocks.Count * MemoryManager.BlockSize;
                return (int)Math.Min(int.MaxValue, size);
            }
            set
            {
                AssertNotDisposed();
                EnsureCapacity(value);
            }
        }

        /// <summary>
        /// Gets the number of bytes written to this stream.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public override long Length
        {
            get
            {
                AssertNotDisposed();
                return _length;
            }
        }

        /// <summary>
        /// Gets the current position in the stream
        /// </summary>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public override long Position
        {
            get
            {
                AssertNotDisposed();
                return _position;
            }
            set
            {
                AssertNotDisposed();

                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value), "value must be non-negative");

                if (value > MaxStreamLength)
                    throw new ArgumentOutOfRangeException(nameof(value), "value cannot be more than " + MaxStreamLength);

                _position = (int)value;
            }
        }

        /// <summary>
        /// Whether the stream can currently read
        /// </summary>
        public override bool CanRead => !IsDisposed;

        /// <summary>
        /// Whether the stream can currently seek
        /// </summary>
        public override bool CanSeek => !IsDisposed;

        /// <summary>
        /// Always false
        /// </summary>
        public override bool CanTimeout => false;

        /// <summary>
        /// Whether the stream can currently write
        /// </summary>
        public override bool CanWrite => !IsDisposed;

        /// <summary>
        /// Returns a single buffer containing the contents of the stream.
        /// The buffer may be longer than the stream length.
        /// </summary>
        /// <returns>A byte[] buffer</returns>
        /// <remarks>IMPORTANT: Doing a Write() after calling GetBuffer() invalidates the buffer. The old buffer is held onto
        /// until Dispose is called, but the next time GetBuffer() is called, a new buffer from the pool will be required.</remarks>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
#if NETSTANDARD1_4
        public byte[] GetBuffer()
#else
        public override byte[] GetBuffer()
#endif
        {
            AssertNotDisposed();

            if (_largeBuffer != null)
                return _largeBuffer;

            if (_blocks.Count == 1)
                return _blocks[0];

            // Buffer needs to reflect the capacity, not the length, because
            // it's possible that people will manipulate the buffer directly
            // and set the length afterward. Capacity sets the expectation
            // for the size of the buffer.
            var newBuffer = MemoryManager.GetLargeBuffer(Capacity, Tag);

            // InternalRead will check for existence of largeBuffer, so make sure we
            // don't set it until after we've copied the data.
            InternalRead(newBuffer, 0, _length, 0);
            _largeBuffer = newBuffer;

            if (_blocks.Count > 0 && MemoryManager.AggressiveBufferReturn)
            {
                MemoryManager.ReturnBlocks(_blocks, Tag);
                _blocks.Clear();
            }

            return _largeBuffer;
        }

        /// <summary>
        /// Returns an ArraySegment that wraps a single buffer containing the contents of the stream.
        /// </summary>
        /// <param name="buffer">An ArraySegment containing a reference to the underlying bytes.</param>
        /// <returns>Always returns true.</returns>
        /// <remarks>GetBuffer has no failure modes (it always returns something, even if it's an empty buffer), therefore this method
        /// always returns a valid ArraySegment to the same buffer returned by GetBuffer.</remarks>
#if NET40 || NET45
        public bool TryGetBuffer(out ArraySegment<byte> buffer)  
#else
        public override bool TryGetBuffer(out ArraySegment<byte> buffer)
#endif
        {
            AssertNotDisposed();
            buffer = new ArraySegment<byte>(GetBuffer(), 0, (int)Length);
            // GetBuffer has no failure modes, so this should always succeed
            return true;
        }

        /// <summary>
        /// Returns a new array with a copy of the buffer's contents. You should almost certainly be using GetBuffer combined with the Length to 
        /// access the bytes in this stream. Calling ToArray will destroy the benefits of pooled buffers, but it is included
        /// for the sake of completeness.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
#pragma warning disable CS0809
        [Obsolete("This method has degraded performance vs. GetBuffer and should be avoided.")]
        public override byte[] ToArray()
        {
            AssertNotDisposed();
            var newBuffer = new byte[Length];

            InternalRead(newBuffer, 0, _length, 0);
            string stack = MemoryManager.GenerateCallStacks ? Environment.StackTrace : null;
            RecyclableMemoryManager.Events.Writer.MemoryStreamToArray(ID, Tag, stack, 0);
            MemoryManager.ReportStreamToArray();

            return newBuffer;
        }
#pragma warning restore CS0809

        /// <summary>
        /// Reads from the current position into the provided buffer
        /// </summary>
        /// <param name="buffer">Destination buffer</param>
        /// <param name="offset">Offset into buffer at which to start placing the read bytes.</param>
        /// <param name="count">Number of bytes to read.</param>
        /// <returns>The number of bytes read</returns>
        /// <exception cref="ArgumentNullException">buffer is null</exception>
        /// <exception cref="ArgumentOutOfRangeException">offset or count is less than 0</exception>
        /// <exception cref="ArgumentException">offset subtracted from the buffer length is less than count</exception>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public override int Read(byte[] buffer, int offset, int count)
        {
            return SafeRead(buffer, offset, count, ref _position);
        }

        /// <summary>
        /// Reads from the specified position into the provided buffer
        /// </summary>
        /// <param name="buffer">Destination buffer</param>
        /// <param name="offset">Offset into buffer at which to start placing the read bytes.</param>
        /// <param name="count">Number of bytes to read.</param>
        /// <param name="streamPosition">Position in the stream to start reading from</param>
        /// <returns>The number of bytes read</returns>
        /// <exception cref="ArgumentNullException">buffer is null</exception>
        /// <exception cref="ArgumentOutOfRangeException">offset or count is less than 0</exception>
        /// <exception cref="ArgumentException">offset subtracted from the buffer length is less than count</exception>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public int SafeRead(byte[] buffer, int offset, int count, ref int streamPosition)
        {
            AssertNotDisposed();
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "offset cannot be negative");

            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "count cannot be negative");

            if (offset + count > buffer.Length)
                throw new ArgumentException("buffer length must be at least offset + count");

            int amountRead = InternalRead(buffer, offset, count, streamPosition);
            streamPosition += amountRead;
            return amountRead;
        }

#if NETCOREAPP2_1 || NETSTANDARD2_1
        /// <summary>
        /// Reads from the current position into the provided buffer
        /// </summary>
        /// <param name="buffer">Destination buffer</param>
        /// <returns>The number of bytes read</returns>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public override int Read(Span<byte> buffer)
        {
            return this.SafeRead(buffer, ref this.position);
        }

        /// <summary>
        /// Reads from the specified position into the provided buffer
        /// </summary>
        /// <param name="buffer">Destination buffer</param>
        /// <param name="streamPosition">Position in the stream to start reading from</param>
        /// <returns>The number of bytes read</returns>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public int SafeRead(Span<byte> buffer, ref int streamPosition)
        {
            this.CheckDisposed();

            int amountRead = this.InternalRead(buffer, streamPosition);
            streamPosition += amountRead;
            return amountRead;
        }
#endif

        /// <summary>
        /// Writes the buffer to the stream
        /// </summary>
        /// <param name="buffer">Source buffer</param>
        /// <param name="offset">Start position</param>
        /// <param name="count">Number of bytes to write</param>
        /// <exception cref="ArgumentNullException">buffer is null</exception>
        /// <exception cref="ArgumentOutOfRangeException">offset or count is negative</exception>
        /// <exception cref="ArgumentException">buffer.Length - offset is not less than count</exception>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public override void Write(byte[] buffer, int offset, int count)
        {
            AssertNotDisposed();
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (offset < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(offset), offset, "Offset must be in the range of 0 - buffer.Length-1");

            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), count, "count must be non-negative");

            if (count + offset > buffer.Length)
                throw new ArgumentException("count must be greater than buffer.Length - offset");

            int blockSize = MemoryManager.BlockSize;
            long end = (long)_position + count;
            // Check for overflow
            if (end > MaxStreamLength)
                throw new IOException("Maximum capacity exceeded");

            EnsureCapacity((int)end);

            if (_largeBuffer == null)
            {
                int bytesRemaining = count;
                int bytesWritten = 0;
                var blockAndOffset = GetBlockAndRelativeOffset(_position);

                while (bytesRemaining > 0)
                {
                    byte[] currentBlock = _blocks[blockAndOffset.Block];
                    int remainingInBlock = blockSize - blockAndOffset.Offset;
                    int amountToWriteInBlock = Math.Min(remainingInBlock, bytesRemaining);

                    Buffer.BlockCopy(
                        buffer, offset + bytesWritten,
                        currentBlock, blockAndOffset.Offset, amountToWriteInBlock);

                    bytesRemaining -= amountToWriteInBlock;
                    bytesWritten += amountToWriteInBlock;

                    ++blockAndOffset.Block;
                    blockAndOffset.Offset = 0;
                }
            }
            else
            {
                Buffer.BlockCopy(buffer, offset, _largeBuffer, _position, count);
            }
            _position = (int)end;
            _length = Math.Max(_position, _length);
        }

#if NETCOREAPP2_1 || NETSTANDARD2_1
        /// <summary>
        /// Writes the buffer to the stream
        /// </summary>
        /// <param name="source">Source buffer</param>
        /// <exception cref="ArgumentNullException">buffer is null</exception>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public override void Write(ReadOnlySpan<byte> source)
        {
            this.CheckDisposed();

            int blockSize = this.memoryManager.BlockSize;
            long end = (long)this.position + source.Length;
            // Check for overflow
            if (end > MaxStreamLength)
            {
                throw new IOException("Maximum capacity exceeded");
            }

            this.EnsureCapacity((int)end);

            if (this.largeBuffer == null)
            {
                var blockAndOffset = this.GetBlockAndRelativeOffset(this.position);

                while (source.Length > 0)
                {
                    byte[] currentBlock = this.blocks[blockAndOffset.Block];
                    int remainingInBlock = blockSize - blockAndOffset.Offset;
                    int amountToWriteInBlock = Math.Min(remainingInBlock, source.Length);

                    source.Slice(0, amountToWriteInBlock)
                        .CopyTo(currentBlock.AsSpan(blockAndOffset.Offset));

                    source = source.Slice(amountToWriteInBlock);

                    ++blockAndOffset.Block;
                    blockAndOffset.Offset = 0;
                }
            }
            else
            {
                source.CopyTo(this.largeBuffer.AsSpan(this.position));
            }
            this.position = (int)end;
            this.length = Math.Max(this.position, this.length);
        }
#endif

        /// <summary>
        /// Returns a useful string for debugging. This should not normally be called in actual production code.
        /// </summary>
        public override string ToString()
        {
            return $"Id = {ID}, Tag = {Tag}, Length = {Length:N0} bytes";
        }

        /// <summary>
        /// Writes a single byte to the current position in the stream.
        /// </summary>
        /// <param name="value">byte value to write</param>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public override void WriteByte(byte value)
        {
            AssertNotDisposed();

            if (_byteBuffer == null)
                _byteBuffer = new byte[1];

            _byteBuffer[0] = value;
            Write(_byteBuffer, 0, 1);
        }

        /// <summary>
        /// Reads a single byte from the current position in the stream.
        /// </summary>
        /// <returns>The byte at the current position, or -1 if the position is at the end of the stream.</returns>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public override int ReadByte()
        {
            return SafeReadByte(ref _position);
        }

        /// <summary>
        /// Reads a single byte from the specified position in the stream.
        /// </summary>
        /// <param name="streamPosition">The position in the stream to read from</param>
        /// <returns>The byte at the current position, or -1 if the position is at the end of the stream.</returns>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public int SafeReadByte(ref int streamPosition)
        {
            AssertNotDisposed();
            if (streamPosition == _length)
            {
                return -1;
            }
            byte value;
            if (_largeBuffer == null)
            {
                var blockAndOffset = GetBlockAndRelativeOffset(streamPosition);
                value = _blocks[blockAndOffset.Block][blockAndOffset.Offset];
            }
            else
            {
                value = _largeBuffer[streamPosition];
            }
            streamPosition++;
            return value;
        }

        /// <summary>
        /// Sets the length of the stream
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">value is negative or larger than MaxStreamLength</exception>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        public override void SetLength(long value)
        {
            AssertNotDisposed();
            if (value < 0 || value > MaxStreamLength)
                throw new ArgumentOutOfRangeException(
                    nameof(value), "value must be non-negative and at most " + MaxStreamLength);

            EnsureCapacity((int)value);

            _length = (int)value;
            if (_position > value)
                _position = (int)value;
        }

        /// <summary>
        /// Sets the position to the offset from the seek location
        /// </summary>
        /// <param name="offset">How many bytes to move</param>
        /// <param name="loc">From where</param>
        /// <returns>The new position</returns>
        /// <exception cref="ObjectDisposedException">Object has been disposed</exception>
        /// <exception cref="ArgumentOutOfRangeException">offset is larger than MaxStreamLength</exception>
        /// <exception cref="ArgumentException">Invalid seek origin</exception>
        /// <exception cref="IOException">Attempt to set negative position</exception>
        public override long Seek(long offset, SeekOrigin loc)
        {
            AssertNotDisposed();
            if (offset > MaxStreamLength)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "offset cannot be larger than " + MaxStreamLength);
            }

            int newPosition;
            switch (loc)
            {
                case SeekOrigin.Begin:
                    newPosition = (int)offset;
                    break;
                case SeekOrigin.Current:
                    newPosition = (int)offset + _position;
                    break;
                case SeekOrigin.End:
                    newPosition = (int)offset + _length;
                    break;
                default:
                    throw new ArgumentException("Invalid seek origin", nameof(loc));
            }
            if (newPosition < 0)
            {
                throw new IOException("Seek before beginning");
            }
            _position = newPosition;
            return _position;
        }

        /// <summary>
        /// Synchronously writes this stream's bytes to the parameter stream.
        /// </summary>
        /// <param name="stream">Destination stream</param>
        /// <remarks>Important: This does a synchronous write, which may not be desired in some situations</remarks>
        public override void WriteTo(Stream stream)
        {
            AssertNotDisposed();
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            if (_largeBuffer == null)
            {
                int currentBlock = 0;
                int bytesRemaining = _length;

                while (bytesRemaining > 0)
                {
                    int amountToCopy = Math.Min(_blocks[currentBlock].Length, bytesRemaining);
                    stream.Write(_blocks[currentBlock], 0, amountToCopy);

                    bytesRemaining -= amountToCopy;
                    currentBlock++;
                }
            }
            else
            {
                stream.Write(_largeBuffer, 0, _length);
            }
        }
        #endregion

        #region Helper Methods
        public bool IsDisposed => Interlocked.Read(ref _disposedState) != 0;

        [DebuggerHidden]
        private void AssertNotDisposed()
        {
            if (IsDisposed)
                throw new ObjectDisposedException($"The stream with ID {ID} and Tag {Tag} is disposed.");
        }

        private int InternalRead(byte[] buffer, int offset, int count, int fromPosition)
        {
            if (_length - fromPosition <= 0)
               return 0;
            
            int amountToCopy;

            if (_largeBuffer == null)
            {
                var blockAndOffset = GetBlockAndRelativeOffset(fromPosition);
                int bytesWritten = 0;
                int bytesRemaining = Math.Min(count, _length - fromPosition);

                while (bytesRemaining > 0)
                {
                    amountToCopy = Math.Min(_blocks[blockAndOffset.Block].Length - blockAndOffset.Offset, bytesRemaining);
                    Buffer.BlockCopy(
                        _blocks[blockAndOffset.Block], blockAndOffset.Offset, buffer, bytesWritten + offset, amountToCopy);

                    bytesWritten += amountToCopy;
                    bytesRemaining -= amountToCopy;

                    ++blockAndOffset.Block;
                    blockAndOffset.Offset = 0;
                }
                return bytesWritten;
            }
            amountToCopy = Math.Min(count, _length - fromPosition);
            Buffer.BlockCopy(_largeBuffer, fromPosition, buffer, offset, amountToCopy);
            return amountToCopy;
        }

#if NETCOREAPP2_1 || NETSTANDARD2_1
        private int InternalRead(Span<byte> buffer, int fromPosition)
        {
            if (this.length - fromPosition <= 0)
            {
                return 0;
            }

            int amountToCopy;

            if (this.largeBuffer == null)
            {
                var blockAndOffset = this.GetBlockAndRelativeOffset(fromPosition);
                int bytesWritten = 0;
                int bytesRemaining = Math.Min(buffer.Length, this.length - fromPosition);

                while (bytesRemaining > 0)
                {
                    amountToCopy = Math.Min(this.blocks[blockAndOffset.Block].Length - blockAndOffset.Offset,
                                            bytesRemaining);
                    this.blocks[blockAndOffset.Block].AsSpan(blockAndOffset.Offset, amountToCopy)
                        .CopyTo(buffer.Slice(bytesWritten));

                    bytesWritten += amountToCopy;
                    bytesRemaining -= amountToCopy;

                    ++blockAndOffset.Block;
                    blockAndOffset.Offset = 0;
                }
                return bytesWritten;
            }
            amountToCopy = Math.Min(buffer.Length, this.length - fromPosition);
            this.largeBuffer.AsSpan(fromPosition, amountToCopy).CopyTo(buffer);
            return amountToCopy;
        }
#endif

        private struct BlockAndOffset
        {
            public int Block;
            public int Offset;

            public BlockAndOffset(int block, int offset)
            {
                Block = block;
                Offset = offset;
            }
        }

        private BlockAndOffset GetBlockAndRelativeOffset(int offset)
        {
            var blockSize = MemoryManager.BlockSize;
            return new BlockAndOffset(offset / blockSize, offset % blockSize);
        }

        private void EnsureCapacity(int newCapacity)
        {
            if (newCapacity > MemoryManager.MaximumStreamCapacity &&
                MemoryManager.MaximumStreamCapacity > 0)
            {
                RecyclableMemoryManager.Events.Writer.MemoryStreamOverCapacity(
                    newCapacity, MemoryManager.MaximumStreamCapacity, Tag, AllocationStack);
                throw new InvalidOperationException(
                    "Requested capacity is too large: " + newCapacity + ". Limit is " + MemoryManager.MaximumStreamCapacity);
            }

            if (_largeBuffer != null)
            {
                if (newCapacity > _largeBuffer.Length)
                {
                    var newBuffer = MemoryManager.GetLargeBuffer(newCapacity, Tag);
                    InternalRead(newBuffer, 0, _length, 0);
                    ReleaseLargeBuffer();
                    _largeBuffer = newBuffer;
                }
            }
            else
            {
                while (Capacity < newCapacity)
                    _blocks.Add(MemoryManager.GetBlock());
            }
        }

        /// <summary>
        /// Release the large buffer (either stores it for eventual release or returns it immediately).
        /// </summary>
        private void ReleaseLargeBuffer()
        {
            if (MemoryManager.AggressiveBufferReturn)
            {
                MemoryManager.ReturnLargeBuffer(_largeBuffer, Tag);
            }
            else
            {
                if (_dirtyBuffers == null)
                {
                    // We most likely will only ever need space for one
                    _dirtyBuffers = MemoryManager.GetArrayList();
                }
                _dirtyBuffers.Add(_largeBuffer);
            }

            _largeBuffer = null;
        }
        #endregion
    }
}