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

//#define DEBUG_MOUSETRACKING

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics;
using System.Text;
using System.IO;
using System.Threading;

using Poderosa.Document;
using Poderosa.View;
using Poderosa.Sessions;
using Poderosa.ConnectionParam;
using Poderosa.Protocols;
using Poderosa.Forms;
using Poderosa.Commands;

namespace Poderosa.Terminal {

    /// <summary>
    /// <ja>
    /// ターミナルを示すコントロールです。
    /// </ja>
    /// <en>
    /// Control to show the terminal.
    /// </en>
    /// </summary>
    /// <exclude/>
    /// 
    public class TerminalControl : CharacterDocumentViewer {
        //ID
        private int _instanceID;
        private static int _instanceCount = 1;
        public string InstanceID {
            get {
                return "TC" + _instanceID;
            }
        }

        private System.Windows.Forms.Timer _sizeTipTimer;
        private ITerminalControlHost _session;
        private TerminalDocument _documentCache; // cache _session.Terminal.Document
        private readonly ReaderWriterLockSlim _documentLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

        private readonly TerminalEmulatorMouseHandler _terminalEmulatorMouseHandler;
        private readonly MouseTrackingHandler _mouseTrackingHandler;
        private readonly MouseWheelHandler _mouseWheelHandler;

        private Label _sizeTip;

        private delegate void AdjustIMECompositionDelegate();

        private bool _inIMEComposition; //IMEによる文字入力の最中であればtrueになる
        private bool _ignoreValueChangeEvent;

        private bool _escForVI;

        private bool _resetViewTop;

        private bool _isResizeSuspended;
        private Size? _pendingSize;

        /// <summary>
        /// Scope to guarantee consistent access to the TerminalDocument bound to this control
        /// </summary>
        /// <remarks>
        /// To avoid frequent memory allocations, this type is defined as struct.
        /// </remarks>
        private struct TerminalDocumentScope : IDisposable {
            /// <summary>
            /// Document bound to the viewer. This may be null.
            /// </summary>
            public readonly TerminalDocument Document;

            private readonly ReaderWriterLockSlim _lock;

            public TerminalDocumentScope(TerminalDocument document, ReaderWriterLockSlim documentLock) {
                this.Document = document;
                this._lock = documentLock;
            }

