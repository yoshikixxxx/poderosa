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
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;

namespace Poderosa.Protocols {

    /// <summary>
    /// TelnetOptionの送受信をする。あまり複雑なサポートをするつもりはない。
    /// Guevaraで必要なのはSuppressGoAhead(双方向), TerminalType, NAWSの３つだけで、これらが成立しなければ例外を投げる。
    /// それ以外のTelnetOptionは拒否するが、拒否が成立しなくても_refusedOptionに格納するだけでエラーにはしない。
    /// オプションのネゴシエーションが終了したら、最後に受信したパケットはもうシェル本体であるので、呼び出し側はこれを使うようにしないといけない。
    /// </summary>
    internal class TelnetNegotiator {
        private string _terminalType;
        //必要ならここから情報を読む
        private int _width;
        private int _height;

        private TelnetCode _state;
        private MemoryStream _sequenceBuffer;
        private TelnetOptionWriter _optionWriter;
        private bool _defaultOptionSent;

        internal enum ProcessResult {
            NOP,
            REAL_0xFF
        }

        //接続を中断するほどではないが期待どおりでなかった場合に警告を出す
        private List<string> _warnings;
        public List<string> Warnings {
            get {
                return _warnings;
            }
        }

        public TelnetNegotiator(string terminal_type, int width, int height) {
            Debug.Assert(terminal_type != null);
            _terminalType = terminal_type;
            _width = width;
            _height = height;
            _warnings = new List<string>();
            _state = TelnetCode.NA;
            _sequenceBuffer = new MemoryStream();
            _optionWriter = new TelnetOptionWriter();
            _defaultOptionSent = false;
        }

        public void SetTerminalSize(int width, int height) {
            _width = width;
            _height = height;
        }

        public void Flush(IPoderosaSocket s) {
            if (!_defaultOptionSent) {
                WriteDefaultOptions();
                _defaultOptionSent = true;
            }

            if (_optionWriter.Length > 0) {
                _optionWriter.WriteTo(s);
                //s.Flush();
                _optionWriter.Clear();
            }
        }

        private void WriteDefaultOptions() {
            _optionWriter.Write(TelnetCode.WILL, TelnetOption.TerminalType);
            _optionWriter.Write(TelnetCode.DO, TelnetOption.SuppressGoAhead);
            _optionWriter.Write(TelnetCode.WILL, TelnetOption.SuppressGoAhead);
            _optionWriter.Write(TelnetCode.WILL, TelnetOption.NAWS);
        }

        public bool InProcessing {
            get {
                return _state != TelnetCode.NA;
            }
        }
        public void StartNegotiate() {
            _state = TelnetCode.IAC;
        }

        public ProcessResult Process(byte data) {
            Debug.Assert(_state != TelnetCode.NA);
            switch (_state) {
                case TelnetCode.IAC:
                    if (data == (byte)TelnetCode.SB || ((byte)TelnetCode.WILL <= data && data <= (byte)TelnetCode.DONT))
                        _state = (TelnetCode)data;
                    else if (data == (byte)TelnetCode.IAC) {
                        _state = TelnetCode.NA;
                        return ProcessResult.REAL_0xFF;
                    }
                    else
                        _state = TelnetCode.NA;
                    break;
                case TelnetCode.SB:
                    if (data != (byte)TelnetCode.SE && data != (byte)TelnetOption.NAWS) //IAC SB 0x1F ときてそれっきり、というケースがあった。ホスト側の仕様違反のように見えるが、Poderosaが何かの応答を返すわけではないのでこれで回避
                        _sequenceBuffer.WriteByte(data);
                    else {
                        ProcessSequence(_sequenceBuffer.ToArray());
                        _state = TelnetCode.NA;
                        _sequenceBuffer.SetLength(0);
                    }
                    break;
                case TelnetCode.DO:
                case TelnetCode.DONT:
                case TelnetCode.WILL:
                case TelnetCode.WONT:
                    ProcessOptionRequest(data);
                    _state = TelnetCode.NA;
                    break;
            }

            return ProcessResult.NOP;
        }

