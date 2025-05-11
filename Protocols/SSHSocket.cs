﻿// Copyright 2004-2025 The Poderosa Project.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Granados;
using Granados.SSH2;
using Granados.IO;
using Granados.KeyboardInteractive;
using Granados.SSH;

namespace Poderosa.Protocols {
    //SSHの入出力系
    internal abstract class SSHConnectionEventReceiverBase : ISSHConnectionEventHandler {
        protected readonly SSHTerminalConnection _parent;
        protected ISSHConnection _connection;
        protected IByteAsyncInputStream _callback;
        private bool _normalTerminationCalled;

        public SSHConnectionEventReceiverBase(SSHTerminalConnection parent) {
            _parent = parent;
        }

        //SSHConnection確立時に呼ぶ
        public void SetSSHConnection(ISSHConnection connection) {
            _connection = connection;
        }

        public ISSHConnection Connection {
            get {
                return _connection;
            }
        }

        public virtual void CleanupErrorStatus() {
            if (_connection != null) {
                _connection.Close();
            }
        }

        public abstract void Close();

        public virtual void OnConnectionClosed() {
            OnNormalTerminationCore();
            if (_connection != null) {
                _connection.Close();
            }
        }

        public virtual void OnError(Exception error) {
            OnAbnormalTerminationCore(error.Message);
        }

        //TODO 滅多にないことではあるがこれを拾う先をEXTPで
        public virtual void OnDebugMessage(bool alwaysDisplay, string message) {
            Debug.WriteLine(String.Format("SSH debug {0}", message));
        }

        public virtual void OnIgnoreMessage(byte[] data) {
            Debug.WriteLine(String.Format("SSH ignore {0}[{1}]", data.Length, data[0]));
        }

        public virtual void OnUnhandledMessage(byte type, byte[] data) {
            Debug.WriteLine(String.Format("Unexpected SSH packet type {0}", type));
        }

        protected void OnNormalTerminationCore() {
            if (_normalTerminationCalled)
                return;

            /* NOTE
             *  正常終了の場合でも、SSHパケットレベルではChannelEOF, ChannelClose, ConnectionCloseがあり、場合によっては複数個が組み合わされることもある。
             *  組み合わせの詳細はサーバの実装依存でもあるので、ここでは１回だけ必ず呼ぶということにする。
             */
            _normalTerminationCalled = true;
            _parent.CloseBySocket();

            try {
                if (_callback != null)
                    _callback.OnNormalTermination();
            }
            catch (Exception ex) {
                CloseError(ex);
            }
        }
        protected void OnAbnormalTerminationCore(string msg) {
            _parent.CloseBySocket();

            try {
                if (_callback != null)
                    _callback.OnAbnormalTermination(msg);
            }
            catch (Exception ex) {
                CloseError(ex);
            }
        }
        //Termination処理の失敗時の処理
        private void CloseError(Exception ex) {
            try {
                RuntimeUtil.ReportException(ex);
                CleanupErrorStatus();
            }
            catch (Exception ex2) {
                RuntimeUtil.ReportException(ex2);
            }
        }
    }

