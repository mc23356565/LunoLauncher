using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using OpenLauncher.Content;
using OpenLauncher.Game;

namespace OpenLauncher.UI.Pages
{
    public partial class DownloadPage : UserControl, IPage
    {
        GameListView _game;
        readonly Dictionary<ContentType, ContentSearchView> _views = new Dictionary<ContentType, ContentSearchView>();

        public DownloadPage()
        {
            InitializeComponent();
            NavGame.IsChecked = true;
        }

        public void OnShown()
        {
            (Host.Content as IPage)?.OnShown();
        }

        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (Host == null) return;
            if (sender == NavGame) Host.Content = _game ?? (_game = new GameListView());
            else Host.Content = View(TypeOf(sender));
            (Host.Content as IPage)?.OnShown();
        }

        ContentType TypeOf(object nav) =>
            nav == NavPack ? ContentType.Modpack :
            nav == NavRes ? ContentType.ResourcePack :
            nav == NavShader ? ContentType.Shader :
            nav == NavData ? ContentType.DataPack : ContentType.Mod;

        ContentSearchView View(ContentType t)
        {
            if (!_views.TryGetValue(t, out var v)) _views[t] = v = new ContentSearchView(t);
            return v;
        }

        /// <summary>从别的页面跳过来：打开某类资源并预设目标版本</summary>
        public void OpenContent(ContentType type, Instance target)
        {
            var nav = type switch
            {
                ContentType.Modpack => NavPack,
                ContentType.ResourcePack => NavRes,
                ContentType.Shader => NavShader,
                ContentType.DataPack => NavData,
                _ => NavMod,
            };
            var v = View(type);
            if (target != null) v.SetTarget(target);
            if (nav.IsChecked == true) { Host.Content = v; v.OnShown(); }
            else nav.IsChecked = true;
        }

        async void ImportPack_Click(object sender, RoutedEventArgs e)
        {
            var file = Pickers.PickFile("选择整合包", "整合包 (*.mrpack;*.zip)|*.mrpack;*.zip|所有文件|*.*");
            if (file != null) await VersionsPage.ImportPackAsync(file);
        }
    }
}
