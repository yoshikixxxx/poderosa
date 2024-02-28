﻿// Copyright 2004-2017 The Poderosa Project.
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
using System.Collections;
using System.IO;
using System.Diagnostics;
using System.Windows.Forms;
using System.Text;

using Granados;
using Granados.KnownHosts;
using Poderosa.Plugins;
using Poderosa.Protocols;
using Poderosa.Preferences;
using Poderosa.Forms;
using Poderosa.Util.Collections;

namespace Poderosa.Usability {
    internal class SSHKnownHosts : IPreferenceSupplier, ISSHHostKeyVerifier2 {
        private TypedHashtable<string, string> _dataForSSH1; //hostからエントリへのマップ
        private TypedHashtable<string, string> _dataForSSH2;
        private bool _modified;
        private bool _loaded;
        private IStringPreferenceItem _fileName;

        public SSHKnownHosts() {
            _dataForSSH1 = new TypedHashtable<string, string>();
            _dataForSSH2 = new TypedHashtable<string, string>();
            _modified = false;
            _loaded = false;
        }
        public void Clear() {
            _dataForSSH1 = new TypedHashtable<string, string>();
            _dataForSSH2 = new TypedHashtable<string, string>();
        }

        public bool Loaded {
            get {
                return _loaded;
            }
        }
        public bool Modified {
            get {
                return _modified;
            }
        }

        private static void WriteEntry(StreamWriter w, string host, string key_expr) {
            w.Write(host);
            w.Write(' ');
            w.WriteLine(key_expr);
        }

        #region ISSHHostKeyVerifier2
        public bool Verify(ISSHHostKeyInformationProvider info) {
            if (!_loaded) {
                try {
                    Load();
                }
                catch (Exception ex) { //ロード中のエラーのときは鍵は拒否。安全側に！
                    RuntimeUtil.ReportException(ex);
                    return false;
                }
            }

            string keystr = info.KnownHostsString;
            string local = (info.Protocol == SSHProtocol.SSH1) ? _dataForSSH1[ToKeyString(info)] : _dataForSSH2[ToKeyString(info)];

            if (local == null) {
                Update(info, keystr, true);
                return true;
            }
            else if (keystr != local) {
                return AskUserReliability(info, keystr, "Message.HostKeyChecker.AskHostKeyRenew");
            }
            else
                return true;
        }

        private bool AskUserReliability(ISSHHostKeyInformationProvider info, string keystr, string message_text_id) {
            //比較結果に基づく処理
            IPoderosaForm form = UsabilityPlugin.Instance.WindowManager.ActiveWindow;
            Debug.Assert(form.AsForm().InvokeRequired); //別スレッドで実行しているはず

            //fingerprint
            StringBuilder bld = new StringBuilder();
            byte[] fingerprint = info.HostKeyFingerPrint;
            for (int i = 0; i < fingerprint.Length; i++) {
                if (bld.Length > 0)
                    bld.Append(':');
                bld.Append(fingerprint[i].ToString("x2"));
            }

            string message = String.Format("ssh hostkey fingerprint {0}\n\n{1}", bld.ToString(), UsabilityPlugin.Strings.GetString(message_text_id));

            if (form.AskUserYesNo(message) == DialogResult.Yes) {
                Update(info, keystr, true);
                return true;
            }
            else
                return false;
        }

        #endregion

        private void Load() {
            Clear();
            _loaded = true;
            _modified = false;

            string filename = GetKnownHostsFileName();
            if (!File.Exists(filename))
                return;

            using (StreamReader r = new StreamReader(filename, Encoding.Default)) {
                string line = r.ReadLine();
                while (line != null) {
                    int sp = line.IndexOf(' ');
                    if (sp == -1)
                        throw new IOException("known_hosts is corrupted: host name field is not found");

                    string body = line.Substring(sp + 1);
                    if (body.StartsWith("ssh1"))
                        _dataForSSH1[line.Substring(0, sp)] = body;
                    else
                        _dataForSSH2[line.Substring(0, sp)] = body;

                    line = r.ReadLine();
                }
            }
        }

        private void Update(ISSHHostKeyInformationProvider info, string key, bool flush) {
            if (info.Protocol == SSHProtocol.SSH1)
                _dataForSSH1[ToKeyString(info)] = key;
            else
                _dataForSSH2[ToKeyString(info)] = key;

            _modified = true;
            if (flush)
                Flush();
        }


        public void Flush() {
            Debug.Assert(_loaded);
            using (StreamWriter w = new StreamWriter(GetKnownHostsFileName(), false, Encoding.Default)) {
                IDictionaryEnumerator ie = _dataForSSH1.GetEnumerator();
                while (ie.MoveNext())
                    WriteEntry(w, (string)ie.Key, (string)ie.Value);

                ie = _dataForSSH2.GetEnumerator();
                while (ie.MoveNext())
                    WriteEntry(w, (string)ie.Key, (string)ie.Value);

                _modified = false;
            }
        }

        private static string ToKeyString(ISSHHostKeyInformationProvider info) {
            string h = info.HostName;
            if (info.PortNumber != 22)
                h += ":" + info.PortNumber;
            return h;
        }


        #region IPreferenceSupplier
        public string PreferenceID {
            get {
                return "org.poderosa.usability.ssh-knownhosts";
            }
        }

        public void InitializePreference(IPreferenceBuilder builder, IPreferenceFolder folder) {
            _fileName = builder.DefineStringValue(folder, "filename", "ssh_known_hosts", null);
        }

        public object QueryAdapter(IPreferenceFolder folder, Type type) {
            return null;
        }

        public void ValidateFolder(IPreferenceFolder folder, IPreferenceValidationResult output) {
        }
        #endregion

        private string GetKnownHostsFileName() {
            IPoderosaApplication app = (IPoderosaApplication)UsabilityPlugin.Instance.PoderosaWorld.GetAdapter(typeof(IPoderosaApplication));
            string result = Path.Combine(app.ProfileHomeDirectory, _fileName.Value);
            Debug.WriteLineIf(DebugOpt.SSH, "known hosts file=" + result);
            return result;
        }
    }
}
