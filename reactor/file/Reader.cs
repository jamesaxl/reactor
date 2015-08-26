﻿/*--------------------------------------------------------------------------

Reactor

The MIT License (MIT)

Copyright (c) 2015 Haydn Paterson (sinclair) <haydn.developer@gmail.com>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

---------------------------------------------------------------------------*/

using System;
using System.IO;

namespace Reactor.File {

    /// <summary>
    /// Reactor file reader.
    /// </summary>
    public class Reader : Reactor.IReadable, IDisposable {

        #region States

        /// <summary>
        /// Readable state.
        /// </summary>
        internal enum State {
            /// <summary>
            /// The initial state of this stream. A stream
            /// in a pending state signals that the stream
            /// is waiting on the caller to issue a read request
            /// the the underlying resource, by attaching a
            /// OnRead, OnReadable, or calling Read().
            /// </summary>
            Pending,
            /// <summary>
            /// A stream in a reading state signals that the
            /// stream is currently requesting data from the
            /// underlying resource and is waiting on a 
            /// response.
            /// </summary>
            Reading,
            /// <summary>
            /// A stream in a paused state will bypass attempts
            /// to read on the underlying resource. A paused
            /// stream must be resumed by the caller.
            /// </summary>
            Paused,
            /// <summary>
            /// Indicates this stream has ended. Streams can end
            /// by way of reaching the end of the stream, or through
            /// error.
            /// </summary>
            Ended
        }

        /// <summary>
        /// Readable mode.
        /// </summary>
        internal enum Mode {
            /// <summary>
            /// This stream is using flowing semantics.
            /// </summary>
            Flowing,
            /// <summary>
            /// This stream is using non-flowing semantics.
            /// </summary>
            NonFlowing
        }

        #endregion

        private Reactor.Async.Event                   onreadable;
        private Reactor.Async.Event<Reactor.Buffer>   onread;
        private Reactor.Async.Event<Exception>        onerror;
        private Reactor.Async.Event                   onend;
        private Reactor.Streams.Reader                reader;
        private Reactor.Buffer                        buffer;
        private State                                 state;
        private Mode                                  mode;
        private long                                  offset;
        private long                                  count;
        private long                                  received;
        private long                                  length;

        #region Constructors

        /// <summary>
        /// Creates a new file reader.
        /// </summary>
        /// <param name="filename">The file to read.</param>
        /// <param name="offset">The offset to begin reading.</param>
        /// <param name="count">The number of bytes to read.</param>
        /// <param name="mode">The file mode.</param>
        /// <param name="share">The file share.</param>
        public Reader (string filename, long offset, long count, System.IO.FileMode mode, System.IO.FileShare share) {
            this.onreadable = Reactor.Async.Event.Create();
            this.onread     = Reactor.Async.Event.Create<Reactor.Buffer>();
            this.onerror    = Reactor.Async.Event.Create<Exception>();
            this.onend      = Reactor.Async.Event.Create();
            this.buffer     = Reactor.Buffer.Create();
            this.state      = State.Pending;
            this.mode       = Mode.NonFlowing;
            var stream      = new FileStream(filename, mode, FileAccess.Read, share);
            this.length     = stream.Length;
            this.received   = 0;
            this.offset     = (offset > (stream.Length)) ? stream.Length : offset;
            this.count      = (count  > (stream.Length - this.offset)) ? (stream.Length - this.offset) : count;
            stream.Seek(this.offset, SeekOrigin.Begin);
            this.reader     = Reactor.Streams.Reader.Create(stream, Reactor.Settings.DefaultBufferSize);
            this.reader.OnRead  (this._Data);
            this.reader.OnError (this._Error);
            this.reader.OnEnd   (this._End);
        }

        #endregion

        #region Events

        /// <summary>
        /// Subscribes this action to the 'readable' event. When a chunk of 
        /// data can be read from the stream, it will emit a 'readable' event.
        /// Listening for a 'readable' event will cause some data to be read 
        /// into the internal buffer from the underlying resource. If a stream 
        /// happens to be in a 'paused' state, attaching a readable event will 
        /// transition into a pending state prior to reading from the resource.
        /// </summary>
        /// <param name="callback"></param>
        public void OnReadable (Reactor.Action callback) {
            this.onreadable.On(callback);
            this.mode = Mode.NonFlowing;
            if (this.state == State.Paused) {
                this.state = State.Pending;
            }
            this._Read();
        }