            public void Dispose() {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// The current document bound to this control. This may be null.
        /// </summary>
        /// <remarks>
        /// <para>Direct access to the current document still exist to work with other existing components.</para>
        /// <para>For consistent access to the current document, <see cref="GetDocumentScope()"/> or <see cref="GetTerminalDocumentScope()"/> should be used.</para>
        /// </remarks>
        public IPoderosaDocument CurrentDocument {
            get {
                return _documentCache;
            }
        }

        protected ITerminalSettings GetTerminalSettings() {
            // FIXME: In rare case, _session may be null...
            return _session.TerminalSettings;
        }
        protected TerminalTransmission GetTerminalTransmission() {
            // FIXME: In rare case, _session may be null...
            return _session.TerminalTransmission;
        }
        protected AbstractTerminal GetTerminal() {
            // FIXME: In rare case, _session may be null...
            return _session.Terminal;
        }
        private bool IsConnectionClosed() {
            // FIXME: In rare case, _session may be null...
            return _session.TerminalTransmission.Connection.IsClosed;
        }


        /// <summary>
        /// 必要なデザイナ変数です。
        /// </summary>
        private System.ComponentModel.Container components = null;

        public TerminalControl() {
            _instanceID = _instanceCount++;
            _enableAutoScrollBarAdjustment = false;
            _escForVI = false;
            _resetViewTop = false;

            // この呼び出しは、Windows.Forms フォーム デザイナで必要です。
            InitializeComponent();

            _mouseWheelHandler = new MouseWheelHandler(this, _VScrollBar);
            _mouseHandlerManager.AddFirstHandler(_mouseWheelHandler);    // mouse wheel handler will become second handler
            _mouseTrackingHandler = new MouseTrackingHandler(this);
            _mouseHandlerManager.AddFirstHandler(_mouseTrackingHandler);    // mouse tracking handler become first handler
            _terminalEmulatorMouseHandler = new TerminalEmulatorMouseHandler(this);
            _mouseHandlerManager.AddLastHandler(_terminalEmulatorMouseHandler);
            //TODO タイマーは共用化？
            _sizeTipTimer = new System.Windows.Forms.Timer();
            _sizeTipTimer.Interval = 2000;
            _sizeTipTimer.Tick += new EventHandler(this.OnHideSizeTip);

            this.SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        }

        public void Attach(ITerminalControlHost session) {
            _documentLock.EnterWriteLock();
            try {
                _session = session;
                _documentCache = session.Terminal.Document;
                DocumentChanged(true);
                ChangeUICursor(_documentCache.UICursor);

                _mouseTrackingHandler.Attach(session);
                _mouseWheelHandler.Attach(session);

                ITerminalEmulatorOptions opt = TerminalEmulatorPlugin.Instance.TerminalEmulatorOptions;
                _caret.Blink = opt.CaretBlink;
                _caret.Color = opt.CaretColor;
                _caret.Style = opt.CaretType;
                _caret.Reset();

                //KeepAliveタイマ起動は最も遅らせた場合でココ
                TerminalEmulatorPlugin.Instance.KeepAlive.Refresh(opt.KeepAliveInterval);

                //ASCIIWordBreakTable : 今は共有設定だが、Session固有にデータを持つようにするかもしれない含みを持たせて。
                ASCIIWordBreakTable table = ASCIIWordBreakTable.Default;
                table.Reset();
                foreach (char ch in opt.AdditionalWordElement)
                    table.Set(ch, ASCIIWordBreakTable.LETTER);

                TerminalDocument document = _documentCache;
                lock (document) {
                    _ignoreValueChangeEvent = true;
                    _session.Terminal.CommitScrollBar(_VScrollBar, false);
                    _ignoreValueChangeEvent = false;

                    if (!IsConnectionClosed()) {
                        Size ts = CalcTerminalSize(GetRenderProfile());

                        //TODO ネゴ開始前はここを抑制したい
                        if (ts.Width != document.TerminalWidth || ts.Height != document.TerminalHeight) {
                            ResizeTerminal(document, ts.Width, ts.Height);
                        }
                    }
                }
                Invalidate(true);
            }
            finally {
                _documentLock.ExitWriteLock();
            }
        }

        public void Detach() {
            _documentLock.EnterWriteLock();
            try {
                if (DebugOpt.DrawingPerformance)
                    DrawingPerformance.Output();

                if (_inIMEComposition)
                    ClearIMEComposition();

                _mouseTrackingHandler.Detach();
                _mouseWheelHandler.Detach();

                _session = null;
                _documentCache = null;
                DocumentChanged(false);
                ChangeUICursor(null);
                Invalidate(true);
            }
            finally {
                _documentLock.ExitWriteLock();
            }
        }

        public void ChangeUICursor(Cursor uiCursor) {
            if (uiCursor != null) {
                SetUICursor(uiCursor);
            }
            else {
                ResetUICursor();
            }
        }

        public override bool HasDocument {
            get {
                return _documentCache != null;
            }
        }

        public override DocumentScope GetDocumentScope() {
            _documentLock.EnterReadLock();
            return new DocumentScope(_documentCache, _documentLock);
        }

        private TerminalDocumentScope GetTerminalDocumentScope() {
            _documentLock.EnterReadLock();
            return new TerminalDocumentScope(_documentCache, _documentLock);
        }

        protected override void OnUpdatingTimer() {
            if (_resetViewTop) {
                _resetViewTop = false;
                using (TerminalDocumentScope docScope = GetTerminalDocumentScope()) {
                    if (docScope.Document != null) {
                        lock (docScope.Document) {
                            docScope.Document.ResetViewTop();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 使用されているリソースに後処理を実行します。
        /// </summary>
        protected override void Dispose(bool disposing) {
            if (disposing) {
                if (components != null) {
                    components.Dispose();
                }
            }
            base.Dispose(disposing);
        }


        private void InitializeComponent() {
            this.SuspendLayout();
            this._sizeTip = new Label();
            // 
            // _sizeTip
            // 
            this._sizeTip.Visible = false;
            this._sizeTip.BorderStyle = BorderStyle.FixedSingle;
            this._sizeTip.TextAlign = ContentAlignment.MiddleCenter;
            this._sizeTip.BackColor = Color.FromKnownColor(KnownColor.Info);
            this._sizeTip.ForeColor = Color.FromKnownColor(KnownColor.InfoText);
            this._sizeTip.Size = new Size(64, 16);
            // 
            // TerminalPane
            // 
            this.TabStop = false;
            this.AllowDrop = true;

            this.Controls.Add(_sizeTip);
            this.ImeMode = ImeMode.NoControl;
            this.ResumeLayout(false);

        }

        /// <summary>
        /// Sends bytes. Data may be repeated as local echo.
        /// </summary>
        /// <param name="data">Byte array that contains data to send.</param>
        /// <param name="offset">Offset in data</param>
        /// <param name="length">Length of bytes to transmit</param>
        internal void Transmit(byte[] data, int offset, int length) {
            if (this.InvokeRequired) {
                // UI thread may be waiting for unlocking of the current document in the OnPaint handler.
                // If the caller is locking the current document, Invoke() causes dead lock.
                this.BeginInvoke((MethodInvoker)delegate() {
                    GetTerminalTransmission().Transmit(data, offset, length);
                });
            }
            else {
                GetTerminalTransmission().Transmit(data, offset, length);
            }
        }

        /// <summary>
        /// Sends bytes without local echo.
        /// </summary>
        /// <param name="data">Byte array that contains data to send.</param>
        /// <param name="offset">Offset in data</param>
        /// <param name="length">Length of bytes to transmit</param>
        internal void TransmitDirect(byte[] data, int offset, int length) {
            if (this.InvokeRequired) {
                // UI thread may be waiting for unlocking of the current document in the OnPaint handler.
                // If the caller is locking the current document, Invoke() causes dead lock.
                this.BeginInvoke((MethodInvoker)delegate() {
                    GetTerminalTransmission().TransmitDirect(data, offset, length);
                });
            }
            else {
                GetTerminalTransmission().TransmitDirect(data, offset, length);
            }
        }

        /*
         * ↓  受信スレッドによる実行のエリア
         */

        public void DataArrived() {
            //よくみると、ここを実行しているときはdocumentをロック中なので、上のパターンのようにSendMessageを使うとデッドロックの危険がある
            InternalDataArrived();
        }

        private void InternalDataArrived() {
            using (TerminalDocumentScope docScope = GetTerminalDocumentScope()) {
                if (docScope.Document == null) {
                    return;	// ペインを閉じる時に _tag が null になっていることがある
                }

                if (!this.ITextSelection.IsEmpty) {
                    docScope.Document.InvalidatedRegion.InvalidatedAll = true; //面倒だし
                    this.ITextSelection.Clear();
                }
                //Debug.WriteLine(String.Format("v={0} l={1} m={2}", _VScrollBar.Value, _VScrollBar.LargeChange, _VScrollBar.Maximum));
                if (DebugOpt.DrawingPerformance)
                    DrawingPerformance.MarkReceiveData(docScope.Document.InvalidatedRegion);

                //部分変換中であったときのための調整
                if (_inIMEComposition) {
                    if (this.InvokeRequired)
                        this.Invoke(new AdjustIMECompositionDelegate(AdjustIMEComposition));
                    else
                        AdjustIMEComposition();
                }
            }
        }

        /*
         * ↑  受信スレッドによる実行のエリア
         * -------------------------------
         * ↓  UIスレッドによる実行のエリア
         */

        [ThreadStatic]
        private static List<Sixel.LineIdAndColumnSpan> _lineIdAndColumnSpanList;

        protected override void OverlayAfter(Graphics g, CharacterDocumentViewer.RenderParameter param) {
            using (TerminalDocumentScope docScope = GetTerminalDocumentScope()) {
                if (docScope.Document != null) {
                    // Cut out regions of newly updated text from the sixel images.
                    List<Sixel.LineIdAndColumnSpan> tmpList = _lineIdAndColumnSpanList;
                    if (tmpList == null) {
                        tmpList = new List<Sixel.LineIdAndColumnSpan>();
                        _lineIdAndColumnSpanList = tmpList;
                    }
                    else {
                        tmpList.Clear();
                    }

                    foreach (GLine l in param.GLines) {
                        if (l.UpdatedSpans != null) {
                            foreach (GLineColumnSpan s in l.UpdatedSpans) {
                                tmpList.Add(new Sixel.LineIdAndColumnSpan(l.ID, s));
                            }
                        }
                    }

                    if (tmpList.Count > 0) {
                        docScope.Document.SixelImageManager.ClearSpans(
                            spans: tmpList.ToArray(),
                            topLineId: param.TopLineId,
                            lineIdFrom: param.TopLineId + param.LineFrom,
                            lineIdTo: param.TopLineId + param.LineFrom + param.LineCount - 1,
                            linePitch: param.LinePitch,
                            columnPitch: param.ColumnPitch
                        );
                    }

                    // Calculate the region to exclude temporarily to display the cursor.
                    Rectangle? excludedRect;
                    if (param.CaretEnabled) {
                        float fy = param.CaretLineOffset * param.LinePitch;
                        int y1 = param.Origin.Y + (int)fy;
                        int y2 = param.Origin.Y + (int)(fy + param.LinePitch);
                        float fx = param.CaretColumnIndex * param.ColumnPitch;
                        int x1 = param.Origin.X + (int)fx;
                        int x2 = param.Origin.X + (int)(fx + param.ColumnPitch * param.CaretWidth);
                        excludedRect = new Rectangle(x1, y1, x2 - x1, y2 - y1);
                    }
                    else {
                        excludedRect = null;
                    }

                    // Draw sixel images
                    docScope.Document.SixelImageManager.Draw(
                        topLineId: param.TopLineId,
                        lineIdFrom: param.TopLineId + param.LineFrom,
                        lineIdTo: param.TopLineId + param.LineFrom + param.LineCount - 1,
                        linePitch: param.LinePitch,
                        columnPitch: param.ColumnPitch,
                        g: g,
                        origin: param.Origin,
                        excludedRect: excludedRect
                    );
                }
            }
        }

        private delegate void InvalidateDelegate1();
        private delegate void InvalidateDelegate2(Rectangle rc);
        private void DelInvalidate(Rectangle rc) {
            Invalidate(rc);
        }
        private void DelInvalidate() {
            Invalidate();
        }


        protected override void VScrollBarValueChanged() {
            if (_ignoreValueChangeEvent)
                return;

            using (TerminalDocumentScope docScope = GetTerminalDocumentScope()) {
                if (docScope.Document != null) {
                    lock (docScope.Document) {
                        docScope.Document.SetViewTopLineNumber(docScope.Document.FirstLineNumber + _VScrollBar.Value);
                        _session.Terminal.TransientScrollBarValues.Value = _VScrollBar.Value;
                        Invalidate(); // redraw now
                    }
                }
            }
        }

        /* キーボード処理系について
         * 　送信は最終的にはSendChar/Stringへ行く。
         * 
         * 　そこに至る過程では、
         *  ProcessCmdKey: Altキーの設定次第で、ベースクラスに渡す（＝コマンド起動を試みる）かどうか決める
         *  ProcessDialogKey: 文字キー以外は基本的にここで処理。
         *  OnKeyPress: 文字の送信
         */
        private readonly CacheByEncodingType<EncodingProfile.Encoder> _encoderCache =
                new CacheByEncodingType<EncodingProfile.Encoder>((encodingProfile) => encodingProfile.CreateEncoder());
        private byte[] _sendCharBuffer = new byte[1];
        public void SendChar(char ch) { //ISからのコールバックあるので
            if (ch < 0x80) {
                //Debug.WriteLine("SendChar " + (int)ch);
                _sendCharBuffer[0] = (byte)ch;
                SendBytes(_sendCharBuffer);
            }
            else {
                byte[] data;
                if (_encoderCache.Get(GetTerminalSettings().Encoding).GetBytes(ch, out data)) {
                    SendBytes(data);
                }
            }
        }

        public void SendCharArray(char[] chs) {
            byte[] data;
            if (_encoderCache.Get(GetTerminalSettings().Encoding).GetBytes(chs, out data)) {
                SendBytes(data);
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
            Keys modifiers = keyData & Keys.Modifiers;
            using (TerminalDocumentScope docScope = GetTerminalDocumentScope()) {
                if (IsAcceptableUserInput(docScope.Document) && (modifiers & Keys.Alt) != Keys.None) { //Altキーの横取り処理を開始
                    Keys keybody = keyData & Keys.KeyCode;
                    if (GEnv.Options.LeftAltKey != AltKeyAction.Menu && (Win32.GetKeyState(Win32.VK_LMENU) & 0x8000) != 0) {
                        ProcessSpecialAltKey(GEnv.Options.LeftAltKey, modifiers, keybody);
                        return true;
                    }
                    else if (GEnv.Options.RightAltKey != AltKeyAction.Menu && (Win32.GetKeyState(Win32.VK_RMENU) & 0x8000) != 0) {
                        ProcessSpecialAltKey(GEnv.Options.RightAltKey, modifiers, keybody);
                        return true;
                    }
                }
            }

            //これまでで処理できなければ上位へ渡す
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override bool IsInputKey(Keys key) {
            Keys mod = key & Keys.Modifiers;
            Keys body = key & Keys.KeyCode;
            if (mod == Keys.None && (body == Keys.Tab || body == Keys.Escape))
                return true;
            else
                return false;
        }

        protected override bool ProcessDialogKey(Keys key) {
            Keys modifiers = key & Keys.Modifiers;
            Keys keybody = key & Keys.KeyCode;

            //接続中でないとだめなキー
            using (TerminalDocumentScope docScope = GetTerminalDocumentScope()) {
                if (docScope.Document != null) {
                    if (IsAcceptableUserInput(docScope.Document)) {
                        //TODO Enter,Space,SequenceKey系もカスタムキーに入れてしまいたい
                        char[] custom = TerminalEmulatorPlugin.Instance.CustomKeySettings.Scan(key); //カスタムキー
                        if (custom != null) {
                            SendCharArray(custom);
                            return true;
                        }
                        else if (ProcessAdvancedFeatureKey(modifiers, keybody)) {
                            return true;
                        }
                        else if (keybody == Keys.Enter && modifiers == Keys.None) {
                            _escForVI = false;
                            SendCharArray(
                                TerminalUtil.NewLineChars(
                                    docScope.Document.ForceNewLine
                                        ? NewLine.CRLF
                                        : GetTerminalSettings().TransmitNL));
                            return true;
                        }
                        else if (keybody == Keys.Space && modifiers == Keys.Control) { //これはOnKeyPressにわたってくれない
                            SendChar('\0');
                            return true;
                        }
                        if ((keybody == Keys.Tab) && (modifiers == Keys.Shift)) {
                            this.SendChar('\t');
                            return true;
                        }
                        else if (IsSequenceKey(keybody)) {
                            ProcessSequenceKey(modifiers, keybody);
                            return true;
                        }
                    }

                    //常に送れるキー
                    if (keybody == Keys.Apps) { //コンテキストメニュー
                        int x, y;
                        lock (docScope.Document) {
                            x = docScope.Document.CaretColumn;
                            y = Math.Min(docScope.Document.CurrentLineNumber - docScope.Document.ViewTopLineNumber, docScope.Document.TerminalHeight - 1);
                        }
                        RenderProfile renderProfile = GetRenderProfile();
                        SizeF p = renderProfile.Pitch;
                        int lineSpacing = renderProfile.LineSpacing;
                        _terminalEmulatorMouseHandler.ShowContextMenu(new Point((int)(p.Width * x), (int)((p.Height + lineSpacing) * y)));
                        return true;
                    }
                }
            }

            return base.ProcessDialogKey(key);
        }

        private bool ProcessAdvancedFeatureKey(Keys modifiers, Keys keybody) {
            if (_session.Terminal.TerminalMode == TerminalMode.Application)
                return false;

            if (_session.Terminal.IntelliSense.ProcessKey(modifiers, keybody))
                return true;
            else if (_session.Terminal.PopupStyleCommandResultRecognizer.ProcessKey(modifiers, keybody))
                return true;
            else
                return false;
        }

        private static bool IsSequenceKey(Keys key) {
            return ((int)Keys.F1 <= (int)key && (int)key <= (int)Keys.F12) ||
                key == Keys.Insert || key == Keys.Delete || IsScrollKey(key);
        }
        private static bool IsScrollKey(Keys key) {
            return key == Keys.Up || key == Keys.Down ||
                key == Keys.Left || key == Keys.Right ||
                key == Keys.PageUp || key == Keys.PageDown ||
                key == Keys.Home || key == Keys.End;
        }
        private void ProcessSpecialAltKey(AltKeyAction act, Keys modifiers, Keys body) {
            if (!this.HasDocument)
                return;
            char ch = KeyboardInfo.Scan(body, (modifiers & Keys.Shift) != Keys.None);
            if (ch == '\0')
                return; //割り当てられていないやつは無視

            if ((modifiers & Keys.Control) != Keys.None)
                ch = (char)((int)ch % 32); //Controlを押したら制御文字

            if (act == AltKeyAction.ESC) {
                //Debug.WriteLine("ESC " + (int)ch);
                SendBytes(new byte[] { 0x1b, (byte)ch });
            }
            else { //Meta
                ch = (char)(0x80 + ch);
                byte[] t = new byte[1];
                t[0] = (byte)ch;
                //Debug.WriteLine("META " + (int)ch);
                SendBytes(t);
            }
        }

        protected override void OnKeyPress(KeyPressEventArgs e) {
            base.OnKeyPress(e);
            if (e.KeyChar == '\x001b') {
                _escForVI = true;
            }
            using (TerminalDocumentScope docScope = GetTerminalDocumentScope()) {
                if (!IsAcceptableUserInput(docScope.Document))
                    return;
            }
            /* ここの処理について
             * 　IMEで入力文字を確定すると（部分確定ではない）、WM_IME_CHAR、WM_ENDCOMPOSITION、WM_CHARの順でメッセージが送られてくる。Controlはその両方でKeyPressイベントを
             * 　発生させるので、IMEの入力が２回送信されてしまう。
             * 　一方部分確定のときはWM_IME_CHARのみである。
             */
            //if((int)e.KeyChar>=100) {
            //    if(_currentMessage.Msg!=Win32.WM_IME_CHAR) return;
            //}
            if (this._escForVI) {
                this.SendChar(e.KeyChar);
            }
            else {
                this.SendChar(e.KeyChar);
                if (_session.TerminalSettings.EnabledCharTriggerIntelliSense && _session.Terminal.TerminalMode == TerminalMode.Normal)
                    _session.Terminal.IntelliSense.ProcessChar(e.KeyChar);
            }
        }

        private void SendBytes(byte[] data) {
            //キーを押しっぱなしにしたときにキャレットがブリンクするのはちょっと見苦しいのでキー入力があるたびにタイマをリセット
            _caret.KeepActiveUntilNextTick();
            _resetViewTop = true;
            GetTerminalTransmission().Transmit(data);
        }

        private bool IsAcceptableUserInput(TerminalDocument document) {
            //TODO: ModalTerminalTaskの存在が理由で拒否するときはステータスバーか何かに出すのがよいかも
            if (document == null || IsConnectionClosed() || _session.Terminal.CurrentModalTerminalTask != null || document.KeySendLocked)
                return false;
            else
                return true;

        }

        private void ProcessSequenceKey(Keys modifier, Keys body) {
            byte[] data;
            data = GetTerminal().SequenceKeyData(modifier, body);
            SendBytes(data);
        }

        public void SuspendResize() {
            _isResizeSuspended = true;
        }

        public void ResumeResize() {
            _isResizeSuspended = false;

            if (_pendingSize.HasValue) {
                ApplyTerminalSize(_pendingSize.Value);
                _pendingSize = null;
            }
        }

        protected override void OnResize(EventArgs args) {
            base.OnResize(args);

            //Debug.WriteLine(String.Format("TC RESIZE {0} {1} {2},{3}", _resizeCount++, DateTime.Now.ToString(), this.Size.Width, this.Size.Height));
            //Debug.WriteLine(new StackTrace(true).ToString());
            //最小化時にはなぜか自身の幅だけが０になってしまう
            if (this.DesignMode || this.FindForm() == null || this.FindForm().WindowState == FormWindowState.Minimized || _session == null)
                return;

            Size ts = CalcTerminalSize(GetRenderProfile());

            if (_isResizeSuspended) {
                _pendingSize = ts;
                return;
            }

            ApplyTerminalSize(ts);
        }

        private void ApplyTerminalSize(Size size) {
            using (TerminalDocumentScope docScope = GetTerminalDocumentScope()) {
                if (!IsConnectionClosed() && docScope.Document != null && (size.Width != docScope.Document.TerminalWidth || size.Height != docScope.Document.TerminalHeight)) {
                    ResizeTerminal(docScope.Document, size.Width, size.Height);
                    ShowSizeTip(size.Width, size.Height);
                    CommitTransientScrollBar();
                }
            }
        }

        private void OnHideSizeTip(object sender, EventArgs args) {
            Debug.Assert(!this.InvokeRequired);
            _sizeTip.Visible = false;
            _sizeTipTimer.Stop();
        }

        public override RenderProfile GetRenderProfile() {
            if (_session != null) {
                ITerminalSettings ts = _session.TerminalSettings;
                if (ts.UsingDefaultRenderProfile)
                    return GEnv.DefaultRenderProfile;
                else
                    return ts.RenderProfile;
            }
            else
                return GEnv.DefaultRenderProfile;
        }
        protected override void CommitTransientScrollBar() {
            if (_session != null) {	// TerminalPaneを閉じるタイミングでこのメソッドが呼ばれたときにNullReferenceExceptionになるのを防ぐ
                _ignoreValueChangeEvent = true;
                GetTerminal().CommitScrollBar(_VScrollBar, true);	//!! ここ（スクロールバー）の処理は重い
                _ignoreValueChangeEvent = false;
            }
        }

        public override GLine GetTopLine() {
            TerminalDocument document = _documentCache;
            return (document != null) ? document.ViewTopLine : base.GetTopLine();
        }

        protected override void AdjustCaret(Caret caret) {
            if (_session == null)
                return;

            if (IsConnectionClosed() || !this.Focused || _inIMEComposition) {
                caret.Enabled = false;
            }
            else {
                using (TerminalDocumentScope docScope = GetTerminalDocumentScope()) {
                    if (docScope.Document != null) {
                        lock (docScope.Document) {
                            // Note:
                            //  After a character was added to the last column of the row,
                            //  the value of CaretColumn will indicate outside of the terminal view.
                            //  In such case we draw the caret on the last column of the row.
                            caret.X = Math.Min(docScope.Document.CaretColumn, docScope.Document.TerminalWidth - 1);
                            caret.Y = docScope.Document.CurrentLineNumber - docScope.Document.ViewTopLineNumber;
                            caret.Enabled = docScope.Document.ShowCaret && caret.Y >= 0 && caret.Y < docScope.Document.TerminalHeight;
                        }
                    }
                }
            }
        }

        public Size CalcTerminalSize(RenderProfile prof) {
            SizeF charPitch = prof.Pitch;
            Win32.SystemMetrics sm = GEnv.SystemMetrics;
            int width = (int)Math.Floor((float)(this.ClientSize.Width - sm.ScrollBarWidth - CharacterDocumentViewer.BORDER * 2) / charPitch.Width);
            int height = (int)Math.Floor((float)(this.ClientSize.Height - CharacterDocumentViewer.BORDER * 2 + prof.LineSpacing) / (charPitch.Height + prof.LineSpacing));
            if (width <= 0)
                width = 1; //極端なリサイズをすると負の値になることがある
            if (height <= 0)
                height = 1;
            return new Size(width, height);
        }

        private void ShowSizeTip(int width, int height) {
            const int MARGIN = 8;
            //Form form = GEnv.Frame.AsForm();
            //if(form==null || !form.Visible) return; //起動時には表示しない
            if (!this.Visible)
                return;

            Point pt = new Point(this.Width - _VScrollBar.Width - _sizeTip.Width - MARGIN, this.Height - _sizeTip.Height - MARGIN);

            _sizeTip.Text = String.Format("{0} * {1}", width, height);
            _sizeTip.Location = pt;
            _sizeTip.Visible = true;

            _sizeTipTimer.Stop();
            _sizeTipTimer.Start();
        }
        //ピクセル単位のサイズを受け取り、チップを表示
        public void SplitterDragging(int width, int height) {
            SizeF charSize = GetRenderProfile().Pitch;
            Win32.SystemMetrics sm = GEnv.SystemMetrics;
            width = (int)Math.Floor(((float)width - sm.ScrollBarWidth - sm.ControlBorderWidth * 2) / charSize.Width);
            height = (int)Math.Floor((float)(height - sm.ControlBorderHeight * 2) / charSize.Height);
            ShowSizeTip(width, height);
        }

        private void ResizeTerminal(TerminalDocument document, int width, int height) {
            //Debug.WriteLine(String.Format("Resize {0} {1}", width, height));

            //   In the terminal display, the rendered area should be restricted to
            // the screen size, since the remote application only updates within
            // the notified screen size.
            RestrictDisplayArea(width, height);

            //Documentへ通知
            document.Resize(width, height);

            if (_session.Terminal.CurrentModalTerminalTask != null)
                return; //別タスクが走っているときは無視
            GetTerminal().Reset();

            //接続先へ通知
            GetTerminalTransmission().Resize(width, height);

            Invalidate(); // redraw now
        }

        //IMEの位置合わせなど。日本語入力開始時、現在のキャレット位置からIMEをスタートさせる。
        private void AdjustIMEComposition() {
            using (TerminalDocumentScope docScope = GetTerminalDocumentScope()) {
                if (docScope.Document != null) {
                    IntPtr hIMC = Win32.ImmGetContext(this.Handle);
                    RenderProfile prof = GetRenderProfile();

                    //フォントのセットは１回やればよいのか？
                    Win32.LOGFONT lf = new Win32.LOGFONT();
                    prof.GetIMECompositionFont().ToLogFont(lf);
                    Win32.ImmSetCompositionFont(hIMC, lf);

                    Win32.COMPOSITIONFORM form = new Win32.COMPOSITIONFORM();
                    form.dwStyle = Win32.CFS_POINT;
                    Win32.SystemMetrics sm = GEnv.SystemMetrics;
                    //Debug.WriteLine(String.Format("{0} {1} {2}", document.CaretColumn, charwidth, document.CurrentLine.CharPosToDisplayPos(document.CaretColumn)));
                    lock (docScope.Document) {
                        form.ptCurrentPos.x = sm.ControlBorderWidth + (int)(prof.Pitch.Width * (docScope.Document.CaretColumn));
                        form.ptCurrentPos.y = sm.ControlBorderHeight + (int)((prof.Pitch.Height + prof.LineSpacing) * Math.Min(docScope.Document.CurrentLineNumber - docScope.Document.ViewTopLineNumber, docScope.Document.TerminalHeight - 1));
                    }
                    bool r = Win32.ImmSetCompositionWindow(hIMC, ref form);
                    Debug.Assert(r);
                    Win32.ImmReleaseContext(this.Handle, hIMC);
                }
            }
        }
        private void ClearIMEComposition() {
            IntPtr hIMC = Win32.ImmGetContext(this.Handle);
            Win32.ImmNotifyIME(hIMC, Win32.NI_COMPOSITIONSTR, Win32.CPS_CANCEL, 0);
            Win32.ImmReleaseContext(this.Handle, hIMC);
            _inIMEComposition = false;
        }

        public void ApplyRenderProfile(RenderProfile prof) {
            if (this.HasDocument) {
                this.BackColor = prof.BackColor;
                Size ts = CalcTerminalSize(prof);
                using (TerminalDocumentScope docScope = GetTerminalDocumentScope()) {
                    if (!IsConnectionClosed() && docScope.Document != null && (ts.Width != docScope.Document.TerminalWidth || ts.Height != docScope.Document.TerminalHeight)) {
                        ResizeTerminal(docScope.Document, ts.Width, ts.Height);
                    }
                }
                Invalidate();
            }
        }
        public void ApplyTerminalOptions(ITerminalEmulatorOptions opt) {
            if (this.HasDocument) {
                if (GetTerminalSettings().UsingDefaultRenderProfile) {
                    ApplyRenderProfile(opt.CreateRenderProfile());
                }
                _caret.Style = opt.CaretType;
                _caret.Blink = opt.CaretBlink;
                _caret.Color = opt.CaretColor;
                _caret.Reset();
            }
        }

        // Overrides CharacterDocumentViewer's scrollbar control
        protected override void OnMouseWheelCore(MouseEventArgs e) {
            // do nothing.
            // Scrollbar control will be done by MouseWheelHandler.
        }

        protected override void OnGotFocus(EventArgs args) {
            base.OnGotFocus(args);
            if (!this.HasDocument)
                return;
            if (GetTerminal().GetFocusReportingMode()) {
                byte[] data = new byte[] { 0x1b, 0x5b, 0x49 };
                TransmitDirect(data, 0, data.Length);
            }

            using (DocumentScope docScope = GetDocumentScope()) {
                if (docScope.Document != null) {
                    //NOTE TerminalControlはSessionについては無知、という前提にしたほうがいいのかもしれない
                    TerminalEmulatorPlugin.Instance.GetSessionManager().ActivateDocument(docScope.Document, ActivateReason.ViewGotFocus);
                }
            }
        }

        protected override void OnLostFocus(EventArgs args) {
            base.OnLostFocus(args);
            if (!this.HasDocument)
                return;
            if (GetTerminal().GetFocusReportingMode()) {
                byte[] data = new byte[] { 0x1b, 0x5b, 0x4f };
                TransmitDirect(data, 0, data.Length);
            }

            if (_inIMEComposition)
                ClearIMEComposition();
        }
        //Drag&Drop関係
        protected override void OnDragEnter(DragEventArgs args) {
            base.OnDragEnter(args);
            try {
                IWinFormsService wfs = TerminalEmulatorPlugin.Instance.GetWinFormsService();
                IPoderosaDocument document = (IPoderosaDocument)wfs.GetDraggingObject(args.Data, typeof(IPoderosaDocument));
                if (document != null)
                    args.Effect = DragDropEffects.Move;
                else
                    wfs.BypassDragEnter(this, args);
            }
            catch (Exception ex) {
                RuntimeUtil.ReportException(ex);
            }
        }
        protected override void OnDragDrop(DragEventArgs args) {
            base.OnDragDrop(args);
            try {
                IWinFormsService wfs = TerminalEmulatorPlugin.Instance.GetWinFormsService();
                IPoderosaDocument document = (IPoderosaDocument)wfs.GetDraggingObject(args.Data, typeof(IPoderosaDocument));
                if (document != null) {
                    IPoderosaView view = (IPoderosaView)this.GetAdapter(typeof(IPoderosaView));
                    TerminalEmulatorPlugin.Instance.GetSessionManager().AttachDocumentAndView(document, view);
                    TerminalEmulatorPlugin.Instance.GetSessionManager().ActivateDocument(document, ActivateReason.DragDrop);
                }
                else
                    wfs.BypassDragDrop(this, args);
            }
            catch (Exception ex) {
                RuntimeUtil.ReportException(ex);
            }
        }

        private void ProcessVScrollMessage(int cmd) {
            int newval = _VScrollBar.Value;
            switch (cmd) {
                case 0: //SB_LINEUP
                    newval--;
                    break;
                case 1: //SB_LINEDOWN
                    newval++;
                    break;
                case 2: //SB_PAGEUP
                    {
                        TerminalDocument document = _documentCache;
                        if (document != null) {
                            newval -= document.TerminalHeight;
                        }
                    }
                    break;
                case 3: //SB_PAGEDOWN
                    {
                        TerminalDocument document = _documentCache;
                        if (document != null) {
                            newval += document.TerminalHeight;
                        }
                    }
                    break;
            }

            if (newval < 0)
                newval = 0;
            if (newval > _VScrollBar.Maximum - _VScrollBar.LargeChange)
                newval = _VScrollBar.Maximum - _VScrollBar.LargeChange + 1;
            _VScrollBar.Value = newval;
        }


        /*
         * この周辺で使いそうなデバッグ用のコード断片
         private static bool _IMEFlag;
         private static int _callnest;
         
            _callnest++;
            if(_IMEFlag) {
                if(msg.Msg!=13 && msg.Msg!=14 && msg.Msg!=15 && msg.Msg!=0x14 && msg.Msg!=0x85 && msg.Msg!=0x20 && msg.Msg!=0x84) //うざいのはきる
                    Debug.WriteLine(String.Format("{0} Msg {1:X} WP={2:X} LP={3:X}", _callnest, msg.Msg, msg.WParam.ToInt32(), msg.LParam.ToInt32()));
            }
            base.WndProc(ref msg);
            _callnest--;
         */
        private bool _lastCompositionFlag;
        //IME関係を処理するためにかなりの苦労。なぜこうなのかについては別ドキュメント参照
        protected override void WndProc(ref Message msg) {
            if (_lastCompositionFlag) {
                LastCompositionWndProc(ref msg);
                return;
            }

            int m = msg.Msg;
            if (m == Win32.WM_IME_COMPOSITION) {
                if ((msg.LParam.ToInt32() & 0xFF) == 0) { //最終確定時の特殊処理へ迂回させるフラグを立てる
                    _lastCompositionFlag = true;
                    base.WndProc(ref msg); //この中で送られてくるWM_IME_CHARは無視
                    _lastCompositionFlag = false;
                    return;
                }
            }

            base.WndProc(ref msg); //通常時

            if (m == Win32.WM_IME_STARTCOMPOSITION) {
                _inIMEComposition = true; //_inIMECompositionはWM_IME_STARTCOMPOSITIONでしかセットしない
                AdjustIMEComposition();
            }
            else if (m == Win32.WM_IME_ENDCOMPOSITION) {
                _inIMEComposition = false;
            }
        }
        private void LastCompositionWndProc(ref Message msg) {
            if (msg.Msg == Win32.WM_IME_CHAR) {
                char ch = (char)msg.WParam;
                SendChar(ch);
            }
            else
                base.WndProc(ref msg);
        }
    }

    /// <summary>
    /// Mouse wheel handler.
    /// </summary>
    internal class MouseWheelHandler : DefaultMouseHandler {
        private readonly TerminalControl _control;
        private readonly VScrollBar _scrollBar;
        private AbstractTerminal _terminal = null;
        private readonly object _terminalSync = new object();

        public MouseWheelHandler(TerminalControl control, VScrollBar scrollBar)
            : base("mousewheel") {
            _control = control;
            _scrollBar = scrollBar;
        }

        public void Attach(ITerminalControlHost session) {
            lock (_terminalSync) {
                _terminal = session.Terminal;
            }
        }

        public void Detach() {
            lock (_terminalSync) {
                _terminal = null;
            }
        }

        public override UIHandleResult OnMouseWheel(MouseEventArgs args) {
            if (!_control.HasDocument)
                return UIHandleResult.Pass;

            lock (_terminalSync) {
                if (_terminal != null && !GEnv.Options.AllowsScrollInAppMode && _terminal.TerminalMode == TerminalMode.Application) {
                    // Emulate Up Down keys
                    int m = GEnv.Options.WheelAmount;
                    for (int i = 0; i < m; i++) {
                        byte[] data = _terminal.SequenceKeyData(Keys.None, args.Delta > 0 ? Keys.Up : Keys.Down);
                        _control.TransmitDirect(data, 0, data.Length);
                    }
                    return UIHandleResult.Stop;
                }
            }

            if (_scrollBar.Enabled) {
                int d = args.Delta / 120; //開発環境だとDeltaに120。これで1か-1が入るはず
                d *= GEnv.Options.WheelAmount;

                int newval = _scrollBar.Value - d;
                if (newval < 0)
                    newval = 0;
                if (newval > _scrollBar.Maximum - _scrollBar.LargeChange)
                    newval = _scrollBar.Maximum - _scrollBar.LargeChange + 1;
                _scrollBar.Value = newval;
            }

            return UIHandleResult.Stop;
        }
    }

    /// <summary>
    /// XTerm mouse tracking support.
    /// </summary>
    /// <remarks>
    /// <para>This handler must be placed on the head of the handler list.</para>
    /// <para>This handler controls whether other handler should process the incoming event.
    /// Actual processes for the mouse tracking are delegated to the AbstractTerminal.</para>
    /// </remarks>
    internal class MouseTrackingHandler : DefaultMouseHandler {
        private readonly TerminalControl _control;
        private AbstractTerminal _terminal = null;
        private readonly object _terminalSync = new object();
        private MouseButtons _pressedButtons = MouseButtons.None;   // buttons that being grabbed by mouse tracking

#if DEBUG_MOUSETRACKING
        private static int _instanceCounter = 0;
        private readonly string _instance;
#endif

        public MouseTrackingHandler(TerminalControl control)
            : base("mousetracking") {
            _control = control;
#if DEBUG_MOUSETRACKING
            _instance = "MT[" + (++_instanceCounter).ToString() + "]";
#endif
        }

        public void Attach(ITerminalControlHost session) {
            lock (_terminalSync) {
                _terminal = session.Terminal;
            }
        }

        public void Detach() {
            lock (_terminalSync) {
                _terminal = null;
            }
        }

        private bool IsGrabbing() {
            return _pressedButtons != MouseButtons.None;
        }

        private bool IsEscaped() {
            return false;   // TODO
        }

        public override UIHandleResult OnMouseDown(MouseEventArgs args) {
            Keys modKeys = Control.ModifierKeys;

#if DEBUG_MOUSETRACKING
            Debug.WriteLine(_instance + " OnMouseDown: Buttons = " + _pressedButtons.ToString());
#endif
            if (!IsGrabbing()) {
                if (IsEscaped())
                    return UIHandleResult.Pass; // bypass mouse tracking
            }

            int col, row;
            _control.MousePosToTextPos(args.X, args.Y, out col, out row);

            bool processed;

            lock (_terminalSync) {
                if (_terminal == null)
                    return UIHandleResult.Pass;

                processed = _terminal.ProcessMouse(TerminalMouseAction.ButtonDown, args.Button, modKeys, row, col);
            }

            if (processed) {
                _pressedButtons |= args.Button;
#if DEBUG_MOUSETRACKING
                Debug.WriteLine(_instance + " OnMouseDown: Processed --> Capture : Buttons = " + _pressedButtons.ToString());
#endif
                return UIHandleResult.Capture;  // process next mouse events exclusively.
            }
            else {
#if DEBUG_MOUSETRACKING
                Debug.WriteLine(_instance + " OnMouseDown: Not Processed : Buttons = " + _pressedButtons.ToString());
#endif
                if (IsGrabbing())
                    return UIHandleResult.Stop;
                else
                    return UIHandleResult.Pass;
            }
        }

        public override UIHandleResult OnMouseUp(MouseEventArgs args) {
#if DEBUG_MOUSETRACKING
            Debug.WriteLine(_instance + " OnMouseUp: Buttons = " + _pressedButtons.ToString());
#endif
            if (!IsGrabbing())
                return UIHandleResult.Pass;

            Keys modKeys = Control.ModifierKeys;

            int col, row;
            _control.MousePosToTextPos(args.X, args.Y, out col, out row);

            // Note:
            // We keep this handler in "Captured" status while any other mouse buttons being pressed.
            // "Captured" handler can process mouse events exclusively.
            //
            // This trick would provide good experience to the user,
            // but it doesn't work expectedly in the following scenario.
            //
            //   1. Press left button on Terminal-1
            //   2. Press right button on Terminal-1
            //   3. Move (drag) mouse to Terminal-2
            //   4. Release left button on Terminal-2
            //   5. Release right button on Terminal-2
            //
            // In step 1, System.Windows.Forms.Control object starts mouse capture automatically
            // when left button was pressed.
            // So the next mouse-up event will be notified to the Terminal-1 (step 4).
            // But Control object stops mouse capture by mouse-up event for any button.
            // Mouse-up event of the right button in step 5 will not be notified to the Terminal-1,
            // and the handler of the Terminal-1 will not end "Captured" status.
            //
            // The case like above will happen rarely.
            // To avoid never ending "Captured" status, OnMouseMove() ends "Captured" status
            // if no mouse buttons were set in the MouseEventArgs.Button.
            // 

            lock (_terminalSync) {
                if (_terminal != null) {
                    // Mouse tracking mode may be already turned off.
                    // We just ignore result of ProcessMouse().
                    _terminal.ProcessMouse(TerminalMouseAction.ButtonUp, args.Button, modKeys, row, col);
                }
            }

            _pressedButtons &= ~args.Button;

            if (IsGrabbing()) {
#if DEBUG_MOUSETRACKING
                Debug.WriteLine(_instance + " OnMouseUp: Continue Capture : Buttons = " + _pressedButtons.ToString());
#endif
                return UIHandleResult.Stop;
            }
            else {
#if DEBUG_MOUSETRACKING
                Debug.WriteLine(_instance + " OnMouseUp: End Capture : Buttons = " + _pressedButtons.ToString());
#endif
                return UIHandleResult.EndCapture;
            }
        }

        public override UIHandleResult OnMouseMove(MouseEventArgs args) {
            Keys modKeys = Control.ModifierKeys;

#if DEBUG_MOUSETRACKING
            Debug.WriteLine(_instance + " OnMouseMove: Buttons = " + _pressedButtons.ToString());
#endif
            if (!IsGrabbing()) {
                if (IsEscaped())
                    return UIHandleResult.Pass; // bypass mouse tracking
            }

            int col, row;
            _control.MousePosToTextPos(args.X, args.Y, out col, out row);

            if (IsGrabbing() && args.Button == MouseButtons.None) {
                // mouse button has been released in another terminal ?
#if DEBUG_MOUSETRACKING
                Debug.WriteLine(_instance + " OnMouseMove: End Capture (Reset)");
#endif
                lock (_terminalSync) {
                    if (_terminal != null) {
                        int buttons = (int)_pressedButtons;
                        int buttonBit = 1;
                        while (buttonBit != 0) {
                            if ((buttons & buttonBit) != 0) {
#if DEBUG_MOUSETRACKING
                                Debug.WriteLine(_instance + " OnMouseMove: MouseUp " + ((MouseButtons)buttonBit).ToString());
#endif
                                _terminal.ProcessMouse(TerminalMouseAction.ButtonUp, (MouseButtons)buttonBit, modKeys, row, col);
                            }
                            buttonBit <<= 1;
                        }
                    }
                }

                _pressedButtons = MouseButtons.None;
                return UIHandleResult.EndCapture;
            }

            bool processed;

            lock (_terminalSync) {
                if (_terminal == null)
                    return UIHandleResult.Pass;

                processed = _terminal.ProcessMouse(TerminalMouseAction.MouseMove, MouseButtons.None, modKeys, row, col);
            }

            if (processed) {
#if DEBUG_MOUSETRACKING
                Debug.WriteLine(_instance + " OnMouseMove: Processed");
#endif
                return UIHandleResult.Stop;
            }
            else {
#if DEBUG_MOUSETRACKING
                Debug.WriteLine(_instance + " OnMouseMove: Ignored");
#endif
                return UIHandleResult.Pass;
            }
        }

        public override UIHandleResult OnMouseWheel(MouseEventArgs args) {
            Keys modKeys = Control.ModifierKeys;

#if DEBUG_MOUSETRACKING
            Debug.WriteLine(_instance + " OnMouseWheel: Buttons = " + _pressedButtons.ToString());
#endif
            if (!IsGrabbing()) {
                if (IsEscaped())
                    return UIHandleResult.Pass; // bypass mouse tracking
            }

            int col, row;
            _control.MousePosToTextPos(args.X, args.Y, out col, out row);

            TerminalMouseAction action = (args.Delta > 0) ?
                TerminalMouseAction.WheelUp : TerminalMouseAction.WheelDown;

            bool processed;

            lock (_terminalSync) {
                if (_terminal == null)
                    return UIHandleResult.Pass;

                processed = _terminal.ProcessMouse(action, MouseButtons.None, modKeys, row, col);
            }

            if (processed) {
#if DEBUG_MOUSETRACKING
                Debug.WriteLine(_instance + " OnMouseWheel: Processed");
#endif
                return UIHandleResult.Stop;
            }
            else {
#if DEBUG_MOUSETRACKING
                Debug.WriteLine(_instance + " OnMouseWheel: Ignored");
#endif
                return UIHandleResult.Pass;
            }
        }
    }

    internal class TerminalEmulatorMouseHandler : DefaultMouseHandler {
        private TerminalControl _control;

        public TerminalEmulatorMouseHandler(TerminalControl control)
            : base("terminal") {
            _control = control;
        }

        public override UIHandleResult OnMouseDown(MouseEventArgs args) {
            return UIHandleResult.Pass;
        }
        public override UIHandleResult OnMouseMove(MouseEventArgs args) {
            return UIHandleResult.Pass;
        }
        public override UIHandleResult OnMouseUp(MouseEventArgs args) {
            if (args.Button == MouseButtons.Right || args.Button == MouseButtons.Middle) {
                ITerminalEmulatorOptions opt = TerminalEmulatorPlugin.Instance.TerminalEmulatorOptions;
                MouseButtonAction act = args.Button == MouseButtons.Right ? opt.RightButtonAction : opt.MiddleButtonAction;
                if (act != MouseButtonAction.None) {
                    if (Control.ModifierKeys == Keys.Shift ^ act == MouseButtonAction.ContextMenu) //シフトキーで動作反転
                        ShowContextMenu(new Point(args.X, args.Y));
                    else { //Paste
                        if (!_control.HasDocument)
                            return UIHandleResult.Pass;

                        IGeneralViewCommands vc = (IGeneralViewCommands)_control.GetAdapter(typeof(IGeneralViewCommands));
                        TerminalEmulatorPlugin.Instance.GetCommandManager().Execute(vc.Paste, (ICommandTarget)vc.GetAdapter(typeof(ICommandTarget)));
                        //ペースト後はフォーカス
                        if (!_control.Focused)
                            _control.Focus();
                    }

                    return UIHandleResult.Stop;
                }
            }

            return UIHandleResult.Pass;
        }

        public void ShowContextMenu(Point pt) {
            IPoderosaView view = (IPoderosaView)_control.GetAdapter(typeof(IPoderosaView));
            view.ParentForm.ShowContextMenu(TerminalEmulatorPlugin.Instance.ContextMenu, view, _control.PointToScreen(pt), ContextMenuFlags.None);
            //コマンド実行後自分にフォーカス
            if (!_control.Focused)
                _control.Focus();
        }
    }

    //描画パフォーマンス調査用クラス
    internal static class DrawingPerformance {
        private static int _receiveDataCount;
        private static long _lastReceivedTime;
        private static int _shortReceiveTimeCount;

        private static int _fullInvalidateCount;
        private static int _partialInvalidateCount;
        private static int _totalInvalidatedLineCount;
        private static int _invalidate1LineCount;

        public static void MarkReceiveData(InvalidatedRegion region) {
            _receiveDataCount++;
            long now = DateTime.Now.Ticks;
            if (_lastReceivedTime != 0) {
                if (now - _lastReceivedTime < 10 * 1000 * 100)
                    _shortReceiveTimeCount++;
            }
            _lastReceivedTime = now;

            if (region.InvalidatedAll)
                _fullInvalidateCount++;
            else {
                _partialInvalidateCount++;
                _totalInvalidatedLineCount += region.LineIDEnd - region.LineIDStart + 1;
                if (region.LineIDStart == region.LineIDEnd)
                    _invalidate1LineCount++;
            }
        }

        public static void Output() {
            Debug.WriteLine(String.Format("ReceiveData:{0}  (short:{1})", _receiveDataCount, _shortReceiveTimeCount));
            Debug.WriteLine(String.Format("FullInvalidate:{0} PartialInvalidate:{1} 1-Line:{2} AvgLine:{3:F2}", _fullInvalidateCount, _partialInvalidateCount, _invalidate1LineCount, (double)_totalInvalidatedLineCount / _partialInvalidateCount));
        }

    }


}