        private void ProcessSequence(byte[] response) {
            if (response.Length > 1 && response[1] == 1) {
                if (response[0] == (byte)TelnetOption.TerminalType)
                    _optionWriter.WriteTerminalName(_terminalType);
            }
        }

        private void ProcessOptionRequest(byte option_) {
            TelnetOption option = (TelnetOption)option_;
            switch (option) {
                case TelnetOption.TerminalType:
                    if (_state == TelnetCode.DO)
                        _optionWriter.Write(TelnetCode.WILL, option);
                    else
                        _warnings.Add(PEnv.Strings.GetString("Message.Telnet.FailedToSendTerminalType"));
                    break;
                case TelnetOption.NAWS:
                    if (_state == TelnetCode.DO)
                        _optionWriter.WriteTerminalSize(_width, _height);
                    else
                        _warnings.Add(PEnv.Strings.GetString("Message.Telnet.FailedToSendWidnowSize"));
                    break;
                case TelnetOption.SuppressGoAhead:
                    if (_state != TelnetCode.WILL && _state != TelnetCode.DO) //!!両方が来たことを確認する
                        _warnings.Add(PEnv.Strings.GetString("Message.Telnet.FailedToSendSuppressGoAhead"));
                    break;
                case TelnetOption.LocalEcho:
                    if (_state == TelnetCode.DO)
                        _optionWriter.Write(TelnetCode.WILL, option);
                    break;
                default: //上記以外はすべて拒否。DOにはWON'T, WILLにはDON'Tの応答を返す。 
                    if (_state == TelnetCode.DO)
                        _optionWriter.Write(TelnetCode.WONT, option);
                    else if (_state == TelnetCode.WILL)
                        _optionWriter.Write(TelnetCode.DONT, option);
                    break;
            }
        }

    }


    internal class TelnetOptionWriter {
        private MemoryStream _strm;
        public TelnetOptionWriter() {
            _strm = new MemoryStream();
        }
        public long Length {
            get {
                return _strm.Length;
            }
        }
        public void Clear() {
            _strm.SetLength(0);
        }

        public void WriteTo(IPoderosaSocket target) {
            byte[] data = _strm.ToArray();
            target.Transmit(data, 0, data.Length);
            //target.Flush();
        }
        public void Write(TelnetCode code, TelnetOption opt) {
            _strm.WriteByte((byte)TelnetCode.IAC);
            _strm.WriteByte((byte)code);
            _strm.WriteByte((byte)opt);
        }
        public void WriteTerminalName(string name) {
            _strm.WriteByte((byte)TelnetCode.IAC);
            _strm.WriteByte((byte)TelnetCode.SB);
            _strm.WriteByte((byte)TelnetOption.TerminalType);
            _strm.WriteByte(0); //0 = IS
            byte[] t = Encoding.ASCII.GetBytes(name);
            _strm.Write(t, 0, t.Length);
            _strm.WriteByte((byte)TelnetCode.IAC);
            _strm.WriteByte((byte)TelnetCode.SE);
        }
        public void WriteTerminalSize(int width, int height) {
            _strm.WriteByte((byte)TelnetCode.IAC);
            _strm.WriteByte((byte)TelnetCode.SB);
            _strm.WriteByte((byte)TelnetOption.NAWS);
            _strm.WriteByte((byte)(width >> 8));
            _strm.WriteByte((byte)width);
            _strm.WriteByte((byte)(height >> 8));
            _strm.WriteByte((byte)height);
            _strm.WriteByte((byte)TelnetCode.IAC);
            _strm.WriteByte((byte)TelnetCode.SE);
        }
    }

    internal enum TelnetCode {
        NA = 0,
        SE = 240,
        NOP = 241,
        Break = 243,
        AreYouThere = 246,
        SB = 250,
        WILL = 251,
        WONT = 252,
        DO = 253,
        DONT = 254,
        IAC = 255
    }
    internal enum TelnetOption {
        LocalEcho = 1,
        SuppressGoAhead = 3,
        TerminalType = 24,
        NAWS = 31
    }

    internal class TelnetNegotiationException : ApplicationException {
        public TelnetNegotiationException(string msg)
            : base(msg) {
        }
    }

}
