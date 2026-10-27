using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OpenLauncher.Core;

namespace OpenLauncher.Accounts
{
    /// <summary>
    /// 账户列表。保存在 Paths.Accounts，内容是 JSON，用 DPAPI（当前 Windows 用户）加密。
    /// 文件被拷到别的电脑 / 别的用户下无法解密时：记一条警告、把旧文件备份一份，然后从空列表开始。
    /// 所有方法都可以在后台线程调用；Changed 可能在任意线程触发，界面订阅时请自己切回 UI 线程。
    /// </summary>
    public static class AccountStore
    {
        // 文件头：魔数 + 格式版本，后面是 DPAPI 密文
        static readonly byte[] Magic = { (byte)'O', (byte)'L', (byte)'A', (byte)'C', 1 };
        // 固定的附加熵：防止别的程序用同一个用户身份随手解开这个文件
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OpenLauncher/accounts/v1 — DPAPI entropy");

        static readonly object Lock = new object();
        static readonly object FileLock = new object();
        static List<Account> _list = new List<Account>();
        static volatile bool _loaded;

        public static event Action Changed;

        /// <summary>当前所有账户的快照（新列表，可以随意遍历；修改列表本身不会影响存储）</summary>
        public static List<Account> All
        {
            get
            {
                EnsureLoaded();
                lock (Lock) return new List<Account>(_list);
            }
        }

        public static int Count { get { EnsureLoaded(); lock (Lock) return _list.Count; } }

        public static Account Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            EnsureLoaded();
            lock (Lock) return _list.FirstOrDefault(a => a.Id == id);
        }

