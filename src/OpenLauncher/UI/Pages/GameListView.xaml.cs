using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenLauncher.Core;
using OpenLauncher.Game;

namespace OpenLauncher.UI.Pages
{
    public partial class GameListView : UserControl, IPage
    {
        List<McVersionEntry> _all;
        bool _loading;

        public GameListView()
        {
            InitializeComponent();
        }

        public void OnShown()
        {
            if (_all == null && !_loading) _ = LoadAsync(false);
        }

        async Task LoadAsync(bool force)
        {
            _loading = true;
            Loading.Visibility = Visibility.Visible;
            LoadingText.Text = "正在从官方获取版本列表…";
            List.ItemsSource = null;
            try
            {
                _all = await Task.Run(() => VersionList.GetMinecraftAsync(force));
                Loading.Visibility = Visibility.Collapsed;
                Apply();
            }
            catch (Exception ex)
            {
                LoadingText.Text = "获取失败：" + ex.Message + "\n检查网络，或在 设置 → 下载 里换下载源后点刷新";
            }
            finally { _loading = false; }
        }

        void Apply()
        {
            if (_all == null || List == null) return;
            IEnumerable<McVersionEntry> q = _all;
            if (FRelease.IsChecked == true) q = q.Where(v => v.Type == "release");
            else if (FSnapshot.IsChecked == true) q = q.Where(v => v.Type == "snapshot" && !v.IsAprilFools);
            else if (FOld.IsChecked == true) q = q.Where(v => v.Type == "old_beta" || v.Type == "old_alpha");
            else if (FFools.IsChecked == true) q = q.Where(v => v.IsAprilFools);
            var s = Search.Text.Trim();
            if (s.Length > 0) q = q.Where(v => v.Id.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
            List.ItemsSource = q.ToList();
        }

        void Filter_Changed(object sender, RoutedEventArgs e) => Apply();
        void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync(true);

        void Install_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is McVersionEntry v) MainWindow.Current.Push(new GameInstallPage(v));
        }

        void List_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (List.SelectedItem is McVersionEntry v) MainWindow.Current.Push(new GameInstallPage(v));
        }
    }
}
