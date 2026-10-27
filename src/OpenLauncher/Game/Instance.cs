using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLauncher.Core;

namespace OpenLauncher.Game
{
    /// <summary>
    /// versions 下的一个可启动版本，带自己的设置（版本隔离、Java、内存……）。
    /// 设置存在 versions/&lt;id&gt;/OpenLauncher/settings.json，跟着版本文件夹走。
    /// </summary>
    public class Instance
    {
        public GameRepo Repo { get; }
        public string Id { get; }
        public string Dir => Repo.VersionDir(Id);
        public string SettingsFile => Path.Combine(Dir, Branding.AppName, "settings.json");

        VersionInfo _info;
        JNode _settings;

        public Instance(GameRepo repo, string id)
        {
            Repo = repo;
            Id = id;
        }

        public VersionInfo Info => _info ?? (_info = VersionInfo.Load(Repo, Id));
        public void Reload() { _info = null; _settings = null; }

        /// <summary>读 JSON 失败时为 false（缺原版、文件损坏）</summary>
        public bool IsValid
        {
            get { try { return Info != null; } catch { return false; } }
        }

        public string LoadError
        {
            get { try { _ = Info; return null; } catch (Exception ex) { return ex.Message; } }
        }

        public string McVersion => IsValid ? Info.McVersion : "?";
        public List<LoaderInfo> Loaders => IsValid ? Info.Loaders : new List<LoaderInfo>();
        public LoaderType PrimaryLoader => IsValid ? Info.PrimaryLoader : LoaderType.None;
        public bool IsModded => IsValid && Info.IsModded;

        public string Description
        {
            get
            {
                if (!IsValid) return "版本文件不完整：" + LoadError;
                var parts = new List<string> { McVersion };
                parts.AddRange(Loaders.Select(l => l.ToString()));
                var desc = string.Join("，", parts);
                var custom = Settings["description"].Str;
                return string.IsNullOrEmpty(custom) ? desc : custom + "  ·  " + desc;
            }
        }

        // ---------- 版本设置 ----------
        public JNode Settings
        {
            get
            {
                if (_settings != null) return _settings;
                try { _settings = File.Exists(SettingsFile) ? JNode.ParseFile(SettingsFile) : JNode.Obj(); }
                catch { _settings = JNode.Obj(); }
                if (!_settings.IsObj) _settings = JNode.Obj();
                return _settings;
            }
        }

        public void SaveSettings()
        {
            try { Settings.SaveFile(SettingsFile); }
            catch (Exception ex) { Log.Error("保存版本设置失败", ex); }
        }

        public string GetSetting(string key, string def = "") => Settings[key].Str ?? def;
        public void SetSetting(string key, JNode value) { Settings.Set(key, value); SaveSettings(); }

        /// <summary>on / off / 空（跟随全局）</summary>
        public string IsolationOverride { get => GetSetting("isolation"); set => SetSetting("isolation", value); }

        /// <summary>是否版本隔离：隔离时游戏目录就是版本文件夹本身</summary>
        public bool Isolated
        {
            get
            {
                var o = IsolationOverride;
                if (o == "on") return true;
                if (o == "off") return false;
                // 文件夹里已经有 mods / saves 的，说明之前就是隔离的（兼容 PCL / HMCL 创建的版本）
                if (Directory.Exists(Path.Combine(Dir, "mods")) || Directory.Exists(Path.Combine(Dir, "saves"))) return true;
                switch (Core.Settings.Isolation)
                {
                    case "all": return true;
                    case "none": return false;
                    default: return IsModded;
                }
            }
        }

        public string GameDir => Isolated ? Dir : Repo.Root;
        public string ModsDir => Path.Combine(GameDir, "mods");
        public string ResourcePacksDir => Path.Combine(GameDir, "resourcepacks");
        public string ShaderPacksDir => Path.Combine(GameDir, "shaderpacks");
        public string SavesDir => Path.Combine(GameDir, "saves");
        public string ScreenshotsDir => Path.Combine(GameDir, "screenshots");
        public string LogsDir => Path.Combine(GameDir, "logs");
        public string CrashReportsDir => Path.Combine(GameDir, "crash-reports");
        public string NativesDir => Path.Combine(Dir, Id + "-natives");

        public string Icon
        {
            get
            {
                var custom = GetSetting("icon");
                if (!string.IsNullOrEmpty(custom)) return custom;
                return PrimaryLoader.ToString();
            }
        }

        public override string ToString() => Id;
    }
}