        static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (Lock)
            {
                if (_loaded) return;
                LoadCore();
            }
        }

        public static void Load()
        {
            lock (Lock) LoadCore();
            RaiseChanged();
        }

        // 调用方已持有 Lock
        static void LoadCore()
        {
            _loaded = true;
            var list = new List<Account>();
            var path = Paths.Accounts;
            try
            {
                if (File.Exists(path))
                {
                    byte[] raw;
                    lock (FileLock) raw = File.ReadAllBytes(path);
                    var json = Decode(raw, path);
                    if (json != null)
                    {
                        var root = JNode.Parse(json);
                        var arr = root.IsArr ? root : root["accounts"];
                        foreach (var n in arr)
                        {
                            var a = Account.FromJson(n);
                            if (a == null) continue;
                            if (list.Any(x => x.Id == a.Id)) a.Id = Guid.NewGuid().ToString("N");
                            list.Add(a);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取账户列表失败，已从空列表开始：" + ex.Message);
                Backup(path, "broken");
                list.Clear();
            }
            _list = list;
            Log.Info($"已加载 {list.Count} 个账户");
        }

        /// <summary>解密文件内容；无法解密时备份旧文件并返回 null</summary>
        static string Decode(byte[] raw, string path)
        {
            if (raw.Length == 0) return null;
            if (StartsWithMagic(raw))
            {
                var cipher = new byte[raw.Length - Magic.Length];
                Buffer.BlockCopy(raw, Magic.Length, cipher, 0, cipher.Length);
                try
                {
                    var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(plain);
                }
                catch (CryptographicException ex)
                {
                    Log.Warn("账户文件无法解密（可能是从别的电脑或别的 Windows 用户拷来的），需要重新登录。旧文件已备份。原因：" + ex.Message);
                    Backup(path, "undecryptable");
                    return null;
                }
            }
            // 兼容：明文 JSON（手动导入 / 调试），下次保存时会自动加密
            var text = Encoding.UTF8.GetString(raw).TrimStart('﻿', ' ', '\r', '\n', '\t');
            if (text.StartsWith("{") || text.StartsWith("["))
            {
                Log.Warn("账户文件是未加密的 JSON，将在下次保存时加密");
                return text;
            }
            Log.Warn("账户文件格式无法识别，已备份并从空列表开始");
            Backup(path, "unknown");
            return null;
        }

        static bool StartsWithMagic(byte[] raw)
        {
            if (raw.Length < Magic.Length) return false;
            for (int i = 0; i < Magic.Length; i++) if (raw[i] != Magic[i]) return false;
            return true;
        }

        static void Backup(string path, string tag)
        {
            try
            {
                if (!File.Exists(path)) return;
                var bak = $"{path}.{tag}-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
                lock (FileLock) File.Copy(path, bak, true);
                Log.Info("已备份旧账户文件：" + Path.GetFileName(bak));
            }
            catch (Exception ex) { Log.Warn("备份账户文件失败：" + ex.Message); }
        }

        public static void Save()
        {
            EnsureLoaded();
            string json;
            lock (Lock)
            {
                var arr = JNode.Arr();
                foreach (var a in _list) arr.Add(a.ToJson());
                json = JNode.Obj().Set("version", 1).Set("accounts", arr).ToJson(false);
            }
            try
            {
                var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);
                var data = new byte[Magic.Length + cipher.Length];
                Buffer.BlockCopy(Magic, 0, data, 0, Magic.Length);
                Buffer.BlockCopy(cipher, 0, data, Magic.Length, cipher.Length);
                var path = Paths.Accounts;
                lock (FileLock)
                {
                    Util.EnsureParent(path);
                    var tmp = path + ".tmp";
                    File.WriteAllBytes(tmp, data);
                    if (File.Exists(path))
                    {
                        try { File.Replace(tmp, path, null); }
                        catch (Exception) { File.Delete(path); File.Move(tmp, path); }
                    }
                    else File.Move(tmp, path);
                }
            }
            catch (Exception ex)
            {
                Log.Error("保存账户列表失败", ex);
            }
        }

        /// <summary>
        /// 添加或更新账户。同一个对象 / 同一个 Id / 同类型同 UUID（外置登录还要同服务器）视为同一个账户：
        /// 新对象会替换掉列表里的旧对象，并沿用旧对象的 Id（所以“当前选中”不会丢）。
        /// </summary>
        public static void AddOrUpdate(Account a)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            EnsureLoaded();
            a.Uuid = AccountUtil.NormalizeUuid(a.Uuid);
            if (a.Type == AccountType.Yggdrasil && a.ServerUrl != null) a.ServerUrl = a.ServerUrl.TrimEnd('/');
            if (string.IsNullOrEmpty(a.Id)) a.Id = Guid.NewGuid().ToString("N");
            lock (Lock)
            {
                int idx = _list.FindIndex(x => ReferenceEquals(x, a));
                if (idx < 0) idx = _list.FindIndex(x => x.Id == a.Id);
                if (idx < 0)
                {
                    var key = a.DedupeKey;
                    idx = _list.FindIndex(x => x.DedupeKey == key);
                }
                if (idx >= 0)
                {
                    a.Id = _list[idx].Id;
                    _list[idx] = a;
                    // 换了身份后可能和别的条目撞车，顺手去重
                    var key = a.DedupeKey;
                    _list.RemoveAll(x => !ReferenceEquals(x, a) && x.DedupeKey == key);
                }
                else _list.Add(a);
            }
            Save();
            RaiseChanged();
        }

        public static void Remove(Account a)
        {
            if (a == null) return;
            EnsureLoaded();
            bool removed;
            lock (Lock) removed = _list.RemoveAll(x => ReferenceEquals(x, a) || x.Id == a.Id) > 0;
            if (!removed) return;
            if (Settings.SelectedAccount == a.Id) Settings.SelectedAccount = "";
            Save();
            RaiseChanged();
        }

        /// <summary>当前选中的账户（存在 Settings.SelectedAccount 里）；没选或找不到时退回第一个；列表为空时为 null</summary>
        public static Account Selected
        {
            get
            {
                EnsureLoaded();
                var id = Settings.SelectedAccount;
                lock (Lock)
                {
                    if (_list.Count == 0) return null;
                    return (!string.IsNullOrEmpty(id) ? _list.FirstOrDefault(x => x.Id == id) : null) ?? _list[0];
                }
            }
            set
            {
                var id = value?.Id ?? "";
                if (Settings.SelectedAccount == id) return;
                Settings.SelectedAccount = id;
                RaiseChanged();
            }
        }

        static void RaiseChanged()
        {
            var h = Changed;
            if (h == null) return;
            try { h(); }
            catch (Exception ex) { Log.Error("账户列表变更通知出错", ex); }
        }
    }
}
