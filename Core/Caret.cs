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
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Diagnostics;
using System.Text;
using System.Windows.Forms;

using Poderosa.Util;
using Poderosa.Forms;

namespace Poderosa.View {

    /// <summary>
    /// 
    /// </summary>
    /// <exclude/>
    public enum CaretType {
        Line = 0,
        Box = 2,
        Underline = 4,
        StyleMask = Box | Underline,
    }

    //Caretの座標と状態を収録
    /// <summary>
    /// <ja>
    /// キャレットの座標と状態を格納するオブジェクトです。
    /// </ja>
    /// <en>
    /// Object that stores coordinates and state of caret
    /// </en>
    /// </summary>
    /// <exclude/>
    public class Caret {
        private const int TICKER_LOOP_INTERVAL = 2;

        private CaretType _style; //Line, Box, Underlineのいずれか
        private Color _color;
        private int _x; //文字単位での座標
        private int _y;
        private int _tick; //一定時間毎の切り替わり
        private bool _enabled;
        private bool _blink;
        private bool _pending;
        private Pen _pen;

        public Caret() {
            _style = CaretType.Box;
            _color = Color.Empty;
            _enabled = false;
            _blink = true;
        }

        public CaretType Style {
            get {
                return _style;
            }
            set {
                _style = value & CaretType.StyleMask;
            }
        }
        public int X {
            get {
                return _x;
            }
            set {
                _x = value;
            }
        }
        public int Y {
            get {
                return _y;
            }
            set {
                _y = value;
            }
        }
        public bool Enabled {
            get {
                return _enabled;
            }
            set {
                _enabled = value;
            }
        }
        public bool Blink {
            get {
                return _blink;
            }
            set {
                _blink = value;
            }
        }
        public Color Color {
            get {
                return _color;
            }
            set {
                _color = value;
                DisposePen();
            }
        }


        public bool IsActiveTick {
            get {
                return _tick <= 0;
            }
        }
        public void Tick() {
            if (_pending) {
                _pending = false;
                _tick = 0;
            }
            else {
                _tick = (_tick + 1) % TICKER_LOOP_INTERVAL;
            }
        }
        public void KeepActiveUntilNextTick() {
            _pending = true;
        }
        public void Reset() {
            DisposePen();
        }

        //Pen
        internal Pen ToPen(RenderProfile p) {
            if (_pen == null) {
                _pen = new Pen(_color == Color.Empty ? p.ForeColor : _color);
            }
            return _pen;
        }

        internal void Dispose() {
            DisposePen();
        }

        private void DisposePen() {
            if (_pen != null) {
                _pen.Dispose(); //ペンのセットでリセット
                _pen = null;
            }
        }


    }
}