        /// <summary>
        /// Subscribes this action once to the 'readable' event. When a chunk of 
        /// data can be read from the stream, it will emit a 'readable' event.
        /// Listening for a 'readable' event will cause some data to be read 
        /// into the internal buffer from the underlying resource. If a stream 
        /// happens to be in a 'paused' state, attaching a readable event will 
        /// transition into a pending state prior to reading from the resource.
        /// </summary>
        /// <param name="callback"></param>
        public void OnceReadable (Reactor.Action callback) {
            this.onreadable.Once(callback);
            this.mode  = Mode.NonFlowing;
            if (this.state == State.Paused) {
                this.state = State.Pending;
            }
            this._Read();
        }

        /// <summary>
        /// Unsubscribes this action from the 'readable' event.
        /// </summary>
        /// <param name="callback"></param>
        public void RemoveReadable (Reactor.Action callback) {
            this.onreadable.Remove(callback);
        }

        /// <summary>
        /// Subscribes this action to the 'read' event. Attaching a data event 
        /// listener to a stream that has not been explicitly paused will 
        /// switch the stream into flowing mode and begin reading immediately. 
        /// Data will then be passed as soon as it is available.
        /// </summary>
        /// <param name="callback"></param>
        public void OnRead (Reactor.Action<Reactor.Buffer> callback) {
            this.onread.On(callback);
            if (this.state == State.Pending) {
                this.Resume();
            }
        }

        /// <summary>
        /// Subscribes this action once to the 'read' event. Attaching a data event 
        /// listener to a stream that has not been explicitly paused will 
        /// switch the stream into flowing mode and begin reading immediately. 
        /// Data will then be passed as soon as it is available.
        /// </summary>
        /// <param name="callback"></param>
        public void OnceRead (Reactor.Action<Reactor.Buffer> callback) {
            this.onread.Once(callback);
            if (this.state == State.Pending) {
                this.Resume();
            }
        }

        /// <summary>
        /// Unsubscribes this action from the 'read' event.
        /// </summary>
        /// <param name="callback"></param>
        public void RemoveRead (Reactor.Action<Reactor.Buffer> callback) {
            this.onread.Remove(callback);
        }

        /// <summary>
        /// Subscribes this action to the 'error' event.
        /// </summary>
        /// <param name="callback"></param>
        public void OnError (Reactor.Action<Exception> callback) {
            this.onerror.On(callback);
        }

        /// <summary>
        /// Unsubscribes this action from the 'error' event.
        /// </summary>
        /// <param name="callback"></param>
        public void RemoveError (Reactor.Action<Exception> callback) {
            this.onerror.Remove(callback);
        }

        /// <summary>
        /// Subscribes this action to the 'end' event.
        /// </summary>
        /// <param name="callback"></param>
        public void OnEnd (Reactor.Action callback) {
            this.onend.On(callback);
        }

        /// <summary>
        /// Unsubscribes this action from the 'end' event.
        /// </summary>
        /// <param name="callback"></param>
        public void RemoveEnd (Reactor.Action callback) {
            this.onend.Remove(callback);
        }

        #endregion

        #region Methods

        /// <summary>
        /// Will read this number of bytes out of the internal buffer. If there 
        /// is no data available, then it will return a zero length buffer. If 
        /// the internal buffer has been completely read, then this method will 
        /// issue a new read request on the underlying resource in non-flowing 
        /// mode. Any data read with a length > 0 will also be emitted as a 'read' 
        /// event.
        /// </summary>
        /// <param name="count">The number of bytes to read.</param>
        /// <returns></returns>
        public Reactor.Buffer Read (int count) {
            var result = Reactor.Buffer.Create(this.buffer.Read(count));
            if (result.Length > 0) {
                this.onread.Emit(result);
            }
            if (this.buffer.Length == 0) {
                this.mode = Mode.NonFlowing;
                this._Read();
            }
            return result;
        }

        /// <summary>
        /// Will read all data out of the internal buffer. If no data is available 
        /// then it will return a zero length buffer. This method will then issue 
        /// a new read request on the underlying resource in non-flowing mode. Any 
        /// data read with a length > 0 will also be emitted as a 'read' event.
        /// </summary>
        public Reactor.Buffer Read () {
            return this.Read(this.buffer.Length);
        }