    internal class SSHSocket
        : SSHConnectionEventReceiverBase,
          IPoderosaSocketInet, ITerminalOutput, IKeyboardInteractiveAuthenticationHandler {

        private readonly string _remote;
        private readonly IPEndPoint _endPoint;

        private SSHChannelHandler _channelHandler;
        private readonly ByteDataFragment _data;
        private MemoryStream _buffer = new MemoryStream();

        private KeyboardInteractiveAuthHanlder _keyboardInteractiveAuthHanlder;

        public SSHSocket(SSHTerminalConnection parent, string remote, IPEndPoint endPoint)
            : base(parent) {
            _remote = remote;
            _endPoint = endPoint;
            _data = new ByteDataFragment();
        }

        public void RepeatAsyncRead(IByteAsyncInputStream cb) {
            _callback = cb;
            if (_channelHandler != null) {
                _channelHandler.SetReceptionHandler(cb);
            }
        }

        public override void CleanupErrorStatus() {
            if (_channelHandler != null)
                _channelHandler.Operator.Close();
            base.CleanupErrorStatus();
        }

        public void OpenShell() {
            var channelHandler =
                _connection.OpenShell(
                    channelOperator => {
                        var handler = new SSHChannelHandler(channelOperator, OnNormalTerminationCore, OnAbnormalTerminationCore);
                        if (_callback != null) {
                            handler.SetReceptionHandler(_callback);
                        }
                        return handler;
                    }
                );

            bool isReady = channelHandler.Operator.WaitReady();
            if (!isReady) {
                ForceDisposed();
                throw new Exception(PEnv.Strings.GetString("Message.SSHSocket.FailedToStartShell"));
            }

            SSHChannelHandler oldChannelHandler = _channelHandler;
            _channelHandler = channelHandler;

            if (oldChannelHandler != null && oldChannelHandler.Operator is NullSSHChannel) {
                uint tw, th, pw, ph;
                if (((NullSSHChannel)oldChannelHandler.Operator).HasPendingTerminalResize(out tw, out th, out pw, out ph)) {
                    channelHandler.Operator.ResizeTerminal(tw, th, pw, ph);
                }
            }
        }

        public void OpenKeyboardInteractiveShell() {
            _channelHandler = new SSHChannelHandler(new NullSSHChannel(), OnNormalTerminationCore, OnAbnormalTerminationCore);
            if (_callback != null) {
                _channelHandler.SetReceptionHandler(_callback);
            }
        }

        public override void Close() {
            ForceDisposed();
        }

        public void ForceDisposed() {
            try {
                if (_connection != null) {
                    _connection.Disconnect(DisconnectionReasonCode.ByApplication, "bye");
                }
            }
            catch (Exception e) {
                Debug.WriteLine(e.Message);
                Debug.WriteLine(e.StackTrace);
            }
        }

        public void Transmit(ByteDataFragment data) {
            Transmit(data.Buffer, data.Offset, data.Length);
        }

        public void Transmit(byte[] buf, int offset, int length) {
            if (_keyboardInteractiveAuthHanlder != null) {
                // intercept input
                _keyboardInteractiveAuthHanlder.OnData(buf, offset, length);
                return;
            }
            if (_channelHandler != null) {
                _channelHandler.Operator.Send(new DataFragment(buf, offset, length));
            }
        }

        //以下、ITerminalOutput
        public void Resize(int width, int height) {
            if (!_parent.IsClosed && _channelHandler != null)
                _channelHandler.Operator.ResizeTerminal((uint)width, (uint)height, 0, 0);
        }
        public void SendBreak() {
            if (_parent.SSHLoginParameter.Method == SSHProtocol.SSH1)
                throw new NotSupportedException();
            else if (_channelHandler != null) {
                _channelHandler.Operator.SendBreak(500);
            }
        }
        public void SendKeepAliveData() {
            if (!_parent.IsClosed) {
                // Note:
                //  Disconnecting or Closing socket may happen before Send() is called.
                //  In such case, SocketException or ObjectDisposedException will be thrown in Send().
                //  We just ignore the exceptions.
                try {
                    _connection.SendIgnorableData("keep alive");
                }
                catch (SocketException) {
                }
                catch (ObjectDisposedException) {
                }
            }
        }
        public void AreYouThere() {
            throw new NotSupportedException();
        }

        public bool Available {
            get {
                return _connection.SocketStatusReader.DataAvailable;
            }
        }

        public string Remote {
            get {
                return _remote;
            }
        }
        public IPAddress RemoteAddress {
            get {
                return (_endPoint != null) ? _endPoint.Address : null;
            }
        }

        public int? RemotePortNumber {
            get {
                return (_endPoint != null) ? _endPoint.Port : (int?)null;
            }
        }

        #region IKeyboardInteractiveAuthenticationHandler

        public string[] KeyboardInteractiveAuthenticationPrompt(string[] prompts, bool[] echoes) {
            if (_keyboardInteractiveAuthHanlder != null) {
                return _keyboardInteractiveAuthHanlder.KeyboardInteractiveAuthenticationPrompt(prompts, echoes);
            }
            else {
                return prompts.Select(s => "").ToArray();
            }
        }

        public void OnKeyboardInteractiveAuthenticationStarted() {
            _keyboardInteractiveAuthHanlder =
                new KeyboardInteractiveAuthHanlder(
                    (data) => {
                        if (_channelHandler != null) {
                            _channelHandler.OnData(new DataFragment(data, 0, data.Length));
                        }
                    });
        }

        public void OnKeyboardInteractiveAuthenticationCompleted(bool success, Exception error) {
            _keyboardInteractiveAuthHanlder = null;
            try {
                if (!success) {
                    ForceDisposed();
                    throw new Exception(PEnv.Strings.GetString("Message.SSHSocket.AuthenticationFailed"));
                }

                OpenShell();
            }
            catch (Exception e) {
                // FIXME:
                //  the message will not be displayed...
                OnAbnormalTerminationCore(e.Message);
            }
        }

        #endregion
    }

