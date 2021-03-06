﻿using Medallion.Shell.Async;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Medallion.Shell.Streams
{
    internal sealed class ProcessStreamHandler
    {
        private enum Mode
        {
            /// <summary>
            /// The contents of the stream is buffered internally so that the process will never be blocked because
            /// the pipe is full. This is the default mode
            /// </summary>
            Buffer = 0,
            /// <summary>
            /// The contents of the stream can be accessed manually via <see cref="Stream"/>
            /// operations. However, internal buffering ensures that the process will never be blocked because
            /// the output stream internal buffer is full.
            /// </summary>
            BufferedManualRead,
            /// <summary>
            /// The contents of the stream can be accessed manually via <see cref="Stream"/>
            /// operations. If the process writes faster than the consumer reads, it may fill the buffer
            /// and block
            /// </summary>
            ManualRead,
            /// <summary>
            /// The contents of the stream is discarded. This is implemented by closing the underlying stream
            /// </summary>
            DiscardContents,
        }

        /// <summary>
        /// The underlying output stream of the process
        /// </summary>
        private readonly Stream processStream;
        /// <summary>
        /// Acts as a buffer for data coming from <see cref="processStream"/>
        /// Volatile just to be safe, since this is mutable in async contexts
        /// </summary>
        private volatile MemoryStream memoryStream;
        /// <summary>
        /// Protects reads and writes to all streams
        /// </summary>
        private readonly AsyncLock streamLock = new AsyncLock();

        /// <summary>
        /// Protects access to <see cref="mode"/> and related variables
        /// </summary>
        private readonly object modeLock = new object();
        /// <summary>
        /// Represents the current mode of the handler
        /// </summary>
        private Mode mode;

        /// <summary>
        /// Exposes the underlying stream
        /// </summary>
        private readonly ProcessStreamReader reader;

        /// <summary>
        /// Used to track when the stream is fully consumed, as well as errors that may occur in various tasks
        /// </summary>
        private readonly TaskCompletionSource<bool> taskCompletionSource = new TaskCompletionSource<bool>();

        private readonly Task readLoopTask;

        public ProcessStreamHandler(StreamReader reader)
        {
            Throw.IfNull(reader, "reader");

            this.processStream = reader.BaseStream;
            this.reader = new InternalReader(this, reader.CurrentEncoding);
            // we don't need Task.Run here since this reaches an await very
            // quickly
            this.readLoopTask = this.ReadLoop()
                .ContinueWith(t => 
                {
                    if (t.IsFaulted)
                    {
                        this.taskCompletionSource.TrySetException(t.Exception);
                    }
                    else
                    {
                        this.taskCompletionSource.TrySetResult(true);
                    }
                });
        }

        public ProcessStreamReader Reader
        {
            get { return this.reader; }
        }

        public Task Task { get { return this.taskCompletionSource.Task; } }

        private void SetMode(Mode mode)
        {
            lock (this.modeLock)
            {
                if (this.mode == mode)
                {
                    return;
                }

                // when in the default (buffer) mode, you can switch to any other mode (important since we start
                // in this mode)
                if (this.mode == Mode.Buffer
                    // can always go into discard mode
                    || mode == Mode.DiscardContents
                    // when in manual read mode, you can always start buffering
                    || (this.mode == Mode.ManualRead && mode == Mode.BufferedManualRead)
                    // when in buffered read mode, you can always stop buffering
                    || (this.mode == Mode.BufferedManualRead && mode == Mode.ManualRead))
                {
                    this.mode = mode;

                    if (mode == Mode.DiscardContents)
                    {
                        // we don't do this actively in order to prevent deadlock/blocking from taking the stream
                        // lock within the mode lock and to let any "current" stream operations finish first
                        this.readLoopTask.ContinueWith(_ => this.DiscardContent());
                    }
                }
                else
                {
                    switch (this.mode)
                    {
                        case Mode.DiscardContents:
                            throw new ObjectDisposedException("process stream", "The stream has been set to discard its contents, so it cannot be used in another mode");
                        case Mode.ManualRead:
                        case Mode.BufferedManualRead:
                            throw new InvalidOperationException("The stream is already being read from, so it cannot be used in another mode");
                        default:
                            throw new NotImplementedException("Unexpected mode " + mode.ToString());
                    }
                }
            }
        }

        /// <summary>
        /// asynchronously processes the stream depending on the current mode
        /// </summary>
        private async Task ReadLoop()
        {
            var localBuffer = new byte[Constants.ByteBufferSize];
            while (true)
            {
                // safely capture the current mode
                Mode mode;
                lock (this.modeLock)
                {
                    mode = this.mode;
                }

                const int delayTimeMillis = 100;
                switch (mode)
                {
                    case Mode.ManualRead:
                        // in manual read mode, we just delay so that we can periodically check to
                        // see whether the mode has switched
                        await Task.Delay(millisecondsDelay: delayTimeMillis).ConfigureAwait(false);
                        break;
                    case Mode.BufferedManualRead:
                        // in buffered manual read mode, we read from the buffer periodically
                        // to avoid the process blocking
                        await Task.Delay(millisecondsDelay: delayTimeMillis).ConfigureAwait(false);
                        goto case Mode.Buffer;
                    case Mode.Buffer:
                        // grab the stream lock and read some bytes into the buffer
                        using (await this.streamLock.AcquireAsync().ConfigureAwait(false))
                        {
                            // initialized memory stream if necessary. Note that this can happen
                            // more than once due to InternalStream "grabbing" the memory stream
                            if (this.memoryStream == null)
                            {
                                this.memoryStream = new MemoryStream();
                            }

                            // read from the process
                            var bytesRead = await this.processStream.ReadAsync(localBuffer, offset: 0, count: localBuffer.Length).ConfigureAwait(false);
                            if (bytesRead == 0)
                            {
                                return; // end of stream
                            }

                            // write to the buffer
                            this.memoryStream.Write(localBuffer, offset: 0, count: bytesRead);
                        }
                        break;
                    case Mode.DiscardContents:
                        return; // handled by a continuation
                }
            }
        }

        private void DiscardContent()
        {
            Log.WriteLine("Discarding content");

            // grab the stream lock and close all streams
            using (this.streamLock.AcquireAsync().Result)
            {
                if (this.memoryStream != null)
                {
                    // free the memory stream
                    this.memoryStream.Dispose();
                    this.memoryStream = null;
                }

                this.processStream.Dispose();

                // we used to read to the end, but Dispose() seems to do the same thing
                //while (true)
                //{
                //    var bytesRead = await this.processStream.ReadAsync(localBuffer, offset: 0, count: localBuffer.Length).ConfigureAwait(false);
                //    if (bytesRead == 0)
                //    {
                //        return; // end of stream
                //    }
                //}
            }

            Log.WriteLine("Finished discarding content");
        }

        #region ---- Stream implementation ----
        private sealed class InternalStream : Stream
        {
            private readonly ProcessStreamHandler handler;
            private MemoryStream memoryStream;

            public InternalStream(ProcessStreamHandler handler)
            {
                this.handler = handler;
            }

            #region ---- Stream flags ----
            public override bool CanRead
            {
                get { return true; }
            }

            public override bool CanSeek
            {
                get { return false; }
            }

            public override bool CanWrite
            {
                get { return false; }
            }
            #endregion

            #region ---- Read ----
            public override int Read(byte[] buffer, int offset, int count)
            {
                // NOTE keep this in sync with the other Read method

                Throw.IfNull(buffer, "buffer");
                // offset is allowed to be buffer.Length. See http://referencesource.microsoft.com/#mscorlib/system/io/memorystream.cs
                Throw.IfOutOfRange(offset, "offset", min: 0, max: buffer.Length);
                Throw.IfOutOfRange(count, "count", min: 0, max: buffer.Length - offset);
                this.EnsureManualReadMode();

                using (this.handler.streamLock.AcquireAsync().Result)
                {
                    // first read from the memory streams
                    var bytesReadFromMemoryStreams = this.ReadFromMemoryStreams(buffer, offset, count);

                    // if there are still bytes to be read, read from the underlying stream
                    if (bytesReadFromMemoryStreams < count)
                    {
                        var bytesReadFromProcessStream = this.handler.processStream.Read(buffer, offset + bytesReadFromMemoryStreams, count - bytesReadFromMemoryStreams);
                        if (bytesReadFromProcessStream == 0)
                        {
                            this.handler.taskCompletionSource.TrySetResult(true);
                        }
                        return bytesReadFromMemoryStreams + bytesReadFromProcessStream;
                    }
                    
                    // otherwise, just return the bytes from the memory stream
                    return bytesReadFromMemoryStreams;
                }
            }
            
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                // NOTE keep this in sync with the other Read method

                Throw.IfNull(buffer, "buffer");
                // offset is allowed to be buffer.Length. See http://referencesource.microsoft.com/#mscorlib/system/io/memorystream.cs
                Throw.IfOutOfRange(offset, "offset", min: 0, max: buffer.Length);
                Throw.IfOutOfRange(count, "count", min: 0, max: buffer.Length - offset);
                this.EnsureManualReadMode();

                using (await this.handler.streamLock.AcquireAsync().ConfigureAwait(false))
                {
                    // first read from the memory streams
                    var bytesReadFromMemoryStreams = this.ReadFromMemoryStreams(buffer, offset, count);

                    // if there are still bytes to be read, read from the underlying stream
                    if (bytesReadFromMemoryStreams < count)
                    {
                        var bytesReadFromProcessStream = await this.handler.processStream.ReadAsync(buffer, offset + bytesReadFromMemoryStreams, count - bytesReadFromMemoryStreams, cancellationToken)
                            .ConfigureAwait(false);
                        if (bytesReadFromProcessStream == 0)
                        {
                            this.handler.taskCompletionSource.TrySetResult(true);
                        }
                        return bytesReadFromMemoryStreams + bytesReadFromProcessStream;
                    }

                    // otherwise, just return the bytes from the memory stream
                    return bytesReadFromMemoryStreams;
                }
            }

            #region ---- Begin and End Read ----
            public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
            {
                return new BeginReadResult(this.ReadAsync(buffer, offset, count), callback, state);
            }

            public override int EndRead(IAsyncResult asyncResult)
            {
                Throw.IfNull(asyncResult, "asyncResult");
                var beginReadResult = asyncResult as BeginReadResult;
                Throw.If(beginReadResult == null, "asyncResult: the provided result was not created from a stream of this type!");

                return beginReadResult.EndRead();
            }

            private class BeginReadResult : IAsyncResult
            {
                private readonly Task<int> readAsyncTask;
                private readonly object state;

                public BeginReadResult(Task<int> readAsyncTask, AsyncCallback callback, object state)
                {
                    this.readAsyncTask = readAsyncTask;
                    this.state = state;

                    if (callback != null)
                    {
                        this.readAsyncTask.ContinueWith(t => callback(this));
                    }
                }

                public int EndRead()
                {
                    var result = this.readAsyncTask.Result;
                    this.readAsyncTask.Dispose();
                    return result;
                }

                object IAsyncResult.AsyncState
                {
                    get { return this.state; }
                }

                WaitHandle IAsyncResult.AsyncWaitHandle
                {
                    get { return this.readAsyncTask.As<IAsyncResult>().AsyncWaitHandle; }
                }

                bool IAsyncResult.CompletedSynchronously
                {
                    get { return this.readAsyncTask.As<IAsyncResult>().CompletedSynchronously; }
                }

                bool IAsyncResult.IsCompleted
                {
                    get { return this.readAsyncTask.As<IAsyncResult>().IsCompleted; }
                }
            }
            #endregion

            // tracks whether we've checked the mode for the stream along with whether it has been disposed
            private volatile sbyte modeCheckState;
            private const sbyte ModeChecked = 1, Disposed = -1;

            private void EnsureManualReadMode()
            {
                switch (this.modeCheckState)
                {
                    case default(sbyte):
                        lock (this.handler.modeLock)
                        {
                            var mode = this.handler.mode;
                            if (mode != Mode.ManualRead && mode != Mode.BufferedManualRead)
                            {
                                this.handler.SetMode(Mode.BufferedManualRead);
                            }
                        }
                        this.modeCheckState = ModeChecked;
                        break;
                    case Disposed:
                        throw new ObjectDisposedException("Process stream");
                }
            }

            /// <summary>
            /// Attempts to read from the memory stream buffers if the are available.
            /// Returns the number of bytes read
            /// </summary>
            private int ReadFromMemoryStreams(byte[] buffer, int offset, int count)
            {
                var bytesRead = 0;
                while (bytesRead < count)
                {
                    // if we have no memory stream, try to take one from the handler
                    if (this.memoryStream == null)
                    {
                        var handlerStream = this.handler.memoryStream;
                        if (handlerStream != null && handlerStream.Length > 0)
                        {
                            handlerStream.Seek(0, SeekOrigin.Begin);
                            this.memoryStream = handlerStream;
                            this.handler.memoryStream = null;
                            // fall through
                        }
                        else
                        {
                            // nothing more to read
                            break;
                        }
                    }

                    // read from our memory stream
                    var result = this.memoryStream.Read(buffer, offset + bytesRead, count - bytesRead);
                    if (result > 0)
                    {
                        bytesRead += result;
                    }
                    else
                    {
                        // our stream is exhausted: clean it up
                        this.memoryStream.Dispose();
                        this.memoryStream = null;
                    }
                }

                return bytesRead;
            }
            #endregion

            #region ---- Other Stream methods ----
            protected override void Dispose(bool disposing)
            {
                this.handler.SetMode(Mode.DiscardContents);
                this.modeCheckState = Disposed;
            }
            #endregion

            #region ---- Non-supported Stream methods ----
            public override void Flush()
            {
                // no-op, since we don't write
            }

            public override long Length
            {
                get { throw new NotSupportedException(MethodBase.GetCurrentMethod().Name); }
            }

            public override long Position
            {
                get { throw new NotSupportedException(MethodBase.GetCurrentMethod().Name); }
                set { throw new NotSupportedException(MethodBase.GetCurrentMethod().Name); }
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException(MethodBase.GetCurrentMethod().Name);
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException(MethodBase.GetCurrentMethod().Name);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException(MethodBase.GetCurrentMethod().Name);
            }
            #endregion
        }
        #endregion

        #region ---- ProcessStreamReader implementation ----
        private sealed class InternalReader : ProcessStreamReader
        {
            private readonly ProcessStreamHandler handler;
            private readonly StreamReader reader;

            public InternalReader(ProcessStreamHandler handler, Encoding encoding)
            {
                this.handler = handler;
                this.reader = new StreamReader(new InternalStream(handler), encoding);
            }

            #region ---- ProcessStreamReader implementation ----
            public override Stream BaseStream
            {
                get { return this.reader.BaseStream; }
            }

            public override void Discard()
            {
                this.handler.SetMode(Mode.DiscardContents);
            }

            public override void StopBuffering()
            {
                this.handler.SetMode(Mode.ManualRead);
            }
            #endregion

            #region ---- TextReader implementation ----
            // all reader methods are overriden to call the same method on the underlying StreamReader.
            // This approach is preferable to extending StreamReader directly, since many of the async methods
            // on StreamReader are conservative and fall back to threaded asynchrony when inheritance is in play
            // (this is done to respect any overriden Read() call). This way, we get the full benefit of asynchrony.

            public override int Peek()
            {
                return this.reader.Peek();
            }

            public override int Read()
            {
                return this.reader.Read();
            }

            public override int Read(char[] buffer, int index, int count)
            {
                return this.reader.Read(buffer, index, count);
            }

            public override Task<int> ReadAsync(char[] buffer, int index, int count)
            {
                return this.reader.ReadAsync(buffer, index, count);
            }

            public override int ReadBlock(char[] buffer, int index, int count)
            {
                return this.reader.ReadBlock(buffer, index, count);
            }

            public override Task<int> ReadBlockAsync(char[] buffer, int index, int count)
            {
                return this.reader.ReadBlockAsync(buffer, index, count);
            }

            public override string ReadLine()
            {
                return this.reader.ReadLine();
            }

            public override Task<string> ReadLineAsync()
            {
                return this.reader.ReadLineAsync();
            }

            public override string ReadToEnd()
            {
                return this.reader.ReadToEnd();
            }

            public override Task<string> ReadToEndAsync()
            {
                return this.reader.ReadToEndAsync();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    this.reader.Dispose();
                }
            }
            #endregion
        }
        #endregion
    }
}