        /// <summary>
        /// Unshifts this buffer back to this stream.
        /// </summary>
        /// <param name="buffer">The buffer to unshift.</param>
        public void Unshift (Reactor.Buffer buffer) {
            this.buffer.Unshift(buffer);
        }

        /// <summary>
        /// Pauses this stream. This method will cause a 
        /// stream in flowing mode to stop emitting data events, 
        /// switching out of flowing mode. Any data that becomes 
        /// available will remain in the internal buffer.
        /// </summary>
        public void Pause() {
            this.mode  = Mode.NonFlowing;
            this.state = State.Paused;
        }

        /// <summary>
        /// This method will cause the readable stream to resume emitting data events.
        /// This method will switch the stream into flowing mode. If you do not want 
        /// to consume the data from a stream, but you do want to get to its end event, 
        /// you can call readable.resume() to open the flow of data.
        /// </summary>
        public void Resume() {
            this.mode = Mode.Flowing;
            this.state = State.Pending;
            this._Read();
        }

        /// <summary>
        /// Pipes data to a writable stream.
        /// </summary>
        /// <param name="writable"></param>
        /// <returns></returns>
        public Reactor.IReadable Pipe (Reactor.IWritable writable) {
            this.OnRead(data => {
                this.Pause();
                writable.Write(data)
                        .Then(this.Resume)
                        .Error(this._Error);
            });
            this.OnEnd (() => writable.End());
            return this;
        }

        #endregion

        #region Properties
        
        /// <summary>
        /// Gets the length in bytes of the stream.
        /// </summary>
        public long Length {
            get { return this.length; }
        }

        #endregion

        #region Buffer

        /// <summary>
        /// Reads a boolean from this stream.
        /// </summary>
        /// <returns></returns>
        public System.Boolean ReadBool () {
            var data = this.Read(sizeof(System.Boolean));
            return BitConverter.ToBoolean(data.ToArray(), 0);
        }

        /// <summary>
        /// Reads a Int16 value from this stream.
        /// </summary>
        /// <returns></returns>
        public System.Int16 ReadInt16 () {
            var data = this.Read(sizeof(System.Int16));
            return BitConverter.ToInt16(data.ToArray(), 0);
        }

        /// <summary>
        /// Reads a UInt16 value from this stream.
        /// </summary>
        /// <returns></returns>
        public System.UInt16 ReadUInt16 () {
            var data = this.Read(sizeof(System.UInt16));
            return BitConverter.ToUInt16(data.ToArray(), 0);
        }

        /// <summary>
        /// Reads a Int32 value from this stream.
        /// </summary>
        /// <returns></returns>
        public System.Int32 ReadInt32 () {
            var data = this.Read(sizeof(System.Int32));
            return BitConverter.ToInt32(data.ToArray(), 0);
        }

        /// <summary>
        /// Reads a UInt32 value from this stream.
        /// </summary>
        /// <returns></returns>
        public System.UInt32 ReadUInt32 () {
            var data = this.Read(sizeof(System.UInt32));
            return BitConverter.ToUInt32(data.ToArray(), 0);
        }

        /// <summary>
        /// Reads a Int64 value from this stream.
        /// </summary>
        /// <returns></returns>
        public System.Int64 ReadInt64 () {
            var data = this.Read(sizeof(System.Int64));
            return BitConverter.ToInt64(data.ToArray(), 0);
        }

        /// <summary>
        /// Reads a UInt64 value from this stream.
        /// </summary>
        /// <returns></returns>
        public System.UInt64 ReadUInt64 () {
            var data = this.Read(sizeof(System.UInt64));
            return BitConverter.ToUInt64(data.ToArray(), 0);
        }

        /// <summary>
        /// Reads a Single precision value from this stream.
        /// </summary>
        /// <returns></returns>
        public System.Single ReadSingle () {
            var data = this.Read(sizeof(System.Single));
            return BitConverter.ToSingle(data.ToArray(), 0);
        }

        /// <summary>
        /// Reads a Double precision value from this stream.
        /// </summary>
        /// <returns></returns>
        public System.Double ReadDouble () {
            var data = this.Read(sizeof(System.Double));
            return BitConverter.ToDouble(data.ToArray(), 0);
        }