    internal class SSHChannelHandler : ISSHChannelEventHandler {

        private readonly ISSHChannel _channelOperator;
        private readonly Action _onNormalTermination;
        private readonly Action<string> _onAbnormalTermination;
        private MemoryStream _buffer = new MemoryStream();
        private readonly ByteDataFragment _dataFragment = new ByteDataFragment();
        private IByteAsyncInputStream _output;
        private readonly object _outputSync = new object();

        public SSHChannelHandler(ISSHChannel channelOperator, Action onNormalTermination, Action<string> onAbnormalTermination) {
            _channelOperator = channelOperator;
            _onNormalTermination = onNormalTermination;
            _onAbnormalTermination = onAbnormalTermination;
        }

        public ISSHChannel Operator {
            get {
                return _channelOperator;
            }
        }

        public void SetReceptionHandler(IByteAsyncInputStream output) {
            lock (_outputSync) {
                if (_output != null) {
                    return;
                }
                _output = output;
                if (_buffer != null && _buffer.Length > 0) {
                    byte[] bytes = _buffer.ToArray();
                    _buffer.Dispose();
                    _buffer = null;
                    _dataFragment.Set(bytes, 0, bytes.Length);
                    _output.OnReception(_dataFragment);
                }
            }
        }

        public void OnEstablished(DataFragment data) {
        }

        public void OnReady() {
        }

        public void OnData(DataFragment data) {
            lock (_outputSync) {
                if (_output == null) {
                    if (_buffer != null) {
                        _buffer.Write(data.Data, data.Offset, data.Length);
                    }
                    return;
                }

                _dataFragment.Set(data.Data, data.Offset, data.Length);
                _output.OnReception(_dataFragment);
            }
        }

        public void OnExtendedData(uint type, DataFragment data) {
        }

        public void OnClosing(bool byServer) {
        }

        public void OnClosed(bool byServer) {
            _onNormalTermination();
        }

        public void OnEOF() {
            _onNormalTermination();
        }

        public void OnRequestFailed() {
        }

        public void OnError(Exception error) {
            // FIXME: In this case, something message should be displayed for the user.
            //        OnAbnormalTerminationCore() doesn't show the message.
            _onAbnormalTermination(error.Message);
        }

        public void OnUnhandledPacket(byte packetType, DataFragment data) {
        }

        public void OnConnectionLost() {
        }

        public void Dispose() {
            if (_buffer != null) {
                _buffer.Dispose();
                _buffer = null;
            }
        }
    }

    /// <summary>
    /// Dummy channel object during keyboard-interactive authentication.
    /// </summary>
    internal class NullSSHChannel : ISSHChannel {

        private uint? _pendingResizeWidth = null;
        private uint? _pendingResizeHeight = null;
        private uint? _pendingResizePixelWidth = null;
        private uint? _pendingResizePixelHeight = null;

        public uint LocalChannel {
            get {
                throw new NotImplementedException();
            }
        }

        public uint RemoteChannel {
            get {
                throw new NotImplementedException();
            }
        }

        public ChannelType ChannelType {
            get {
                throw new NotImplementedException();
            }
        }

        public string ChannelTypeString {
            get {
                throw new NotImplementedException();
            }
        }

        public bool IsOpen {
            get {
                return true;
            }
        }

        public bool IsReady {
            get {
                return true;
            }
        }

        public int MaxChannelDatagramSize {
            get {
                return 1024;
            }
        }

        public void ResizeTerminal(uint width, uint height, uint pixelWidth, uint pixelHeight) {
            _pendingResizeWidth = width;
            _pendingResizeHeight = height;
            _pendingResizePixelWidth = pixelWidth;
            _pendingResizePixelHeight = pixelHeight;
        }

        public bool HasPendingTerminalResize(out uint width, out uint height, out uint pixelWidth, out uint pixelHeight) {
            width = _pendingResizeWidth ?? 0;
            height = _pendingResizeHeight ?? 0;
            pixelWidth = _pendingResizePixelWidth ?? 0;
            pixelHeight = _pendingResizePixelHeight ?? 0;
            return _pendingResizeWidth.HasValue && _pendingResizeHeight.HasValue && _pendingResizePixelWidth.HasValue && _pendingResizePixelHeight.HasValue;
        }

        public bool WaitReady() {
            return true;
        }

        public void AbortWaitReady() {
        }

        public void Send(DataFragment data) {
        }

        public void SendEOF() {
        }

        public bool SendBreak(int breakLength) {
            return true;
        }

        public void Close() {
        }
    }

    /// <summary>
    /// Keyboard-interactive authentication support for <see cref="SSHSocket"/>.
    /// </summary>
    internal class KeyboardInteractiveAuthHanlder {
        private bool _echoing = true;
        private readonly MemoryStream _inputBuffer = new MemoryStream();
        private readonly object _inputSync = new object();
        private readonly Action<byte[]> _output;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="output">a method to output data to the terminal</param>
        public KeyboardInteractiveAuthHanlder(Action<byte[]> output) {
            _output = output;
        }

        /// <summary>
        /// Show prompt lines and input texts.
        /// </summary>
        /// <param name="prompts"></param>
        /// <param name="echoes"></param>
        /// <returns></returns>
        public string[] KeyboardInteractiveAuthenticationPrompt(string[] prompts, bool[] echoes) {
            Encoding encoding = (Encoding)Encoding.UTF8.Clone();    // TODO:
            encoding.EncoderFallback = EncoderFallback.ReplacementFallback;
            string[] inputs = new string[prompts.Length];
            for (int i = 0; i < prompts.Length; ++i) {
                bool echo = (i < echoes.Length) ? echoes[i] : true;
                byte[] promptBytes = encoding.GetBytes(prompts[i]);
                // echo prompt text
                byte[] lineBytes;
                lock (_inputSync) {
                    _output(promptBytes);
                    _echoing = echo;
                    _inputBuffer.SetLength(0);
                    Monitor.Wait(_inputSync);
                    _echoing = true;
                    lineBytes = _inputBuffer.ToArray();
                }
                string line = encoding.GetString(lineBytes);
                inputs[i] = line;
            }
            return inputs;
        }

        /// <summary>
        /// Process user input.
        /// </summary>
        public void OnData(byte[] data, int offset, int length) {
            int endIndex = offset + length;
            int currentIndex = offset;
            while (currentIndex < endIndex) {
                lock (_inputSync) {
                    int startIndex = currentIndex;
                    bool newLine = false;
                    for (; currentIndex < endIndex; ++currentIndex) {
                        byte b = data[currentIndex];
                        if (b == 13 || b == 10) { //CR/LF
                            newLine = true;
                            break;
                        }
                        _inputBuffer.WriteByte(b);
                    }
                    // flush
                    if (_echoing && currentIndex > startIndex) {
                        _output(GetBytes(data, startIndex, currentIndex - startIndex));
                    }
                    if (newLine) {
                        currentIndex++;
                        _output(new byte[] { 13, 10 });   // CRLF
                        // notify
                        Monitor.PulseAll(_inputSync);
                    }
                }
            }
        }

        private byte[] GetBytes(byte[] data, int offset, int length) {
            byte[] buf = new byte[length];
            if (length > 0) {
                Buffer.BlockCopy(data, offset, buf, 0, length);
            }
            return buf;
        }
    }

}