        /// <summary>
        /// Unshifts this data to the stream.
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="index"></param>
        /// <param name="count"></param>
        public void Unshift (byte[] buffer, int index, int count) {
            this.Unshift(Reactor.Buffer.Create(buffer, 0, count));
        }

        /// <summary>
        /// Unshifts this data to the stream.
        /// </summary>
        /// <param name="buffer"></param>
        public void Unshift (byte[] buffer) {
            this.Unshift(Reactor.Buffer.Create(buffer));
        }

        /// <summary>
        /// Unshifts this data to the stream.
        /// </summary>
        /// <param name="data"></param>
        public void Unshift (char data) {
            this.Unshift(data.ToString());
        }

        /// <summary>
        /// Unshifts this data to the stream.
        /// </summary>
        /// <param name="data"></param>
        public void Unshift (string data) {
            this.Unshift(System.Text.Encoding.UTF8.GetBytes(data));
        }

        /// <summary>
        /// Unshifts this data to the stream.
        /// </summary>
        /// <param name="format"></param>
        /// <param name="args"></param>
        public void Unshift (string format, params object[] args) {
            format = string.Format(format, args);
            this.Unshift(System.Text.Encoding.UTF8.GetBytes(format));
        }

        /// <summary>
        /// Unshifts this data to the stream.
        /// </summary>
        /// <param name="data"></param>
        public void Unshift (byte data) {
            this.Unshift(new byte[1] { data });
        }

        /// <summary>
        /// Unshifts a System.Boolean value to the stream.
        /// </summary>
        /// <param name="value"></param>
        public void Unshift (bool value) {
            this.Unshift(BitConverter.GetBytes(value));
        }

        /// <summary>
        /// Unshifts a System.Int16 value to the stream.
        /// </summary>
        /// <param name="value"></param>
        public void Unshift (short value) {
            this.Unshift(BitConverter.GetBytes(value));
        }

        /// <summary>
        /// Unshifts a System.UInt16 value to the stream.
        /// </summary>
        /// <param name="value"></param>
        public void Unshift (ushort value) {
            this.Unshift(BitConverter.GetBytes(value));
        }

        /// <summary>
        /// Unshifts a System.Int32 value to the stream.
        /// </summary>
        /// <param name="value"></param>
        public void Unshift (int value) {
            this.Unshift(BitConverter.GetBytes(value));
        }

        /// <summary>
        /// Unshifts a System.UInt32 value to the stream.
        /// </summary>
        /// <param name="value"></param>
        public void Unshift (uint value) {
            this.Unshift(BitConverter.GetBytes(value));
        }

        /// <summary>
        /// Unshifts a System.Int64 value to the stream.
        /// </summary>
        /// <param name="value"></param>
        public void Unshift (long value) {
            this.Unshift(BitConverter.GetBytes(value));
        }

        /// <summary>
        /// Unshifts a System.UInt64 value to the stream.
        /// </summary>
        /// <param name="value"></param>
        public void Unshift (ulong value) {
            this.Unshift(BitConverter.GetBytes(value));
        }

        /// <summary>
        /// Unshifts a System.Single value to the stream.
        /// </summary>
        /// <param name="value"></param>
        public void Unshift (float value) {
            this.Unshift(BitConverter.GetBytes(value));
        }

        /// <summary>
        /// Unshifts a System.Double value to the stream.
        /// </summary>
        /// <param name="value"></param>
        public void Unshift (double value) {
            this.Unshift(BitConverter.GetBytes(value));
        }

        #endregion

        #region Machine

        /// <summary>
        /// Reads from the internal stream if we are
        /// in a resume state.
        /// </summary>
        private void _Read () {
            if (this.state == State.Pending) {
                this.state = State.Reading;
                if (this.buffer.Length > 0) {
                    var clone = this.buffer.Clone();
                    this.buffer.Clear();
                    this._Data(clone);
                }
                else {
                    this.reader.Read();
                }
            }
        }

        /// <summary>
        /// Handles incoming data from the stream.
        /// </summary>
        /// <param name="buffer"></param>
        private void _Data (Reactor.Buffer buffer) {
            if (this.state == State.Reading) {
                this.state = State.Pending;
                /* in the case of file readers, we
                 * have semantics around read ranges,
                 * because of this, and because we
                 * read data in fixed size chunks, 
                 * the following detects for buffer
                 * overflows and trims the end of 
                 * the buffer prior to emitting
                 * back to the caller.
                 */ 
                var length    = buffer.Length;
                var ended     = false;
                this.received = this.received + length;
                if (this.received >= this.count) {
                    var overflow = this.received - this.count;
                    length = length - (int)overflow;
                    var truncated = Reactor.Buffer.Create();
                    truncated.Write(buffer.ToArray(), 0, length);
                    buffer.Dispose();
                    buffer = truncated;
                    ended  = true;
                }

                this.buffer.Write(buffer);
                buffer.Dispose();

                switch (this.mode) {
                    case Mode.Flowing:
                        var clone = this.buffer.Clone();
                        this.buffer.Clear();
                        this.onread.Emit(clone);
                        if(ended)
                            this._End();
                        else
                            this._Read();
                        break;
                    case Mode.NonFlowing:
                        this.onreadable.Emit();
                        if(ended)
                            this._End();
                        break;
                } 
            }
        }

        /// <summary>
        /// Handles stream errors.
        /// </summary>
        /// <param name="error"></param>
        private void _Error (Exception error) {
            if (this.state != State.Ended) {
                this.onerror.Emit(error);
                this._End();
            }
        }

        /// <summary>
        /// Terminates the stream.
        /// </summary>
        private void _End () {
            if (this.state != State.Ended) {
                this.state = State.Ended;
                this.reader.Dispose();
                this.onend.Emit();
            }
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// Disposes of this stream.
        /// </summary>
        public void Dispose() {
            this._End();
        }

        #endregion

        #region Statics

        /// <summary>
        /// Creates a new file reader.
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="mode"></param>
        /// <param name="share"></param>
        /// <returns></returns>
        public static Reader Create(string filename, FileMode mode, FileShare share) {
            return new Reader(filename, 0, long.MaxValue, mode, share);
        }

        /// <summary>
        /// Creates a new file reader.
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="index"></param>
        /// <param name="mode"></param>
        /// <param name="share"></param>
        /// <returns></returns>
        public static Reader Create(string filename, long index, FileMode mode, FileShare share) {
            return new Reader(filename, index, long.MaxValue, mode, share);
        }

        /// <summary>
        /// Creates a new file reader.
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="index"></param>
        /// <param name="count"></param>
        /// <param name="mode"></param>
        /// <param name="share"></param>
        /// <returns></returns>
        public static Reader Create(string filename, long index, long count, FileMode mode, FileShare share) {
            return new Reader(filename, index, count, mode, share);
        }

        /// <summary>
        /// Creates a new file reader.
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="mode"></param>
        /// <returns></returns>
        public static Reader Create(string filename, FileMode mode) {
            return new Reader(filename, 0, long.MaxValue, mode, FileShare.Read);
        }

        /// <summary>
        /// Creates a new file reader.
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="index"></param>
        /// <param name="mode"></param>
        /// <returns></returns>
        public static Reader Create(string filename, long index, FileMode mode) {
            return new Reader(filename, index, long.MaxValue, mode, FileShare.Read);
        }

        /// <summary>
        /// Creates a new file reader.
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="index"></param>
        /// <param name="count"></param>
        /// <param name="mode"></param>
        /// <returns></returns>
        public static Reader Create(string filename, long index, long count, FileMode mode) {
            return new Reader(filename, index, count, mode, FileShare.Read);
        }

        /// <summary>
        /// Creates a new file reader.
        /// </summary>
        /// <param name="filename"></param>
        /// <returns></returns>
        public static Reader Create(string filename) {
            return new Reader(filename, 0, long.MaxValue, FileMode.Open, FileShare.Read);
        }

        /// <summary>
        /// Creates a new file reader.
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="index"></param>
        /// <returns></returns>
        public static Reader Create(string filename, long index) {
            return new Reader(filename, index, long.MaxValue, FileMode.OpenOrCreate, FileShare.Read);
        }

        /// <summary>
        /// Creates a new file reader.
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="index"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        public static Reader Create(string filename, long index, long count) {
            return new Reader(filename, index, count, FileMode.OpenOrCreate, FileShare.Read);
        }

        #endregion
    }
}